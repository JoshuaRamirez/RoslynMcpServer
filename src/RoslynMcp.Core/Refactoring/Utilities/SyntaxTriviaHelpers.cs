using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared trivia filters used by
/// <c>ConvertToBlockBodyOperation</c> and <c>RemoveBracesOperation</c>
/// when preserving non-whitespace trivia across rewrite boundaries.
/// Same body as the prior private <c>NonWhitespaceTrivia</c> copies.
/// </summary>
internal static class SyntaxTriviaHelpers
{
    /// <summary>
    /// Yields trivia from <paramref name="trivia"/> excluding
    /// <see cref="SyntaxKind.WhitespaceTrivia"/> and
    /// <see cref="SyntaxKind.EndOfLineTrivia"/>. Same body as the prior
    /// ConvertToBlockBody / RemoveBraces private copies.
    /// </summary>
    internal static IEnumerable<SyntaxTrivia> NonWhitespaceTrivia(SyntaxTriviaList trivia) =>
        trivia.Where(item => !item.IsKind(SyntaxKind.WhitespaceTrivia)
            && !item.IsKind(SyntaxKind.EndOfLineTrivia));
}
