using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared helpers for redundant <see cref="ParenthesizedExpressionSyntax"/> wrappers.
/// </summary>
internal static class ParenthesizedExpressionHelpers
{
    /// <summary>
    /// Strips every outer <see cref="ParenthesizedExpressionSyntax"/> layer from
    /// <paramref name="expression"/> and returns the first non-parenthesized
    /// node (the same instance, no new syntax is created). Parentheses nested
    /// below a non-parenthesized node are left alone. Same loop as the former
    /// ConvertTupleToStruct / ExtractVariable / SafeDelete private copies.
    /// </summary>
    internal static ExpressionSyntax SkipParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        return expression;
    }
}
