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
/// Removes a named parameter from a method and updates call sites, overrides,
/// and interface implementations.
/// Optional <c>allFiles</c> walks every C# document (or the optional single
/// <c>sourceFile</c>) and removes the named parameter from every eligible method,
/// skipping ineligible methods rather than throwing.
/// </summary>
public sealed class RemoveParameterOperation : RefactoringOperationBase<RemoveParameterParams>
{
    /// <summary>
    /// Creates a new remove parameter operation.
    /// </summary>
    public RemoveParameterOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(RemoveParameterParams @params) => Validate(@params);

    /// <summary>
    /// Validates remove-parameter inputs. Internal so tests can exercise rules
    /// without loading a workspace.
    /// </summary>
    internal static void Validate(RemoveParameterParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.ParameterName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "parameterName is required.");

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
            // (AddParameter / ChangeSignature allFiles / Copilot).
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
        RemoveParameterParams @params,
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

        var parameter = FindParameter(methodSymbol, @params.ParameterName);
        var removeIndex = parameter.Ordinal;

        var solution = document.Project.Solution;

        var relatedMethods = await GetRelatedMethodsAsync(
            methodSymbol,
            @params.UpdateOverrides,
            @params.UpdateImplementations,
            solution,
            cancellationToken);

        var namesAtIndex = relatedMethods
            .Where(m => m.Parameters.Length > removeIndex)
            .Select(m => m.Parameters[removeIndex].Name)
            .ToHashSet(StringComparer.Ordinal);

        var declarationTargets = await CollectDeclarationTargetsAsync(relatedMethods, solution, cancellationToken);
        foreach (var target in declarationTargets)
            DocumentEditableHelpers.ValidateDocumentIsEditable(target.Document, Context.Workspace);

        var callSites = await CollectCallSitesAsync(relatedMethods, solution, cancellationToken);
        foreach (var callSite in callSites)
            DocumentEditableHelpers.ValidateDocumentIsEditable(callSite.Document, Context.Workspace);

        var bodyUsages = await CollectBodyUsagesAsync(relatedMethods, removeIndex, solution, cancellationToken);
        if (bodyUsages.Count > 0 && !@params.Force)
        {
            throw new RefactoringException(
                ErrorCodes.ParameterUsedInBody,
                $"Parameter '{@params.ParameterName}' is referenced in the method body. Set force=true to remove it.",
                new Dictionary<string, object>
                {
                    ["usageCount"] = bodyUsages.Count,
                    ["parameterName"] = @params.ParameterName
                },
                ["Set force=true to remove the parameter if leftover body usages can be replaced without breaking compilation."]);
        }

        if (bodyUsages.Any(u => !u.CanReplaceWithDefault))
        {
            throw new RefactoringException(
                ErrorCodes.CompilationError,
                $"Parameter '{@params.ParameterName}' is used in the method body in a way that cannot be replaced without leaving the solution uncompilable.");
        }

        foreach (var usage in bodyUsages)
            DocumentEditableHelpers.ValidateDocumentIsEditable(usage.Document, Context.Workspace);

        var newSolution = await ApplyChangesAsync(
            document,
            declarationTargets,
            callSites,
            bodyUsages,
            methodSymbol.Parameters.ToList(),
            removeIndex,
            namesAtIndex,
            cancellationToken);

        await EnsureNoNewCompilationErrorsAsync(solution, newSolution, cancellationToken);

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
    /// document filter as <c>AddParameterOperation.ExecuteAllFilesAsync</c>)
    /// and removes <paramref name="params"/>.ParameterName from every eligible
    /// <see cref="MethodDeclarationSyntax"/>. Optional <c>sourceFile</c> limits
    /// via <see cref="DocumentSourceFileFilter"/>. Linked multi-project views of
    /// the same path are skipped rather than coalescing (same contract as
    /// <c>SafeDeleteOperation.ExecuteAllFilesAsync</c> / add_parameter allFiles);
    /// a candidate is also skipped when any related declaration, call site, or
    /// body usage lives on a multi-view path. Methods that lack the parameter,
    /// fail existing single-site validation, uneditable / source-generated docs,
    /// and otherwise inapplicable methods are skipped rather than failing the
    /// walk. Deterministic <c>SpanStart</c> order within a file. The document-group
    /// walk repeats until a full pass makes no progress so cross-file call-site
    /// rewrites can unlock previously skipped methods (Codex). When every file
    /// is a no-op, succeeds with empty changes.
    /// </summary>
    private async Task<RefactoringResult> ExecuteAllFilesAsync(
        Guid operationId,
        RemoveParameterParams @params,
        CancellationToken cancellationToken)
    {
        var originalSolution = Context.Solution;
        var currentSolution = originalSolution;
        var allDocuments = AllFilesDocumentHelpers.EnumerateCsharpDocuments(originalSolution);
        var linkedPathCounts = AllFilesDocumentHelpers.BuildLinkedPathCounts(originalSolution);

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
            allDocuments = DocumentSourceFileFilter.FilterDocumentsBySourceFile(allDocuments, @params.SourceFile!);

        var documentGroups = AllFilesDocumentHelpers.GroupByLinkedPath(allDocuments);

        var changedCountByDoc = new Dictionary<DocumentId, int>();

        // Repeat until a full document-group pass makes no progress so that
        // call-site rewrites in later files can unlock earlier methods that were
        // skipped for body usages (Codex).
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
                            updated = await TryRemoveOneAsync(
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
    /// Preview description for a file that removed
    /// <paramref name="changedCount"/> parameters.
    /// </summary>
    internal static string BuildAllFilesDescription(int changedCount) =>
        changedCount == 1
            ? "Remove parameter"
            : $"Remove {changedCount} parameters";

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
    /// True when removing the parameter at <paramref name="removeIndex"/> would
    /// collide with another method of the same name in the containing type
    /// (same arity + bound types) (ChangeSignature / Codex).
    /// </summary>
    internal static bool WouldCollideWithSibling(IMethodSymbol method, int removeIndex)
    {
        var containingType = method.ContainingType;
        if (containingType == null)
            return false;

        if (removeIndex < 0 || removeIndex >= method.Parameters.Length)
            return false;

        var prospectiveLength = method.Parameters.Length - 1;

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
                var originalIndex = i < removeIndex ? i : i + 1;
                var expectedType = method.Parameters[originalIndex].Type;
                var expectedRef = method.Parameters[originalIndex].RefKind;

                if (sibling.Parameters[i].RefKind != expectedRef)
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

    private async Task<Solution?> TryRemoveOneAsync(
        Document document,
        SemanticModel semanticModel,
        MethodDeclarationSyntax methodDecl,
        RemoveParameterParams @params,
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
        var parameter = methodSymbol.Parameters.FirstOrDefault(p => p.Name == normalizedName);
        if (parameter == null)
            return null;

        var removeIndex = parameter.Ordinal;

        if (WouldCollideWithSibling(methodSymbol, removeIndex))
            return null;

        // Use the document's solution (allFiles currentSolution), not Context.Solution,
        // so DeclaringSyntaxReferences resolve after prior bulk rewrites (AddParameter peer).
        var solution = document.Project.Solution;

        // Base virtual/abstract still eligible under IsOverride==false, but
        // rewriting it while derived overrides keep the old signature breaks
        // the hierarchy when bulk does not cascade (AddParameter / Codex).
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

        var namesAtIndex = relatedMethods
            .Where(m => m.Parameters.Length > removeIndex)
            .Select(m => m.Parameters[removeIndex].Name)
            .ToHashSet(StringComparer.Ordinal);

        var declarationTargets = await CollectDeclarationTargetsAsync(relatedMethods, solution, cancellationToken);
        foreach (var target in declarationTargets)
        {
            if (!DocumentEditableHelpers.IsDocumentEditable(target.Document, Context.Workspace))
                return null;
            if (AllFilesDocumentHelpers.DocumentPathHasLinkedMultiView(target.Document, linkedPathCounts))
                return null;
        }

        var callSites = await CollectCallSitesAsync(relatedMethods, solution, cancellationToken);
        foreach (var callSite in callSites)
        {
            if (!DocumentEditableHelpers.IsDocumentEditable(callSite.Document, Context.Workspace))
                return null;
            if (AllFilesDocumentHelpers.DocumentPathHasLinkedMultiView(callSite.Document, linkedPathCounts))
                return null;
        }

        var bodyUsages = await CollectBodyUsagesAsync(relatedMethods, removeIndex, solution, cancellationToken);
        if (bodyUsages.Count > 0 && !@params.Force)
            return null;

        if (bodyUsages.Any(u => !u.CanReplaceWithDefault))
            return null;

        foreach (var usage in bodyUsages)
        {
            if (!DocumentEditableHelpers.IsDocumentEditable(usage.Document, Context.Workspace))
                return null;
            if (AllFilesDocumentHelpers.DocumentPathHasLinkedMultiView(usage.Document, linkedPathCounts))
                return null;
        }

        var beforeText = await document.GetTextAsync(cancellationToken);
        var newSolution = await ApplyChangesAsync(
            document,
            declarationTargets,
            callSites,
            bodyUsages,
            methodSymbol.Parameters.ToList(),
            removeIndex,
            namesAtIndex,
            cancellationToken);

        try
        {
            await EnsureNoNewCompilationErrorsAsync(solution, newSolution, cancellationToken);
        }
        catch (RefactoringException)
        {
            return null;
        }

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

    internal static IParameterSymbol FindParameter(IMethodSymbol method, string name)
    {
        var normalized = SyntaxIdentifierValidation.NormalizeIdentifier(name);
        var parameter = method.Parameters.FirstOrDefault(p => p.Name == normalized);
        if (parameter != null)
            return parameter;

        var available = string.Join(", ", method.Parameters.Select(p => p.Name));
        throw new RefactoringException(
            ErrorCodes.ParameterNotFound,
            string.IsNullOrEmpty(available)
                ? $"Parameter '{name}' not found on method '{method.Name}'."
                : $"Parameter '{name}' not found on method '{method.Name}'. Available: {available}");
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

    private static async Task<List<DeclarationTarget>> CollectDeclarationTargetsAsync(
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
                        $"Method '{method.Name}' is an unsupported target for remove_parameter.");
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
                "The selected method is an unsupported target for remove_parameter.");
        }

        return targets;
    }

    private static async Task<List<CallSite>> CollectCallSitesAsync(
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

                        var model = await document.GetSemanticModelAsync(cancellationToken);
                        var invoked = model?.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                        var isReduced = invoked?.MethodKind == MethodKind.ReducedExtension ||
                                        invoked?.ReducedFrom != null;
                        callSites.Add(new CallSite(document, invocation.Span, isReduced));
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

    private static async Task<List<BodyUsage>> CollectBodyUsagesAsync(
        IReadOnlyList<IMethodSymbol> methods,
        int parameterIndex,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var usages = new List<BodyUsage>();
        var seen = new HashSet<(DocumentId Id, TextSpan Span)>();

        foreach (var method in methods)
        {
            if (parameterIndex < 0 || parameterIndex >= method.Parameters.Length)
                continue;

            var parameter = method.Parameters[parameterIndex];
            foreach (var syntaxRef in method.DeclaringSyntaxReferences)
            {
                if (await syntaxRef.GetSyntaxAsync(cancellationToken) is not MethodDeclarationSyntax declaration)
                    continue;

                var document = solution.GetDocument(syntaxRef.SyntaxTree);
                if (document == null)
                    continue;

                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (model == null)
                    continue;

                var body = (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody;
                if (body == null)
                    continue;

                foreach (var identifier in body.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    var symbol = model.GetSymbolInfo(identifier, cancellationToken).Symbol;
                    if (!SymbolEqualityComparer.Default.Equals(symbol, parameter))
                        continue;

                    if (!seen.Add((document.Id, identifier.Span)))
                        continue;

                    usages.Add(new BodyUsage(
                        document,
                        identifier.Span,
                        GetParameterTypeDisplay(parameter),
                        CanReplaceWithDefault(identifier)));
                }
            }
        }

        return usages;
    }

    private static async Task<Solution> ApplyChangesAsync(
        Document originatingDocument,
        IReadOnlyList<DeclarationTarget> declarations,
        IReadOnlyList<CallSite> callSites,
        IReadOnlyList<BodyUsage> bodyUsages,
        IReadOnlyList<IParameterSymbol> originalParams,
        int removeIndex,
        IReadOnlySet<string> namesAtIndex,
        CancellationToken cancellationToken)
    {
        var solution = originatingDocument.Project.Solution;
        var documentIds = declarations.Select(d => d.Document.Id)
            .Concat(callSites.Select(c => c.Document.Id))
            .Concat(bodyUsages.Select(u => u.Document.Id))
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
            var documentUsages = bodyUsages.Where(u => u.Document.Id == documentId).ToList();
            var usageSpans = documentUsages.Select(u => u.Span).ToHashSet();
            var identifierTypes = documentUsages
                .GroupBy(u => u.Span)
                .ToDictionary(g => g.Key, g => g.First().TypeDisplay);

            var methods = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => declarationSpans.Contains(m.Span))
                .ToList();
            var invocations = root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => invocationSpans.Contains(i.Span))
                .ToList();
            var identifiers = root.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Where(i => usageSpans.Contains(i.Span))
                .ToList();

            var rewriter = new RemoveParameterRewriter(
                methods,
                invocations,
                identifiers,
                identifierTypes,
                reducedSpans,
                removeIndex,
                originalParams,
                namesAtIndex);
            root = rewriter.Visit(root)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to rewrite remove_parameter targets.");

            solution = document.WithSyntaxRoot(root).Project.Solution;
        }

        return solution;
    }

    internal static InvocationExpressionSyntax UpdateInvocation(
        InvocationExpressionSyntax invocation,
        IReadOnlyList<IParameterSymbol> originalParams,
        int removeIndex,
        IReadOnlySet<string> namesAtIndex,
        bool isReducedExtension = false)
    {
        var newArgs = RemoveArgument(
            invocation.ArgumentList,
            removeIndex,
            originalParams,
            namesAtIndex,
            isReducedExtension);
        return invocation.WithArgumentList(newArgs);
    }

    internal static ArgumentListSyntax RemoveArgument(
        ArgumentListSyntax args,
        int removeIndex,
        IReadOnlyList<IParameterSymbol> originalParams,
        IReadOnlySet<string> namesAtIndex,
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

        var newArgs = new List<ArgumentSyntax>();
        var positionalIndex = 0;
        var firstExplicitOrdinal = isReducedExtension ? 1 : 0;
        var removingParams = removeIndex >= 0 &&
                             removeIndex < originalParams.Count &&
                             originalParams[removeIndex].IsParams;

        for (var i = 0; i < originalParams.Count; i++)
        {
            var originalParam = originalParams[i];
            if (namedOriginal.TryGetValue(originalParam.Name, out var namedArg))
            {
                if (i != removeIndex)
                    newArgs.Add(namedArg);
                continue;
            }

            if (i < firstExplicitOrdinal)
                continue;

            if (positionalIndex >= positionalOriginal.Count)
                continue;

            var positional = positionalOriginal[positionalIndex++];
            if (i == removeIndex)
            {
                if (removingParams)
                    positionalIndex = positionalOriginal.Count;
                continue;
            }

            newArgs.Add(positional);
        }

        while (positionalIndex < positionalOriginal.Count)
            newArgs.Add(positionalOriginal[positionalIndex++]);

        foreach (var leftover in namedOriginal)
        {
            if (namesAtIndex.Contains(leftover.Key))
                continue;
            if (!newArgs.Contains(leftover.Value))
                newArgs.Add(leftover.Value);
        }

        return SyntaxFactory.ArgumentList(KeepNodesPreservingSeparators(args.Arguments, newArgs))
            .WithTriviaFrom(args);
    }

    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        RemoveParameterParams @params,
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
                    Description = $"Remove parameter '{@params.ParameterName}' from '{@params.MethodName}' ({callSiteCount} call site(s) to update)",
                    BeforeSnippet = before.ToString(),
                    AfterSnippet = after.ToString()
                });
            }
        }

        if (pendingChanges.Count == 0)
        {
            pendingChanges.Add(new PendingChange
            {
                File = originalDocument.FilePath
                    ?? @params.SourceFile
                    ?? throw new RefactoringException(ErrorCodes.RoslynError, "Missing source file for preview."),
                ChangeType = ChangeKind.Modify,
                Description = $"Remove parameter '{@params.ParameterName}' from '{@params.MethodName}'",
                BeforeSnippet = null,
                AfterSnippet = null
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private static bool CanReplaceWithDefault(IdentifierNameSyntax identifier)
    {
        if (SignatureReferenceHelpers.IsNameOfArgument(identifier))
            return false;

        if (identifier.Parent is MemberAccessExpressionSyntax member && member.Expression == identifier)
            return false;

        if (identifier.Parent is MemberBindingExpressionSyntax)
            return false;

        if (identifier.Parent is ElementAccessExpressionSyntax element && element.Expression == identifier)
            return false;

        if (identifier.Parent is AssignmentExpressionSyntax assignment && assignment.Left == identifier)
            return false;

        if (identifier.Parent is PrefixUnaryExpressionSyntax prefix &&
            (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)))
        {
            return false;
        }

        if (identifier.Parent is PostfixUnaryExpressionSyntax postfix &&
            (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression)))
        {
            return false;
        }

        if (identifier.Parent is ArgumentSyntax argument &&
            (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) ||
             argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) ||
             argument.RefKindKeyword.IsKind(SyntaxKind.InKeyword)))
        {
            return false;
        }

        return true;
    }

    private static string GetParameterTypeDisplay(IParameterSymbol parameter)
    {
        foreach (var syntaxRef in parameter.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax() is ParameterSyntax { Type: { } type })
                return type.ToString().Trim();
        }

        return parameter.Type.ToDisplayString();
    }

    private static async Task EnsureNoNewCompilationErrorsAsync(
        Solution original,
        Solution updated,
        CancellationToken cancellationToken)
    {
        var before = await CollectErrorKeysAsync(original, cancellationToken);
        var after = await CollectErrorKeysAsync(updated, cancellationToken);
        var introduced = after.Where(key => !before.Contains(key)).ToList();
        if (introduced.Count == 0)
            return;

        throw new RefactoringException(
            ErrorCodes.CompilationError,
            "Removing the parameter would leave the solution uncompilable.");
    }

    private static async Task<HashSet<string>> CollectErrorKeysAsync(
        Solution solution,
        CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
                continue;

            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
            {
                if (diagnostic.Severity != DiagnosticSeverity.Error)
                    continue;

                keys.Add($"{diagnostic.Id}:{diagnostic.GetMessage()}");
            }
        }

        return keys;
    }

    internal static SeparatedSyntaxList<T> RemoveAtPreservingSeparators<T>(
        SeparatedSyntaxList<T> list,
        int index)
        where T : SyntaxNode
    {
        if (index < 0 || index >= list.Count)
            return list;

        var nodes = list.ToList();
        var separators = list.GetSeparators().ToList();
        nodes.RemoveAt(index);

        if (separators.Count > 0)
        {
            if (index >= separators.Count)
                separators.RemoveAt(separators.Count - 1);
            else
                separators.RemoveAt(index);
        }

        if (nodes.Count == 0)
            return SyntaxFactory.SeparatedList<T>();

        if (separators.Count >= nodes.Count)
            separators = separators.Take(nodes.Count - 1).ToList();

        return SyntaxFactory.SeparatedList(nodes, separators);
    }

    internal static SeparatedSyntaxList<T> KeepNodesPreservingSeparators<T>(
        SeparatedSyntaxList<T> original,
        IReadOnlyList<T> keep)
        where T : SyntaxNode
    {
        if (keep.Count == 0)
            return SyntaxFactory.SeparatedList<T>();

        var originalNodes = original.ToList();
        var separators = original.GetSeparators().ToList();
        var keepIndices = new List<int>(keep.Count);
        foreach (var node in keep)
        {
            var index = originalNodes.IndexOf(node);
            if (index < 0)
                return SyntaxFactory.SeparatedList(keep, DefaultCommaSeparators(keep.Count));

            keepIndices.Add(index);
        }

        var newSeparators = new List<SyntaxToken>();
        for (var i = 0; i < keepIndices.Count - 1; i++)
        {
            var from = keepIndices[i];
            var to = keepIndices[i + 1];
            if (to == from + 1 && from < separators.Count)
                newSeparators.Add(separators[from]);
            else
                newSeparators.Add(CommaWithSpace());
        }

        return SyntaxFactory.SeparatedList(keep, newSeparators);
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

    private sealed record CallSite(Document Document, TextSpan Span, bool IsReducedExtension);

    private sealed record BodyUsage(Document Document, TextSpan Span, string TypeDisplay, bool CanReplaceWithDefault);

    private sealed class RemoveParameterRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<MethodDeclarationSyntax> _methods;
        private readonly HashSet<InvocationExpressionSyntax> _invocations;
        private readonly HashSet<IdentifierNameSyntax> _identifiers;
        private readonly IReadOnlyDictionary<TextSpan, string> _identifierTypes;
        private readonly HashSet<TextSpan> _reducedSpans;
        private readonly int _removeIndex;
        private readonly IReadOnlyList<IParameterSymbol> _originalParams;
        private readonly IReadOnlySet<string> _namesAtIndex;

        public RemoveParameterRewriter(
            IReadOnlyList<MethodDeclarationSyntax> methods,
            IReadOnlyList<InvocationExpressionSyntax> invocations,
            IReadOnlyList<IdentifierNameSyntax> identifiers,
            IReadOnlyDictionary<TextSpan, string> identifierTypes,
            HashSet<TextSpan> reducedSpans,
            int removeIndex,
            IReadOnlyList<IParameterSymbol> originalParams,
            IReadOnlySet<string> namesAtIndex)
        {
            _methods = new HashSet<MethodDeclarationSyntax>(methods);
            _invocations = new HashSet<InvocationExpressionSyntax>(invocations);
            _identifiers = new HashSet<IdentifierNameSyntax>(identifiers);
            _identifierTypes = identifierTypes;
            _reducedSpans = reducedSpans;
            _removeIndex = removeIndex;
            _originalParams = originalParams;
            _namesAtIndex = namesAtIndex;
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            var original = _methods.FirstOrDefault(m => m.Span == node.Span && m.Identifier.Text == node.Identifier.Text);
            if (original == null)
                return visited;

            if (_removeIndex < 0 || _removeIndex >= visited.ParameterList.Parameters.Count)
                return visited;

            return visited.WithParameterList(
                SyntaxFactory.ParameterList(
                        RemoveAtPreservingSeparators(visited.ParameterList.Parameters, _removeIndex))
                    .WithTriviaFrom(visited.ParameterList));
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (!_invocations.Contains(node) && !_invocations.Any(i => i.Span == node.Span))
                return visited;

            var isReduced = _reducedSpans.Contains(node.Span);
            return UpdateInvocation(visited, _originalParams, _removeIndex, _namesAtIndex, isReduced);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (!_identifiers.Contains(node) && !_identifiers.Any(i => i.Span == node.Span && i.Identifier.Text == node.Identifier.Text))
                return base.VisitIdentifierName(node);

            var typeDisplay = _identifierTypes.TryGetValue(node.Span, out var stored)
                ? stored
                : "object";
            return SyntaxFactory.DefaultExpression(SyntaxFactory.ParseTypeName(typeDisplay))
                .WithTriviaFrom(node);
        }
    }
}
