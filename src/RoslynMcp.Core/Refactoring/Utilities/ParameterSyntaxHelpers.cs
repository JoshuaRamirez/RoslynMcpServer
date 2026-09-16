using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared <see cref="ParameterSyntax"/> predicate helpers used by Signature
/// operations when classifying params / optional parameters.
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
}
