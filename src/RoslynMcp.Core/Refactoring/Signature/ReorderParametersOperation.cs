using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Signature;

/// <summary>
/// Reorders a method's parameters by a 0-based permutation and updates
/// call sites, overrides, and interface implementations.
/// Optional <c>allFiles</c> walks every C# document (or the optional single
/// <c>sourceFile</c>) and applies the same <c>newOrder</c> to every eligible method,
/// skipping ineligible methods rather than throwing.
/// </summary>
public sealed class ReorderParametersOperation : RefactoringOperationBase<ReorderParametersParams>
{
    /// <summary>
    /// Creates a new reorder parameters operation.
    /// </summary>
    public ReorderParametersOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ReorderParametersParams @params) => Validate(@params);

    /// <summary>
    /// Validates reorder-parameter inputs. Internal so tests can exercise rules
    /// without loading a workspace.
    /// </summary>
    internal static void Validate(ReorderParametersParams @params)
    {
        if (@params.NewOrder is null || @params.NewOrder.Length == 0)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "newOrder is required.");

        if (@params.NewOrder.Length < 2 || !IsPermutation(@params.NewOrder, @params.NewOrder.Length))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidParameterPosition,
                "newOrder must be a permutation of 0..n-1 for a method with at least two parameters.");
        }

        if (@params.AllFiles)
        {
            if (!string.IsNullOrWhiteSpace(@params.MethodName) ||
                @params.Line.HasValue ||
                @params.Column.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with methodName, line, or column.");
            }

            // Optional sourceFile still must be an absolute .cs path when set
            // (RemoveParameter / AddParameter allFiles / Copilot).
            if (!string.IsNullOrWhiteSpace(@params.SourceFile))
                ValidateSourceFilePath(@params.SourceFile!);

            return;
        }

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        ValidateSourceFilePath(@params.SourceFile!);

        if (!File.Exists(@params.SourceFile!))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");
    }

    private static void ValidateSourceFilePath(string sourceFile)
    {
        if (!PathResolver.IsAbsolutePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ReorderParametersParams @params,
        CancellationToken cancellationToken)
    {
        if (@params.AllFiles)
            return await ExecuteAllFilesAsync(operationId, @params, cancellationToken);

        var sourceFile = @params.SourceFile!;
        var methodName = @params.MethodName!;

        var document = GetDocumentOrThrow(sourceFile);
        DocumentEditableHelpers.ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var methodDecl = FindMethodHelpers.FindMethodDeclaration(root, methodName, @params.Line, @params.Column);
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");

        var newOrder = ValidateNewOrder(methodSymbol.Parameters.Length, @params.NewOrder);
        ValidateResultingSignature(methodDecl.ParameterList, methodSymbol, newOrder);

        var solution = document.Project.Solution;

        var relatedMethods = await GetRelatedMethodsAsync(
            methodSymbol,
            @params.UpdateOverrides,
            @params.UpdateImplementations,
            solution,
            cancellationToken);

        await ValidateRelatedSignaturesAsync(relatedMethods, newOrder, cancellationToken);

        var declarationTargets = await CollectDeclarationTargetsAsync(relatedMethods, solution, cancellationToken);
        foreach (var target in declarationTargets)
            DocumentEditableHelpers.ValidateDocumentIsEditable(target.Document, Context.Workspace);

        var fallbackNames = methodSymbol.Parameters.Select(p => p.Name).ToArray();
        var callSites = await CollectCallSitesAsync(relatedMethods, fallbackNames, solution, cancellationToken);
        foreach (var callSite in callSites)
            DocumentEditableHelpers.ValidateDocumentIsEditable(callSite.Document, Context.Workspace);

        var newSolution = await ApplyChangesAsync(
            document,
            declarationTargets,
            callSites,
            fallbackNames,
            newOrder,
            cancellationToken);

        if (@params.Preview)
        {
            return await CreatePreviewResultAsync(
                operationId,
                @params,
                document,
                newSolution,
                callSites.Count,
                cancellationToken);
        }

        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return RefactoringResult.Succeeded(
            operationId,
            new FileChanges
            {
                FilesModified = commitResult.FilesModified,
                FilesCreated = commitResult.FilesCreated,
                FilesDeleted = commitResult.FilesDeleted
            },
            new Contracts.Models.SymbolInfo
            {
                Name = methodName,
                FullyQualifiedName = methodSymbol.ToDisplayString(),
                Kind = Contracts.Enums.SymbolKind.Method
            },
            callSites.Count,
            0);
    }


    /// <summary>
    /// Walks every C# document (<c>FilePath</c> ends with <c>.cs</c>; same
    /// document filter as <c>RemoveParameterOperation.ExecuteAllFilesAsync</c>)
    /// and applies <paramref name="params"/>.NewOrder to every eligible
    /// <see cref="MethodDeclarationSyntax"/>. Optional <c>sourceFile</c> limits
    /// via <see cref="DocumentSourceFileFilter"/>. Linked multi-project views of
    /// the same path are skipped rather than coalescing (same contract as
    /// <c>SafeDeleteOperation.ExecuteAllFilesAsync</c> / remove_parameter allFiles);
    /// a candidate is also skipped when any related declaration or call site
    /// lives on a multi-view path. Methods whose arity does not match
    /// <c>newOrder</c>, fail existing single-site validation, uneditable /
    /// source-generated docs, and otherwise inapplicable methods are skipped
    /// rather than failing the walk. Deterministic <c>SpanStart</c> order within
    /// a file. The document-group walk repeats until a full pass makes no
    /// progress so cross-file call-site rewrites can unlock previously skipped
    /// methods (Codex / remove_parameter). When every file is a no-op, succeeds
    /// with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        ReorderParametersParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);
        var linkedPathCounts = BuildLinkedPathCounts(originalSolution);

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            allDocuments = DocumentSourceFileFilter.FilterDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        var documentGroups = AllFilesDocumentHelpers.GroupByLinkedPath(allDocuments);

        var changedCountByDoc = new Dictionary<DocumentId, int>();

        // Repeat until a full document-group pass makes no progress so that
        // call-site rewrites in later files can unlock earlier methods that were
        // skipped (Codex / remove_parameter peer).
        bool madeProgress;
        do
        {
            madeProgress = false;

            foreach (var linkedDocuments in documentGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (linkedDocuments.Count > 1)
                    continue;

                var primary = linkedDocuments.FirstOrDefault(d =>
                    d is not SourceGeneratedDocument &&
                    DocumentEditableHelpers.IsDocumentEditable(d, Context.Workspace));
                if (primary == null)
                    continue;

                while (true)
                {
                    var currentDocument = currentSolution.GetDocument(primary.Id);
                    if (currentDocument == null ||
                        currentDocument is SourceGeneratedDocument ||
                        !DocumentEditableHelpers.IsDocumentEditable(currentDocument, Context.Workspace))
                    {
                        break;
                    }

                    var root = await currentDocument.GetSyntaxRootAsync(cancellationToken);
                    var semanticModel = await currentDocument.GetSemanticModelAsync(cancellationToken);
                    if (root == null || semanticModel == null)
                        break;

                    Solution? updated = null;
                    foreach (var methodDecl in CollectMethods(root))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            updated = await TryReorderOneAsync(
                                currentDocument,
                                semanticModel,
                                methodDecl,
                                @params,
                                linkedPathCounts,
                                cancellationToken);
                        }
                        catch (RefactoringException)
                        {
                            updated = null;
                        }

                        if (updated != null)
                            break;
                    }

                    if (updated == null)
                        break;

                    var beforeSolution = currentSolution;
                    currentSolution = await AllFilesDocumentHelpers.CoalesceLinkedDocumentTextAsync(
                        beforeSolution,
                        updated,
                        Context.Workspace,
                        cancellationToken);

                    changedCountByDoc[primary.Id] =
                        changedCountByDoc.GetValueOrDefault(primary.Id) + 1;
                    madeProgress = true;
                }
            }
        } while (madeProgress);

        var documentsToCompare = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);

        var allPendingChanges = new List<PendingChange>();
        var anyChanged = false;
        var previewedPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in documentsToCompare)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var originalDocument = originalSolution.GetDocument(document.Id);
            var currentDocument = currentSolution.GetDocument(document.Id);
            if (originalDocument == null || currentDocument == null)
                continue;

            var beforeText = await originalDocument.GetTextAsync(cancellationToken);
            var afterText = await currentDocument.GetTextAsync(cancellationToken);
            if (beforeText.ContentEquals(afterText))
                continue;

            if (@params.Preview)
            {
                var pathKey = PathResolver.GetPathComparisonKey(originalDocument.FilePath!);
                if (!previewedPaths.Add(pathKey))
                    continue;

                var originalRoot = await originalDocument.GetSyntaxRootAsync(cancellationToken);
                var currentRoot = await currentDocument.GetSyntaxRootAsync(cancellationToken);
                if (originalRoot == null || currentRoot == null)
                    continue;

                var span = originalRoot.GetLocation().GetLineSpan();
                var changedCount = changedCountByDoc.GetValueOrDefault(document.Id);
                if (changedCount == 0)
                {
                    foreach (var linkedId in documentsToCompare
                        .Where(d => d.FilePath != null &&
                                    PathResolver.GetPathComparisonKey(d.FilePath!) == pathKey)
                        .Select(d => d.Id))
                    {
                        changedCount = Math.Max(changedCount, changedCountByDoc.GetValueOrDefault(linkedId));
                    }
                }

                allPendingChanges.Add(new PendingChange
                {
                    File = originalDocument.FilePath!,
                    ChangeType = ChangeKind.Modify,
                    Description = changedCount > 0
                        ? BuildAllFilesDescription(changedCount)
                        : "Update related declarations and call sites",
                    BeforeSnippet = originalRoot.NormalizeWhitespace().ToFullString().Trim(),
                    AfterSnippet = currentRoot.NormalizeWhitespace().ToFullString().Trim(),
                    StartLine = span.StartLinePosition.Line + 1,
                    EndLine = span.EndLinePosition.Line + 1
                });
                continue;
            }

            anyChanged = true;
        }

        if (@params.Preview)
            return RefactoringResult.PreviewResult(operationId, allPendingChanges);

        if (anyChanged)
        {
            var commitResult = await CommitChangesAsync(currentSolution, cancellationToken);
            return RefactoringResult.Succeeded(operationId,
                new FileChanges
                {
                    FilesModified = commitResult.FilesModified,
                    FilesCreated = commitResult.FilesCreated,
                    FilesDeleted = commitResult.FilesDeleted
                },
                null, 0, 0);
        }

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = [], FilesCreated = [], FilesDeleted = [] },
            null, 0, 0);
    }

    /// <summary>
    /// Preview description for a file that reordered
    /// <paramref name="changedCount"/> methods.
    /// </summary>
    internal static string BuildAllFilesDescription(int changedCount) =>
        changedCount == 1
            ? "Reorder parameters"
            : $"Reorder parameters on {changedCount} methods";

    /// <summary>
    /// Collects every <see cref="MethodDeclarationSyntax"/> in
    /// <paramref name="root"/> in deterministic <c>SpanStart</c> then
    /// span-length order.
    /// </summary>
    internal static IReadOnlyList<MethodDeclarationSyntax> CollectMethods(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .OrderBy(m => m.SpanStart)
            .ThenBy(m => m.Span.Length)
            .ToList();

    /// <summary>
    /// Bulk eligibility: skip overrides / interface declarations /
    /// interface implementations / partial pairs / <c>extern</c> /
    /// <c>UnmanagedCallersOnly</c> / <c>ModuleInitializer</c> / extension
    /// methods so allFiles cannot rewrite a signature whose metadata or
    /// sibling contract cannot be updated (RemoveParameter / AddParameter / Codex).
    /// </summary>
    internal static bool IsEligibleForAllFiles(IMethodSymbol method, MethodDeclarationSyntax methodDecl)
    {
        if (method.IsExtensionMethod)
            return false;

        if (method.PartialDefinitionPart != null || method.PartialImplementationPart != null)
            return false;

        if (method.IsExtern)
            return false;

        if (HasUnmanagedCallersOnlyAttribute(method, methodDecl))
            return false;

        if (HasModuleInitializerAttribute(method, methodDecl))
            return false;

        if (method.IsOverride)
            return false;

        if (method.ContainingType?.TypeKind == TypeKind.Interface)
            return false;

        if (!method.ExplicitInterfaceImplementations.IsDefaultOrEmpty &&
            method.ExplicitInterfaceImplementations.Length > 0)
        {
            return false;
        }

        if (ImplementsAnyInterfaceMember(method))
            return false;

        return true;
    }

    /// <summary>
    /// True when <paramref name="method"/> is the implementation of any
    /// interface member on its containing type.
    /// </summary>
    internal static bool ImplementsAnyInterfaceMember(IMethodSymbol method)
    {
        var containingType = method.ContainingType;
        if (containingType == null)
            return false;

        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
            {
                var impl = containingType.FindImplementationForInterfaceMember(member) as IMethodSymbol;
                if (impl != null && SymbolEqualityComparer.Default.Equals(impl, method))
                    return true;
            }
        }

        return false;
    }

    private static bool HasModuleInitializerAttribute(IMethodSymbol method, MethodDeclarationSyntax methodDecl)
    {
        if (method.GetAttributes().Any(attr =>
        {
            var type = attr.AttributeClass;
            if (type == null)
                return false;
            if (type.Name is not ("ModuleInitializerAttribute" or "ModuleInitializer"))
                return false;
            return type.ContainingNamespace?.ToDisplayString() == "System.Runtime.CompilerServices";
        }))
        {
            return true;
        }

        return methodDecl.AttributeLists
            .SelectMany(list => list.Attributes)
            .Any(attr => attr.Name.ToString().Contains("ModuleInitializer", StringComparison.Ordinal));
    }

    private static bool HasUnmanagedCallersOnlyAttribute(IMethodSymbol method, MethodDeclarationSyntax methodDecl)
    {
        if (method.GetAttributes().Any(attr =>
        {
            var type = attr.AttributeClass;
            if (type == null)
                return false;
            if (type.Name is not ("UnmanagedCallersOnlyAttribute" or "UnmanagedCallersOnly"))
                return false;
            return type.ContainingNamespace?.ToDisplayString() == "System.Runtime.InteropServices";
        }))
        {
            return true;
        }

        return methodDecl.AttributeLists
            .SelectMany(list => list.Attributes)
            .Any(attr => attr.Name.ToString().Contains("UnmanagedCallersOnly", StringComparison.Ordinal));
    }

    /// <summary>
    /// Path → linked-view count across the entire solution (not a filtered
    /// <c>sourceFile</c> subset). Same helper as
    /// <c>RemoveParameterOperation.BuildLinkedPathCounts</c>.
    /// </summary>
    internal static Dictionary<string, int> BuildLinkedPathCounts(Solution solution)
    {
        var groups = AllFilesDocumentHelpers.GroupByLinkedPath(
            AllFilesDocumentHelpers.EnumerateCsharpDocuments(solution));
        return groups
            .Where(g => g.Count > 0 && g[0].FilePath != null)
            .GroupBy(g => PathResolver.GetPathComparisonKey(g[0].FilePath!), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Count, StringComparer.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="document"/> shares a physical path with
    /// multiple linked workspace views.
    /// </summary>
    internal static bool DocumentPathHasLinkedMultiView(
        Document document,
        IReadOnlyDictionary<string, int> linkedPathCounts)
    {
        if (document.FilePath == null)
            return false;

        var pathKey = PathResolver.GetPathComparisonKey(document.FilePath);
        return linkedPathCounts.TryGetValue(pathKey, out var count) && count > 1;
    }

    private async Task<Solution?> TryReorderOneAsync(
        Document document,
        SemanticModel semanticModel,
        MethodDeclarationSyntax methodDecl,
        ReorderParametersParams @params,
        IReadOnlyDictionary<string, int> linkedPathCounts,
        CancellationToken cancellationToken)
    {
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken);
        if (methodSymbol == null)
            return null;

        if (!DocumentEditableHelpers.IsDocumentEditable(document, Context.Workspace))
            return null;

        if (!IsEligibleForAllFiles(methodSymbol, methodDecl))
            return null;

        if (methodSymbol.Parameters.Length != @params.NewOrder.Length)
            return null;

        int[] newOrder;
        try
        {
            newOrder = ValidateNewOrder(methodSymbol.Parameters.Length, @params.NewOrder);
            ValidateResultingSignature(methodDecl.ParameterList, methodSymbol, newOrder);
        }
        catch (RefactoringException)
        {
            return null;
        }

        // Identity permutation is a no-op for this method.
        if (IsIdentityPermutation(newOrder))
            return null;

        // Use the document's solution (allFiles currentSolution), not Context.Solution,
        // so DeclaringSyntaxReferences resolve after prior bulk rewrites (RemoveParameter peer).
        var solution = document.Project.Solution;

        // Base virtual/abstract still eligible under IsOverride==false, but
        // rewriting it while derived overrides keep the old signature breaks
        // the hierarchy when bulk does not cascade (RemoveParameter / Codex).
        var overrides = await SymbolFinder.FindOverridesAsync(
            methodSymbol, solution, cancellationToken: cancellationToken);
        if (overrides.Any())
            return null;

        var relatedMethods = await GetRelatedMethodsAsync(
            methodSymbol,
            @params.UpdateOverrides,
            @params.UpdateImplementations,
            solution,
            cancellationToken);

        try
        {
            await ValidateRelatedSignaturesAsync(relatedMethods, newOrder, cancellationToken);
        }
        catch (RefactoringException)
        {
            return null;
        }

        var declarationTargets = await CollectDeclarationTargetsAsync(relatedMethods, solution, cancellationToken);
        foreach (var target in declarationTargets)
        {
            if (!DocumentEditableHelpers.IsDocumentEditable(target.Document, Context.Workspace))
                return null;
            if (DocumentPathHasLinkedMultiView(target.Document, linkedPathCounts))
                return null;
        }

        var fallbackNames = methodSymbol.Parameters.Select(p => p.Name).ToArray();
        var callSites = await CollectCallSitesAsync(relatedMethods, fallbackNames, solution, cancellationToken);
        foreach (var callSite in callSites)
        {
            if (!DocumentEditableHelpers.IsDocumentEditable(callSite.Document, Context.Workspace))
                return null;
            if (DocumentPathHasLinkedMultiView(callSite.Document, linkedPathCounts))
                return null;
        }

        var beforeText = await document.GetTextAsync(cancellationToken);
        var newSolution = await ApplyChangesAsync(
            document,
            declarationTargets,
            callSites,
            fallbackNames,
            newOrder,
            cancellationToken);

        var afterDocument = newSolution.GetDocument(document.Id);
        if (afterDocument == null)
            return null;

        var afterText = await afterDocument.GetTextAsync(cancellationToken);
        if (beforeText.ContentEquals(afterText))
        {
            var anyDiff = false;
            foreach (var project in newSolution.Projects)
            {
                foreach (var doc in project.Documents)
                {
                    var originalDoc = solution.GetDocument(doc.Id);
                    if (originalDoc == null)
                        continue;
                    var before = await originalDoc.GetTextAsync(cancellationToken);
                    var after = await doc.GetTextAsync(cancellationToken);
                    if (!before.ContentEquals(after))
                    {
                        anyDiff = true;
                        break;
                    }
                }

                if (anyDiff)
                    break;
            }

            if (!anyDiff)
                return null;
        }

        return newSolution;
    }

    private static bool IsIdentityPermutation(int[] newOrder)
    {
        for (var i = 0; i < newOrder.Length; i++)
        {
            if (newOrder[i] != i)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Validates that <paramref name="newOrder"/> is a permutation of 0..<paramref name="paramCount"/>-1.
    /// </summary>
    internal static int[] ValidateNewOrder(int paramCount, int[] newOrder)
    {
        if (newOrder is null || newOrder.Length == 0)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "newOrder is required.");

        if (paramCount < 2)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidParameterPosition,
                "Method must have at least two parameters to reorder.");
        }

        if (newOrder.Length != paramCount || !IsPermutation(newOrder, paramCount))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidParameterPosition,
                $"newOrder must be a permutation of 0..{paramCount - 1}.");
        }

        return newOrder;
    }

    internal static void ValidateResultingSignature(
        ParameterListSyntax original,
        IMethodSymbol method,
        int[] newOrder)
    {
        if (method.IsExtensionMethod && newOrder[0] != 0)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidParameterPosition,
                "The this parameter of an extension method must remain first.");
        }

        var reordered = newOrder.Select(i => original.Parameters[i]).ToList();
        var seenOptional = false;
        for (var i = 0; i < reordered.Count; i++)
        {
            var parameter = reordered[i];
            var isParams = ParameterSyntaxHelpers.IsParams(parameter);
            var isOptional = ParameterSyntaxHelpers.IsOptional(parameter);

            if (isParams && i != reordered.Count - 1)
            {
                throw new RefactoringException(
                    ErrorCodes.ParamsNotLast,
                    "A params parameter must remain last in the parameter list.");
            }

            if (isOptional)
            {
                seenOptional = true;
            }
            else if (seenOptional && !isParams)
            {
                throw new RefactoringException(
                    ErrorCodes.RequiredAfterOptional,
                    "Required parameters cannot follow optional parameters.");
            }
        }
    }

    private static async Task ValidateRelatedSignaturesAsync(
        IReadOnlyList<IMethodSymbol> methods,
        int[] newOrder,
        CancellationToken cancellationToken)
    {
        foreach (var method in methods)
        {
            foreach (var syntaxRef in method.DeclaringSyntaxReferences)
            {
                if (await syntaxRef.GetSyntaxAsync(cancellationToken) is not MethodDeclarationSyntax declaration)
                    continue;

                if (declaration.ParameterList.Parameters.Count != newOrder.Length)
                    continue;

                ValidateResultingSignature(declaration.ParameterList, method, newOrder);
            }

            ValidateNoOverloadCollision(method, newOrder);
        }
    }

    internal static void ValidateNoOverloadCollision(IMethodSymbol method, int[] newOrder)
    {
        if (method.ContainingType == null || newOrder.Length != method.Parameters.Length)
            return;

        foreach (var candidate in method.ContainingType.GetMembers(method.Name).OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(candidate, method))
                continue;
            if (candidate.Parameters.Length != method.Parameters.Length)
                continue;
            if (candidate.TypeParameters.Length != method.TypeParameters.Length)
                continue;
            if (!ParameterTypesMatch(method, newOrder, candidate))
                continue;

            throw new RefactoringException(
                ErrorCodes.SignatureMatchesOverload,
                $"Reordering parameters of '{method.Name}' would match an existing overload.");
        }
    }

    private static bool ParameterTypesMatch(IMethodSymbol method, int[] newOrder, IMethodSymbol other)
    {
        for (var i = 0; i < newOrder.Length; i++)
        {
            var reordered = method.Parameters[newOrder[i]];
            var existing = other.Parameters[i];
            if (reordered.RefKind != existing.RefKind)
                return false;
            if (!TypeEquivalenceHelpers.TypesEquivalent(reordered.Type, existing.Type))
                return false;
        }

        return true;
    }

    private async Task<List<IMethodSymbol>> GetRelatedMethodsAsync(
        IMethodSymbol method,
        bool updateOverrides,
        bool updateImplementations,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var results = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { method };

        if (updateOverrides)
        {
            var current = method;
            while (current.OverriddenMethod != null)
            {
                results.Add(current.OverriddenMethod);
                current = current.OverriddenMethod;
            }

            foreach (var symbol in results.ToList())
            {
                var overrides = await SymbolFinder.FindOverridesAsync(
                    symbol, solution, cancellationToken: cancellationToken);
                foreach (var ov in overrides.OfType<IMethodSymbol>())
                    results.Add(ov);
            }
        }

        if (updateImplementations)
        {
            foreach (var candidate in results.ToList())
            {
                if (candidate.ContainingType.TypeKind == TypeKind.Interface)
                {
                    var implementations = await SymbolFinder.FindImplementationsAsync(
                        candidate, solution, cancellationToken: cancellationToken);
                    foreach (var impl in implementations.OfType<IMethodSymbol>())
                        results.Add(impl);
                    continue;
                }

                foreach (var iface in candidate.ContainingType.AllInterfaces)
                {
                    foreach (var ifaceMethod in iface.GetMembers(candidate.Name).OfType<IMethodSymbol>())
                    {
                        var impl = candidate.ContainingType.FindImplementationForInterfaceMember(ifaceMethod);
                        if (impl is not IMethodSymbol implMethod ||
                            !SignatureOverrideHelpers.ShareOverrideRoot(implMethod, candidate))
                        {
                            continue;
                        }

                        results.Add(ifaceMethod);
                        var otherImpls = await SymbolFinder.FindImplementationsAsync(
                            ifaceMethod,
                            solution,
                            cancellationToken: cancellationToken);
                        foreach (var other in otherImpls.OfType<IMethodSymbol>())
                            results.Add(other);
                    }
                }
            }
        }

        return results.Where(SignatureOverrideHelpers.HasSourceDeclaration).ToList();
    }

    private async Task<List<DeclarationTarget>> CollectDeclarationTargetsAsync(
        IReadOnlyList<IMethodSymbol> methods,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var targets = new List<DeclarationTarget>();

        foreach (var method in methods)
        {
            foreach (var syntaxRef in method.DeclaringSyntaxReferences)
            {
                if (await syntaxRef.GetSyntaxAsync(cancellationToken) is not MethodDeclarationSyntax declaration)
                {
                    throw new RefactoringException(
                        ErrorCodes.InvalidSelection,
                        $"Method '{method.Name}' is an unsupported target for reorder_parameters.");
                }

                var document = solution.GetDocument(syntaxRef.SyntaxTree)
                    ?? throw new RefactoringException(
                        ErrorCodes.DocumentNotEditable,
                        $"Could not locate the document for method '{method.Name}'.");

                targets.Add(new DeclarationTarget(document, declaration.Span));
            }
        }

        if (targets.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                "The selected method is an unsupported target for reorder_parameters.");
        }

        return targets;
    }

    private async Task<List<CallSite>> CollectCallSitesAsync(
        IReadOnlyList<IMethodSymbol> methods,
        IReadOnlyList<string> fallbackParameterNames,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var callSites = new List<CallSite>();
        var seen = new HashSet<(DocumentId Id, TextSpan Span)>();

        foreach (var method in methods)
        {
            var references = await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken);
            foreach (var referenced in references)
            {
                foreach (var location in referenced.Locations)
                {
                    if (location.Location.Kind != LocationKind.SourceFile)
                        continue;

                    var document = location.Document;
                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    if (root == null)
                        continue;

                    var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                    if (SignatureReferenceHelpers.IsDeclarationName(node, location.Location.SourceSpan))
                        continue;

                    var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (invocation != null && SignatureReferenceHelpers.IsInvokedMethodName(invocation, location.Location.SourceSpan))
                    {
                        if (!seen.Add((document.Id, invocation.Span)))
                            continue;

                        var model = await document.GetSemanticModelAsync(cancellationToken);
                        var invoked = model?.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                        var isReduced = invoked?.MethodKind == MethodKind.ReducedExtension ||
                                        invoked?.ReducedFrom != null;
                        var sourceMethod = invoked?.ReducedFrom ?? invoked ?? method;
                        var parameterNames = sourceMethod.Parameters.Length == fallbackParameterNames.Count
                            ? sourceMethod.Parameters.Select(p => p.Name).ToArray()
                            : fallbackParameterNames;
                        callSites.Add(new CallSite(document, invocation.Span, isReduced, parameterNames));
                        continue;
                    }

                    if (SignatureReferenceHelpers.IsNameOfArgument(node))
                        continue;

                    throw new RefactoringException(
                        ErrorCodes.UnsupportedCallSite,
                        $"Method '{method.Name}' is used as a method group or other unsupported reference and cannot be updated automatically.");
                }
            }
        }

        return callSites;
    }

    private static async Task<Solution> ApplyChangesAsync(
        Document originatingDocument,
        IReadOnlyList<DeclarationTarget> declarations,
        IReadOnlyList<CallSite> callSites,
        IReadOnlyList<string> fallbackParameterNames,
        int[] newOrder,
        CancellationToken cancellationToken)
    {
        var solution = originatingDocument.Project.Solution;
        var documentIds = declarations.Select(d => d.Document.Id)
            .Concat(callSites.Select(c => c.Document.Id))
            .ToHashSet();

        foreach (var documentId in documentIds)
        {
            var document = solution.GetDocument(documentId)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Document disappeared from solution.");
            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var declarationSpans = declarations
                .Where(d => d.Document.Id == documentId)
                .Select(d => d.Span)
                .ToHashSet();
            var documentCallSites = callSites.Where(c => c.Document.Id == documentId).ToList();
            var invocationSpans = documentCallSites.Select(c => c.Span).ToHashSet();
            var reducedSpans = documentCallSites
                .Where(c => c.IsReducedExtension)
                .Select(c => c.Span)
                .ToHashSet();
            var parameterNamesBySpan = documentCallSites
                .GroupBy(c => c.Span)
                .ToDictionary(g => g.Key, g => g.First().ParameterNames);

            var methods = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => declarationSpans.Contains(m.Span))
                .ToList();
            var invocations = root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => invocationSpans.Contains(i.Span))
                .ToList();

            var rewriter = new ReorderParametersRewriter(
                methods,
                invocations,
                reducedSpans,
                parameterNamesBySpan,
                fallbackParameterNames,
                newOrder);
            root = rewriter.Visit(root)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to rewrite reorder_parameters targets.");

            solution = document.WithSyntaxRoot(root).Project.Solution;
        }

        return solution;
    }

    internal static ParameterListSyntax ReorderParameters(ParameterListSyntax list, int[] newOrder)
    {
        var original = list.Parameters;
        var reordered = newOrder.Select(i => original[i]).ToList();
        return SyntaxFactory.ParameterList(ReorderPreservingSeparators(original, reordered))
            .WithTriviaFrom(list);
    }

    internal static InvocationExpressionSyntax UpdateInvocation(
        InvocationExpressionSyntax invocation,
        IReadOnlyList<string> parameterNames,
        int[] newOrder,
        bool isReducedExtension = false)
    {
        var newArgs = ReorderArguments(
            invocation.ArgumentList,
            newOrder,
            parameterNames,
            isReducedExtension);
        return invocation.WithArgumentList(newArgs);
    }

    internal static ArgumentListSyntax ReorderArguments(
        ArgumentListSyntax args,
        int[] newOrder,
        IReadOnlyList<string> parameterNames,
        bool isReducedExtension = false)
    {
        var originalArgs = args.Arguments.ToList();
        var namedOriginal = new Dictionary<string, ArgumentSyntax>(StringComparer.Ordinal);
        var positionalOriginal = new List<ArgumentSyntax>();
        foreach (var arg in originalArgs)
        {
            if (arg.NameColon != null)
                namedOriginal[arg.NameColon.Name.Identifier.ValueText] = arg;
            else
                positionalOriginal.Add(arg);
        }

        if (positionalOriginal.Count == 0)
            return args;

        var firstExplicitOrdinal = isReducedExtension ? 1 : 0;
        var positionalByOldIndex = new Dictionary<int, ArgumentSyntax>();
        var positionalIndex = 0;
        for (var i = 0; i < parameterNames.Count; i++)
        {
            if (namedOriginal.ContainsKey(parameterNames[i]))
                continue;
            if (i < firstExplicitOrdinal)
                continue;
            if (positionalIndex >= positionalOriginal.Count)
                continue;
            positionalByOldIndex[i] = positionalOriginal[positionalIndex++];
        }

        var leftoverPositionals = positionalOriginal.Skip(positionalIndex).ToList();
        var newArgs = new List<ArgumentSyntax>();
        var seenOmittedBefore = false;

        foreach (var oldIndex in newOrder)
        {
            if (oldIndex < firstExplicitOrdinal)
                continue;

            var name = parameterNames[oldIndex];
            if (namedOriginal.TryGetValue(name, out var named))
            {
                newArgs.Add(named);
                continue;
            }

            if (positionalByOldIndex.TryGetValue(oldIndex, out var positional))
            {
                newArgs.Add(seenOmittedBefore ? EnsureNamed(positional, name) : positional);
                continue;
            }

            seenOmittedBefore = true;
        }

        foreach (var leftover in namedOriginal.Values)
        {
            if (!newArgs.Contains(leftover))
                newArgs.Add(leftover);
        }

        newArgs.AddRange(leftoverPositionals);

        return SyntaxFactory.ArgumentList(ReorderPreservingSeparators(args.Arguments, newArgs))
            .WithTriviaFrom(args);
    }

    private static ArgumentSyntax EnsureNamed(ArgumentSyntax argument, string name)
    {
        if (argument.NameColon != null)
            return argument;

        return argument.WithNameColon(CreateNameColon(name));
    }

    private static NameColonSyntax CreateNameColon(string name)
    {
        var keywordKind = SyntaxFacts.GetKeywordKind(name);
        var identifier = keywordKind != SyntaxKind.None
            ? SyntaxFactory.VerbatimIdentifier(default, name, name, default)
            : SyntaxFactory.Identifier(name);
        return SyntaxFactory.NameColon(
            SyntaxFactory.IdentifierName(identifier),
            SyntaxFactory.Token(SyntaxKind.ColonToken).WithTrailingTrivia(SyntaxFactory.Space));
    }

    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        ReorderParametersParams @params,
        Document originalDocument,
        Solution newSolution,
        int callSiteCount,
        CancellationToken cancellationToken)
    {
        var pendingChanges = new List<PendingChange>();
        var originalSolution = originalDocument.Project.Solution;

        foreach (var projectChanges in newSolution.GetChanges(originalSolution).GetProjectChanges())
        {
            foreach (var docId in projectChanges.GetChangedDocuments())
            {
                var oldDoc = originalSolution.GetDocument(docId);
                var newDoc = newSolution.GetDocument(docId);
                if (oldDoc?.FilePath == null || newDoc == null)
                    continue;

                var before = await oldDoc.GetTextAsync(cancellationToken);
                var after = await newDoc.GetTextAsync(cancellationToken);
                pendingChanges.Add(new PendingChange
                {
                    File = oldDoc.FilePath,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Reorder parameters of '{@params.MethodName!}' ({callSiteCount} call site(s) to update)",
                    BeforeSnippet = before.ToString(),
                    AfterSnippet = after.ToString()
                });
            }
        }

        if (pendingChanges.Count == 0)
        {
            pendingChanges.Add(new PendingChange
            {
                File = @params.SourceFile!,
                ChangeType = ChangeKind.Modify,
                Description = $"Reorder parameters of '{@params.MethodName!}'",
                BeforeSnippet = null,
                AfterSnippet = null
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private static bool IsPermutation(int[] order, int count)
    {
        if (order.Length != count)
            return false;

        var seen = new bool[count];
        foreach (var index in order)
        {
            if (index < 0 || index >= count || seen[index])
                return false;
            seen[index] = true;
        }

        return true;
    }

    internal static SeparatedSyntaxList<T> ReorderPreservingSeparators<T>(
        SeparatedSyntaxList<T> original,
        IReadOnlyList<T> reordered)
        where T : SyntaxNode
    {
        if (reordered.Count == 0)
            return SyntaxFactory.SeparatedList<T>();

        var originalNodes = original.ToList();
        var separators = original.GetSeparators().ToList();
        var newIndices = new List<int>(reordered.Count);
        foreach (var node in reordered)
        {
            var index = originalNodes.IndexOf(node);
            if (index < 0)
                return SyntaxFactory.SeparatedList(reordered, DefaultCommaSeparators(reordered.Count));

            newIndices.Add(index);
        }

        var newSeparators = new List<SyntaxToken>();
        for (var i = 0; i < newIndices.Count - 1; i++)
        {
            var from = newIndices[i];
            var to = newIndices[i + 1];
            if (to == from + 1 && from < separators.Count)
                newSeparators.Add(separators[from]);
            else
                newSeparators.Add(CommaWithSpace());
        }

        return SyntaxFactory.SeparatedList(reordered, newSeparators);
    }

    private static IReadOnlyList<SyntaxToken> DefaultCommaSeparators(int nodeCount)
    {
        if (nodeCount <= 1)
            return Array.Empty<SyntaxToken>();

        return Enumerable.Repeat(CommaWithSpace(), nodeCount - 1).ToArray();
    }

    private static SyntaxToken CommaWithSpace() =>
        SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space);

    private sealed record DeclarationTarget(Document Document, TextSpan Span);

    private sealed record CallSite(
        Document Document,
        TextSpan Span,
        bool IsReducedExtension,
        IReadOnlyList<string> ParameterNames);

    private sealed class ReorderParametersRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<MethodDeclarationSyntax> _methods;
        private readonly HashSet<InvocationExpressionSyntax> _invocations;
        private readonly HashSet<TextSpan> _reducedSpans;
        private readonly IReadOnlyDictionary<TextSpan, IReadOnlyList<string>> _parameterNamesBySpan;
        private readonly IReadOnlyList<string> _fallbackParameterNames;
        private readonly int[] _newOrder;

        public ReorderParametersRewriter(
            IReadOnlyList<MethodDeclarationSyntax> methods,
            IReadOnlyList<InvocationExpressionSyntax> invocations,
            HashSet<TextSpan> reducedSpans,
            IReadOnlyDictionary<TextSpan, IReadOnlyList<string>> parameterNamesBySpan,
            IReadOnlyList<string> fallbackParameterNames,
            int[] newOrder)
        {
            _methods = new HashSet<MethodDeclarationSyntax>(methods);
            _invocations = new HashSet<InvocationExpressionSyntax>(invocations);
            _reducedSpans = reducedSpans;
            _parameterNamesBySpan = parameterNamesBySpan;
            _fallbackParameterNames = fallbackParameterNames;
            _newOrder = newOrder;
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            var original = _methods.FirstOrDefault(m => m.Span == node.Span && m.Identifier.Text == node.Identifier.Text);
            if (original == null)
                return visited;

            if (visited.ParameterList.Parameters.Count != _newOrder.Length)
                return visited;

            return visited.WithParameterList(ReorderParameters(visited.ParameterList, _newOrder));
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (!_invocations.Contains(node) && !_invocations.Any(i => i.Span == node.Span))
                return visited;

            var isReduced = _reducedSpans.Contains(node.Span);
            var parameterNames = _parameterNamesBySpan.TryGetValue(node.Span, out var stored)
                ? stored
                : _fallbackParameterNames;
            return UpdateInvocation(visited, parameterNames, _newOrder, isReduced);
        }
    }
}
