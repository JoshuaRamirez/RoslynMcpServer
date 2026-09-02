using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring;

/// <summary>
/// Moves a type declaration to a different file.
/// Handles file creation, using directive updates, and reference preservation.
/// Honors optional <c>line</c> and <c>column</c> to disambiguate
/// same-named top-level types in one file. Omitted column keeps today's
/// symbolName + optional line pick (start-line equality /
/// <c>SymbolAmbiguous</c> / single-match ignores line). Column without
/// line keeps that omitted-line path. When column is set with line, picks
/// the covering top-level type (identifier preferred, then smallest
/// covering type). Nested types stay unmoveable.
/// Optional <c>allFiles</c> walks every C# document (or the optional
/// single <c>sourceFile</c>) and extracts every eligible top-level type
/// into <c>{directory}/{TypeName}.cs</c> (skip nested / already
/// well-placed / SameLocation / NameCollision / occupied destinations /
/// uneditable / resolution failures rather than throwing). Bulk walks
/// every eligible type, not a broader search for one symbolName.
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
            Validate(@params);

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
                CreateSymbolInfo(resolution, @params.TargetFile!),
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

    /// <summary>
    /// Validates move-type-to-file parameters. Internal so tests can
    /// exercise input rules without loading a workspace.
    /// </summary>
    internal static void Validate(MoveTypeToFileParams @params)
    {
        if (@params.AllFiles)
        {
            if (!string.IsNullOrWhiteSpace(@params.SymbolName) ||
                !string.IsNullOrWhiteSpace(@params.TargetFile) ||
                @params.Line.HasValue ||
                @params.Column.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with symbolName, targetFile, line, or column.");
            }

            return;
        }

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

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        // Check source != target
        if (PathResolver.NormalizePath(@params.SourceFile) == PathResolver.NormalizePath(@params.TargetFile!))
            throw new RefactoringException(ErrorCodes.SameLocation, "Source and target files are the same.");
    }

    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>FormatDocumentOperation.ExecuteAllFilesAsync</c>
    /// / <c>RenameFileToMatchTypeOperation.ExecuteAllFilesAsync</c> /
    /// <c>UseBaseTypeOperation.ExecuteAllFilesAsync</c> /
    /// <c>EncapsulateFieldOperation.ExecuteAllFilesAsync</c>) and extracts
    /// every eligible top-level <c>TypeDeclarationSyntax</c> into
    /// <c>{directory}/{TypeName}.cs</c>. Optional <c>sourceFile</c> limits
    /// the walk to that one file. Nested types, already well-placed
    /// single-type matching files, SameLocation, NameCollision / occupied
    /// destinations today's <see cref="ValidateTargetAsync"/> would reject,
    /// uneditable documents, and resolution failures are skipped. When two
    /// types would claim the same destination, the later claim is skipped.
    /// When every type is a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        MoveTypeToFileParams @params,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var originalSolution = _context.Solution;
        var allDocuments = originalSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
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

        var plans = new List<TypeMovePlan>();
        var claimedDestinations = new List<string>();
        var caseDistinctCache = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var document in allDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsDocumentEditable(document, _context.Workspace))
                continue;

            var sourceFile = document.FilePath;
            if (string.IsNullOrWhiteSpace(sourceFile))
                continue;

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (root == null)
                continue;

            var topLevel = CollectTopLevelTypes(root);
            foreach (var typeDecl in topLevel)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var typeName = typeDecl.Identifier.Text;
                if (string.IsNullOrWhiteSpace(typeName))
                    continue;

                if (IsAlreadyWellPlaced(sourceFile, typeName, topLevel.Count))
                    continue;

                var targetFile = GetDerivedTargetFile(sourceFile, typeName);
                if (IsSamePhysicalLocation(sourceFile, targetFile, caseDistinctCache))
                    continue;

                if (IsDestinationOccupiedOutsideWorkspace(sourceFile, targetFile))
                    continue;

                if (claimedDestinations.Any(claimed =>
                        RenameFileToMatchTypeOperation.DestinationsReferToSameLocation(
                            claimed, targetFile, caseDistinctCache)))
                {
                    continue;
                }

                claimedDestinations.Add(targetFile);
                plans.Add(new TypeMovePlan(
                    sourceFile,
                    typeName,
                    GetNamespaceName(typeDecl, semanticModel, cancellationToken),
                    targetFile));
            }
        }

        var pendingChanges = new List<PendingChange>();
        var anyMove = false;
        var totalReferences = 0;

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var resolution = await TryResolvePlannedTypeAsync(plan, cancellationToken);
                if (resolution == null)
                    continue;

                if (!IsDocumentEditable(resolution.Document, _context.Workspace))
                    continue;

                var moveParams = new MoveTypeToFileParams
                {
                    SourceFile = plan.SourceFile,
                    SymbolName = plan.TypeName,
                    TargetFile = plan.TargetFile,
                    CreateTargetFile = @params.CreateTargetFile,
                    Preview = @params.Preview
                };

                if (IsDestinationOccupiedOutsideWorkspace(plan.SourceFile, plan.TargetFile))
                    continue;

                await ValidateTargetAsync(moveParams, resolution, cancellationToken);

                var references = await _referenceTracker.FindAllReferencesAsync(
                    resolution.Symbol,
                    cancellationToken);

                var (newSolution, changeInfo) = await ComputeChangesAsync(
                    moveParams,
                    resolution,
                    references,
                    cancellationToken,
                    preserveNonTypeMembers: true);

                pendingChanges.AddRange(CreatePendingChanges(changeInfo, resolution, plan.TargetFile));
                totalReferences += references.TotalReferenceCount;
                _context.UpdateSolution(newSolution);
                anyMove = true;
            }
            catch (RefactoringException)
            {
                // Skip per-type failures that single-site would throw.
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

        stopwatch.Stop();
        return RefactoringResult.Succeeded(
            operationId,
            new FileChanges
            {
                FilesModified = commitResult.FilesModified,
                FilesCreated = commitResult.FilesCreated,
                FilesDeleted = commitResult.FilesDeleted
            },
            null,
            totalReferences,
            stopwatch.ElapsedMilliseconds);
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
    /// Builds the destination path as <c>{directory of source}/{TypeName}.cs</c>.
    /// </summary>
    internal static string GetDerivedTargetFile(string sourceFile, string typeName)
    {
        var directory = Path.GetDirectoryName(sourceFile) ?? string.Empty;
        var extension = Path.GetExtension(sourceFile);
        if (string.IsNullOrEmpty(extension))
            extension = ".cs";
        return Path.Combine(directory, typeName + extension);
    }

    /// <summary>
    /// True when the type name already matches the current file stem and
    /// the file has exactly one top-level type (already well-placed — same
    /// spirit as <c>rename_file_to_match_type</c> allFiles no-op).
    /// </summary>
    internal static bool IsAlreadyWellPlaced(string sourceFile, string typeName, int topLevelTypeCount) =>
        topLevelTypeCount == 1 &&
        string.Equals(Path.GetFileNameWithoutExtension(sourceFile), typeName, StringComparison.Ordinal);

    /// <summary>
    /// Preview description for moving <paramref name="typeName"/> into
    /// <paramref name="targetFile"/>.
    /// </summary>
    internal static string BuildAllFilesDescription(string typeName, string targetFile) =>
        $"Move {typeName} to {Path.GetFileName(targetFile)}";

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

    /// <summary>
    /// True when <paramref name="sourceFile"/> and <paramref name="targetFile"/>
    /// refer to the same physical location, including case-only differences
    /// on a case-insensitive volume (same filesystem semantics as
    /// <c>RenameFileToMatchTypeOperation.DestinationsReferToSameLocation</c>).
    /// </summary>
    internal static bool IsSamePhysicalLocation(
        string sourceFile,
        string targetFile,
        IDictionary<string, bool>? caseDistinctCache = null) =>
        RenameFileToMatchTypeOperation.DestinationsReferToSameLocation(
            sourceFile, targetFile, caseDistinctCache);

    /// <summary>
    /// True when the derived destination exists on disk, is not the source
    /// file, and is not a workspace document. Bulk skips rather than
    /// creating a second document that would overwrite the file.
    /// </summary>
    internal bool IsDestinationOccupiedOutsideWorkspace(string sourceFile, string targetFile)
    {
        if (_context.GetDocumentByPath(targetFile) != null)
            return false;

        return RenameFileToMatchTypeOperation.IsDestinationOccupiedByDifferentFile(sourceFile, targetFile);
    }

    /// <summary>
    /// Full namespace of a top-level type, including nested namespace
    /// declarations (<c>namespace A { namespace B { class C } }</c> is
    /// <c>A.B</c>, not just the nearest <c>B</c>). Prefers the semantic
    /// containing namespace when a model is available.
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
    /// True when the source still has a type, enum, delegate, or global
    /// statement after the move. Bulk uses this so leftover excluded
    /// declarations are not deleted with the last eligible type.
    /// </summary>
    internal static bool HasRemainingSourceContent(SyntaxNode? root, bool preserveNonTypeMembers)
    {
        if (root == null)
            return false;

        if (root.DescendantNodes().OfType<TypeDeclarationSyntax>().Any())
            return true;

        if (!preserveNonTypeMembers)
            return false;

        return root.DescendantNodes().Any(node =>
            node is EnumDeclarationSyntax or DelegateDeclarationSyntax or GlobalStatementSyntax);
    }

    private async Task<SymbolResolutionResult?> TryResolvePlannedTypeAsync(
        TypeMovePlan plan,
        CancellationToken cancellationToken)
    {
        var document = _context.GetDocumentByPath(plan.SourceFile);
        if (document == null)
            return null;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            return null;

        var match = CollectTopLevelTypes(root).FirstOrDefault(t =>
            t.Identifier.Text == plan.TypeName &&
            GetNamespaceName(t, semanticModel, cancellationToken) == plan.Namespace);
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

    private static List<PendingChange> CreatePendingChanges(
        ChangeInfo changeInfo,
        SymbolResolutionResult resolution,
        string targetFile) =>
    [
        new()
        {
            File = resolution.Document.FilePath!,
            ChangeType = changeInfo.SourceFileEmptied ? ChangeKind.Delete : ChangeKind.Modify,
            Description = changeInfo.SourceFileEmptied
                ? $"Delete file (emptied after removing {resolution.Symbol.Name})"
                : $"Remove {resolution.Symbol.Name} declaration"
        },
        new()
        {
            File = targetFile,
            ChangeType = changeInfo.TargetFileCreated ? ChangeKind.Create : ChangeKind.Modify,
            Description = changeInfo.TargetFileCreated
                ? $"Create file with {resolution.Symbol.Name}"
                : $"Add {resolution.Symbol.Name} declaration"
        }
    ];

    private sealed record TypeMovePlan(
        string SourceFile,
        string TypeName,
        string Namespace,
        string TargetFile);

    private async Task ValidateTargetAsync(
        MoveTypeToFileParams @params,
        SymbolResolutionResult resolution,
        CancellationToken cancellationToken)
    {
        var targetDoc = _context.GetDocumentByPath(@params.TargetFile!);

        if (targetDoc == null && !@params.CreateTargetFile)
        {
            throw new RefactoringException(
                ErrorCodes.SourceFileNotFound,
                $"Target file does not exist: {@params.TargetFile!}. Set createTargetFile=true to create it.");
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
        CancellationToken cancellationToken,
        bool preserveNonTypeMembers = false)
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
        var targetDoc = _context.GetDocumentByPath(@params.TargetFile!);

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
                Path.GetFileNameWithoutExtension(@params.TargetFile!),
                targetContent,
                filePath: @params.TargetFile);
            solution = newDoc.Project.Solution;
        }

        // Remove type from source file
        var newSourceRoot = sourceRoot.RemoveNode(typeNode, SyntaxRemoveOptions.KeepNoTrivia);

        // If source file is now empty (no types), we'll delete it.
        // Bulk also keeps leftover enums / delegates / global statements.
        var remainingContent = HasRemainingSourceContent(newSourceRoot, preserveNonTypeMembers);

        if (!remainingContent && newSourceRoot != null)
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
            SourceFileEmptied = !remainingContent,
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
