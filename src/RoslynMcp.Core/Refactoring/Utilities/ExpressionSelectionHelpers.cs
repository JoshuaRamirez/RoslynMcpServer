using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared expression-from-selection helpers used by extract_variable /
/// introduce_field when resolving the smallest enclosing expression for a
/// <see cref="TextSpan"/>.
/// </summary>
internal static class ExpressionSelectionHelpers
{
    /// <summary>
    /// Walks from <paramref name="node"/> upward and returns the smallest
    /// <see cref="ExpressionSyntax"/> whose span contains <paramref name="span"/>,
    /// or <see langword="null"/> when none matches. Same body as the prior
    /// private copies on ExtractVariable / IntroduceField (ExtractVariable's
    /// trailing ExpressionStatement branch was a no-op and is omitted).
    /// </summary>
    internal static ExpressionSyntax? FindEnclosingExpression(SyntaxNode node, TextSpan span)
    {
        ExpressionSyntax? bestMatch = null;
        var current = node;

        while (current != null)
        {
            if (current is ExpressionSyntax expr && current.Span.Contains(span))
            {
                if (bestMatch == null || current.Span.Length <= bestMatch.Span.Length)
                    bestMatch = expr;
            }

            current = current.Parent;
        }

        return bestMatch;
    }
}
