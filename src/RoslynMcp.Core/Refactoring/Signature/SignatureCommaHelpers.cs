using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynMcp.Core.Refactoring.Signature;

/// <summary>
/// Shared comma+space separator helpers used by
/// <c>RemoveParameterOperation</c> and <c>ReorderParametersOperation</c>
/// when rebuilding <see cref="SeparatedSyntaxList{TNode}"/> separators.
/// Same bodies as the prior private copies.
/// </summary>
internal static class SignatureCommaHelpers
{
    /// <summary>
    /// A <c>,</c> token with a single trailing space. Same body as the prior
    /// RemoveParameter / ReorderParameters private copies.
    /// </summary>
    internal static SyntaxToken CommaWithSpace() =>
        SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space);

    /// <summary>
    /// Default comma separators for a separated list of
    /// <paramref name="nodeCount"/> nodes (empty when ≤ 1). Same body as the
    /// prior RemoveParameter / ReorderParameters private copies.
    /// </summary>
    internal static IReadOnlyList<SyntaxToken> DefaultCommaSeparators(int nodeCount)
    {
        if (nodeCount <= 1)
            return Array.Empty<SyntaxToken>();

        return Enumerable.Repeat(CommaWithSpace(), nodeCount - 1).ToArray();
    }
}
