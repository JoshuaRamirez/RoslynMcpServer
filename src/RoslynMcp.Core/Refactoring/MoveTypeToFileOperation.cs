using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring;

/// <summary>
/// Moves a type declaration to a different file.
/// Handles file creation, using directive updates, and reference preservation.
/// </summary>
public sealed class MoveTypeToFileOperation
{
    private readonly WorkspaceContext _context;
    private readonly TypeSymbolResolver _symbolResolver;
    private readonly ReferenceTracker _referenceTracker;

    /// <summary>
    /// Creates a new move type to file operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public MoveTypeToFileOperation(WorkspaceContext context)
    {
        _context = context;
        _symbolResolver = context.CreateSymbolResolver();
        _referenceTracker = context.CreateReferenceTracker();
    }

    /// <summary>
    /// Executes the move operation.
    /// </summary>
    /// <param name="params">Operation parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Refactoring result.</returns>
    public async Task<RefactoringResult> ExecuteAsync(
        MoveTypeToFileParams @params,
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

            // Validate target
            await ValidateTargetAsync(@params, resolution, cancellationToken);

            // Find all references
            var references = await _referenceTracker.FindAllReferencesAsync(
                resolution.Symbol,
                cancellationToken);

            // Compute changes
            var (newSolution, changeInfo) = await ComputeChangesAsync(
                @params,
                resolution,
                references,
                cancellationToken);

            // If preview mode, return without applying
            if (@params.Preview)
            {
                return CreatePreviewResult(operationId, changeInfo, resolution);
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

            return RefactoringResult.Succeeded(
                operationId,
                new FileChanges
                {
                    FilesModified = commitResult.FilesModified,
                    FilesCreated = commitResult.FilesCreated,
                    FilesDeleted = commitResult.FilesDeleted
                },
                CreateSymbolInfo(resolution, @params.TargetFile),
                references.TotalReferenceCount,
                stopwatch.ElapsedMilliseconds);
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

    private void ValidateInputs(MoveTypeToFileParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "symbolName is required.");

        if (string.IsNullOrWhiteSpace(@params.TargetFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "targetFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsAbsolutePath(@params.TargetFile))
            throw new RefactoringException(ErrorCodes.InvalidTargetPath, "targetFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!PathResolver.IsValidCSharpFilePath(@params.TargetFile))
            throw new RefactoringException(ErrorCodes.InvalidTargetPath, "targetFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        // Check source != target
        if (PathResolver.NormalizePath(@params.SourceFile) == PathResolver.NormalizePath(@params.TargetFile))
            throw new RefactoringException(ErrorCodes.SameLocation, "Source and target files are the same.");
    }

    private async Task ValidateTargetAsync(
        MoveTypeToFileParams @params,
        SymbolResolutionResult resolution,
        CancellationToken cancellationToken)
    {
        var targetDoc = _context.GetDocumentByPath(@params.TargetFile);

        if (targetDoc == null && !@params.CreateTargetFile)
        {
            throw new RefactoringException(
                ErrorCodes.SourceFileNotFound,
                $"Target file does not exist: {@params.TargetFile}. Set createTargetFile=true to create it.");
        }

        // If target exists, check for name collision
        if (targetDoc != null)
        {
            var targetRoot = await targetDoc.GetSyntaxRootAsync(cancellationToken);
            if (targetRoot != null)
            {
                var existingTypes = targetRoot.DescendantNodes()
                    .OfType<TypeDeclarationSyntax>()
                    .Where(t => t.Identifier.Text == resolution.Symbol.Name)
                    .ToList();

                if (existingTypes.Count > 0)
                {
                    throw new RefactoringException(
                        ErrorCodes.NameCollision,
                        $"Target file already contains a type named '{resolution.Symbol.Name}'.",
                        suggestions: ["Rename the type before moving", "Choose a different target file"]);
                }
            }
        }
    }

    private async Task<(Solution, ChangeInfo)> ComputeChangesAsync(
        MoveTypeToFileParams @params,
        SymbolResolutionResult resolution,
        ReferenceSearchResult references,
        CancellationToken cancellationToken)
    {
        var solution = _context.Solution;
        var sourceDoc = resolution.Document;
        var sourceRoot = await sourceDoc.GetSyntaxRootAsync(cancellationToken);

        // Extract the type declaration with its trivia
        var typeNode = resolution.Declaration;

        // Get the namespace
        var namespaceDecl = typeNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var namespaceName = namespaceDecl?.Name.ToString() ?? resolution.Symbol.ContainingNamespace.ToDisplayString();

        // Get required using directives
        var usings = sourceRoot!.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .ToList();

        // Create the target file content
        var targetContent = CreateTargetFileContent(typeNode, namespaceName, usings);

        // Check if target document exists
        var targetDoc = _context.GetDocumentByPath(@params.TargetFile);

        if (targetDoc != null)
        {
            // Add to existing file
            var targetRoot = await targetDoc.GetSyntaxRootAsync(cancellationToken);
            var newTargetRoot = AddTypeToExistingFile(targetRoot!, typeNode, namespaceName);
            solution = solution.WithDocumentSyntaxRoot(targetDoc.Id, newTargetRoot);
        }
        else
        {
            // Create new document
            var project = sourceDoc.Project;
            var newDoc = project.AddDocument(
                Path.GetFileNameWithoutExtension(@params.TargetFile),
                targetContent,
                filePath: @params.TargetFile);
            solution = newDoc.Project.Solution;
        }

        // Remove type from source file
        var newSourceRoot = sourceRoot.RemoveNode(typeNode, SyntaxRemoveOptions.KeepNoTrivia);

        // If source file is now empty (no types), we'll delete it
        var remainingTypes = newSourceRoot?.DescendantNodes().OfType<TypeDeclarationSyntax>().Any() ?? false;

        if (!remainingTypes && newSourceRoot != null)
        {
            // Remove the document entirely
            solution = solution.RemoveDocument(sourceDoc.Id);
        }
        else if (newSourceRoot != null)
        {
            solution = solution.WithDocumentSyntaxRoot(sourceDoc.Id, newSourceRoot);
        }

        return (solution, new ChangeInfo
        {
            SourceFileEmptied = !remainingTypes,
            TargetFileCreated = targetDoc == null,
            TypeNode = typeNode,
            Namespace = namespaceName
        });
    }

    private string CreateTargetFileContent(
        TypeDeclarationSyntax typeNode,
        string namespaceName,
        List<UsingDirectiveSyntax> usings)
    {
        // Build using directives
        var usingStatements = string.Join("\n", usings.Select(u => u.ToFullString().TrimEnd()));

        // Use file-scoped namespace (modern C# style)
        var namespaceDecl = $"namespace {namespaceName};";

        // Get the type declaration with original formatting
        var typeDecl = typeNode.ToFullString();

        var content = string.IsNullOrEmpty(usingStatements)
            ? $"{namespaceDecl}\n\n{typeDecl}"
            : $"{usingStatements}\n\n{namespaceDecl}\n\n{typeDecl}";

        return content.TrimStart();
    }

    private SyntaxNode AddTypeToExistingFile(
        SyntaxNode root,
        TypeDeclarationSyntax typeNode,
        string namespaceName)
    {
        // Find the namespace in the target file
        var targetNamespace = root.DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault(n => n.Name.ToString() == namespaceName);

        if (targetNamespace != null)
        {
            // Add to existing namespace
            var newNamespace = targetNamespace.AddMembers(typeNode);
            return root.ReplaceNode(targetNamespace, newNamespace);
        }

        // Check for file-scoped namespace
        var fileScopedNs = root.DescendantNodes()
            .OfType<FileScopedNamespaceDeclarationSyntax>()
            .FirstOrDefault();

        if (fileScopedNs != null)
        {
            var newNs = fileScopedNs.AddMembers(typeNode);
            return root.ReplaceNode(fileScopedNs, newNs);
        }

        // No matching namespace, add at compilation unit level
        var compilationUnit = (CompilationUnitSyntax)root;
        return compilationUnit.AddMembers(typeNode);
    }

    private Contracts.Models.SymbolInfo CreateSymbolInfo(SymbolResolutionResult resolution, string targetFile)
    {
        var location = resolution.Declaration.GetLocation().GetLineSpan();

        return new Contracts.Models.SymbolInfo
        {
            Name = resolution.Symbol.Name,
            FullyQualifiedName = resolution.Symbol.ToDisplayString(),
            Kind = MapSymbolKind(resolution.Symbol),
            PreviousLocation = new SymbolLocation
            {
                File = resolution.Document.FilePath!,
                Line = location.StartLinePosition.Line + 1,
                Column = location.StartLinePosition.Character + 1
            },
            NewLocation = new SymbolLocation
            {
                File = targetFile,
                Line = 1, // Will be accurate after file is written
                Column = 1
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
        ChangeInfo changeInfo,
        SymbolResolutionResult resolution)
    {
        var pendingChanges = new List<PendingChange>();

        // Source file change
        pendingChanges.Add(new PendingChange
        {
            File = resolution.Document.FilePath!,
            ChangeType = changeInfo.SourceFileEmptied ? ChangeKind.Delete : ChangeKind.Modify,
            Description = changeInfo.SourceFileEmptied
                ? $"Delete file (emptied after removing {resolution.Symbol.Name})"
                : $"Remove {resolution.Symbol.Name} declaration"
        });

        // Target file change
        pendingChanges.Add(new PendingChange
        {
            File = changeInfo.TypeNode.SyntaxTree.FilePath,
            ChangeType = changeInfo.TargetFileCreated ? ChangeKind.Create : ChangeKind.Modify,
            Description = changeInfo.TargetFileCreated
                ? $"Create file with {resolution.Symbol.Name}"
                : $"Add {resolution.Symbol.Name} declaration"
        });

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed class ChangeInfo
    {
        public bool SourceFileEmptied { get; init; }
        public bool TargetFileCreated { get; init; }
        public required TypeDeclarationSyntax TypeNode { get; init; }
        public required string Namespace { get; init; }
    }
}
