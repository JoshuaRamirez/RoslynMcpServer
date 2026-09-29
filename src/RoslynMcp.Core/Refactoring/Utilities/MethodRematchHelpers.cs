using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared method-declaration rematch after a syntax-tree rewrite for
/// <c>change_return_type</c>, <c>change_signature</c>, and
/// <c>introduce_parameter</c>. Same body as the prior private
/// <c>RematchMethod</c> copies on those operations. Intentionally not used
/// by <c>inline_method</c>, which rematches via exact <c>Span</c> plus
/// identifier.
/// </summary>
internal static class MethodRematchHelpers
{
    /// <summary>
    /// Rematches a method declaration in <paramref name="root"/> by
    /// identifier text; when several candidates share that name, picks the
    /// closest <c>SpanStart</c> then shortest <c>Span.Length</c>. Same body
    /// as the prior private copies on ChangeReturnType / ChangeSignature /
    /// IntroduceParameter.
    /// </summary>
    internal static MethodDeclarationSyntax? RematchMethod(
        SyntaxNode root,
        MethodDeclarationSyntax original)
    {
        var candidates = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == original.Identifier.Text)
            .ToList();
        if (candidates.Count == 0)
            return null;
        if (candidates.Count == 1)
            return candidates[0];

        return candidates
            .OrderBy(m => Math.Abs(m.SpanStart - original.SpanStart))
            .ThenBy(m => m.Span.Length)
            .FirstOrDefault();
    }
}
