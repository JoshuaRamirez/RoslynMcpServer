using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared visibility-string validation and → <see cref="SyntaxToken"/> parsing
/// used by generate_constructor / generate_property / generate_method_stub /
/// extract_method / extract_constant. Callers choose empty/unknown default
/// (<see cref="SyntaxKind.PublicKeyword"/> for property/ctor,
/// <see cref="SyntaxKind.PrivateKeyword"/> for extract/stub) and whether each
/// token gets trailing Space trivia (stub only).
/// </summary>
internal static class VisibilityTokenHelpers
{
    private static readonly HashSet<string> ValidVisibilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "private", "protected", "internal", "protected internal", "private protected"
    };

    /// <summary>
    /// True when <paramref name="visibility"/> is one of the six C# accessibility
    /// spellings (OrdinalIgnoreCase). Callers keep their own Trim /
    /// null-or-whitespace gates; this only checks set membership.
    /// </summary>
    internal static bool IsValidVisibility(string visibility) =>
        ValidVisibilities.Contains(visibility);

    /// <summary>
    /// Splits <paramref name="visibility"/> on whitespace into modifier
    /// tokens so <c>protected internal</c> and <c>private protected</c>
    /// emit both modifiers. Empty / whitespace-only defaults to
    /// <paramref name="emptyOrUnknownDefault"/> (property/ctor:
    /// <see cref="SyntaxKind.PublicKeyword"/>; extract/stub:
    /// <see cref="SyntaxKind.PrivateKeyword"/>). When
    /// <paramref name="trailingSpace"/> is true, each token gets trailing
    /// Space trivia (generate_method_stub).
    /// </summary>
    internal static IEnumerable<SyntaxToken> ParseVisibilityTokens(
        string visibility,
        SyntaxKind emptyOrUnknownDefault = SyntaxKind.PublicKeyword,
        bool trailingSpace = false)
    {
        var tokens = visibility
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => ParseVisibilityKeyword(k, emptyOrUnknownDefault))
            .ToList();

        if (tokens.Count == 0)
            tokens.Add(SyntaxFactory.Token(emptyOrUnknownDefault));

        if (trailingSpace)
            return tokens.Select(t => t.WithTrailingTrivia(SyntaxFactory.Space));

        return tokens;
    }

    /// <summary>
    /// Maps a single case-insensitive visibility keyword to a
    /// <see cref="SyntaxToken"/>; unknown keywords become
    /// <paramref name="unknownDefault"/> (same default as empty for that caller).
    /// </summary>
    internal static SyntaxToken ParseVisibilityKeyword(
        string keyword,
        SyntaxKind unknownDefault = SyntaxKind.PublicKeyword) => keyword.ToLowerInvariant() switch
        {
            "public" => SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            "private" => SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
            "protected" => SyntaxFactory.Token(SyntaxKind.ProtectedKeyword),
            "internal" => SyntaxFactory.Token(SyntaxKind.InternalKeyword),
            _ => SyntaxFactory.Token(unknownDefault)
        };
}
