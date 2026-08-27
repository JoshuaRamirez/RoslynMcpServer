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
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Signature;

/// <summary>
/// Reorders a method's parameters by a 0-based permutation and updates
/// call sites, overrides, and interface implementations.
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
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        if (@params.NewOrder is null || @params.NewOrder.Length == 0)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "newOrder is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");

        if (@params.NewOrder.Length < 2 || !IsPermutation(@params.NewOrder, @params.NewOrder.Length))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidParameterPosition,
                "newOrder must be a permutation of 0..n-1 for a method with at least two parameters.");
        }
    }

    /// <summary>
    /// Rejects documents that cannot receive source edits.
    /// </summary>
    internal static void ValidateDocumentIsEditable(Document document, Microsoft.CodeAnalysis.Workspace workspace)
    {
        if (document is SourceGeneratedDocument)
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable (source-generated).");
        }

        if (string.IsNullOrWhiteSpace(document.FilePath) || !File.Exists(document.FilePath))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable.");
        }

        if (!workspace.CanApplyChange(ApplyChangesKind.ChangeDocument))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable (workspace cannot apply changes).");
        }
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ReorderParametersParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        ValidateDocumentIsEditable(document, Context.Workspace);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var methodDecl = FindMethodDeclaration(root, @params);
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");

        var newOrder = ValidateNewOrder(methodSymbol.Parameters.Length, @params.NewOrder);
        ValidateResultingSignature(methodDecl.ParameterList, methodSymbol, newOrder);

        var relatedMethods = await GetRelatedMethodsAsync(
            methodSymbol,
            @params.UpdateOverrides,
            @params.UpdateImplementations,
            cancellationToken);

        var declarationTargets = await CollectDeclarationTargetsAsync(relatedMethods, cancellationToken);
        foreach (var target in declarationTargets)
            ValidateDocumentIsEditable(target.Document, Context.Workspace);

        var callSites = await CollectCallSitesAsync(relatedMethods, cancellationToken);
        foreach (var callSite in callSites)
            ValidateDocumentIsEditable(callSite.Document, Context.Workspace);

        var newSolution = await ApplyChangesAsync(
            document,
            declarationTargets,
            callSites,
            methodSymbol.Parameters.ToList(),
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
                Name = @params.MethodName,
                FullyQualifiedName = methodSymbol.ToDisplayString(),
                Kind = Contracts.Enums.SymbolKind.Method
            },
            callSites.Count,
            0);
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
            var isParams = IsParams(parameter);
            var isOptional = IsOptional(parameter);

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

    internal static MethodDeclarationSyntax FindMethodDeclaration(SyntaxNode root, ReorderParametersParams @params)
    {
        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == @params.MethodName)
            .ToList();

        if (methods.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                $"Method '{@params.MethodName}' not found.");
        }

        if (methods.Count == 1 && !@params.Line.HasValue && !@params.Column.HasValue)
            return methods[0];

        IEnumerable<MethodDeclarationSyntax> filtered = methods;
        if (@params.Line.HasValue)
        {
            filtered = filtered.Where(m =>
                m.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == @params.Line.Value);
        }

        if (@params.Column.HasValue)
        {
            filtered = filtered.Where(m =>
                m.GetLocation().GetLineSpan().StartLinePosition.Character + 1 == @params.Column.Value);
        }

        var matches = filtered.ToList();
        if (matches.Count == 1)
            return matches[0];

        if (matches.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                @params.Line.HasValue
                    ? $"Method '{@params.MethodName}' not found at line {@params.Line}."
                    : $"Method '{@params.MethodName}' not found.");
        }

        var lines = matches
            .Select(m => m.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
            .ToList();
        throw new RefactoringException(
            ErrorCodes.SymbolAmbiguous,
            $"Multiple methods named '{@params.MethodName}' found. Provide line number. Options: {string.Join(", ", lines)}");
    }

    private async Task<List<IMethodSymbol>> GetRelatedMethodsAsync(
        IMethodSymbol method,
        bool updateOverrides,
        bool updateImplementations,
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
                    symbol, Context.Solution, cancellationToken: cancellationToken);
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
                        candidate, Context.Solution, cancellationToken: cancellationToken);
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
                            !ShareOverrideRoot(implMethod, candidate))
                        {
                            continue;
                        }

                        results.Add(ifaceMethod);
                        var otherImpls = await SymbolFinder.FindImplementationsAsync(
                            ifaceMethod,
                            Context.Solution,
                            cancellationToken: cancellationToken);
                        foreach (var other in otherImpls.OfType<IMethodSymbol>())
                            results.Add(other);
                    }
                }
            }
        }

        return results.Where(HasSourceDeclaration).ToList();
    }

    private async Task<List<DeclarationTarget>> CollectDeclarationTargetsAsync(
        IReadOnlyList<IMethodSymbol> methods,
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

                var document = Context.Solution.GetDocument(syntaxRef.SyntaxTree)
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
        CancellationToken cancellationToken)
    {
        var callSites = new List<CallSite>();
        var seen = new HashSet<(DocumentId Id, TextSpan Span)>();

        foreach (var method in methods)
        {
            var references = await SymbolFinder.FindReferencesAsync(method, Context.Solution, cancellationToken);
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
                    if (IsDeclarationName(node, location.Location.SourceSpan))
                        continue;

                    var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (invocation != null && IsInvokedMethodName(invocation, location.Location.SourceSpan))
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

                    if (IsNameOfArgument(node))
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
                originalParams,
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
        IReadOnlyList<IParameterSymbol> originalParams,
        int[] newOrder,
        bool isReducedExtension = false)
    {
        var newArgs = ReorderArguments(
            invocation.ArgumentList,
            newOrder,
            originalParams,
            isReducedExtension);
        return invocation.WithArgumentList(newArgs);
    }

    internal static ArgumentListSyntax ReorderArguments(
        ArgumentListSyntax args,
        int[] newOrder,
        IReadOnlyList<IParameterSymbol> originalParams,
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
        for (var i = 0; i < originalParams.Count; i++)
        {
            if (namedOriginal.ContainsKey(originalParams[i].Name))
                continue;
            if (i < firstExplicitOrdinal)
                continue;
            if (positionalIndex >= positionalOriginal.Count)
                continue;
            positionalByOldIndex[i] = positionalOriginal[positionalIndex++];
        }

        var leftoverPositionals = positionalOriginal.Skip(positionalIndex).ToList();

        var newArgs = new List<ArgumentSyntax>();
        if (namedOriginal.Count == 0)
        {
            foreach (var oldIndex in newOrder)
            {
                if (positionalByOldIndex.TryGetValue(oldIndex, out var positional))
                    newArgs.Add(positional);
            }
        }
        else
        {
            foreach (var oldIndex in newOrder)
            {
                var originalParam = originalParams[oldIndex];
                if (namedOriginal.TryGetValue(originalParam.Name, out var named))
                {
                    newArgs.Add(named);
                    continue;
                }

                if (positionalByOldIndex.TryGetValue(oldIndex, out var positional))
                    newArgs.Add(positional);
            }

            foreach (var leftover in namedOriginal.Values)
            {
                if (!newArgs.Contains(leftover))
                    newArgs.Add(leftover);
            }
        }

        newArgs.AddRange(leftoverPositionals);

        return SyntaxFactory.ArgumentList(ReorderPreservingSeparators(args.Arguments, newArgs))
            .WithTriviaFrom(args);
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
                    Description = $"Reorder parameters of '{@params.MethodName}' ({callSiteCount} call site(s) to update)",
                    BeforeSnippet = before.ToString(),
                    AfterSnippet = after.ToString()
                });
            }
        }

        if (pendingChanges.Count == 0)
        {
            pendingChanges.Add(new PendingChange
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Reorder parameters of '{@params.MethodName}'",
                BeforeSnippet = null,
                AfterSnippet = null
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private static bool IsDeclarationName(SyntaxNode node, TextSpan referenceSpan)
    {
        return node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Span.IntersectsWith(referenceSpan));
    }

    private static bool IsInvokedMethodName(InvocationExpressionSyntax invocation, TextSpan referenceSpan)
    {
        if (invocation.ArgumentList.Span.Contains(referenceSpan))
            return false;

        return invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Span.IntersectsWith(referenceSpan),
            MemberAccessExpressionSyntax member => member.Name.Span.IntersectsWith(referenceSpan),
            MemberBindingExpressionSyntax binding => binding.Name.Span.IntersectsWith(referenceSpan),
            _ => invocation.Expression.Span.IntersectsWith(referenceSpan)
        };
    }

    private static bool IsNameOfArgument(SyntaxNode node)
    {
        foreach (var invocation in node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is IdentifierNameSyntax identifier &&
                identifier.Identifier.Text == "nameof")
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSourceDeclaration(IMethodSymbol method) =>
        method.DeclaringSyntaxReferences.Length > 0 &&
        method.Locations.Any(l => l.IsInSource);

    private static bool ShareOverrideRoot(IMethodSymbol left, IMethodSymbol right) =>
        SymbolEqualityComparer.Default.Equals(GetOverrideRoot(left), GetOverrideRoot(right));

    private static IMethodSymbol GetOverrideRoot(IMethodSymbol method)
    {
        var current = method;
        while (current.OverriddenMethod != null)
            current = current.OverriddenMethod;
        return current;
    }

    private static bool IsParams(ParameterSyntax parameter) =>
        parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword));

    private static bool IsOptional(ParameterSyntax parameter) =>
        parameter.Default != null;

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

    private sealed record CallSite(Document Document, TextSpan Span, bool IsReducedExtension);

    private sealed class ReorderParametersRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<MethodDeclarationSyntax> _methods;
        private readonly HashSet<InvocationExpressionSyntax> _invocations;
        private readonly HashSet<TextSpan> _reducedSpans;
        private readonly IReadOnlyList<IParameterSymbol> _originalParams;
        private readonly int[] _newOrder;

        public ReorderParametersRewriter(
            IReadOnlyList<MethodDeclarationSyntax> methods,
            IReadOnlyList<InvocationExpressionSyntax> invocations,
            HashSet<TextSpan> reducedSpans,
            IReadOnlyList<IParameterSymbol> originalParams,
            int[] newOrder)
        {
            _methods = new HashSet<MethodDeclarationSyntax>(methods);
            _invocations = new HashSet<InvocationExpressionSyntax>(invocations);
            _reducedSpans = reducedSpans;
            _originalParams = originalParams;
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
            return UpdateInvocation(visited, _originalParams, _newOrder, isReduced);
        }
    }
}
