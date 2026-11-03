using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Transactions;
using RoslynMcp.Core.Coordinates;

namespace RoslynMcp.Core.Refactoring;

/// <summary>
/// Layer 4 semantic <see cref="IOperation"/>-guided syntax pattern matcher and rewriter across a <c>DocumentScope</c>.
/// </summary>
public static class IOperationSemanticRewriter
{
    /// <summary>
    /// Executes a <see cref="SemanticPatternRewriteOperation"/> across all matching documents in <paramref name="solution"/>.
    /// </summary>
    /// <param name="solution">Input solution snapshot.</param>
    /// <param name="operation">Semantic pattern rewrite operation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated solution snapshot.</returns>
    public static async Task<Solution> ExecuteAsync(
        Solution solution,
        SemanticPatternRewriteOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(operation);

        var effectiveScope = operation.Target?.Scope ?? operation.Scope;
        var documents = TargetCoordinateResolver.ResolveDocuments(solution, effectiveScope);

        TextSpan? targetSpanFilter = null;
        DocumentId? targetDocFilter = null;
        if (operation.Target is not null)
        {
            var resolved = await TargetCoordinateResolver.ResolveAsync(solution, operation.Target, cancellationToken).ConfigureAwait(false);
            if (resolved.Count == 1)
            {
                targetDocFilter = resolved[0].DocumentId;
                targetSpanFilter = resolved[0].Span;
            }
        }

        var updatedSolution = solution;
        foreach (var document in documents)
        {
            if (targetDocFilter is not null && document.Id != targetDocFilter)
            {
                continue;
            }

            var currentDoc = updatedSolution.GetDocument(document.Id);
            if (currentDoc is null)
            {
                continue;
            }

            var root = await currentDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var semanticModel = await currentDoc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || semanticModel is null)
            {
                continue;
            }

            var rewriter = new SemanticPatternSyntaxRewriter(
                semanticModel,
                operation.Pattern,
                targetSpanFilter,
                cancellationToken);

            var newRoot = rewriter.Visit(root);
            if (newRoot is not null && !ReferenceEquals(newRoot, root) && newRoot.ToFullString() != root.ToFullString())
            {
                updatedSolution = updatedSolution.WithDocumentSyntaxRoot(document.Id, newRoot.NormalizeWhitespace());
            }
        }

        return updatedSolution;
    }

    private sealed class SemanticPatternSyntaxRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _semanticModel;
        private readonly SemanticPatternKind _pattern;
        private readonly TextSpan? _spanFilter;
        private readonly CancellationToken _cancellationToken;

        public SemanticPatternSyntaxRewriter(
            SemanticModel semanticModel,
            SemanticPatternKind pattern,
            TextSpan? spanFilter,
            CancellationToken cancellationToken)
        {
            _semanticModel = semanticModel;
            _pattern = pattern;
            _spanFilter = spanFilter;
            _cancellationToken = cancellationToken;
        }

        private bool IsWithinTargetSpan(SyntaxNode node) =>
            _spanFilter is null || _spanFilter.Value.IntersectsWith(node.Span);

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            if (_pattern == SemanticPatternKind.ConvertForeachToLinq && IsWithinTargetSpan(node))
            {
                var op = _semanticModel.GetOperation(node, _cancellationToken) as IForEachLoopOperation;
                if (op is not null && TryRewriteForeachToLinq(node, out var replacement))
                {
                    return replacement.WithTriviaFrom(node);
                }
            }

            return base.VisitForEachStatement(node);
        }

        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            if (!IsWithinTargetSpan(node))
            {
                return base.VisitIfStatement(node);
            }

            if (_pattern == SemanticPatternKind.ConvertIfToSwitchExpression)
            {
                if (_semanticModel.GetOperation(node, _cancellationToken) is IConditionalOperation &&
                    TryRewriteIfToSwitchExpression(node, out var switchReturn))
                {
                    return switchReturn.WithTriviaFrom(node);
                }
            }
            else if (_pattern == SemanticPatternKind.ConvertToPatternMatching)
            {
                if (_semanticModel.GetOperation(node.Condition, _cancellationToken) is IIsTypeOperation &&
                    TryRewriteTypeCheckToPatternMatching(node, out var patternIf))
                {
                    return patternIf.WithTriviaFrom(node);
                }
            }
            else if (_pattern == SemanticPatternKind.InvertIf)
            {
                if (_semanticModel.GetOperation(node, _cancellationToken) is IConditionalOperation { WhenFalse: not null } &&
                    node.Else is not null)
                {
                    var invertedCondition = NegateCondition(node.Condition);
                    var trueBranch = node.Statement;
                    var falseBranch = node.Else.Statement;
                    return node
                        .WithCondition(invertedCondition)
                        .WithStatement(falseBranch)
                        .WithElse(node.Else.WithStatement(trueBranch));
                }
            }

            return base.VisitIfStatement(node);
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (_pattern == SemanticPatternKind.ConvertToInterpolatedString && IsWithinTargetSpan(node))
            {
                if (_semanticModel.GetOperation(node, _cancellationToken) is IInvocationOperation invocation &&
                    invocation.TargetMethod.Name == "Format" &&
                    invocation.TargetMethod.ContainingType?.SpecialType == SpecialType.System_String &&
                    TryRewriteStringFormat(node, out var interpolated))
                {
                    return interpolated.WithTriviaFrom(node);
                }
            }

            return base.VisitInvocationExpression(node);
        }

        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            if (_pattern == SemanticPatternKind.ConvertToInterpolatedString &&
                IsWithinTargetSpan(node) &&
                node.IsKind(SyntaxKind.AddExpression) &&
                node.Parent is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression })
            {
                if (_semanticModel.GetOperation(node, _cancellationToken) is IBinaryOperation
                    {
                        OperatorKind: BinaryOperatorKind.Add,
                        Type.SpecialType: SpecialType.System_String
                    } &&
                    TryRewriteStringConcatenation(node, out var interpolated))
                {
                    return interpolated.WithTriviaFrom(node);
                }
            }

            return base.VisitBinaryExpression(node);
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            if (!IsWithinTargetSpan(node))
            {
                return base.VisitMethodDeclaration(node);
            }

            if (_pattern == SemanticPatternKind.ConvertExpressionToBlockBody && node.ExpressionBody is not null)
            {
                var methodSymbol = _semanticModel.GetDeclaredSymbol(node, _cancellationToken);
                var expr = node.ExpressionBody.Expression;
                var stmt = methodSymbol is { ReturnsVoid: true }
                    ? (StatementSyntax)SyntaxFactory.ExpressionStatement(expr)
                    : SyntaxFactory.ReturnStatement(expr);

                return node
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithBody(SyntaxFactory.Block(stmt));
            }

            if (_pattern == SemanticPatternKind.ConvertBlockToExpressionBody &&
                node.Body is { Statements.Count: 1 } body)
            {
                if (_semanticModel.GetOperation(body, _cancellationToken) is IBlockOperation)
                {
                    ExpressionSyntax? expr = body.Statements[0] switch
                    {
                        ReturnStatementSyntax { Expression: { } retExpr } => retExpr,
                        ExpressionStatementSyntax { Expression: { } stmtExpr } => stmtExpr,
                        _ => null
                    };

                    if (expr is not null)
                    {
                        return node
                            .WithBody(null)
                            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(expr))
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                    }
                }
            }

            if (_pattern == SemanticPatternKind.AddNullChecks && node.Body is { } methodBody)
            {
                var methodSymbol = _semanticModel.GetDeclaredSymbol(node, _cancellationToken);
                if (methodSymbol is not null)
                {
                    var existingBodyText = methodBody.ToString();
                    var guardStatements = new List<StatementSyntax>();

                    foreach (var param in methodSymbol.Parameters)
                    {
                        if (param.Type.IsReferenceType &&
                            param.NullableAnnotation != NullableAnnotation.Annotated &&
                            !existingBodyText.Contains($"ThrowIfNull({param.Name})", StringComparison.Ordinal) &&
                            !existingBodyText.Contains($"{param.Name} == null", StringComparison.Ordinal) &&
                            !existingBodyText.Contains($"{param.Name} is null", StringComparison.Ordinal))
                        {
                            guardStatements.Add(
                                SyntaxFactory.ParseStatement($"System.ArgumentNullException.ThrowIfNull({param.Name});"));
                        }
                    }

                    if (guardStatements.Count > 0)
                    {
                        var updatedStatements = guardStatements.Concat(methodBody.Statements);
                        return node.WithBody(SyntaxFactory.Block(updatedStatements));
                    }
                }
            }

            return base.VisitMethodDeclaration(node);
        }

        private static bool TryRewriteForeachToLinq(ForEachStatementSyntax node, out StatementSyntax replacement)
        {
            replacement = default!;
            var iterVar = node.Identifier.Text;
            var sourceExpr = node.Expression.ToString();

            var singleStmt = node.Statement switch
            {
                BlockSyntax { Statements.Count: 1 } b => b.Statements[0],
                StatementSyntax s and not BlockSyntax => s,
                _ => null
            };

            if (singleStmt is ExpressionStatementSyntax
                {
                    Expression: InvocationExpressionSyntax
                    {
                        Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Add" } memberAccess,
                        ArgumentList.Arguments.Count: 1
                    } invocation
                })
            {
                var targetCollection = memberAccess.Expression.ToString();
                var addedArg = invocation.ArgumentList.Arguments[0].Expression.ToString();
                var selectClause = string.Equals(addedArg, iterVar, StringComparison.Ordinal)
                    ? sourceExpr
                    : $"{sourceExpr}.Select({iterVar} => {addedArg})";

                replacement = SyntaxFactory.ParseStatement($"{targetCollection}.AddRange({selectClause});");
                return true;
            }

            if (singleStmt is IfStatementSyntax
                {
                    Else: null,
                    Condition: { } filterCondition,
                    Statement: var ifBody
                })
            {
                var innerAddStmt = ifBody switch
                {
                    BlockSyntax { Statements.Count: 1 } b => b.Statements[0],
                    StatementSyntax s and not BlockSyntax => s,
                    _ => null
                };

                if (innerAddStmt is ExpressionStatementSyntax
                    {
                        Expression: InvocationExpressionSyntax
                        {
                            Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Add" } innerMemberAccess,
                            ArgumentList.Arguments.Count: 1
                        } innerInvocation
                    })
                {
                    var targetCollection = innerMemberAccess.Expression.ToString();
                    var addedArg = innerInvocation.ArgumentList.Arguments[0].Expression.ToString();
                    var whereChain = $"{sourceExpr}.Where({iterVar} => {filterCondition})";
                    var fullChain = string.Equals(addedArg, iterVar, StringComparison.Ordinal)
                        ? whereChain
                        : $"{whereChain}.Select({iterVar} => {addedArg})";

                    replacement = SyntaxFactory.ParseStatement($"{targetCollection}.AddRange({fullChain});");
                    return true;
                }
            }

            return false;
        }

        private static bool TryRewriteIfToSwitchExpression(IfStatementSyntax node, out StatementSyntax switchReturn)
        {
            switchReturn = default!;
            var arms = new List<(string Pattern, string Result)>();
            string? subject = null;
            IfStatementSyntax? currentIf = node;
            StatementSyntax? finalElseStmt = null;

            while (currentIf is not null)
            {
                if (currentIf.Condition is not BinaryExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.EqualsExpression,
                        Left: var left,
                        Right: var right
                    })
                {
                    return false;
                }

                var leftText = left.ToString();
                if (subject is null)
                {
                    subject = leftText;
                }
                else if (!string.Equals(subject, leftText, StringComparison.Ordinal))
                {
                    return false;
                }

                var returnExpr = ExtractSingleReturnExpression(currentIf.Statement);
                if (returnExpr is null)
                {
                    return false;
                }

                arms.Add((right.ToString(), returnExpr.ToString()));

                if (currentIf.Else?.Statement is IfStatementSyntax nextIf)
                {
                    currentIf = nextIf;
                }
                else
                {
                    finalElseStmt = currentIf.Else?.Statement;
                    currentIf = null;
                }
            }

            if (subject is null || arms.Count == 0 || finalElseStmt is null)
            {
                return false;
            }

            var defaultExpr = ExtractSingleReturnExpression(finalElseStmt);
            if (defaultExpr is null)
            {
                return false;
            }

            var armStrings = arms
                .Select(a => $"{a.Pattern} => {a.Result}")
                .Append($"_ => {defaultExpr}");

            switchReturn = SyntaxFactory.ParseStatement(
                $"return {subject} switch {{ {string.Join(", ", armStrings)} }};");
            return true;
        }

        private static ExpressionSyntax? ExtractSingleReturnExpression(StatementSyntax stmt) => stmt switch
        {
            ReturnStatementSyntax { Expression: { } expr } => expr,
            BlockSyntax { Statements: [ReturnStatementSyntax { Expression: { } expr }] } => expr,
            _ => null
        };

        private static bool TryRewriteTypeCheckToPatternMatching(IfStatementSyntax node, out IfStatementSyntax rewritten)
        {
            rewritten = default!;
            if (node.Condition is not BinaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.IsExpression,
                    Left: IdentifierNameSyntax testedId,
                    Right: TypeSyntax targetType
                })
            {
                return false;
            }

            if (node.Statement is not BlockSyntax { Statements.Count: > 0 } block)
            {
                return false;
            }

            if (block.Statements[0] is not LocalDeclarationStatementSyntax
                {
                    Declaration.Variables: [{ Initializer.Value: CastExpressionSyntax castExpr } declarator]
                } ||
                castExpr.Expression is not IdentifierNameSyntax castTargetId ||
                !string.Equals(castTargetId.Identifier.Text, testedId.Identifier.Text, StringComparison.Ordinal))
            {
                return false;
            }

            var localName = declarator.Identifier.Text;
            var newCondition = SyntaxFactory.ParseExpression($"{testedId.Identifier.Text} is {targetType} {localName}");
            var remainingStatements = block.Statements.RemoveAt(0);
            rewritten = node
                .WithCondition(newCondition)
                .WithStatement(SyntaxFactory.Block(remainingStatements));
            return true;
        }

        private static bool TryRewriteStringFormat(InvocationExpressionSyntax node, out ExpressionSyntax interpolated)
        {
            interpolated = default!;
            var args = node.ArgumentList.Arguments;
            if (args.Count < 2 ||
                args[0].Expression is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } formatLiteral)
            {
                return false;
            }

            var formatText = formatLiteral.Token.ValueText;
            var replaced = Regex.Replace(formatText, @"\{(\d+)\}", match =>
            {
                var idx = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                return idx + 1 < args.Count
                    ? "{" + args[idx + 1].Expression + "}"
                    : match.Value;
            });

            interpolated = SyntaxFactory.ParseExpression($"$\"{replaced.Replace("\"", "\\\"", StringComparison.Ordinal)}\"");
            return true;
        }

        private static bool TryRewriteStringConcatenation(BinaryExpressionSyntax node, out ExpressionSyntax interpolated)
        {
            interpolated = default!;
            var parts = new List<ExpressionSyntax>();
            FlattenAddExpressions(node, parts);
            if (!parts.Any(p => p is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression }))
            {
                return false;
            }

            var sb = new System.Text.StringBuilder("$\"");
            foreach (var part in parts)
            {
                if (part is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } strLit)
                {
                    sb.Append(strLit.Token.ValueText.Replace("\"", "\\\"", StringComparison.Ordinal));
                }
                else
                {
                    sb.Append('{').Append(part.WithoutTrivia()).Append('}');
                }
            }

            sb.Append('"');
            interpolated = SyntaxFactory.ParseExpression(sb.ToString());
            return true;
        }

        private static void FlattenAddExpressions(ExpressionSyntax expr, List<ExpressionSyntax> collector)
        {
            if (expr is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } bin)
            {
                FlattenAddExpressions(bin.Left, collector);
                FlattenAddExpressions(bin.Right, collector);
            }
            else
            {
                collector.Add(expr);
            }
        }

        private static ExpressionSyntax NegateCondition(ExpressionSyntax condition)
        {
            if (condition is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } unaryNot)
            {
                return unaryNot.Operand is ParenthesizedExpressionSyntax paren
                    ? paren.Expression
                    : unaryNot.Operand;
            }

            if (condition is BinaryExpressionSyntax binary)
            {
                SyntaxKind? flipped = binary.Kind() switch
                {
                    SyntaxKind.EqualsExpression => SyntaxKind.NotEqualsExpression,
                    SyntaxKind.NotEqualsExpression => SyntaxKind.EqualsExpression,
                    SyntaxKind.LessThanExpression => SyntaxKind.GreaterThanOrEqualExpression,
                    SyntaxKind.GreaterThanOrEqualExpression => SyntaxKind.LessThanExpression,
                    SyntaxKind.GreaterThanExpression => SyntaxKind.LessThanOrEqualExpression,
                    SyntaxKind.LessThanOrEqualExpression => SyntaxKind.GreaterThanExpression,
                    _ => null
                };

                if (flipped.HasValue)
                {
                    return SyntaxFactory.BinaryExpression(flipped.Value, binary.Left, binary.Right);
                }
            }

            return SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                SyntaxFactory.ParenthesizedExpression(condition.WithoutTrivia()));
        }
    }
}
