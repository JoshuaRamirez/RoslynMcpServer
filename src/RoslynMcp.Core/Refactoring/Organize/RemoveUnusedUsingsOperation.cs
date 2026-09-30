using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Organize.Utilities;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Organize;

/// <summary>
/// Removes unused using directives from a file.
/// </summary>
public sealed class RemoveUnusedUsingsOperation : RefactoringOperationBase<RemoveUnusedUsingsParams>
{
    /// <summary>
    /// Creates a new remove unused usings operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public RemoveUnusedUsingsOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(RemoveUnusedUsingsParams @params)
    {
        if (@params.AllFiles)
        {
            // When processing all files, sourceFile is optional; validate path
            // shape only (filter throws SourceFileNotFound / SourceNotInWorkspace).
            if (!string.IsNullOrWhiteSpace(@params.SourceFile))
                SourceFilePathHelpers.ValidateSourceFilePath(@params.SourceFile!);

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required when allFiles is false.");

        SourceFilePathHelpers.ValidateSourceFilePath(@params.SourceFile);

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        RemoveUnusedUsingsParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
        {
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);
        }

        return await ExecuteSingleFileAsync(operationId, @params.SourceFile!, @params.Preview, cancellationToken);
    }

    /// <summary>
    /// Processes a single file to remove unused using directives.
    /// </summary>
    private async Task<RefactoringResult> ExecuteSingleFileAsync(
        Guid operationId,
        string sourceFile,
        bool preview,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(sourceFile);
        var rewrite = await TryBuildUnusedUsingsRewriteAsync(document, cancellationToken);
        if (rewrite is null)
        {
            return RefactoringResult.Succeeded(
                operationId,
                new FileChanges
                {
                    FilesModified = [],
                    FilesCreated = [],
                    FilesDeleted = []
                },
                null,
                0,
                0);
        }

        var (root, newRoot, unusedUsings) = rewrite.Value;

        // If preview mode, return without applying (but include before/after snippets)
        if (preview)
        {
            return CreatePreviewResult(operationId, sourceFile, unusedUsings, root);
        }

        var newDocument = document.WithSyntaxRoot(newRoot);
        var newSolution = newDocument.Project.Solution;

        // Commit changes
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

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
            UsingDirectivesRemoved = unusedUsings.Count
        };
    }

    /// <summary>
    /// Processes all C# documents in the solution to remove unused using directives.
    /// Optional <c>sourceFile</c> limits the walk via
    /// <see cref="AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile"/>
    /// (default path; same as FormatDocument / ConvertToBlockBody / AddMissingUsings).
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        RemoveUnusedUsingsParams @params,
        CancellationToken cancellationToken)
    {
        // Accumulate on a local solution snapshot (same as SortUsings / FormatDocument /
        // AddMissingUsings) so CommitChangesAsync can diff against the original Context.Solution.
        var currentSolution = Context.Solution;
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(currentSolution);

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
        {
            allDocuments = AllFilesDocumentHelpers.FilterAllFilesDocumentsBySourceFile(allDocuments, @params.SourceFile!);
        }

        // One physical path may appear as multiple Documents when linked into
        // several projects. Rewrite once per normalized path and apply the same
        // text to every sibling DocumentId (ConvertToBlockBody / AddMissingUsings / Copilot).
        var documentGroups = AllFilesDocumentHelpers.GroupByLinkedPath(allDocuments);

        var totalUsingsRemoved = 0;
        var anyChanged = false;
        var allPendingChanges = new List<PendingChange>();

        foreach (var linkedDocuments in documentGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SourceText? changedText = null;
            List<UsingDirectiveSyntax>? chosenUnused = null;
            CompilationUnitSyntax? previewRoot = null;
            Document? previewDocument = null;
            // Track no-op editable siblings so rewrite/no-op mixed groups reject
            // regardless of Document walk order (Codex P2 on linked global usings).
            var sawNoOpEditable = false;

            foreach (var linked in linkedDocuments)
            {
                var currentDocument = currentSolution.GetDocument(linked.Id) ?? linked;
                if (currentDocument is SourceGeneratedDocument)
                    continue;
                if (!DocumentEditableHelpers.IsDocumentEditable(currentDocument, Context.Workspace))
                    continue;

                var rewrite = await TryBuildUnusedUsingsRewriteAsync(currentDocument, cancellationToken);
                if (rewrite is null)
                {
                    sawNoOpEditable = true;
                    if (changedText != null)
                    {
                        throw new RefactoringException(
                            ErrorCodes.CannotConvert,
                            $"Linked workspace documents for '{currentDocument.FilePath}' produce different rewrites under current project contexts.");
                    }

                    continue;
                }

                if (sawNoOpEditable)
                {
                    throw new RefactoringException(
                        ErrorCodes.CannotConvert,
                        $"Linked workspace documents for '{currentDocument.FilePath}' produce different rewrites under current project contexts.");
                }

                var (root, newRoot, unusedUsings) = rewrite.Value;
                var newDocument = currentDocument.WithSyntaxRoot(newRoot);
                var afterText = await newDocument.GetTextAsync(cancellationToken);

                if (changedText != null && !changedText.ContentEquals(afterText))
                {
                    throw new RefactoringException(
                        ErrorCodes.CannotConvert,
                        $"Linked workspace documents for '{currentDocument.FilePath}' produce different rewrites under current project contexts.");
                }

                changedText ??= afterText;
                chosenUnused ??= unusedUsings;
                previewRoot ??= root;
                previewDocument ??= currentDocument;
            }

            if (changedText == null || chosenUnused == null || previewRoot == null || previewDocument == null)
                continue;

            if (@params.Preview)
            {
                var previewResult = CreatePreviewResult(operationId, previewDocument.FilePath!, chosenUnused, previewRoot);
                if (previewResult.PendingChanges != null)
                    allPendingChanges.AddRange(previewResult.PendingChanges);
                totalUsingsRemoved += chosenUnused.Count;
                continue;
            }

            foreach (var linked in linkedDocuments)
            {
                var sibling = currentSolution.GetDocument(linked.Id) ?? linked;
                if (sibling is SourceGeneratedDocument)
                    continue;
                if (!DocumentEditableHelpers.IsDocumentEditable(sibling, Context.Workspace))
                    continue;
                currentSolution = currentSolution.WithDocumentText(sibling.Id, changedText);
            }

            totalUsingsRemoved += chosenUnused.Count;
            anyChanged = true;
        }

        // If preview mode, return aggregated preview
        if (@params.Preview)
        {
            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges = allPendingChanges,
                UsingDirectivesRemoved = totalUsingsRemoved
            };
        }

        // Commit all accumulated changes at once
        if (anyChanged)
        {
            var commitResult = await CommitChangesAsync(currentSolution, cancellationToken);
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
                UsingDirectivesRemoved = totalUsingsRemoved
            };
        }

        // No files needed changes
        return RefactoringResult.Succeeded(
            operationId,
            new FileChanges
            {
                FilesModified = [],
                FilesCreated = [],
                FilesDeleted = []
            },
            null,
            0,
            0);
    }

    /// <summary>
    /// Builds an unused-usings rewrite for one document, or null when no usings
    /// need to be removed.
    /// </summary>
    private static async Task<(CompilationUnitSyntax Root, CompilationUnitSyntax NewRoot, List<UsingDirectiveSyntax> UnusedUsings)?> TryBuildUnusedUsingsRewriteAsync(
        Document document,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            return null;

        // Find unused usings via diagnostics using defined diagnostic IDs
        var unusedUsingDiagnostics = semanticModel.GetDiagnostics(cancellationToken: cancellationToken)
            .Where(d => d.Id == DiagnosticIds.UnnecessaryUsing ||
                        d.Id == DiagnosticIds.UnnecessaryUsingIde)
            .ToList();

        // Also do semantic analysis for unused usings
        var usedNamespaces = GetUsedNamespaces(root, semanticModel, cancellationToken);
        var unusedUsings = new List<UsingDirectiveSyntax>();

        foreach (var usingDirective in root.Usings)
        {
            var namespaceName = usingDirective.Name?.ToString();
            if (namespaceName == null) continue;

            // Check if this using is in the diagnostics
            var isUnused = unusedUsingDiagnostics.Any(d =>
                d.Location.SourceSpan.IntersectsWith(usingDirective.Span));

            // Or check if it's not in our used namespaces
            if (!isUnused && !usedNamespaces.Contains(namespaceName))
            {
                // Do a more thorough check - see if any type from this namespace is used
                var nsSymbol = semanticModel.Compilation.GlobalNamespace
                    .GetNamespaceMembers()
                    .FirstOrDefault(n => n.ToDisplayString() == namespaceName);

                if (nsSymbol == null)
                {
                    // Namespace not found in compilation, might be unused
                    isUnused = true;
                }
            }

            if (isUnused)
            {
                unusedUsings.Add(usingDirective);
            }
        }

        // Also collect from diagnostics directly — only compilation-unit usings.
        // Namespace-scoped usings (block / file-scoped) can appear in diagnostics but
        // are not rewritten by WithUsings(root.Usings); counting them would yield a
        // non-null rewrite with unchanged text (Copilot on #1727).
        foreach (var diagnostic in unusedUsingDiagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan);
            if (node is UsingDirectiveSyntax usingNode &&
                usingNode.Parent is CompilationUnitSyntax &&
                !unusedUsings.Contains(usingNode))
            {
                unusedUsings.Add(usingNode);
            }
        }

        if (unusedUsings.Count == 0)
            return null;

        // Remove unused usings and re-sort the remaining ones
        var remainingUsings = root.Usings
            .Where(u => !unusedUsings.Contains(u))
            .ToList();

        var sortedUsings = UsingDirectiveSorter.Sort(remainingUsings);
        var newRoot = root.WithUsings(SyntaxFactory.List(sortedUsings));
        return (root, newRoot, unusedUsings);
    }

    /// <summary>
    /// Collects all namespaces that are actually used in the file.
    /// </summary>
    /// <param name="root">Compilation unit to analyze.</param>
    /// <param name="semanticModel">Semantic model for symbol resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Set of namespace names that are used.</returns>
    /// <remarks>
    /// Properly detects extension method namespaces by analyzing InvocationExpressions
    /// using GetSymbolInfo, not just IdentifierNameSyntax nodes.
    /// </remarks>
    private static HashSet<string> GetUsedNamespaces(
        CompilationUnitSyntax root,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var usedNamespaces = new HashSet<string>();

        // Walk all identifier names and get their containing namespaces
        foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbolInfo = semanticModel.GetSymbolInfo(identifier, cancellationToken);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

            if (symbol != null)
            {
                AddNamespaceFromSymbol(usedNamespaces, symbol);
            }
        }

        // Check invocation expressions specifically to detect extension method calls
        // Extension methods appear as method calls on an instance but resolve to static methods
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

            if (symbol is IMethodSymbol method)
            {
                // Add the method's containing namespace
                AddNamespaceFromSymbol(usedNamespaces, method);

                // For extension methods, also add the namespace of the static class containing the method
                if (method.IsExtensionMethod)
                {
                    var containingType = method.ContainingType;
                    if (containingType?.ContainingNamespace != null &&
                        !containingType.ContainingNamespace.IsGlobalNamespace)
                    {
                        usedNamespaces.Add(containingType.ContainingNamespace.ToDisplayString());
                    }

                    // Also check for reduced extension method (the original definition)
                    var reducedFrom = method.ReducedFrom;
                    if (reducedFrom?.ContainingType?.ContainingNamespace != null &&
                        !reducedFrom.ContainingType.ContainingNamespace.IsGlobalNamespace)
                    {
                        usedNamespaces.Add(reducedFrom.ContainingType.ContainingNamespace.ToDisplayString());
                    }
                }
            }
        }

        // Also check type syntax
        foreach (var typeSyntax in root.DescendantNodes().OfType<TypeSyntax>())
        {
            var typeInfo = semanticModel.GetTypeInfo(typeSyntax, cancellationToken);
            if (typeInfo.Type?.ContainingNamespace != null &&
                !typeInfo.Type.ContainingNamespace.IsGlobalNamespace)
            {
                usedNamespaces.Add(typeInfo.Type.ContainingNamespace.ToDisplayString());
            }
        }

        return usedNamespaces;
    }

    /// <summary>
    /// Adds the containing namespace of a symbol to the used namespaces set.
    /// </summary>
    private static void AddNamespaceFromSymbol(HashSet<string> usedNamespaces, ISymbol symbol)
    {
        var containingNamespace = symbol.ContainingNamespace;
        if (containingNamespace != null && !containingNamespace.IsGlobalNamespace)
        {
            usedNamespaces.Add(containingNamespace.ToDisplayString());
        }

        // For extension methods on IMethodSymbol, add containing type's namespace
        if (symbol is IMethodSymbol method && method.IsExtensionMethod)
        {
            var containingType = method.ContainingType;
            if (containingType?.ContainingNamespace != null &&
                !containingType.ContainingNamespace.IsGlobalNamespace)
            {
                usedNamespaces.Add(containingType.ContainingNamespace.ToDisplayString());
            }
        }
    }

    /// <summary>
    /// Creates a preview result with before/after using directive snippets.
    /// </summary>
    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        string filePath,
        List<UsingDirectiveSyntax> unusedUsings,
        CompilationUnitSyntax root)
    {
        var namespaces = unusedUsings.Select(u => u.Name?.ToString() ?? "unknown").ToList();

        // Build the "before" snippet showing all usings
        var allUsings = root.Usings.Select(u => u.ToString().Trim()).ToList();
        var beforeSnippet = string.Join(Environment.NewLine, allUsings);

        // Build the "after" snippet showing usings without the unused ones
        var unusedSet = new HashSet<UsingDirectiveSyntax>(unusedUsings);
        var remainingUsings = root.Usings
            .Where(u => !unusedSet.Contains(u))
            .Select(u => u.ToString().Trim())
            .ToList();
        var afterSnippet = remainingUsings.Count > 0
            ? string.Join(Environment.NewLine, remainingUsings)
            : "// All using directives removed";

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = filePath,
                ChangeType = ChangeKind.Modify,
                Description = $"Remove {unusedUsings.Count} unused using directive(s): {string.Join(", ", namespaces)}",
                StartLine = 1,
                BeforeSnippet = beforeSnippet,
                AfterSnippet = afterSnippet
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }
}
