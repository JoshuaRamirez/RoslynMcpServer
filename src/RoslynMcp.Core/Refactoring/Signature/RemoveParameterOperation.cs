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
/// Removes a named parameter from a method and updates call sites, overrides,
/// and interface implementations.
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
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        if (string.IsNullOrWhiteSpace(@params.ParameterName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "parameterName is required.");

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
        RemoveParameterParams @params,
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

        var parameter = FindParameter(methodSymbol, @params.ParameterName);
        var removeIndex = parameter.Ordinal;

        var relatedMethods = await GetRelatedMethodsAsync(
            methodSymbol,
            @params.UpdateOverrides,
            @params.UpdateImplementations,
            cancellationToken);

        var namesAtIndex = relatedMethods
            .Where(m => m.Parameters.Length > removeIndex)
            .Select(m => m.Parameters[removeIndex].Name)
            .ToHashSet(StringComparer.Ordinal);

        var declarationTargets = await CollectDeclarationTargetsAsync(relatedMethods, cancellationToken);
        foreach (var target in declarationTargets)
            ValidateDocumentIsEditable(target.Document, Context.Workspace);

        var callSites = await CollectCallSitesAsync(relatedMethods, cancellationToken);
        foreach (var callSite in callSites)
            ValidateDocumentIsEditable(callSite.Document, Context.Workspace);

        var bodyUsages = await CollectBodyUsagesAsync(relatedMethods, removeIndex, cancellationToken);
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
            ValidateDocumentIsEditable(usage.Document, Context.Workspace);

        var newSolution = await ApplyChangesAsync(
            document,
            declarationTargets,
            callSites,
            bodyUsages,
            methodSymbol.Parameters.ToList(),
            removeIndex,
            namesAtIndex,
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

    internal static MethodDeclarationSyntax FindMethodDeclaration(SyntaxNode root, RemoveParameterParams @params)
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

    internal static IParameterSymbol FindParameter(IMethodSymbol method, string name)
    {
        var normalized = NormalizeIdentifier(name);
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
                        $"Method '{method.Name}' is an unsupported target for remove_parameter.");
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
                "The selected method is an unsupported target for remove_parameter.");
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

                        callSites.Add(new CallSite(document, invocation.Span));
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

    private async Task<List<BodyUsage>> CollectBodyUsagesAsync(
        IReadOnlyList<IMethodSymbol> methods,
        int parameterIndex,
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

                var document = Context.Solution.GetDocument(syntaxRef.SyntaxTree);
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

                    usages.Add(new BodyUsage(document, identifier.Span, CanReplaceWithDefault(identifier)));
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
            var invocationSpans = callSites
                .Where(c => c.Document.Id == documentId)
                .Select(c => c.Span)
                .ToHashSet();
            var usageSpans = bodyUsages
                .Where(u => u.Document.Id == documentId)
                .Select(u => u.Span)
                .ToHashSet();

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
        IReadOnlySet<string> namesAtIndex)
    {
        var newArgs = RemoveArgument(invocation.ArgumentList, removeIndex, originalParams, namesAtIndex);
        return invocation.WithArgumentList(newArgs);
    }

    internal static ArgumentListSyntax RemoveArgument(
        ArgumentListSyntax args,
        int removeIndex,
        IReadOnlyList<IParameterSymbol> originalParams,
        IReadOnlySet<string> namesAtIndex)
    {
        var originalArgs = args.Arguments.ToList();
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

        return SyntaxFactory.ArgumentList(SeparatedWithSpaces(newArgs))
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
                File = @params.SourceFile,
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
        if (IsNameOfArgument(identifier))
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

    private static string NormalizeIdentifier(string name) =>
        name.StartsWith('@') && name.Length > 1 ? name[1..] : name;

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

    private sealed record BodyUsage(Document Document, TextSpan Span, bool CanReplaceWithDefault);

    private sealed class RemoveParameterRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<MethodDeclarationSyntax> _methods;
        private readonly HashSet<InvocationExpressionSyntax> _invocations;
        private readonly HashSet<IdentifierNameSyntax> _identifiers;
        private readonly int _removeIndex;
        private readonly IReadOnlyList<IParameterSymbol> _originalParams;
        private readonly IReadOnlySet<string> _namesAtIndex;

        public RemoveParameterRewriter(
            IReadOnlyList<MethodDeclarationSyntax> methods,
            IReadOnlyList<InvocationExpressionSyntax> invocations,
            IReadOnlyList<IdentifierNameSyntax> identifiers,
            int removeIndex,
            IReadOnlyList<IParameterSymbol> originalParams,
            IReadOnlySet<string> namesAtIndex)
        {
            _methods = new HashSet<MethodDeclarationSyntax>(methods);
            _invocations = new HashSet<InvocationExpressionSyntax>(invocations);
            _identifiers = new HashSet<IdentifierNameSyntax>(identifiers);
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

            var parameters = visited.ParameterList.Parameters.ToList();
            if (_removeIndex < 0 || _removeIndex >= parameters.Count)
                return visited;

            parameters.RemoveAt(_removeIndex);
            return visited.WithParameterList(
                SyntaxFactory.ParameterList(SeparatedWithSpaces(parameters))
                    .WithTriviaFrom(visited.ParameterList));
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (!_invocations.Contains(node) && !_invocations.Any(i => i.Span == node.Span))
                return visited;

            return UpdateInvocation(visited, _originalParams, _removeIndex, _namesAtIndex);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (!_identifiers.Contains(node) && !_identifiers.Any(i => i.Span == node.Span && i.Identifier.Text == node.Identifier.Text))
                return base.VisitIdentifierName(node);

            return SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)
                .WithTriviaFrom(node);
        }
    }
}
