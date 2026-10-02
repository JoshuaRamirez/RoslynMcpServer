using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared parameter / default-expression predicate helpers used by Signature
/// operations when classifying params / optional parameters and default values.
/// </summary>
internal static class ParameterSyntaxHelpers
{
    /// <summary>
    /// True when <paramref name="parameter"/> has the <c>params</c>
    /// modifier. Same body as the two private copies on add_parameter /
    /// reorder_parameters.
    /// </summary>
    internal static bool IsParams(ParameterSyntax parameter) =>
        parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword));

    /// <summary>
    /// True when <paramref name="parameter"/> has a default-value clause.
    /// Same body as the two private copies on add_parameter /
    /// reorder_parameters.
    /// </summary>
    internal static bool IsOptional(ParameterSyntax parameter) =>
        parameter.Default != null;

    /// <summary>
    /// True when <paramref name="expression"/> is a <c>default</c> literal
    /// or <c>default(T)</c> expression. Same body as the two private copies
    /// on add_parameter / change_signature.
    /// </summary>
    internal static bool IsDefaultValueExpression(ExpressionSyntax expression) =>
        expression.IsKind(SyntaxKind.DefaultLiteralExpression) ||
        expression is DefaultExpressionSyntax;
}
