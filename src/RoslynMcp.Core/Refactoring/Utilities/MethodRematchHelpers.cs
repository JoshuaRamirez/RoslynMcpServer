using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared rematch helpers after a syntax-tree rewrite for
/// <c>change_return_type</c>, <c>change_signature</c>, and
/// <c>introduce_parameter</c>. Same bodies as the prior private
/// <c>RematchMethod</c> / <c>RematchInvocation</c> copies on those
/// operations. Intentionally not used by <c>inline_method</c>, which
/// rematches via exact <c>Span</c> (plus identifier for methods).
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

    /// <summary>
    /// Rematches an invocation in <paramref name="root"/> after a rewrite:
    /// out-of-range spans pick the nearest invocation by <c>SpanStart</c>
    /// then shortest length; in-range spans try <c>FindNode</c> + ancestors,
    /// then overlapping invocations with the same distance order. Same body
    /// as the prior private copies on ChangeSignature / IntroduceParameter.
    /// Do not use for <c>inline_method</c> (exact-<c>Span</c> rematch).
    /// </summary>
    internal static InvocationExpressionSyntax? RematchInvocation(SyntaxNode root, TextSpan span)
    {
        if (span.Start < 0 || span.End > root.FullSpan.End)
        {
            return root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .OrderBy(i => Math.Abs(i.SpanStart - span.Start))
                .ThenBy(i => i.Span.Length)
                .FirstOrDefault();
        }

        var node = root.FindNode(span, getInnermostNodeForTie: true);
        return node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault()
            ?? root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => i.Span.OverlapsWith(span) || span.OverlapsWith(i.Span))
                .OrderBy(i => Math.Abs(i.SpanStart - span.Start))
                .ThenBy(i => i.Span.Length)
                .FirstOrDefault();
    }
}
