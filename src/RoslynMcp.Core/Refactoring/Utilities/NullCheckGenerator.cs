using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Generates null-check statements for method/constructor parameters.
/// </summary>
public static class NullCheckGenerator
{
    /// <summary>
    /// Generates ArgumentNullException.ThrowIfNull() statements (modern .NET 6+ style).
    /// </summary>
    public static StatementSyntax GenerateThrowIfNull(string parameterName)
    {
        // ArgumentNullException.ThrowIfNull(paramName);
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("ArgumentNullException"),
                    SyntaxFactory.IdentifierName("ThrowIfNull")))
            .WithArgumentList(SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(
                        SyntaxFactory.IdentifierName(parameterName))))));
    }

    /// <summary>
    /// Generates if-throw guard clause style null check.
    /// </summary>
    public static StatementSyntax GenerateGuardClause(string parameterName)
    {
        // if (paramName is null) throw new ArgumentNullException(nameof(paramName));
        return SyntaxFactory.IfStatement(
            SyntaxFactory.IsPatternExpression(
                SyntaxFactory.IdentifierName(parameterName),
                SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))),
            SyntaxFactory.ThrowStatement(
                SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.IdentifierName("ArgumentNullException"))
                .WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.IdentifierName("nameof"))
                            .WithArgumentList(SyntaxFactory.ArgumentList(
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.Argument(
                                        SyntaxFactory.IdentifierName(parameterName)))))))))));
    }

    /// <summary>
    /// Determines whether a parameter type should have a null check.
    /// Returns true for non-nullable reference types.
    /// </summary>
    public static bool ShouldCheckForNull(IParameterSymbol parameter)
    {
        var type = parameter.Type;

        // Reference types that are not nullable-annotated
        if (type.IsReferenceType)
            return type.NullableAnnotation != NullableAnnotation.Annotated;

        // Nullable<T> value types
        if (type is INamedTypeSymbol namedType &&
            namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return false;

        // Regular value types can't be null
        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="body"/> already has an equivalent
    /// top-level <c>ThrowIfNull</c> or guard-clause null check for
    /// <paramref name="parameterName"/>. Nested checks that are not always
    /// executed do not count.
    /// </summary>
    public static bool HasExistingNullCheck(BlockSyntax body, string parameterName)
    {
        foreach (var statement in body.Statements)
        {
            if (IsThrowIfNullStatement(statement, parameterName) ||
                IsGuardClauseStatement(statement, parameterName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsThrowIfNullStatement(StatementSyntax statement, string parameterName)
    {
        if (statement is not ExpressionStatementSyntax expressionStatement)
            return false;
        if (expressionStatement.Expression is not InvocationExpressionSyntax invocation)
            return false;
        if (invocation.Expression is not MemberAccessExpressionSyntax member)
            return false;
        if (!string.Equals(member.Name.Identifier.Text, "ThrowIfNull", StringComparison.Ordinal))
            return false;
        if (!IsArgumentNullExceptionType(member.Expression))
            return false;

        var firstArg = invocation.ArgumentList.Arguments.FirstOrDefault();
        return firstArg?.Expression is IdentifierNameSyntax identifier &&
               identifier.Identifier.Text == parameterName;
    }

    private static bool IsGuardClauseStatement(StatementSyntax statement, string parameterName)
    {
        if (statement is not IfStatementSyntax ifStatement)
            return false;
        if (!IsNullCheckCondition(ifStatement.Condition, parameterName))
            return false;
        return ContainsArgumentNullExceptionThrow(ifStatement.Statement);
    }

    private static bool IsNullCheckCondition(ExpressionSyntax condition, string parameterName)
    {
        if (condition is IsPatternExpressionSyntax isPattern &&
            isPattern.Expression is IdentifierNameSyntax isIdentifier &&
            isIdentifier.Identifier.Text == parameterName &&
            isPattern.Pattern is ConstantPatternSyntax constant &&
            constant.Expression.IsKind(SyntaxKind.NullLiteralExpression))
        {
            return true;
        }

        if (condition is BinaryExpressionSyntax binary &&
            binary.IsKind(SyntaxKind.EqualsExpression))
        {
            return (IsIdentifier(binary.Left, parameterName) && IsNullLiteral(binary.Right)) ||
                   (IsIdentifier(binary.Right, parameterName) && IsNullLiteral(binary.Left));
        }

        return false;
    }

    private static bool ContainsArgumentNullExceptionThrow(StatementSyntax statement)
    {
        var throwStatement = statement switch
        {
            ThrowStatementSyntax thrown => thrown,
            BlockSyntax block => block.Statements.OfType<ThrowStatementSyntax>().FirstOrDefault(),
            _ => null
        };

        return throwStatement?.Expression is ObjectCreationExpressionSyntax creation &&
               IsArgumentNullExceptionType(creation.Type);
    }

    private static bool IsArgumentNullExceptionType(ExpressionSyntax expression) =>
        expression switch
        {
            IdentifierNameSyntax identifier =>
                identifier.Identifier.Text == "ArgumentNullException",
            QualifiedNameSyntax qualified =>
                qualified.Right.Identifier.Text == "ArgumentNullException",
            MemberAccessExpressionSyntax member =>
                member.Name.Identifier.Text == "ArgumentNullException",
            AliasQualifiedNameSyntax aliased =>
                aliased.Name.Identifier.Text == "ArgumentNullException",
            _ => false
        };

    private static bool IsIdentifier(ExpressionSyntax expression, string name) =>
        expression is IdentifierNameSyntax identifier && identifier.Identifier.Text == name;

    private static bool IsNullLiteral(ExpressionSyntax expression) =>
        expression.IsKind(SyntaxKind.NullLiteralExpression);
}
