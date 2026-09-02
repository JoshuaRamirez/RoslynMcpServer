using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring;

/// <summary>
/// Changes the namespace of a type, updating all references across the solution.
/// Honors optional <c>line</c> and <c>column</c> to disambiguate
/// same-named top-level types in one file. Omitted column keeps today's
/// symbolName + optional line pick (start-line equality /
/// <c>SymbolAmbiguous</c> / single-match ignores line). Column without
/// line keeps that omitted-line path. When column is set with line, picks
/// the covering top-level type (identifier preferred, then smallest
/// covering type). Nested types stay unmoveable.
/// Optional <c>allFiles</c> walks every C# document (or the optional
/// single <c>sourceFile</c>) and moves every eligible top-level type
/// whose current namespace is not already <c>targetNamespace</c>
/// (skip nested / already-there / SameLocation / NameCollision /
/// uneditable / resolution failures / types today's
/// <see cref="ValidateNamespaceChangeAsync"/> or
/// <see cref="ComputeChangesAsync"/> would reject rather than
/// throwing). Bulk walks every eligible type, not a broader search
/// for one symbolName. <c>updateFileLocation</c> stays valid; when
/// two types would claim the same destination, the later claim is
/// skipped.
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

            if (@params.AllFiles)
                return await ExecuteAllFilesAsync(operationId, @params, stopwatch, cancellationToken);

            // Resolve symbol. Optional line/column disambiguates
            // same-named top-level types. Omitted column keeps today's
            // symbolName + optional line pick (start-line equality /
            // SymbolAmbiguous / single-match ignores line). Column
            // without line keeps that omitted-line path. Column set
            // with line picks the covering top-level type (identifier
            // preferred, then smallest covering type).
            var resolution = await _symbolResolver.FindTypeInFileAsync(
                @params.SourceFile!,
                @params.SymbolName!,
                @params.Line,
                @params.Column,
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

            FileLocationPlan? filePlan = null;
            if (@params.UpdateFileLocation)
                filePlan = PlanFileLocationUpdate(resolution, @params.TargetNamespace);

            // If preview mode, return without applying (including folders)
            if (@params.Preview)
            {
                return CreatePreviewResult(operationId, @params, resolution, changeStats, filePlan);
            }

            // Commit text changes at the current document path first
            var commitResult = await _context.CommitChangesAsync(newSolution, cancellationToken);

            if (!commitResult.Success)
            {
                throw new RefactoringException(
                    ErrorCodes.FilesystemError,
                    $"Failed to write files: {commitResult.Error}");
            }

            var filesModified = commitResult.FilesModified.ToList();
            var filesCreated = commitResult.FilesCreated.ToList();
            var filesDeleted = commitResult.FilesDeleted.ToList();

            if (filePlan != null)
            {
                ApplyFileLocationUpdate(filePlan);
                filesModified = filesModified
                    .Select(path => RemapCommittedPath(path, filePlan))
                    .ToList();
                filesModified.AddRange(filePlan.ProjectTexts.Keys);
                filesCreated.Add(filePlan.DestinationFile);
                filesDeleted.Add(filePlan.SourceFile);
            }

            stopwatch.Stop();

            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Changes = new FileChanges
                {
                    FilesModified = filesModified,
                    FilesCreated = filesCreated,
                    FilesDeleted = filesDeleted
                },
                Symbol = CreateSymbolInfo(resolution, @params.TargetNamespace, filePlan?.DestinationFile),
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

    private void ValidateInputs(MoveTypeToNamespaceParams @params) => Validate(@params);

    /// <summary>
    /// Validates move-type-to-namespace inputs. Internal so tests can exercise
    /// rules without loading a workspace.
    /// </summary>
    internal static void Validate(MoveTypeToNamespaceParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.TargetNamespace))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "targetNamespace is required.");

        if (!NamespacePattern.IsMatch(@params.TargetNamespace))
            throw new RefactoringException(
                ErrorCodes.InvalidNamespace,
                $"Invalid namespace format: {@params.TargetNamespace}. Must be valid C# identifier(s) separated by dots.");

        if (@params.AllFiles)
        {
            if (!string.IsNullOrWhiteSpace(@params.SymbolName) ||
                @params.Line.HasValue ||
                @params.Column.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with symbolName, line, or column.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "symbolName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>FormatDocumentOperation.ExecuteAllFilesAsync</c>
    /// / <c>MoveTypeToFileOperation.ExecuteAllFilesAsync</c> /
    /// <c>RenameFileToMatchTypeOperation.ExecuteAllFilesAsync</c> /
    /// <c>UseBaseTypeOperation.ExecuteAllFilesAsync</c> /
    /// <c>EncapsulateFieldOperation.ExecuteAllFilesAsync</c>) and moves
    /// every eligible top-level <c>TypeDeclarationSyntax</c> into
    /// <paramref name="params"/>.TargetNamespace. Optional <c>sourceFile</c>
    /// limits the walk to that one file. Nested types, types already in
    /// the target namespace, SameLocation, NameCollision, multi-type
    /// files, types without a namespace declaration, uneditable
    /// documents, and resolution failures are skipped. When
    /// <c>updateFileLocation</c> is true and two types would claim the
    /// same destination, the later claim is skipped. When every type is
    /// a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        MoveTypeToNamespaceParams @params,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var originalSolution = _context.Solution;
        var allDocuments = originalSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.FilePath, StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
        {
            string wanted;
            try
            {
                wanted = PathResolver.NormalizePath(@params.SourceFile);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                wanted = @params.SourceFile;
            }

            allDocuments = allDocuments
                .Where(d => string.Equals(
                    PathResolver.NormalizePath(d.FilePath!),
                    wanted,
                    StringComparison.Ordinal))
                .ToList();
        }

        var pendingChanges = new List<PendingChange>();
        var filePlans = new List<FileLocationPlan>();
        var claimedDestinations = new List<string>();
        var caseDistinctCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        var anyMove = false;
        var totalReferences = 0;
        var totalUsingsAdded = 0;
        var totalUsingsRemoved = 0;

        foreach (var document in allDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsDocumentEditable(document, _context.Workspace))
                continue;

            var sourceFile = document.FilePath;
            if (string.IsNullOrWhiteSpace(sourceFile))
                continue;

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
                continue;

            foreach (var typeDecl in CollectTopLevelTypes(root))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var typeName = typeDecl.Identifier.Text;
                if (string.IsNullOrWhiteSpace(typeName))
                    continue;

                try
                {
                    var resolution = await TryResolveTopLevelTypeAsync(
                        sourceFile,
                        typeName,
                        typeDecl,
                        cancellationToken);
                    if (resolution == null)
                        continue;

                    if (!IsDocumentEditable(resolution.Document, _context.Workspace))
                        continue;

                    var currentNamespace = resolution.Symbol.ContainingNamespace.ToDisplayString();
                    if (currentNamespace == @params.TargetNamespace)
                        continue;

                    var moveParams = new MoveTypeToNamespaceParams
                    {
                        SourceFile = sourceFile,
                        SymbolName = typeName,
                        TargetNamespace = @params.TargetNamespace,
                        UpdateFileLocation = @params.UpdateFileLocation,
                        Preview = @params.Preview
                    };

                    await ValidateNamespaceChangeAsync(moveParams, resolution, cancellationToken);

                    var references = await _referenceTracker.FindAllReferencesAsync(
                        resolution.Symbol,
                        cancellationToken);

                    var (newSolution, changeStats) = await ComputeChangesAsync(
                        moveParams,
                        resolution,
                        references,
                        cancellationToken);

                    FileLocationPlan? filePlan = null;
                    if (@params.UpdateFileLocation)
                    {
                        filePlan = TryPlanFileLocationUpdate(resolution, @params.TargetNamespace);
                        if (filePlan != null)
                        {
                            if (claimedDestinations.Any(claimed =>
                                    RenameFileToMatchTypeOperation.DestinationsReferToSameLocation(
                                        claimed, filePlan.DestinationFile, caseDistinctCache)))
                            {
                                filePlan = null;
                            }
                            else
                            {
                                claimedDestinations.Add(filePlan.DestinationFile);
                                filePlans.Add(filePlan);
                            }
                        }
                    }

                    pendingChanges.AddRange(CreateAllFilesPendingChanges(
                        moveParams, resolution, changeStats, filePlan));
                    totalReferences += references.TotalReferenceCount;
                    totalUsingsAdded += changeStats.UsingsAdded;
                    totalUsingsRemoved += changeStats.UsingsRemoved;
                    _context.UpdateSolution(newSolution);
                    anyMove = true;
                }
                catch (RefactoringException)
                {
                    // Skip per-type failures that single-site would throw.
                }
            }
        }

        if (@params.Preview)
        {
            _context.UpdateSolution(originalSolution);
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var finalSolution = _context.Solution;
        _context.UpdateSolution(originalSolution);

        if (!anyMove)
        {
            stopwatch.Stop();
            return RefactoringResult.Succeeded(
                operationId,
                new FileChanges { FilesModified = [], FilesCreated = [], FilesDeleted = [] },
                null,
                0,
                stopwatch.ElapsedMilliseconds);
        }

        var commitResult = await _context.CommitChangesAsync(finalSolution, cancellationToken);
        if (!commitResult.Success)
        {
            throw new RefactoringException(
                ErrorCodes.FilesystemError,
                $"Failed to write files: {commitResult.Error}");
        }

        var filesModified = commitResult.FilesModified.ToList();
        var filesCreated = commitResult.FilesCreated.ToList();
        var filesDeleted = commitResult.FilesDeleted.ToList();

        foreach (var filePlan in filePlans)
        {
            try
            {
                ApplyFileLocationUpdate(filePlan);
                filesModified = filesModified
                    .Select(path => RemapCommittedPath(path, filePlan))
                    .ToList();
                filesModified.AddRange(filePlan.ProjectTexts.Keys);
                filesCreated.Add(filePlan.DestinationFile);
                filesDeleted.Add(filePlan.SourceFile);
            }
            catch (RefactoringException)
            {
                // Skip later file-location claims that collide or fail mid-walk.
            }
        }

        stopwatch.Stop();
        return new RefactoringResult
        {
            Success = true,
            OperationId = operationId,
            Changes = new FileChanges
            {
                FilesModified = filesModified,
                FilesCreated = filesCreated,
                FilesDeleted = filesDeleted
            },
            ReferencesUpdated = totalReferences,
            UsingDirectivesAdded = totalUsingsAdded,
            UsingDirectivesRemoved = totalUsingsRemoved,
            ExecutionTimeMs = stopwatch.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// Top-level named types in a file (class, struct, interface, record).
    /// Nested types are ignored, matching single-site <c>FindTypeInFileAsync</c>.
    /// Enums and delegates are not <see cref="TypeDeclarationSyntax"/> and
    /// stay out of the bulk walk.
    /// </summary>
    internal static IReadOnlyList<TypeDeclarationSyntax> CollectTopLevelTypes(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .ToList();

    /// <summary>
    /// Full namespace of a top-level type, including nested namespace
    /// declarations. Prefers the semantic containing namespace when a
    /// model is available.
    /// </summary>
    internal static string GetNamespaceName(
        TypeDeclarationSyntax typeDecl,
        SemanticModel? semanticModel = null,
        CancellationToken cancellationToken = default)
    {
        if (semanticModel?.GetDeclaredSymbol(typeDecl, cancellationToken) is INamedTypeSymbol symbol
            && symbol.ContainingNamespace != null
            && !symbol.ContainingNamespace.IsGlobalNamespace)
        {
            return symbol.ContainingNamespace.ToDisplayString();
        }

        var parts = typeDecl.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(n => n.Name.ToString())
            .ToList();
        return parts.Count == 0 ? "" : string.Join(".", parts);
    }

    /// <summary>
    /// Preview description for moving <paramref name="typeName"/> into
    /// <paramref name="targetNamespace"/>.
    /// </summary>
    internal static string BuildAllFilesDescription(string typeName, string targetNamespace) =>
        $"Change namespace of {typeName} to {targetNamespace}";

    /// <summary>
    /// True when the document can receive a path or text edit. AllFiles skips
    /// rather than throwing the single-file <see cref="ErrorCodes.DocumentNotEditable"/>.
    /// </summary>
    internal static bool IsDocumentEditable(Document document, Microsoft.CodeAnalysis.Workspace workspace)
    {
        if (document is SourceGeneratedDocument)
            return false;

        if (string.IsNullOrWhiteSpace(document.FilePath) || !File.Exists(document.FilePath))
            return false;

        return workspace.CanApplyChange(ApplyChangesKind.ChangeDocument);
    }

    private async Task<SymbolResolutionResult?> TryResolveTopLevelTypeAsync(
        string sourceFile,
        string typeName,
        TypeDeclarationSyntax typeDecl,
        CancellationToken cancellationToken)
    {
        var document = _context.GetDocumentByPath(sourceFile);
        if (document == null)
            return null;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            return null;

        var expectedNamespace = GetNamespaceName(typeDecl);
        var match = CollectTopLevelTypes(root).FirstOrDefault(t =>
            t.Identifier.Text == typeName &&
            GetNamespaceName(t) == expectedNamespace);
        if (match == null)
            return null;

        if (semanticModel.GetDeclaredSymbol(match, cancellationToken) is not INamedTypeSymbol symbol)
            return null;

        if (symbol.ContainingType != null)
            return null;

        return new SymbolResolutionResult
        {
            Symbol = symbol,
            Declaration = match,
            Document = document
        };
    }

    private FileLocationPlan? TryPlanFileLocationUpdate(
        SymbolResolutionResult resolution,
        string newNamespace)
    {
        try
        {
            return PlanFileLocationUpdate(resolution, newNamespace);
        }
        catch (RefactoringException)
        {
            return null;
        }
    }

    private List<PendingChange> CreateAllFilesPendingChanges(
        MoveTypeToNamespaceParams @params,
        SymbolResolutionResult resolution,
        ChangeStats stats,
        FileLocationPlan? filePlan)
    {
        var pending = new List<PendingChange>
        {
            new()
            {
                File = resolution.Document.FilePath!,
                ChangeType = ChangeKind.Modify,
                Description = BuildAllFilesDescription(
                    resolution.Symbol.Name, @params.TargetNamespace)
            }
        };

        if (stats.UsingsAdded > 0)
        {
            pending.Add(new PendingChange
            {
                File = "(multiple files)",
                ChangeType = ChangeKind.Modify,
                Description = $"Add using directive for {@params.TargetNamespace} in {stats.UsingsAdded} file(s)"
            });
        }

        if (filePlan != null)
        {
            pending.Add(new PendingChange
            {
                File = filePlan.SourceFile,
                ChangeType = ChangeKind.Delete,
                Description = $"Move file to match namespace '{@params.TargetNamespace}'"
            });
            pending.Add(new PendingChange
            {
                File = filePlan.DestinationFile,
                ChangeType = ChangeKind.Create,
                Description = $"Move '{filePlan.SourceFile}' to '{filePlan.DestinationFile}'"
            });

            foreach (var projectPath in filePlan.ProjectTexts.Keys)
            {
                pending.Add(new PendingChange
                {
                    File = projectPath,
                    ChangeType = ChangeKind.Modify,
                    Description = "Update explicit Compile item to the moved file"
                });
            }
        }

        return pending;
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

    /// <summary>
    /// Builds the destination path for <paramref name="sourceFile"/> so it
    /// matches <paramref name="newNamespace"/> (e.g. <c>MyApp.Services</c> →
    /// <c>MyApp/Services/</c>). When the current folder already matches
    /// <paramref name="oldNamespace"/>, that folder is remapped (preserving a
    /// prefix such as <c>src/</c>); otherwise the namespace path is created
    /// under <paramref name="projectDirectory"/>.
    /// </summary>
    internal static string ComputeDestinationFile(
        string sourceFile,
        string oldNamespace,
        string newNamespace,
        string projectDirectory)
    {
        var fileName = Path.GetFileName(sourceFile);
        var matchingFolder = RenameNamespaceOperation.TryFindMatchingFolder(
            sourceFile,
            oldNamespace,
            projectDirectory);

        string destFolder;
        if (matchingFolder != null)
        {
            destFolder = RenameNamespaceOperation.GetDestinationFolder(
                matchingFolder,
                oldNamespace,
                newNamespace);
        }
        else
        {
            var parts = RenameNamespaceOperation.SplitNamespace(newNamespace);
            destFolder = PathResolver.NormalizePath(
                Path.Combine(new[] { projectDirectory }.Concat(parts).ToArray()));
        }

        return PathResolver.NormalizePath(Path.Combine(destFolder, fileName));
    }

    private FileLocationPlan? PlanFileLocationUpdate(
        SymbolResolutionResult resolution,
        string newNamespace)
    {
        var document = resolution.Document;
        if (string.IsNullOrWhiteSpace(document.FilePath) || !File.Exists(document.FilePath))
        {
            throw new RefactoringException(
                ErrorCodes.SourceFileNotFound,
                $"Source file not found: {document.FilePath}");
        }

        RenameFileToMatchTypeOperation.ValidateDocumentIsEditable(document, _context.Workspace);

        if (string.IsNullOrWhiteSpace(document.Project.FilePath))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Project '{document.Project.Name}' is not editable.");
        }

        var projectDirectory = Path.GetDirectoryName(document.Project.FilePath);
        if (string.IsNullOrEmpty(projectDirectory))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Project '{document.Project.Name}' is not editable.");
        }

        var sourceFile = PathResolver.NormalizePath(document.FilePath);
        var oldNamespace = resolution.Symbol.ContainingNamespace.ToDisplayString();
        var destFile = ComputeDestinationFile(sourceFile, oldNamespace, newNamespace, projectDirectory);

        if (IsUnchangedFileLocation(sourceFile, destFile))
            return null;

        if (RenameFileToMatchTypeOperation.IsDestinationOccupiedByDifferentFile(sourceFile, destFile))
        {
            throw new RefactoringException(
                ErrorCodes.TargetFileExists,
                $"Destination file already exists: {destFile}");
        }

        var linkedDocuments = FindDocumentsAtPath(_context.Solution, sourceFile);
        foreach (var linked in linkedDocuments)
            RenameFileToMatchTypeOperation.ValidateDocumentIsEditable(linked, _context.Workspace);

        foreach (var other in _context.Solution.Projects.SelectMany(p => p.Documents))
        {
            if (string.IsNullOrWhiteSpace(other.FilePath))
                continue;

            var otherNorm = PathResolver.NormalizePath(other.FilePath);
            if (IsSameDocumentPath(otherNorm, sourceFile))
                continue;

            if (IsSameDocumentPath(otherNorm, destFile))
            {
                throw new RefactoringException(
                    ErrorCodes.TargetFileExists,
                    $"File name collision after move: {destFile}");
            }
        }

        var projectTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var linked in linkedDocuments)
        {
            if (string.IsNullOrWhiteSpace(linked.Project.FilePath))
            {
                throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Project '{linked.Project.Name}' is not editable.");
            }

            if (projectTexts.ContainsKey(linked.Project.FilePath))
                continue;

            var linkedProjectDirectory = Path.GetDirectoryName(linked.Project.FilePath);
            if (string.IsNullOrEmpty(linkedProjectDirectory))
            {
                throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Project '{linked.Project.Name}' is not editable.");
            }

            var updatedProjectText = TryGetUpdatedProjectText(
                linked.Project.FilePath,
                linkedProjectDirectory,
                sourceFile,
                destFile);
            if (updatedProjectText == null)
                continue;

            if (new FileInfo(linked.Project.FilePath).IsReadOnly)
            {
                throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    $"Project '{linked.Project.Name}' is not editable.");
            }

            projectTexts[linked.Project.FilePath] = updatedProjectText;
        }

        return new FileLocationPlan
        {
            DocumentIds = linkedDocuments.Select(d => d.Id).ToList(),
            SourceFile = sourceFile,
            DestinationFile = destFile,
            ProjectTexts = projectTexts
        };
    }

    private void ApplyFileLocationUpdate(FileLocationPlan plan)
    {
        var destDirectory = Path.GetDirectoryName(plan.DestinationFile);
        if (string.IsNullOrEmpty(destDirectory))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidTargetPath,
                $"Destination file has no directory: {plan.DestinationFile}");
        }

        try
        {
            Directory.CreateDirectory(destDirectory);
            RenameFileToMatchTypeOperation.MoveSourceFile(plan.SourceFile, plan.DestinationFile);
        }
        catch (IOException ex)
        {
            throw new RefactoringException(
                ErrorCodes.FilesystemError,
                $"Failed to move file: {ex.Message}",
                ex);
        }

        foreach (var (projectPath, projectText) in plan.ProjectTexts)
        {
            try
            {
                File.WriteAllText(projectPath, projectText);
            }
            catch (IOException ex)
            {
                throw new RefactoringException(
                    ErrorCodes.FilesystemError,
                    $"Failed to update project file: {ex.Message}",
                    ex);
            }
        }

        var updated = _context.Solution;
        foreach (var documentId in plan.DocumentIds)
            updated = updated.WithDocumentFilePath(documentId, plan.DestinationFile);
        _context.UpdateSolution(updated);
    }

    /// <summary>
    /// True when <paramref name="sourceFile"/> and <paramref name="destFile"/>
    /// identify the same filesystem entry. Case-only path differences on a
    /// case-sensitive volume are a real move and must not be skipped.
    /// </summary>
    internal static bool IsUnchangedFileLocation(string sourceFile, string destFile) =>
        RenameFileToMatchTypeOperation.ReferToSameFile(sourceFile, destFile);

    /// <summary>
    /// True when an explicit Compile spec (literal, glob, or semicolon list)
    /// includes <paramref name="filePath"/>.
    /// </summary>
    internal static bool CompileSpecCoversFile(string spec, string projectDirectory, string filePath)
    {
        foreach (var part in spec.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
                continue;

            if (RenameNamespaceOperation.ContainsMsBuildGlob(trimmed))
            {
                if (GlobSpecCoversFile(trimmed, projectDirectory, filePath))
                    return true;
                continue;
            }

            if (RenameFileToMatchTypeOperation.ProjectItemRefersToFile(projectDirectory, trimmed, filePath))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Rewrites literal Compile items and supplements globs / semicolon lists
    /// so a file-only move stays in the project when default items are disabled.
    /// </summary>
    internal static string UpdateProjectTextForFileMove(
        string projectXml,
        string projectDirectory,
        string sourceFile,
        string destFile)
    {
        var afterLiterals = RenameNamespaceOperation.UpdateExplicitCompileItemsForMoves(
            projectXml,
            projectDirectory,
            [(sourceFile, destFile)]);
        return UpdateGlobAndListCompileItems(afterLiterals, projectDirectory, sourceFile, destFile);
    }

    private static string RemapCommittedPath(string path, FileLocationPlan plan)
    {
        var normalized = PathResolver.NormalizePath(path);
        if (IsSameDocumentPath(normalized, plan.SourceFile))
            return plan.DestinationFile;

        return path;
    }

    private static string? TryGetUpdatedProjectText(
        string? projectPath,
        string projectDirectory,
        string sourceFile,
        string destFile)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
            return null;

        var original = File.ReadAllText(projectPath);
        var updated = UpdateProjectTextForFileMove(original, projectDirectory, sourceFile, destFile);
        return string.Equals(original, updated, StringComparison.Ordinal) ? null : updated;
    }

    private static IReadOnlyList<Document> FindDocumentsAtPath(Solution solution, string filePath)
    {
        return solution.Projects
            .SelectMany(project => project.Documents)
            .Where(document =>
                !string.IsNullOrWhiteSpace(document.FilePath)
                && IsSameDocumentPath(PathResolver.NormalizePath(document.FilePath), filePath))
            .ToList();
    }

    private static bool IsSameDocumentPath(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal)
        || RenameFileToMatchTypeOperation.ReferToSameFile(left, right);

    private static string UpdateGlobAndListCompileItems(
        string projectXml,
        string projectDirectory,
        string sourceFile,
        string destFile)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(projectXml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (System.Xml.XmlException)
        {
            return projectXml;
        }

        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var changed = false;

        foreach (var compile in document.Descendants(ns + "Compile").ToList())
        {
            changed |= TryUpdateGlobOrListAttribute(compile, "Include", projectDirectory, sourceFile, destFile);
            changed |= TryUpdateGlobOrListAttribute(compile, "Update", projectDirectory, sourceFile, destFile);
        }

        if (!changed)
            return projectXml;

        return SerializeProjectXml(document, projectXml);
    }

    private static bool TryUpdateGlobOrListAttribute(
        XElement compile,
        string attributeName,
        string projectDirectory,
        string sourceFile,
        string destFile)
    {
        var attribute = compile.Attribute(attributeName);
        if (attribute == null)
            return false;

        var spec = attribute.Value;
        if (!spec.Contains(';') && !RenameNamespaceOperation.ContainsMsBuildGlob(spec))
            return false;

        var parts = spec.Split(';');
        var rewritten = false;
        for (var i = 0; i < parts.Length; i++)
        {
            var trimmed = parts[i].Trim();
            if (trimmed.Length == 0 || RenameNamespaceOperation.ContainsMsBuildGlob(trimmed))
                continue;
            if (!RenameFileToMatchTypeOperation.ProjectItemRefersToFile(projectDirectory, trimmed, sourceFile))
                continue;

            var replacement = RenameNamespaceOperation.RewriteProjectItemToTarget(
                trimmed,
                projectDirectory,
                destFile);
            if (string.Equals(parts[i], replacement, StringComparison.Ordinal))
                continue;

            parts[i] = replacement;
            rewritten = true;
        }

        var joined = string.Join(';', parts);
        if (!CompileSpecCoversFile(joined, projectDirectory, destFile)
            && CompileSpecCoversFile(joined, projectDirectory, sourceFile))
        {
            var destSpec = RenameNamespaceOperation.RewriteProjectItemToTarget(
                parts[0],
                projectDirectory,
                destFile);
            joined = joined + ";" + destSpec;
            rewritten = true;
        }

        if (!rewritten || string.Equals(attribute.Value, joined, StringComparison.Ordinal))
            return false;

        attribute.Value = joined;
        return true;
    }

    private static bool GlobSpecCoversFile(string spec, string projectDirectory, string filePath)
    {
        string candidate;
        string pattern;
        try
        {
            var specNative = spec.Trim()
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            pattern = spec.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(specNative))
            {
                candidate = PathResolver.NormalizePath(filePath).Replace('\\', '/');
                var specDir = RenameNamespaceOperation.GetGlobDirectoryPrefix(spec);
                if (!string.IsNullOrEmpty(specDir))
                {
                    var specDirNative = specDir
                        .Replace('\\', Path.DirectorySeparatorChar)
                        .Replace('/', Path.DirectorySeparatorChar);
                    var resolvedDir = PathResolver.NormalizePath(specDirNative).Replace('\\', '/');
                    var specDirNormalized = specDir.Replace('\\', '/');
                    if (pattern.StartsWith(specDirNormalized, StringComparison.OrdinalIgnoreCase))
                        pattern = resolvedDir + pattern[specDirNormalized.Length..];
                }
            }
            else
            {
                candidate = Path.GetRelativePath(
                        projectDirectory,
                        PathResolver.NormalizePath(filePath))
                    .Replace('\\', '/');
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var options = OperatingSystem.IsWindows()
            ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            : RegexOptions.CultureInvariant;
        return Regex.IsMatch(candidate, GlobToRegex(pattern), options);
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/')
                {
                    i++;
                    sb.Append("(?:.*/)?");
                }
                else
                {
                    sb.Append(".*");
                }

                continue;
            }

            sb.Append(glob[i] switch
            {
                '*' => "[^/]*",
                '?' => "[^/]",
                _ => Regex.Escape(glob[i].ToString())
            });
        }

        sb.Append('$');
        return sb.ToString();
    }

    private static string SerializeProjectXml(XDocument document, string originalXml)
    {
        var writerSettings = new System.Xml.XmlWriterSettings
        {
            OmitXmlDeclaration = !originalXml.Contains("<?xml", StringComparison.OrdinalIgnoreCase),
            NewLineHandling = System.Xml.NewLineHandling.Replace,
            NewLineChars = originalXml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
            Indent = false
        };

        using var writer = new StringWriter();
        using (var xmlWriter = System.Xml.XmlWriter.Create(writer, writerSettings))
        {
            document.Save(xmlWriter);
        }

        var serialized = writer.ToString();
        if (originalXml.EndsWith('\n') && !serialized.EndsWith('\n'))
            serialized += writerSettings.NewLineChars;
        return serialized;
    }

    private Contracts.Models.SymbolInfo CreateSymbolInfo(
        SymbolResolutionResult resolution,
        string newNamespace,
        string? newFilePath = null)
    {
        var oldNamespace = resolution.Symbol.ContainingNamespace.ToDisplayString();
        var location = resolution.Declaration.GetLocation().GetLineSpan();
        var previousFile = resolution.Document.FilePath!;
        var line = location.StartLinePosition.Line + 1;
        var column = location.StartLinePosition.Character + 1;

        return new Contracts.Models.SymbolInfo
        {
            Name = resolution.Symbol.Name,
            FullyQualifiedName = $"{newNamespace}.{resolution.Symbol.Name}",
            Kind = MapSymbolKind(resolution.Symbol),
            PreviousNamespace = oldNamespace,
            NewNamespace = newNamespace,
            PreviousLocation = new SymbolLocation
            {
                File = previousFile,
                Line = line,
                Column = column
            },
            NewLocation = new SymbolLocation
            {
                File = newFilePath ?? previousFile,
                Line = line,
                Column = column
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
        ChangeStats stats,
        FileLocationPlan? filePlan)
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

        if (filePlan != null)
        {
            pendingChanges.Add(new PendingChange
            {
                File = filePlan.SourceFile,
                ChangeType = ChangeKind.Delete,
                Description = $"Move file to match namespace '{@params.TargetNamespace}'"
            });
            pendingChanges.Add(new PendingChange
            {
                File = filePlan.DestinationFile,
                ChangeType = ChangeKind.Create,
                Description = $"Move '{filePlan.SourceFile}' to '{filePlan.DestinationFile}'"
            });

            foreach (var projectPath in filePlan.ProjectTexts.Keys)
            {
                pendingChanges.Add(new PendingChange
                {
                    File = projectPath,
                    ChangeType = ChangeKind.Modify,
                    Description = "Update explicit Compile item to the moved file"
                });
            }
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed class ChangeStats
    {
        public int UsingsAdded { get; set; }
        public int UsingsRemoved { get; set; }
    }

    private sealed class FileLocationPlan
    {
        public required IReadOnlyList<DocumentId> DocumentIds { get; init; }
        public required string SourceFile { get; init; }
        public required string DestinationFile { get; init; }
        public required IReadOnlyDictionary<string, string> ProjectTexts { get; init; }
    }
}
