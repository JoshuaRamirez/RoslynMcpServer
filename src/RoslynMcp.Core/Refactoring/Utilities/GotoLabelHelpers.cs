using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared goto/label helpers used by add_braces / remove_braces when deciding
/// whether wrapping or unwrapping a body would hide an externally referenced
/// label. Same bodies as the two private copies.
/// </summary>
internal static class GotoLabelHelpers
{
    /// <summary>
    /// True when <paramref name="label"/> sits under an inner
    /// <see cref="BlockSyntax"/> or <see cref="SwitchSectionSyntax"/> that is
    /// still inside <paramref name="body"/> (so the label is already hidden
    /// either way). False when the label is the body itself or reaches the
    /// body before any such inner container.
    /// </summary>
    internal static bool IsLabelAlreadyNestedInInnerBlock(LabeledStatementSyntax label, StatementSyntax body)
    {
        if (label == body)
            return false;

        foreach (var ancestor in label.Ancestors())
        {
            if (ancestor == body)
                return false;

            if (ancestor is BlockSyntax or SwitchSectionSyntax)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Label name targeted by a <c>goto</c>: identifier →
    /// <c>ValueText</c>; any other expression → <c>ToString()</c>; null
    /// expression → null.
    /// </summary>
    internal static string? GetGotoLabelName(GotoStatementSyntax gotoStatement) =>
        gotoStatement.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            { } expression => expression.ToString(),
            _ => null
        };
}
