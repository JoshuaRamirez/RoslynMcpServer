using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared visibility-string → <see cref="SyntaxToken"/> parsing used by
/// generate_constructor / generate_property. Same bodies as the two private
/// copies. Distinct from generate_method_stub (PrivateKeyword empty/unknown
/// default + trailing Space trivia).
/// </summary>
internal static class VisibilityTokenHelpers
{
    /// <summary>
    /// Splits <paramref name="visibility"/> on whitespace into modifier
    /// tokens so <c>protected internal</c> and <c>private protected</c>
    /// emit both modifiers. Empty / whitespace-only defaults to
    /// <see cref="SyntaxKind.PublicKeyword"/>.
    /// </summary>
    internal static IEnumerable<SyntaxToken> ParseVisibilityTokens(string visibility)
    {
        var tokens = visibility
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseVisibilityKeyword)
            .ToList();

        if (tokens.Count == 0)
            tokens.Add(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

        return tokens;
    }

    /// <summary>
    /// Maps a single case-insensitive visibility keyword to a
    /// <see cref="SyntaxToken"/>; unknown keywords become
    /// <see cref="SyntaxKind.PublicKeyword"/>.
    /// </summary>
    internal static SyntaxToken ParseVisibilityKeyword(string keyword) => keyword.ToLowerInvariant() switch
    {
        "public" => SyntaxFactory.Token(SyntaxKind.PublicKeyword),
        "private" => SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
        "protected" => SyntaxFactory.Token(SyntaxKind.ProtectedKeyword),
        "internal" => SyntaxFactory.Token(SyntaxKind.InternalKeyword),
        _ => SyntaxFactory.Token(SyntaxKind.PublicKeyword)
    };
}
