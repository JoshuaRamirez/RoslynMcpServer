using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared goto/label helpers used by add_braces / remove_braces when deciding
/// whether wrapping or unwrapping a body would hide an externally referenced
/// label. Same bodies as the prior private/internal copies.
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

    /// <summary>
    /// Nearest enclosing method / local function / anonymous function /
    /// accessor / constructor / destructor / operator / conversion operator
    /// for label scoping, or null when none.
    /// </summary>
    internal static SyntaxNode? GetLabelContainer(SyntaxNode node) =>
        node.AncestorsAndSelf().FirstOrDefault(ancestor => ancestor is
            MethodDeclarationSyntax or
            LocalFunctionStatementSyntax or
            AnonymousFunctionExpressionSyntax or
            AccessorDeclarationSyntax or
            ConstructorDeclarationSyntax or
            DestructorDeclarationSyntax or
            OperatorDeclarationSyntax or
            ConversionOperatorDeclarationSyntax);

    /// <summary>
    /// True when wrapping or unwrapping <paramref name="body"/> would hide a
    /// label that an external <c>goto</c> in the same container still targets.
    /// </summary>
    internal static bool WouldHideExternallyReferencedLabel(StatementSyntax body)
    {
        var container = GetLabelContainer(body);
        if (container == null)
            return false;

        foreach (var label in body.DescendantNodesAndSelf().OfType<LabeledStatementSyntax>())
        {
            if (IsLabelAlreadyNestedInInnerBlock(label, body))
                continue;

            var name = label.Identifier.ValueText;
            foreach (var gotoStatement in container.DescendantNodes().OfType<GotoStatementSyntax>())
            {
                if (!gotoStatement.IsKind(SyntaxKind.GotoStatement))
                    continue;

                if (!string.Equals(GetGotoLabelName(gotoStatement), name, StringComparison.Ordinal))
                    continue;

                if (GetLabelContainer(gotoStatement) != container)
                    continue;

                if (body.Contains(gotoStatement))
                    continue;

                return true;
            }
        }

        return false;
    }
}
