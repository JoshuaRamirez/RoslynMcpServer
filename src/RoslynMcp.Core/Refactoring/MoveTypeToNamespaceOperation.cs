using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring;

/// <summary>
/// Changes the namespace of a type, updating all references across the solution.
/// </summary>
public sealed class MoveTypeToNamespaceOperation
{
    private static readonly Regex NamespacePattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$",
        RegexOptions.Compiled);

    private readonly WorkspaceContext _context;
    private readonly TypeSymbolResolver _symbolResolver;
    private readonly ReferenceTracker _referenceTracker;

    /// <summary>
    /// Creates a new move type to namespace operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public MoveTypeToNamespaceOperation(WorkspaceContext context)
    {
        _context = context;
        _symbolResolver = context.CreateSymbolResolver();
        _referenceTracker = context.CreateReferenceTracker();
    }

    /// <summary>
    /// Executes the namespace move operation.
    /// </summary>
    /// <param name="params">Operation parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Refactoring result.</returns>
    public async Task<RefactoringResult> ExecuteAsync(
        MoveTypeToNamespaceParams @params,
        CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Validate inputs
            ValidateInputs(@params);

            // Resolve symbol
            var resolution = await _symbolResolver.FindTypeInFileAsync(
                @params.SourceFile,
                @params.SymbolName,
                @params.Line,
                cancellationToken);

            // Validate namespace change
            await ValidateNamespaceChangeAsync(@params, resolution, cancellationToken);

            // Find all references
            var references = await _referenceTracker.FindAllReferencesAsync(
                resolution.Symbol,
                cancellationToken);

            // Compute changes
            var (newSolution, changeStats) = await ComputeChangesAsync(
                @params,
                resolution,
                references,
                cancellationToken);

            // If preview mode, return without applying
            if (@params.Preview)
            {
                return CreatePreviewResult(operationId, @params, resolution, changeStats);
            }

            // Commit changes
            var commitResult = await _context.CommitChangesAsync(newSolution, cancellationToken);

            if (!commitResult.Success)
            {
                throw new RefactoringException(
                    ErrorCodes.FilesystemError,
                    $"Failed to write files: {commitResult.Error}");
            }

            stopwatch.Stop();

            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Changes = new FileChanges
                {
                    FilesModified = commitResult.FilesModified,
                    FilesCreated = commitResult.FilesCreated,
                    FilesDeleted = commitResult.FilesDeleted
                },
                Symbol = CreateSymbolInfo(resolution, @params.TargetNamespace),
                ReferencesUpdated = references.TotalReferenceCount,
                UsingDirectivesAdded = changeStats.UsingsAdded,
                UsingDirectivesRemoved = changeStats.UsingsRemoved,
                ExecutionTimeMs = stopwatch.ElapsedMilliseconds
            };
        }
        catch (RefactoringException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                $"Unexpected error: {ex.Message}",
                ex);
        }
    }

    private void ValidateInputs(MoveTypeToNamespaceParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "symbolName is required.");

        if (string.IsNullOrWhiteSpace(@params.TargetNamespace))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "targetNamespace is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!NamespacePattern.IsMatch(@params.TargetNamespace))
            throw new RefactoringException(
                ErrorCodes.InvalidNamespace,
                $"Invalid namespace format: {@params.TargetNamespace}. Must be valid C# identifier(s) separated by dots.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");
    }

    private async Task ValidateNamespaceChangeAsync(
        MoveTypeToNamespaceParams @params,
        SymbolResolutionResult resolution,
        CancellationToken cancellationToken)
    {
        var currentNamespace = resolution.Symbol.ContainingNamespace.ToDisplayString();

        if (currentNamespace == @params.TargetNamespace)
        {
            throw new RefactoringException(
                ErrorCodes.SameLocation,
                $"Type is already in namespace '{@params.TargetNamespace}'.");
        }

        // Check for name collision in target namespace
        var targetType = await _symbolResolver.FindTypeByNameAsync(
            $"{@params.TargetNamespace}.{resolution.Symbol.Name}",
            cancellationToken);

        if (targetType != null && !SymbolEqualityComparer.Default.Equals(targetType, resolution.Symbol))
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"A type named '{resolution.Symbol.Name}' already exists in namespace '{@params.TargetNamespace}'.",
                suggestions: ["Rename the type before moving", "Choose a different target namespace"]);
        }
    }

    private async Task<(Solution, ChangeStats)> ComputeChangesAsync(
        MoveTypeToNamespaceParams @params,
        SymbolResolutionResult resolution,
        ReferenceSearchResult references,
        CancellationToken cancellationToken)
    {
        var solution = _context.Solution;
        var oldNamespace = resolution.Symbol.ContainingNamespace.ToDisplayString();
        var newNamespace = @params.TargetNamespace;
        var stats = new ChangeStats();

        // Update the type's namespace declaration
        solution = await UpdateTypeNamespaceAsync(
            solution,
            resolution,
            newNamespace,
            cancellationToken);

        // Update using directives in all referencing documents
        foreach (var (docId, _) in references.ReferencesByDocument)
        {
            var doc = solution.GetDocument(docId);
            if (doc == null) continue;

            var (newDoc, added, removed) = await UpdateUsingDirectivesAsync(
                doc,
                oldNamespace,
                newNamespace,
                cancellationToken);

            solution = newDoc.Project.Solution;
            stats.UsingsAdded += added;
            stats.UsingsRemoved += removed;
        }

        return (solution, stats);
    }

    private async Task<Solution> UpdateTypeNamespaceAsync(
        Solution solution,
        SymbolResolutionResult resolution,
        string newNamespace,
        CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(resolution.Document.Id);
        if (document == null) return solution;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return solution;

        // Find the namespace declaration containing the type
        var namespaceDecl = resolution.Declaration.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault();

        if (namespaceDecl == null)
        {
            // Type is at file level, wrap in namespace
            var newNs = SyntaxFactory.FileScopedNamespaceDeclaration(
                SyntaxFactory.ParseName(newNamespace));

            // This is a more complex transformation - for now, throw
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                "Cannot move type without namespace declaration. Type must be in a namespace.");
        }

        // Check if this is the only type in the namespace
        var typesInNamespace = namespaceDecl.Members.OfType<TypeDeclarationSyntax>().ToList();

        SyntaxNode newRoot;
        if (typesInNamespace.Count == 1)
        {
            // Simply rename the namespace
            var newNameNode = SyntaxFactory.ParseName(newNamespace);

            if (namespaceDecl is FileScopedNamespaceDeclarationSyntax fileScopedNs)
            {
                var newNs = fileScopedNs.WithName(newNameNode);
                newRoot = root.ReplaceNode(namespaceDecl, newNs);
            }
            else if (namespaceDecl is NamespaceDeclarationSyntax blockNs)
            {
                var newNs = blockNs.WithName(newNameNode);
                newRoot = root.ReplaceNode(namespaceDecl, newNs);
            }
            else
            {
                return solution;
            }
        }
        else
        {
            // Multiple types - need to extract this one
            // For now, we'll update the entire namespace
            // A more sophisticated implementation would split the file
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                "File contains multiple types. Move the type to its own file first.");
        }

        return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
    }

    private async Task<(Document, int added, int removed)> UpdateUsingDirectivesAsync(
        Document document,
        string oldNamespace,
        string newNamespace,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return (document, 0, 0);

        var compilationUnit = root as CompilationUnitSyntax;
        if (compilationUnit == null) return (document, 0, 0);

        var usings = compilationUnit.Usings.ToList();
        var hasOldUsing = usings.Any(u => u.Name?.ToString() == oldNamespace);
        var hasNewUsing = usings.Any(u => u.Name?.ToString() == newNamespace);

        var added = 0;
        var removed = 0;

        // Add using for new namespace if not present
        if (!hasNewUsing)
        {
            var newUsing = SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName(newNamespace).WithLeadingTrivia(SyntaxFactory.Space))
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
            usings.Add(newUsing);
            added = 1;
        }

        // Optionally remove old using if no other types from that namespace are used
        // For simplicity, we keep the old using directive (it's harmless)

        var newCompilationUnit = compilationUnit.WithUsings(SyntaxFactory.List(usings));
        var newDoc = document.WithSyntaxRoot(newCompilationUnit);

        return (newDoc, added, removed);
    }

    private Contracts.Models.SymbolInfo CreateSymbolInfo(SymbolResolutionResult resolution, string newNamespace)
    {
        var oldNamespace = resolution.Symbol.ContainingNamespace.ToDisplayString();
        var location = resolution.Declaration.GetLocation().GetLineSpan();

        return new Contracts.Models.SymbolInfo
        {
            Name = resolution.Symbol.Name,
            FullyQualifiedName = $"{newNamespace}.{resolution.Symbol.Name}",
            Kind = MapSymbolKind(resolution.Symbol),
            PreviousNamespace = oldNamespace,
            NewNamespace = newNamespace,
            PreviousLocation = new SymbolLocation
            {
                File = resolution.Document.FilePath!,
                Line = location.StartLinePosition.Line + 1,
                Column = location.StartLinePosition.Character + 1
            }
        };
    }

    private static Contracts.Enums.SymbolKind MapSymbolKind(INamedTypeSymbol symbol)
    {
        return symbol.TypeKind switch
        {
            TypeKind.Class => Contracts.Enums.SymbolKind.Class,
            TypeKind.Struct => Contracts.Enums.SymbolKind.Struct,
            TypeKind.Interface => Contracts.Enums.SymbolKind.Interface,
            TypeKind.Enum => Contracts.Enums.SymbolKind.Enum,
            TypeKind.Delegate => Contracts.Enums.SymbolKind.Delegate,
            _ when symbol.IsRecord => Contracts.Enums.SymbolKind.Record,
            _ => Contracts.Enums.SymbolKind.Class
        };
    }

    private RefactoringResult CreatePreviewResult(
        Guid operationId,
        MoveTypeToNamespaceParams @params,
        SymbolResolutionResult resolution,
        ChangeStats stats)
    {
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = resolution.Document.FilePath!,
                ChangeType = ChangeKind.Modify,
                Description = $"Change namespace from {resolution.Symbol.ContainingNamespace.ToDisplayString()} to {@params.TargetNamespace}"
            }
        };

        if (stats.UsingsAdded > 0)
        {
            pendingChanges.Add(new PendingChange
            {
                File = "(multiple files)",
                ChangeType = ChangeKind.Modify,
                Description = $"Add using directive for {@params.TargetNamespace} in {stats.UsingsAdded} file(s)"
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed class ChangeStats
    {
        public int UsingsAdded { get; set; }
        public int UsingsRemoved { get; set; }
    }
}
