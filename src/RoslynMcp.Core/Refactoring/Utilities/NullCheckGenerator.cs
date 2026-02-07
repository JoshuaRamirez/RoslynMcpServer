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
}
