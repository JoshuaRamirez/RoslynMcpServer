using System.Text.RegularExpressions;
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

namespace RoslynMcp.Core.Refactoring.Inline;

/// <summary>
/// Inlines a method by replacing call sites with the method body and optionally removing the method.
/// </summary>
public sealed class InlineMethodOperation : RefactoringOperationBase<InlineMethodParams>
{
    private static readonly Regex IdentifierPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Creates a new inline method operation.
    /// </summary>
    public InlineMethodOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(InlineMethodParams @params)
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

        if (!IdentifierPattern.IsMatch(@params.MethodName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"'{@params.MethodName}' is not a valid method name.");

        if (@params.Line.HasValue && @params.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (@params.Column.HasValue && @params.Column < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (@params.CallSiteLocation != null)
        {
            if (string.IsNullOrWhiteSpace(@params.CallSiteLocation.File))
                throw new RefactoringException(ErrorCodes.MissingRequiredParam, "callSiteLocation.file is required.");

            if (!PathResolver.IsAbsolutePath(@params.CallSiteLocation.File))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "callSiteLocation.file must be an absolute path.");

            if (!PathResolver.IsValidCSharpFilePath(@params.CallSiteLocation.File))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "callSiteLocation.file must be a .cs file.");

            if (@params.CallSiteLocation.Line < 1)
                throw new RefactoringException(ErrorCodes.InvalidLineNumber, "callSiteLocation.line must be >= 1.");

            if (@params.CallSiteLocation.Column < 1)
                throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "callSiteLocation.column must be >= 1.");
        }
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        InlineMethodParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        var methodSyntax = FindMethodDeclaration(root, @params);
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodSyntax, cancellationToken) as IMethodSymbol
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");

        ValidateInlineable(methodSyntax, methodSymbol, semanticModel, cancellationToken);

        var callSites = await FindCallSitesAsync(methodSymbol, methodSyntax, document, @params, cancellationToken);
        if (callSites.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.NoCallSitesFound,
                $"No call sites found for method '{@params.MethodName}'.");
        }

        var removeMethod = @params.RemoveMethod && @params.CallSiteLocation == null;
        var newSolution = await ApplyInliningAsync(
            document,
            methodSyntax,
            methodSymbol,
            callSites,
            removeMethod,
            cancellationToken);

        if (@params.Preview)
        {
            return await CreatePreviewResultAsync(
                operationId,
                @params,
                document,
                newSolution,
                callSites.Count,
                removeMethod,
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

    private static MethodDeclarationSyntax FindMethodDeclaration(SyntaxNode root, InlineMethodParams @params)
    {
        var candidates = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == @params.MethodName)
            .ToList();

        if (candidates.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                $"Method '{@params.MethodName}' not found.");
        }

        if (candidates.Count == 1 && !@params.Line.HasValue)
        {
            return candidates[0];
        }

        if (@params.Line.HasValue)
        {
            var match = candidates.FirstOrDefault(m =>
            {
                var span = m.Identifier.GetLocation().GetLineSpan();
                var line = span.StartLinePosition.Line + 1;
                if (line != @params.Line.Value)
                    return false;

                if (@params.Column.HasValue)
                {
                    var column = span.StartLinePosition.Character + 1;
                    return column == @params.Column.Value;
                }

                return true;
            });

            if (match == null)
            {
                throw new RefactoringException(
                    ErrorCodes.MethodNotFound,
                    $"Method '{@params.MethodName}' not found at line {@params.Line}.");
            }

            return match;
        }

        var lines = candidates
            .Select(m => m.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
            .ToList();
        throw new RefactoringException(
            ErrorCodes.SymbolAmbiguous,
            $"Multiple methods named '{@params.MethodName}' found. Provide line number. Options: {string.Join(", ", lines)}");
    }

    private static void ValidateInlineable(
        MethodDeclarationSyntax methodSyntax,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (methodSymbol.IsAbstract || methodSymbol.IsExtern ||
            (methodSyntax.Body == null && methodSyntax.ExpressionBody == null))
        {
            throw new RefactoringException(
                ErrorCodes.MethodHasNoBody,
                $"Method '{methodSymbol.Name}' has no body and cannot be inlined.");
        }

        if (methodSymbol.IsVirtual || methodSymbol.IsOverride || methodSymbol.IsAbstract)
        {
            throw new RefactoringException(
                ErrorCodes.MethodIsVirtual,
                $"Virtual, override, or abstract method '{methodSymbol.Name}' cannot be inlined.");
        }

        if (IsInterfaceImplementation(methodSymbol))
        {
            throw new RefactoringException(
                ErrorCodes.MethodIsVirtual,
                $"Method '{methodSymbol.Name}' implements an interface and cannot be inlined.");
        }

        if (IsRecursive(methodSyntax, methodSymbol, semanticModel, cancellationToken))
        {
            throw new RefactoringException(
                ErrorCodes.MethodIsRecursive,
                $"Recursive method '{methodSymbol.Name}' cannot be inlined.");
        }
    }

    private static bool IsInterfaceImplementation(IMethodSymbol method)
    {
        if (method.ExplicitInterfaceImplementations.Length > 0)
            return true;

        var containingType = method.ContainingType;
        if (containingType == null)
            return false;

        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
            {
                var implementation = containingType.FindImplementationForInterfaceMember(member);
                if (SymbolEqualityComparer.Default.Equals(implementation, method))
                    return true;
            }
        }

        return false;
    }

    private static bool IsRecursive(
        MethodDeclarationSyntax methodSyntax,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var invocation in methodSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var invoked = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
            if (invoked == null)
                continue;

            if (SymbolEqualityComparer.Default.Equals(invoked.OriginalDefinition, methodSymbol.OriginalDefinition))
                return true;
        }

        return false;
    }

    private async Task<List<CallSite>> FindCallSitesAsync(
        IMethodSymbol methodSymbol,
        MethodDeclarationSyntax methodSyntax,
        Document declaringDocument,
        InlineMethodParams @params,
        CancellationToken cancellationToken)
    {
        var references = await SymbolFinder.FindReferencesAsync(
            methodSymbol,
            Context.Solution,
            cancellationToken);

        var callSites = new List<CallSite>();

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                if (location.Location.Kind != LocationKind.SourceFile)
                    continue;

                var document = location.Document;
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root == null)
                    continue;

                var node = root.FindNode(location.Location.SourceSpan);
                var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (invocation == null)
                    continue;

                if (document.Id == declaringDocument.Id && methodSyntax.Span.Contains(invocation.Span))
                    continue;

                callSites.Add(new CallSite(document, invocation));
            }
        }

        if (@params.CallSiteLocation != null)
        {
            var targetPath = PathResolver.NormalizePath(@params.CallSiteLocation.File);
            callSites = callSites
                .Where(site =>
                {
                    if (PathResolver.NormalizePath(site.Document.FilePath ?? "") != targetPath)
                        return false;

                    var span = site.Invocation.GetLocation().GetLineSpan();
                    var startLine = span.StartLinePosition.Line + 1;
                    var startColumn = span.StartLinePosition.Character + 1;
                    var endLine = span.EndLinePosition.Line + 1;
                    var endColumn = span.EndLinePosition.Character + 1;
                    if (@params.CallSiteLocation.Line < startLine || @params.CallSiteLocation.Line > endLine)
                        return false;

                    if (startLine == endLine)
                    {
                        return @params.CallSiteLocation.Column >= startColumn &&
                               @params.CallSiteLocation.Column <= endColumn;
                    }

                    if (@params.CallSiteLocation.Line == startLine)
                        return @params.CallSiteLocation.Column >= startColumn;
                    if (@params.CallSiteLocation.Line == endLine)
                        return @params.CallSiteLocation.Column <= endColumn;
                    return true;
                })
                .ToList();
        }

        return callSites;
    }

    private static async Task<Solution> ApplyInliningAsync(
        Document declaringDocument,
        MethodDeclarationSyntax methodSyntax,
        IMethodSymbol methodSymbol,
        IReadOnlyList<CallSite> callSites,
        bool removeMethod,
        CancellationToken cancellationToken)
    {
        var solution = declaringDocument.Project.Solution;
        var sitesByDocument = callSites.GroupBy(s => s.Document.Id);

        foreach (var group in sitesByDocument)
        {
            var document = solution.GetDocument(group.Key)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Document disappeared from solution.");
            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

            var invocations = group.Select(s => s.Invocation).ToList();
            var rewriter = new InlineCallSiteRewriter(invocations, methodSyntax, methodSymbol);
            var newRoot = rewriter.Visit(root)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Failed to rewrite call sites.");

            if (removeMethod && document.Id == declaringDocument.Id)
            {
                newRoot = RemoveMethodDeclaration(newRoot, methodSyntax.Identifier.Text, methodSyntax.Span);
            }

            solution = document.WithSyntaxRoot(newRoot).Project.Solution;
        }

        if (removeMethod && callSites.All(s => s.Document.Id != declaringDocument.Id))
        {
            var document = solution.GetDocument(declaringDocument.Id)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Declaring document disappeared from solution.");
            var root = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
            var newRoot = RemoveMethodDeclaration(root, methodSyntax.Identifier.Text, methodSyntax.Span);
            solution = document.WithSyntaxRoot(newRoot).Project.Solution;
        }

        return solution;
    }

    private static SyntaxNode RemoveMethodDeclaration(SyntaxNode root, string methodName, TextSpan originalSpan)
    {
        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName && m.Span.Start == originalSpan.Start);

        if (method == null)
        {
            method = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == methodName);
        }

        if (method == null)
            return root;

        return root.RemoveNode(method, SyntaxRemoveOptions.KeepDirectives)
            ?? root;
    }

    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        InlineMethodParams @params,
        Document originalDocument,
        Solution newSolution,
        int callSiteCount,
        bool removeMethod,
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
                    Description = $"Inline method '{@params.MethodName}' ({callSiteCount} call site(s)" +
                                  (removeMethod ? ", method removed" : "") + ")",
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
                Description = $"Inline method '{@params.MethodName}' ({callSiteCount} call site(s))",
                BeforeSnippet = null,
                AfterSnippet = null
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private sealed record CallSite(Document Document, InvocationExpressionSyntax Invocation);

    private sealed class InlineCallSiteRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<InvocationExpressionSyntax> _targets;
        private readonly MethodDeclarationSyntax _methodSyntax;
        private readonly IMethodSymbol _methodSymbol;

        public InlineCallSiteRewriter(
            IReadOnlyList<InvocationExpressionSyntax> targets,
            MethodDeclarationSyntax methodSyntax,
            IMethodSymbol methodSymbol)
        {
            _targets = new HashSet<InvocationExpressionSyntax>(targets);
            _methodSyntax = methodSyntax;
            _methodSymbol = methodSymbol;
        }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var rewritten = new List<StatementSyntax>();
            var changed = false;

            foreach (var statement in node.Statements)
            {
                if (statement is ExpressionStatementSyntax expressionStatement &&
                    TryGetTargetInvocation(expressionStatement.Expression, out var invocation))
                {
                    var inlined = CreateInlinedStatements(invocation, expressionStatement);
                    rewritten.AddRange(inlined);
                    changed = true;
                    continue;
                }

                var visited = (StatementSyntax?)base.Visit(statement);
                if (visited != null)
                    rewritten.Add(visited);
                if (!ReferenceEquals(visited, statement))
                    changed = true;
            }

            return changed ? node.WithStatements(SyntaxFactory.List(rewritten)) : node;
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (!_targets.Contains(node))
                return base.VisitInvocationExpression(node);

            // Statement-level void calls are handled in VisitBlock.
            if (node.Parent is ExpressionStatementSyntax)
                return node;

            var expression = CreateInlinedExpression(node);
            return ParenthesizeIfNeeded(expression, node.Parent).WithTriviaFrom(node);
        }

        private bool TryGetTargetInvocation(ExpressionSyntax expression, out InvocationExpressionSyntax invocation)
        {
            if (expression is InvocationExpressionSyntax direct && _targets.Contains(direct))
            {
                invocation = direct;
                return true;
            }

            invocation = null!;
            return false;
        }

        private IReadOnlyList<StatementSyntax> CreateInlinedStatements(
            InvocationExpressionSyntax invocation,
            ExpressionStatementSyntax originalStatement)
        {
            var parameterMap = BuildParameterMap(invocation);
            var statements = TransformToStatements(parameterMap);

            if (statements.Count == 0)
                return Array.Empty<StatementSyntax>();

            var result = new List<StatementSyntax>(statements.Count);
            for (var i = 0; i < statements.Count; i++)
            {
                var statement = statements[i].NormalizeWhitespace();
                if (i == 0)
                {
                    statement = statement
                        .WithLeadingTrivia(originalStatement.GetLeadingTrivia())
                        .WithTrailingTrivia(originalStatement.GetTrailingTrivia());
                }
                else
                {
                    statement = statement.WithLeadingTrivia(originalStatement.GetLeadingTrivia());
                }

                result.Add(statement);
            }

            return result;
        }

        private ExpressionSyntax CreateInlinedExpression(InvocationExpressionSyntax invocation)
        {
            var parameterMap = BuildParameterMap(invocation);
            return TransformToExpression(parameterMap);
        }

        private Dictionary<string, ExpressionSyntax> BuildParameterMap(InvocationExpressionSyntax invocation)
        {
            var map = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
            var arguments = invocation.ArgumentList.Arguments;

            for (var i = 0; i < _methodSymbol.Parameters.Length; i++)
            {
                var parameter = _methodSymbol.Parameters[i];
                ArgumentSyntax? argument = null;

                foreach (var candidate in arguments)
                {
                    if (candidate.NameColon != null &&
                        candidate.NameColon.Name.Identifier.Text == parameter.Name)
                    {
                        argument = candidate;
                        break;
                    }
                }

                if (argument == null && i < arguments.Count && arguments[i].NameColon == null)
                    argument = arguments[i];

                if (argument != null)
                {
                    map[parameter.Name] = argument.Expression;
                    continue;
                }

                var defaultSyntax = _methodSyntax.ParameterList.Parameters
                    .FirstOrDefault(p => p.Identifier.Text == parameter.Name)?
                    .Default?.Value;

                if (defaultSyntax != null)
                    map[parameter.Name] = defaultSyntax;
            }

            return map;
        }

        private IReadOnlyList<StatementSyntax> TransformToStatements(
            IReadOnlyDictionary<string, ExpressionSyntax> parameterMap)
        {
            if (_methodSyntax.ExpressionBody != null)
            {
                var expression = Substitute(_methodSyntax.ExpressionBody.Expression, parameterMap);
                if (_methodSymbol.ReturnsVoid)
                    return new[] { SyntaxFactory.ExpressionStatement(expression) };

                return new[] { SyntaxFactory.ExpressionStatement(expression) };
            }

            var body = _methodSyntax.Body
                ?? throw new RefactoringException(ErrorCodes.MethodHasNoBody, "Method has no body.");

            var statements = new List<StatementSyntax>();
            foreach (var statement in body.Statements)
            {
                if (statement is ReturnStatementSyntax returnStatement && returnStatement.Expression == null)
                    continue;

                var substituted = Substitute(statement, parameterMap);
                if (substituted is ReturnStatementSyntax substitutedReturn && substitutedReturn.Expression != null)
                {
                    statements.Add(SyntaxFactory.ExpressionStatement(substitutedReturn.Expression)
                        .WithTriviaFrom(substitutedReturn));
                }
                else
                {
                    statements.Add(substituted);
                }
            }

            return statements;
        }

        private ExpressionSyntax TransformToExpression(
            IReadOnlyDictionary<string, ExpressionSyntax> parameterMap)
        {
            if (_methodSyntax.ExpressionBody != null)
                return Substitute(_methodSyntax.ExpressionBody.Expression, parameterMap);

            var body = _methodSyntax.Body
                ?? throw new RefactoringException(ErrorCodes.MethodHasNoBody, "Method has no body.");

            var returns = body.Statements.OfType<ReturnStatementSyntax>()
                .Where(r => r.Expression != null)
                .ToList();

            if (body.Statements.Count == 1 &&
                body.Statements[0] is ReturnStatementSyntax singleReturn &&
                singleReturn.Expression != null)
            {
                return Substitute(singleReturn.Expression, parameterMap);
            }

            if (returns.Count == 1 && body.Statements[^1] == returns[0])
            {
                return Substitute(returns[0].Expression!, parameterMap);
            }

            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                $"Cannot inline method '{_methodSymbol.Name}' as an expression; it does not have a single return.");
        }

        private static T Substitute<T>(T node, IReadOnlyDictionary<string, ExpressionSyntax> parameterMap)
            where T : SyntaxNode
        {
            var rewriter = new ParameterSubstituter(parameterMap);
            return (T)rewriter.Visit(node)!;
        }

        private static ExpressionSyntax ParenthesizeIfNeeded(ExpressionSyntax expression, SyntaxNode? parent)
        {
            if (expression is IdentifierNameSyntax or LiteralExpressionSyntax or ParenthesizedExpressionSyntax
                or InvocationExpressionSyntax or MemberAccessExpressionSyntax)
            {
                return expression;
            }

            if (parent is BinaryExpressionSyntax or MemberAccessExpressionSyntax or ConditionalExpressionSyntax
                or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax or ElementAccessExpressionSyntax
                or AssignmentExpressionSyntax or CastExpressionSyntax)
            {
                return SyntaxFactory.ParenthesizedExpression(expression);
            }

            return expression;
        }
    }

    private sealed class ParameterSubstituter : CSharpSyntaxRewriter
    {
        private readonly IReadOnlyDictionary<string, ExpressionSyntax> _parameterMap;

        public ParameterSubstituter(IReadOnlyDictionary<string, ExpressionSyntax> parameterMap)
        {
            _parameterMap = parameterMap;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (!_parameterMap.TryGetValue(node.Identifier.Text, out var replacement))
                return base.VisitIdentifierName(node);

            var needsParens = node.Parent is BinaryExpressionSyntax or MemberAccessExpressionSyntax
                or ConditionalExpressionSyntax or PrefixUnaryExpressionSyntax
                or ElementAccessExpressionSyntax or AssignmentExpressionSyntax;

            if (needsParens && replacement is BinaryExpressionSyntax or ConditionalExpressionSyntax
                or AssignmentExpressionSyntax or PrefixUnaryExpressionSyntax)
            {
                return SyntaxFactory.ParenthesizedExpression(replacement.WithoutTrivia())
                    .WithTriviaFrom(node);
            }

            return replacement.WithoutTrivia().WithTriviaFrom(node);
        }
    }
}
