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
/// Adds a named parameter to a method and updates call sites, overrides,
/// and interface implementations.
/// Optional <c>allFiles</c> walks every C# document (or the optional single
/// <c>sourceFile</c>) and adds the same parameter to every eligible method,
/// skipping ineligible methods rather than throwing.
/// </summary>
public sealed class AddParameterOperation : RefactoringOperationBase<AddParameterParams>
{
    /// <summary>
    /// Creates a new add parameter operation.
    /// </summary>
    public AddParameterOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(AddParameterParams @params) => Validate(@params);

    /// <summary>
    /// Validates add-parameter inputs. Internal so tests can exercise rules
    /// without loading a workspace.
    /// </summary>
    internal static void Validate(AddParameterParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.ParameterName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "parameterName is required.");

        if (string.IsNullOrWhiteSpace(@params.ParameterType))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "parameterType is required.");

        if (!SyntaxIdentifierValidation.IsValidIdentifier(@params.ParameterName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"'{@params.ParameterName}' is not a valid parameter name.");

        if (!IsValidParameterType(@params.ParameterType))
            throw new RefactoringException(ErrorCodes.InvalidParameterType, $"'{@params.ParameterType}' is not a valid C# parameter type.");

        if (@params.Position < -1)
            throw new RefactoringException(ErrorCodes.InvalidParameterPosition, "position must be -1 (end) or a 0-based index.");

        if (@params.DefaultValue != null && !IsValidDefaultValueExpression(@params.DefaultValue))
            throw new RefactoringException(ErrorCodes.InvalidDefaultValue, $"'{@params.DefaultValue}' is not a valid default value expression.");

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
            // (ChangeSignature / ChangeReturnType allFiles / Copilot).
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
        AddParameterParams @params,
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

        if (methodSymbol.Parameters.Any(p => p.Name == SyntaxIdentifierValidation.NormalizeIdentifier(@params.ParameterName)))
        {
            throw new RefactoringException(
                ErrorCodes.ParameterAlreadyExists,
                $"Parameter '{@params.ParameterName}' already exists in method '{methodName}'.");
        }

        var insertIndex = ComputeInsertionIndex(methodDecl.ParameterList, @params.Position);
        var attachDefaultToDeclaration = CanAttachDefaultToDeclaration(methodDecl.ParameterList, insertIndex);
        ValidateResultingSignature(methodDecl.ParameterList, insertIndex, @params, attachDefaultToDeclaration);

        if (!string.IsNullOrEmpty(@params.DefaultValue))
        {
            ValidateDefaultValue(
                semanticModel,
                @params.ParameterType,
                @params.DefaultValue);
        }

        var solution = document.Project.Solution;

        var relatedMethods = await GetRelatedMethodsAsync(
            methodSymbol,
            @params.UpdateOverrides,
            @params.UpdateImplementations,
            solution,
            cancellationToken);

        var declarationTargets = await CollectDeclarationTargetsAsync(relatedMethods, solution, cancellationToken);
        foreach (var target in declarationTargets)
            DocumentEditableHelpers.ValidateDocumentIsEditable(target.Document, Context.Workspace);

        var callSites = await CollectCallSitesAsync(relatedMethods, solution, cancellationToken);
        foreach (var callSite in callSites)
            DocumentEditableHelpers.ValidateDocumentIsEditable(callSite.Document, Context.Workspace);

        var declarationDefault = attachDefaultToDeclaration ? @params.DefaultValue : null;
        var newParameter = CreateParameter(SyntaxIdentifierValidation.NormalizeIdentifier(@params.ParameterName), @params.ParameterType, declarationDefault);
        var appending = insertIndex >= methodSymbol.Parameters.Length;

        var newSolution = await ApplyChangesAsync(
            document,
            declarationTargets,
            callSites,
            methodSymbol.Parameters.ToList(),
            insertIndex,
            newParameter,
            @params.ParameterName,
            @params.DefaultValue,
            appending,
            cancellationToken);

        if (@params.Preview)
        {
            return await CreatePreviewResultAsync(
                operationId,
                @params,
                document,
                newSolution,
                insertIndex,
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
    /// document filter as <c>ChangeSignatureOperation.ExecuteAllFilesAsync</c>
    /// / <c>ChangeReturnTypeOperation.ExecuteAllFilesAsync</c>) and adds
    /// <paramref name="params"/>.ParameterName / ParameterType to every eligible
    /// <see cref="MethodDeclarationSyntax"/>. Optional <c>sourceFile</c> limits
    /// via <see cref="DocumentSourceFileFilter"/>. Linked multi-project views of
    /// the same path are skipped rather than coalescing (same contract as
    /// <c>SafeDeleteOperation.ExecuteAllFilesAsync</c> / hierarchy allFiles);
    /// a candidate is also skipped when any related declaration or call site
    /// lives on a multi-view path (so Coalesce cannot overwrite siblings).
    /// Methods that already have the parameter, fail existing single-site
    /// validation, uneditable / source-generated docs, and otherwise inapplicable
    /// methods are skipped rather than failing the walk. Deterministic
    /// <c>SpanStart</c> order within a file. When every file is a no-op, succeeds
    /// with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        AddParameterParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);
        // Full-solution linked-view counts (independent of optional sourceFile filter)
        // so related declaration / call-site rewrites cannot touch a multi-view path
        // and then get coalesced onto divergent siblings (Copilot / push_members_down).
        var linkedPathCounts = BuildLinkedPathCounts(originalSolution);

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            allDocuments = DocumentSourceFileFilter.FilterDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        var documentGroups = AllFilesDocumentHelpers.GroupByLinkedPath(allDocuments);

        var changedCountByDoc = new Dictionary<DocumentId, int>();

        foreach (var linkedDocuments in documentGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Linked multi-project views of the same path can diverge under
            // preprocessor symbols / references; skip rather than coalescing
            // a rewrite that only one compilation can honor (safe_delete /
            // pull_members_up / push_members_down / extract_base_class allFiles).
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
                        updated = await TryAddOneAsync(
                            currentDocument,
                            semanticModel,
                            methodDecl,
                            @params,
                            linkedPathCounts,
                            cancellationToken);
                    }
                    catch (RefactoringException)
                    {
                        // Skip ineligible methods rather than failing the walk.
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
            }
        }

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
    /// Preview description for a file that added
    /// <paramref name="changedCount"/> parameters.
    /// </summary>
    internal static string BuildAllFilesDescription(int changedCount) =>
        changedCount == 1
            ? "Add parameter"
            : $"Add {changedCount} parameters";

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
    /// True when inserting <paramref name="parameterType"/> at
    /// <paramref name="insertIndex"/> would collide with another method of
    /// the same name in the containing type (same arity + bound types)
    /// (ChangeSignature / Codex).
    /// </summary>
    internal static bool WouldCollideWithSibling(
        IMethodSymbol method,
        MethodDeclarationSyntax methodDecl,
        SemanticModel semanticModel,
        string parameterType,
        int insertIndex)
    {
        var containingType = method.ContainingType;
        if (containingType == null)
            return false;

        var newTypeInfo = semanticModel.GetSpeculativeTypeInfo(
            methodDecl.SpanStart,
            SyntaxFactory.ParseTypeName(parameterType),
            SpeculativeBindingOption.BindAsTypeOrNamespace);
        if (newTypeInfo.Type == null || newTypeInfo.Type is IErrorTypeSymbol)
            return false;

        var prospectiveLength = method.Parameters.Length + 1;

        foreach (var sibling in containingType.GetMembers(method.Name).OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(sibling, method))
                continue;
            if (sibling.Parameters.Length != prospectiveLength)
                continue;
            if (sibling.TypeParameters.Length != method.TypeParameters.Length)
                continue;

            var collision = true;
            for (var i = 0; i < prospectiveLength; i++)
            {
                ITypeSymbol expectedType;
                if (i == insertIndex)
                {
                    expectedType = newTypeInfo.Type;
                }
                else
                {
                    var originalIndex = i < insertIndex ? i : i - 1;
                    expectedType = method.Parameters[originalIndex].Type;
                }

                if (sibling.Parameters[i].RefKind != RefKind.None)
                {
                    collision = false;
                    break;
                }

                if (!TypeEquivalenceHelpers.TypesEquivalent(sibling.Parameters[i].Type, expectedType))
                {
                    collision = false;
                    break;
                }
            }

            if (collision)
                return true;
        }

        return false;
    }


    /// <summary>
    /// Path → linked-view count across the entire solution (not a filtered
    /// <c>sourceFile</c> subset). Same helper as
    /// <c>PushMembersDownOperation.BuildLinkedPathCounts</c>.
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

    private async Task<Solution?> TryAddOneAsync(
        Document document,
        SemanticModel semanticModel,
        MethodDeclarationSyntax methodDecl,
        AddParameterParams @params,
        IReadOnlyDictionary<string, int> linkedPathCounts,
        CancellationToken cancellationToken)
    {
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken);
        if (methodSymbol == null)
            return null;

        if (!DocumentEditableHelpers.IsDocumentEditable(document, Context.Workspace))
            return null;

        if (!AllFilesMethodEligibilityHelpers.IsEligibleForAllFiles(methodSymbol, methodDecl))
            return null;

        var normalizedName = SyntaxIdentifierValidation.NormalizeIdentifier(@params.ParameterName);
        if (methodSymbol.Parameters.Any(p => p.Name == normalizedName))
            return null;

        var insertIndex = ComputeInsertionIndex(methodDecl.ParameterList, @params.Position);
        var attachDefaultToDeclaration = CanAttachDefaultToDeclaration(methodDecl.ParameterList, insertIndex);
        ValidateResultingSignature(methodDecl.ParameterList, insertIndex, @params, attachDefaultToDeclaration);

        if (!string.IsNullOrEmpty(@params.DefaultValue))
        {
            ValidateDefaultValue(
                semanticModel,
                @params.ParameterType,
                @params.DefaultValue);
        }

        // Use the document's solution (allFiles currentSolution), not Context.Solution,
        // so DeclaringSyntaxReferences resolve after prior bulk rewrites (ChangeSignature peer).
        var solution = document.Project.Solution;

        // Base virtual/abstract still eligible under IsOverride==false, but
        // rewriting it while derived overrides keep the old signature breaks
        // the hierarchy when bulk does not cascade (ChangeSignature / Codex).
        var overrides = await SymbolFinder.FindOverridesAsync(
            methodSymbol, solution, cancellationToken: cancellationToken);
        if (overrides.Any())
            return null;

        if (WouldCollideWithSibling(
            methodSymbol,
            methodDecl,
            semanticModel,
            @params.ParameterType,
            insertIndex))
        {
            return null;
        }

        var relatedMethods = await GetRelatedMethodsAsync(
            methodSymbol,
            @params.UpdateOverrides,
            @params.UpdateImplementations,
            solution,
            cancellationToken);

        var declarationTargets = await CollectDeclarationTargetsAsync(relatedMethods, solution, cancellationToken);
        foreach (var target in declarationTargets)
        {
            if (!DocumentEditableHelpers.IsDocumentEditable(target.Document, Context.Workspace))
                return null;
            // Related declarations on a linked multi-view path must not be rewritten —
            // CoalesceLinkedDocumentTextAsync would copy onto divergent siblings (Copilot).
            if (DocumentPathHasLinkedMultiView(target.Document, linkedPathCounts))
                return null;
        }

        var callSites = await CollectCallSitesAsync(relatedMethods, solution, cancellationToken);
        foreach (var callSite in callSites)
        {
            if (!DocumentEditableHelpers.IsDocumentEditable(callSite.Document, Context.Workspace))
                return null;
            if (DocumentPathHasLinkedMultiView(callSite.Document, linkedPathCounts))
                return null;
        }

        var declarationDefault = attachDefaultToDeclaration ? @params.DefaultValue : null;
        var newParameter = CreateParameter(normalizedName, @params.ParameterType, declarationDefault);
        var appending = insertIndex >= methodSymbol.Parameters.Length;

        var beforeText = await document.GetTextAsync(cancellationToken);
        var newSolution = await ApplyChangesAsync(
            document,
            declarationTargets,
            callSites,
            methodSymbol.Parameters.ToList(),
            insertIndex,
            newParameter,
            @params.ParameterName,
            @params.DefaultValue,
            appending,
            cancellationToken);

        var afterDocument = newSolution.GetDocument(document.Id);
        if (afterDocument == null)
            return null;

        var afterText = await afterDocument.GetTextAsync(cancellationToken);
        if (beforeText.ContentEquals(afterText))
        {
            // Declaring file unchanged but related declarations / call sites may
            // have changed — keep the solution if any document differs.
            var anyDiff = false;
            foreach (var project in newSolution.Projects)
            {
                foreach (var doc in project.Documents)
                {
                    var originalDoc = document.Project.Solution.GetDocument(doc.Id);
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


    internal static int ComputeInsertionIndex(ParameterListSyntax list, int position)
    {
        var count = list.Parameters.Count;
        if (position == -1)
        {
            for (var i = 0; i < count; i++)
            {
                if (ParameterSyntaxHelpers.IsParams(list.Parameters[i]) || ParameterSyntaxHelpers.IsOptional(list.Parameters[i]))
                    return i;
            }

            return count;
        }

        if (position < 0 || position > count)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidParameterPosition,
                $"Position {position} is out of range. Valid range is 0 to {count}.");
        }

        return position;
    }

    internal static bool CanAttachDefaultToDeclaration(ParameterListSyntax original, int insertIndex)
    {
        for (var i = insertIndex; i < original.Parameters.Count; i++)
        {
            if (!ParameterSyntaxHelpers.IsOptional(original.Parameters[i]) && !ParameterSyntaxHelpers.IsParams(original.Parameters[i]))
                return false;
        }

        return true;
    }

    internal static void ValidateResultingSignature(
        ParameterListSyntax original,
        int insertIndex,
        AddParameterParams @params,
        bool attachDefaultToDeclaration)
    {
        var hypothetical = original.Parameters.ToList();
        hypothetical.Insert(insertIndex, CreateParameter(
            SyntaxIdentifierValidation.NormalizeIdentifier(@params.ParameterName),
            @params.ParameterType,
            attachDefaultToDeclaration ? @params.DefaultValue : null));

        var seenOptional = false;
        for (var i = 0; i < hypothetical.Count; i++)
        {
            var parameter = hypothetical[i];
            var isParams = ParameterSyntaxHelpers.IsParams(parameter);
            var isOptional = ParameterSyntaxHelpers.IsOptional(parameter);

            if (isParams && i != hypothetical.Count - 1)
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

    internal static ParameterSyntax CreateParameter(string name, string type, string? defaultValue)
    {
        var paramSyntax = SyntaxFactory.Parameter(SyntaxFactory.Identifier(name))
            .WithType(SyntaxFactory.ParseTypeName(type).WithTrailingTrivia(SyntaxFactory.Space));

        if (!string.IsNullOrEmpty(defaultValue))
        {
            paramSyntax = paramSyntax.WithDefault(
                SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.Token(SyntaxKind.EqualsToken)
                        .WithLeadingTrivia(SyntaxFactory.Space)
                        .WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.ParseExpression(defaultValue)));
        }

        return paramSyntax;
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
                        $"Method '{method.Name}' is an unsupported target for add_parameter.");
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
                "The selected method is an unsupported target for add_parameter.");
        }

        return targets;
    }

    private async Task<List<CallSite>> CollectCallSitesAsync(
        IReadOnlyList<IMethodSymbol> methods,
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

                        callSites.Add(new CallSite(document, invocation.Span));
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
        IReadOnlyList<IParameterSymbol> originalParams,
        int insertIndex,
        ParameterSyntax newParameter,
        string parameterName,
        string? defaultValue,
        bool appending,
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
            var invocationSpans = callSites
                .Where(c => c.Document.Id == documentId)
                .Select(c => c.Span)
                .ToHashSet();

            var methods = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => declarationSpans.Contains(m.Span))
                .ToList();
            var invocations = root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => invocationSpans.Contains(i.Span))
                .ToList();

            var rewriter = new AddParameterRewriter(
                methods,
                invocations,
                insertIndex,
                newParameter,
                originalParams,
                parameterName,
                defaultValue,
                appending);
            root = rewriter.Visit(root)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to rewrite add_parameter targets.");

            solution = document.WithSyntaxRoot(root).Project.Solution;
        }

        return solution;
    }

    internal static InvocationExpressionSyntax UpdateInvocation(
        InvocationExpressionSyntax invocation,
        IReadOnlyList<IParameterSymbol> originalParams,
        int insertIndex,
        string parameterName,
        string? defaultValue,
        bool appending)
    {
        var originalArgs = invocation.ArgumentList.Arguments.ToList();
        var newArg = CreateArgument(parameterName, defaultValue);
        var useNamedInsertion = !appending || originalArgs.Any(a => a.NameColon != null);

        if (useNamedInsertion && newArg.NameColon == null)
        {
            newArg = newArg.WithNameColon(
                SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(SyntaxIdentifierValidation.NormalizeIdentifier(parameterName))));
        }

        var namedOriginal = new Dictionary<string, ArgumentSyntax>();
        var positionalOriginal = new List<ArgumentSyntax>();
        foreach (var arg in originalArgs)
        {
            if (arg.NameColon != null)
                namedOriginal[arg.NameColon.Name.Identifier.Text] = arg;
            else
                positionalOriginal.Add(arg);
        }

        var newArgs = new List<ArgumentSyntax>();
        var positionalIndex = 0;
        var namedNewArgInserted = false;

        for (var i = 0; i < originalParams.Count + 1; i++)
        {
            if (i == insertIndex)
            {
                newArgs.Add(newArg);
                namedNewArgInserted = newArg.NameColon != null;
                continue;
            }

            var originalIndex = i < insertIndex ? i : i - 1;
            if (originalIndex < 0 || originalIndex >= originalParams.Count)
                continue;

            var originalParam = originalParams[originalIndex];
            if (namedOriginal.TryGetValue(originalParam.Name, out var namedArg))
            {
                newArgs.Add(namedArg);
                continue;
            }

            if (positionalIndex >= positionalOriginal.Count)
                continue;

            var positional = positionalOriginal[positionalIndex++];
            if (namedNewArgInserted && positional.NameColon == null)
            {
                positional = positional.WithNameColon(
                    SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(originalParam.Name)));
            }

            newArgs.Add(positional);
        }

        while (positionalIndex < positionalOriginal.Count)
            newArgs.Add(positionalOriginal[positionalIndex++]);

        foreach (var leftover in namedOriginal.Values)
        {
            if (!newArgs.Contains(leftover))
                newArgs.Add(leftover);
        }

        return invocation.WithArgumentList(
            SyntaxFactory.ArgumentList(SeparatedWithSpaces(newArgs))
                .WithTriviaFrom(invocation.ArgumentList));
    }

    internal static ArgumentSyntax CreateArgument(string paramName, string? defaultValue)
    {
        var expression = SyntaxFactory.ParseExpression(
            string.IsNullOrEmpty(defaultValue) ? "default" : defaultValue);
        return SyntaxFactory.Argument(expression);
    }

    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        AddParameterParams @params,
        Document originalDocument,
        Solution newSolution,
        int insertIndex,
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
                    Description = $"Add parameter '{@params.ParameterName}' to '{@params.MethodName}' at position {insertIndex} ({callSiteCount} call site(s) to update)",
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
                Description = $"Add parameter '{@params.ParameterName}' to '{@params.MethodName}'",
                BeforeSnippet = null,
                AfterSnippet = null
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }


    internal static bool IsValidParameterType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return false;

        var parsed = SyntaxFactory.ParseTypeName(type);
        if (parsed.ContainsDiagnostics || parsed.IsMissing)
            return false;

        var remainder = type.Trim();
        var written = parsed.ToString();
        if (!string.Equals(written.Trim(), remainder, StringComparison.Ordinal))
        {
            // ParseTypeName is permissive; reject leftovers such as "int int".
            if (remainder.Length > written.Trim().Length)
                return false;
        }

        return parsed is not IdentifierNameSyntax identifier || !identifier.IsMissing;
    }

    internal static bool IsValidDefaultValueExpression(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var expression = SyntaxFactory.ParseExpression(value);
        return !expression.ContainsDiagnostics && !expression.IsMissing;
    }

    internal static void ValidateDefaultValue(
        SemanticModel semanticModel,
        string parameterType,
        string defaultValue)
    {
        var expression = SyntaxFactory.ParseExpression(defaultValue);
        if (expression.IsMissing || expression.ContainsDiagnostics)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidDefaultValue,
                $"'{defaultValue}' is not a valid default value expression.");
        }

        var parseOptions = (CSharpParseOptions)semanticModel.SyntaxTree.Options;
        var probeTree = CSharpSyntaxTree.ParseText($$"""
            namespace __RoslynMcpProbe
            {
                static class Probe
                {
                    static void M()
                    {
                        {{parameterType}} __v = {{defaultValue}};
                    }
                }
            }
            """, parseOptions);

        var probeCompilation = semanticModel.Compilation.AddSyntaxTrees(probeTree);
        var probeModel = probeCompilation.GetSemanticModel(probeTree);
        var local = probeTree.GetRoot().DescendantNodes().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
        var initializer = local?.Declaration.Variables.FirstOrDefault()?.Initializer?.Value;
        if (local == null || initializer == null)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidDefaultValue,
                $"'{defaultValue}' is not a valid default value expression.");
        }

        var targetType = probeModel.GetTypeInfo(local.Declaration.Type).Type;
        if (targetType == null || targetType.TypeKind == TypeKind.Error)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidParameterType,
                $"'{parameterType}' is not a valid C# parameter type.");
        }

        var conversion = probeModel.ClassifyConversion(initializer, targetType);
        if (!conversion.Exists || (!conversion.IsIdentity && !conversion.IsImplicit))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidDefaultValue,
                $"'{defaultValue}' is not implicitly convertible to '{parameterType}'.");
        }

        if (IsDefaultValueExpression(initializer))
            return;

        if (!probeModel.GetConstantValue(initializer).HasValue)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidDefaultValue,
                $"'{defaultValue}' is not a compile-time constant or default(T).");
        }
    }

    private static bool IsDefaultValueExpression(ExpressionSyntax expression) =>
        expression.IsKind(SyntaxKind.DefaultLiteralExpression) ||
        expression is DefaultExpressionSyntax;

    private static SeparatedSyntaxList<T> SeparatedWithSpaces<T>(IReadOnlyList<T> nodes)
        where T : SyntaxNode
    {
        if (nodes.Count == 0)
            return SyntaxFactory.SeparatedList<T>();

        var separators = nodes.Count == 1
            ? Array.Empty<SyntaxToken>()
            : Enumerable.Repeat(
                SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space),
                nodes.Count - 1).ToArray();

        return SyntaxFactory.SeparatedList(nodes, separators);
    }

    private sealed record DeclarationTarget(Document Document, TextSpan Span);

    private sealed record CallSite(Document Document, TextSpan Span);

    private sealed class AddParameterRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<MethodDeclarationSyntax> _methods;
        private readonly HashSet<InvocationExpressionSyntax> _invocations;
        private readonly int _insertIndex;
        private readonly ParameterSyntax _newParameter;
        private readonly IReadOnlyList<IParameterSymbol> _originalParams;
        private readonly string _parameterName;
        private readonly string? _defaultValue;
        private readonly bool _appending;

        public AddParameterRewriter(
            IReadOnlyList<MethodDeclarationSyntax> methods,
            IReadOnlyList<InvocationExpressionSyntax> invocations,
            int insertIndex,
            ParameterSyntax newParameter,
            IReadOnlyList<IParameterSymbol> originalParams,
            string parameterName,
            string? defaultValue,
            bool appending)
        {
            _methods = new HashSet<MethodDeclarationSyntax>(methods);
            _invocations = new HashSet<InvocationExpressionSyntax>(invocations);
            _insertIndex = insertIndex;
            _newParameter = newParameter;
            _originalParams = originalParams;
            _parameterName = parameterName;
            _defaultValue = defaultValue;
            _appending = appending;
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            var original = _methods.FirstOrDefault(m => m.Span == node.Span && m.Identifier.Text == node.Identifier.Text);
            if (original == null)
                return visited;

            var parameters = visited.ParameterList.Parameters.ToList();
            var insertAt = Math.Min(_insertIndex, parameters.Count);
            parameters.Insert(insertAt, _newParameter);
            return visited.WithParameterList(
                SyntaxFactory.ParameterList(SeparatedWithSpaces(parameters))
                    .WithTriviaFrom(visited.ParameterList));
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (!_invocations.Contains(node) && !_invocations.Any(i => i.Span == node.Span))
                return visited;

            return UpdateInvocation(
                visited,
                _originalParams,
                _insertIndex,
                _parameterName,
                _defaultValue,
                _appending);
        }
    }
}
