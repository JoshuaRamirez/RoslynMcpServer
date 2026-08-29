using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Convert;

/// <summary>
/// Converts a synchronous method to async/await pattern.
/// </summary>
public sealed class ConvertToAsyncOperation : RefactoringOperationBase<ConvertToAsyncParams>
{
    internal const string SyncCallerSkipReason =
        "Caller is not async; skipped await wrap to keep the method compiling.";

    internal const string NotInvocationSkipReason =
        "Reference is not an invocation (method group or similar); skipped await wrap.";

    /// <summary>
    /// Creates a new convert to async operation.
    /// </summary>
    public ConvertToAsyncOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ConvertToAsyncParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertToAsyncParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Find method declaration
        var methodDeclarations = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == @params.MethodName)
            .ToList();

        if (methodDeclarations.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                $"Method '{@params.MethodName}' not found.");
        }

        MethodDeclarationSyntax methodDecl;
        if (methodDeclarations.Count > 1)
        {
            if (!@params.Line.HasValue)
            {
                var lines = methodDeclarations
                    .Select(m => m.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
                    .ToList();
                throw new RefactoringException(
                    ErrorCodes.SymbolAmbiguous,
                    $"Multiple methods named '{@params.MethodName}' found. Provide line number. Options: {string.Join(", ", lines)}");
            }

            methodDecl = methodDeclarations.FirstOrDefault(m =>
                m.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == @params.Line.Value)
                ?? throw new RefactoringException(
                    ErrorCodes.MethodNotFound,
                    $"Method '{@params.MethodName}' not found at line {@params.Line}.");
        }
        else
        {
            methodDecl = methodDeclarations[0];
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken);
        if (methodSymbol == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");
        }

        // Check if already async
        if (methodDecl.Modifiers.Any(SyntaxKind.AsyncKeyword))
        {
            throw new RefactoringException(
                ErrorCodes.AlreadyAsync,
                "Method is already async.");
        }

        // Check for yield statements (iterators can't be async)
        if (methodDecl.DescendantNodes().OfType<YieldStatementSyntax>().Any())
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvertIterator,
                "Cannot convert iterator method to async.");
        }

        // Find awaitable calls in the method
        var awaitableCalls = FindAwaitableCalls(methodDecl, semanticModel, cancellationToken);

        if (awaitableCalls.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.NoAsyncCalls,
                "Method has no awaitable calls to convert.");
        }

        // Determine new method name
        var newMethodName = @params.RenameToAsync && !@params.MethodName.EndsWith("Async")
            ? @params.MethodName + "Async"
            : @params.MethodName;

        var needsRename = newMethodName != @params.MethodName;
        var callSites = needsRename || @params.UpdateCallers
            ? await CollectCallSitesAsync(
                methodSymbol,
                methodDecl,
                document,
                @params.MethodName,
                cancellationToken)
            : [];

        var plan = PlanCallerUpdates(callSites, @params.UpdateCallers);

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(
                operationId,
                @params,
                methodSymbol,
                newMethodName,
                awaitableCalls.Count,
                plan);
        }

        // Build new method
        var newMethod = methodDecl
            .WithIdentifier(SyntaxFactory.Identifier(newMethodName))
            .WithReturnType(SyntaxGenerationHelper.ToAsyncReturnType(methodSymbol.ReturnType)
                .WithTrailingTrivia(SyntaxFactory.Space))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        // Add await to awaitable calls
        var rewriter = new AsyncRewriter(awaitableCalls, semanticModel);
        newMethod = (MethodDeclarationSyntax)rewriter.Visit(newMethod)!;

        var selfCallSpans = callSites
            .Where(site => site.IsInsideConvertedMethod && site.Invocation != null)
            .Select(site => site.Invocation!.Span)
            .ToHashSet();
        if (selfCallSpans.Count > 0 && (needsRename || @params.UpdateCallers))
        {
            newMethod = (MethodDeclarationSyntax)new SelfCallRewriter(
                selfCallSpans,
                @params.MethodName,
                newMethodName,
                awaitSelfCalls: @params.UpdateCallers).Visit(newMethod)!;
        }

        var newSolution = document.Project.Solution;
        var sourceReplaced = false;
        var externalSites = callSites.Where(site => !site.IsInsideConvertedMethod).ToList();

        foreach (var group in externalSites.GroupBy(site => site.DocumentId))
        {
            var refDoc = newSolution.GetDocument(group.Key);
            if (refDoc == null) continue;

            var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
            if (refRoot == null) continue;

            var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
            if (group.Key == document.Id)
            {
                replacements[methodDecl] = newMethod;
                sourceReplaced = true;
            }

            foreach (var site in group)
            {
                var replacement = BuildCallSiteReplacement(site, needsRename, @params.UpdateCallers, newMethodName);
                if (replacement != null)
                    replacements[replacement.Original] = replacement.Replacement;
            }

            if (replacements.Count == 0) continue;

            var newRefRoot = refRoot.ReplaceNodes(replacements.Keys, (old, _) => replacements[old]);
            newSolution = refDoc.WithSyntaxRoot(newRefRoot).Project.Solution;
        }

        if (!sourceReplaced)
        {
            var currentDoc = newSolution.GetDocument(document.Id) ?? document;
            var currentRoot = await currentDoc.GetSyntaxRootAsync(cancellationToken) ?? root;
            var newRoot = currentRoot.ReplaceNode(methodDecl, newMethod);
            newSolution = currentDoc.WithSyntaxRoot(newRoot).Project.Solution;
        }

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
            Symbol = new Contracts.Models.SymbolInfo
            {
                Name = newMethodName,
                FullyQualifiedName = $"{methodSymbol.ContainingType.ToDisplayString()}.{newMethodName}",
                Kind = Contracts.Enums.SymbolKind.Method
            },
            ReferencesUpdated = awaitableCalls.Count,
            CallersUpdated = plan.Updated.Count,
            CallersSkipped = plan.Skipped.Count == 0 ? null : plan.Skipped
        };
    }

    private static async Task<List<CallSite>> CollectCallSitesAsync(
        IMethodSymbol methodSymbol,
        MethodDeclarationSyntax methodDecl,
        Document document,
        string methodName,
        CancellationToken cancellationToken)
    {
        var solution = document.Project.Solution;
        var references = await SymbolFinder.FindReferencesAsync(
            methodSymbol,
            solution,
            cancellationToken);

        var declarationNameSpan = methodDecl.Identifier.Span;
        var callSites = new List<CallSite>();

        foreach (var location in references.SelectMany(reference => reference.Locations))
        {
            var refDoc = solution.GetDocument(location.Document.Id);
            if (refDoc == null) continue;

            var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
            if (refRoot == null) continue;

            var node = refRoot.FindNode(location.Location.SourceSpan);
            var name = FindNameNode(node, methodName);
            if (name == null) continue;

            var isDeclarationName = location.Document.Id == document.Id
                && declarationNameSpan.Contains(location.Location.SourceSpan);
            if (isDeclarationName) continue;

            var invocation = GetContainingInvocation(name);
            var (isAsync, enclosingName) = GetEnclosingCallable(name);
            var isInsideConvertedMethod = location.Document.Id == document.Id
                && methodDecl.Span.Contains(name.Span);

            callSites.Add(new CallSite(
                location.Document.Id,
                refDoc.FilePath ?? location.Document.Name,
                name,
                invocation,
                isInsideConvertedMethod,
                isAsync,
                invocation != null && IsAlreadyAwaited(invocation),
                enclosingName));
        }

        return callSites;
    }

    internal static CallerUpdatePlan PlanCallerUpdates(IReadOnlyList<CallSite> callSites, bool updateCallers)
    {
        var updated = new List<CallSite>();
        var skipped = new List<SkippedCaller>();

        if (!updateCallers)
            return new CallerUpdatePlan(updated, skipped);

        foreach (var site in callSites)
        {
            if (site.Invocation == null)
            {
                skipped.Add(new SkippedCaller
                {
                    Caller = site.EnclosingName,
                    Reason = NotInvocationSkipReason
                });
                continue;
            }

            if (site.IsAlreadyAwaited)
                continue;

            // The converted method itself becomes async, so self-calls can legally await.
            if (site.IsAsyncContext || site.IsInsideConvertedMethod)
            {
                updated.Add(site);
                continue;
            }

            skipped.Add(new SkippedCaller
            {
                Caller = site.EnclosingName,
                Reason = SyncCallerSkipReason
            });
        }

        return new CallerUpdatePlan(updated, skipped);
    }

    private static NodeReplacement? BuildCallSiteReplacement(
        CallSite site,
        bool needsRename,
        bool updateCallers,
        string newMethodName)
    {
        var willAwait = updateCallers
            && site.Invocation != null
            && !site.IsAlreadyAwaited
            && (site.IsAsyncContext || site.IsInsideConvertedMethod);

        if (willAwait && site.Invocation != null)
        {
            var invocation = site.Invocation;
            if (needsRename)
            {
                var renamed = WithMethodName(site.Name, newMethodName);
                invocation = invocation.ReplaceNode(site.Name, renamed);
            }

            return new NodeReplacement(site.Invocation, WrapWithAwait(invocation));
        }

        if (needsRename)
            return new NodeReplacement(site.Name, WithMethodName(site.Name, newMethodName));

        return null;
    }

    internal static SimpleNameSyntax? FindNameNode(SyntaxNode node, string methodName)
    {
        if (node is SimpleNameSyntax simple && simple.Identifier.Text == methodName)
            return simple;

        return node.DescendantNodesAndSelf()
            .OfType<SimpleNameSyntax>()
            .FirstOrDefault(name => name.Identifier.Text == methodName);
    }

    internal static InvocationExpressionSyntax? GetContainingInvocation(SimpleNameSyntax name)
    {
        if (name.Parent is InvocationExpressionSyntax direct && direct.Expression == name)
            return direct;

        if (name.Parent is MemberAccessExpressionSyntax member
            && member.Name == name
            && member.Parent is InvocationExpressionSyntax memberInvocation
            && memberInvocation.Expression == member)
        {
            return memberInvocation;
        }

        if (name.Parent is MemberBindingExpressionSyntax binding
            && binding.Name == name
            && binding.Parent is ConditionalAccessExpressionSyntax conditional
            && conditional.WhenNotNull is InvocationExpressionSyntax conditionalInvocation)
        {
            return conditionalInvocation;
        }

        return null;
    }

    internal static (bool IsAsync, string Name) GetEnclosingCallable(SyntaxNode node)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            switch (current)
            {
                case MethodDeclarationSyntax method:
                    return (method.Modifiers.Any(SyntaxKind.AsyncKeyword), method.Identifier.Text);
                case LocalFunctionStatementSyntax local:
                    return (local.Modifiers.Any(SyntaxKind.AsyncKeyword), local.Identifier.Text);
                case ParenthesizedLambdaExpressionSyntax paren:
                    return (paren.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword), "(lambda)");
                case SimpleLambdaExpressionSyntax simple:
                    return (simple.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword), "(lambda)");
                case AnonymousMethodExpressionSyntax anon:
                    return (anon.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword), "(anonymous method)");
                case ConstructorDeclarationSyntax ctor:
                    return (false, ctor.Identifier.Text);
                case DestructorDeclarationSyntax:
                    return (false, "destructor");
                case OperatorDeclarationSyntax op:
                    return (false, op.OperatorToken.Text);
                case ConversionOperatorDeclarationSyntax:
                    return (false, "conversion operator");
                case AccessorDeclarationSyntax accessor:
                    return (false, accessor.Keyword.Text);
            }
        }

        return (false, "(unknown)");
    }

    internal static bool IsAlreadyAwaited(ExpressionSyntax expression)
    {
        var parent = expression.Parent;
        while (parent is ParenthesizedExpressionSyntax paren)
            parent = paren.Parent;

        return parent is AwaitExpressionSyntax;
    }

    internal static bool NeedsParentheses(ExpressionSyntax expression)
    {
        return expression.Parent is MemberAccessExpressionSyntax
            or ConditionalAccessExpressionSyntax
            or ElementAccessExpressionSyntax
            or InvocationExpressionSyntax
            or PostfixUnaryExpressionSyntax;
    }

    internal static SimpleNameSyntax WithMethodName(SimpleNameSyntax name, string newName)
    {
        return name.WithIdentifier(SyntaxFactory.Identifier(newName).WithTriviaFrom(name.Identifier));
    }

    internal static ExpressionSyntax WrapWithAwait(ExpressionSyntax expression)
    {
        var awaitExpression = SyntaxFactory.AwaitExpression(
            SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(SyntaxFactory.Space),
            expression.WithoutLeadingTrivia());

        if (NeedsParentheses(expression))
        {
            return SyntaxFactory.ParenthesizedExpression(awaitExpression.WithoutTrivia())
                .WithTriviaFrom(expression);
        }

        return awaitExpression.WithLeadingTrivia(expression.GetLeadingTrivia());
    }

    internal static string DescribeCallerUpdates(bool updateCallers, CallerUpdatePlan plan)
    {
        if (!updateCallers)
            return "Callers will not be updated to await the converted method.";

        if (plan.Updated.Count == 0 && plan.Skipped.Count == 0)
            return "No callers will be updated to await the converted method.";

        var parts = new List<string>();
        if (plan.Updated.Count == 1)
            parts.Add("Update 1 caller to await the converted method.");
        else if (plan.Updated.Count > 1)
            parts.Add($"Update {plan.Updated.Count} callers to await the converted method.");

        if (plan.Skipped.Count == 1)
            parts.Add("Skip 1 caller that cannot legally await.");
        else if (plan.Skipped.Count > 1)
            parts.Add($"Skip {plan.Skipped.Count} callers that cannot legally await.");

        if (parts.Count == 0)
            return "No callers will be updated to await the converted method.";

        return string.Join(" ", parts);
    }

    private static List<InvocationExpressionSyntax> FindAwaitableCalls(
        MethodDeclarationSyntax method,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var awaitableCalls = new List<InvocationExpressionSyntax>();

        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
            if (methodInfo.Symbol is not IMethodSymbol calledMethod) continue;

            // Check if return type is awaitable (Task, Task<T>, ValueTask, ValueTask<T>)
            var returnType = calledMethod.ReturnType;
            if (IsAwaitableType(returnType))
            {
                awaitableCalls.Add(invocation);
            }
        }

        return awaitableCalls;
    }

    private static bool IsAwaitableType(ITypeSymbol type)
    {
        var typeName = type.ToDisplayString();
        return typeName.StartsWith("System.Threading.Tasks.Task") ||
               typeName.StartsWith("System.Threading.Tasks.ValueTask") ||
               typeName.StartsWith("Task<") ||
               typeName.StartsWith("ValueTask<") ||
               typeName == "Task" ||
               typeName == "ValueTask";
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ConvertToAsyncParams @params,
        IMethodSymbol method,
        string newMethodName,
        int awaitableCallCount,
        CallerUpdatePlan plan)
    {
        var oldSig = $"{method.ReturnType.ToDisplayString()} {@params.MethodName}(...)";
        var newReturnType = method.ReturnsVoid ? "Task" : $"Task<{method.ReturnType.ToDisplayString()}>";
        var newSig = $"async {newReturnType} {newMethodName}(...)";
        var callerDescription = DescribeCallerUpdates(@params.UpdateCallers, plan);

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Convert '{@params.MethodName}' to async ({awaitableCallCount} await expressions added)",
                BeforeSnippet = oldSig,
                AfterSnippet = newSig
            },
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = callerDescription
            }
        };

        return new RefactoringResult
        {
            Success = true,
            OperationId = operationId,
            Preview = true,
            PendingChanges = pendingChanges,
            CallersUpdated = plan.Updated.Count,
            CallersSkipped = plan.Skipped.Count == 0 ? null : plan.Skipped
        };
    }

    internal sealed record CallSite(
        DocumentId DocumentId,
        string FilePath,
        SimpleNameSyntax Name,
        InvocationExpressionSyntax? Invocation,
        bool IsInsideConvertedMethod,
        bool IsAsyncContext,
        bool IsAlreadyAwaited,
        string EnclosingName);

    internal sealed record CallerUpdatePlan(
        IReadOnlyList<CallSite> Updated,
        IReadOnlyList<SkippedCaller> Skipped);

    private sealed record NodeReplacement(SyntaxNode Original, SyntaxNode Replacement);

    private sealed class AsyncRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<InvocationExpressionSyntax> _awaitableCalls;
        private readonly SemanticModel _semanticModel;

        public AsyncRewriter(List<InvocationExpressionSyntax> awaitableCalls, SemanticModel semanticModel)
        {
            _awaitableCalls = new HashSet<InvocationExpressionSyntax>(awaitableCalls);
            _semanticModel = semanticModel;
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

            // Check if this invocation should be awaited
            // We need to find the original node in our set
            if (_awaitableCalls.Any(c => c.Span == node.Span))
            {
                // Wrap in await expression
                return SyntaxFactory.AwaitExpression(visited)
                    .WithTriviaFrom(visited);
            }

            return visited;
        }
    }

    private sealed class SelfCallRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<TextSpan> _selfCallSpans;
        private readonly string _oldName;
        private readonly string _newName;
        private readonly bool _awaitSelfCalls;

        public SelfCallRewriter(
            HashSet<TextSpan> selfCallSpans,
            string oldName,
            string newName,
            bool awaitSelfCalls)
        {
            _selfCallSpans = selfCallSpans;
            _oldName = oldName;
            _newName = newName;
            _awaitSelfCalls = awaitSelfCalls;
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (!_selfCallSpans.Contains(node.Span))
                return visited;

            if (_oldName != _newName)
            {
                var name = FindNameNode(visited, _oldName);
                if (name != null)
                    visited = visited.ReplaceNode(name, WithMethodName(name, _newName));
            }

            if (_awaitSelfCalls && !IsAlreadyAwaited(node))
                return WrapWithAwait(visited);

            return visited;
        }
    }
}
