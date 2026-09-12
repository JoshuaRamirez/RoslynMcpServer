using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.Core.Refactoring.Signature;

/// <summary>
/// Shared predicates for Signature-family reference filtering
/// (declaration name vs call-site name vs nameof argument).
/// </summary>
internal static class SignatureReferenceHelpers
{
    /// <summary>
    /// True when <paramref name="referenceSpan"/> intersects a
    /// <see cref="MethodDeclarationSyntax"/> identifier in the ancestor chain of
    /// <paramref name="node"/> (the method's own declaration name).
    /// </summary>
    internal static bool IsDeclarationName(SyntaxNode node, TextSpan referenceSpan)
    {
        return node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Span.IntersectsWith(referenceSpan));
    }

    /// <summary>
    /// True when <paramref name="referenceSpan"/> hits the invoked method name /
    /// expression of <paramref name="invocation"/>, not an argument inside the
    /// argument list.
    /// </summary>
    internal static bool IsInvokedMethodName(InvocationExpressionSyntax invocation, TextSpan referenceSpan)
    {
        if (invocation.ArgumentList.Span.Contains(referenceSpan))
            return false;

        return invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Span.IntersectsWith(referenceSpan),
            MemberAccessExpressionSyntax member => member.Name.Span.IntersectsWith(referenceSpan),
            MemberBindingExpressionSyntax binding => binding.Name.Span.IntersectsWith(referenceSpan),
            _ => invocation.Expression.Span.IntersectsWith(referenceSpan)
        };
    }

    /// <summary>
    /// True when <paramref name="node"/> sits inside a <c>nameof(...)</c>
    /// invocation (identifier expression named nameof).
    /// </summary>
    internal static bool IsNameOfArgument(SyntaxNode node)
    {
        foreach (var invocation in node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is IdentifierNameSyntax identifier &&
                identifier.Identifier.Text == "nameof")
            {
                return true;
            }
        }

        return false;
    }
}
