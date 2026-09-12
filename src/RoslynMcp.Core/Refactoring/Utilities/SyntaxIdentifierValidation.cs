using Microsoft.CodeAnalysis.CSharp;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared SyntaxFacts-based identifier validation used by inline_constant,
/// generate_method_stub / generate_property, add_parameter, and
/// convert_anonymous_to_class / convert_tuple_to_struct name checks.
/// Applies verbatim <c>@</c>-keyword and reserved-keyword rules via
/// <see cref="SyntaxFacts"/>. Distinct from char-based
/// <see cref="IdentifierValidation"/>.
/// </summary>
internal static class SyntaxIdentifierValidation
{
    /// <summary>
    /// True when <paramref name="name"/> is a non-whitespace C# identifier
    /// per <see cref="SyntaxFacts"/>, allowing verbatim <c>@</c>-prefixed
    /// keywords and rejecting reserved keywords without the verbatim prefix
    /// (same body as the InlineConstant / GenerateMethodStub /
    /// GenerateProperty / AddParameter / ConvertAnonymous / ConvertTuple copies).
    /// </summary>
    internal static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.StartsWith('@') && name.Length > 1)
        {
            var bare = name[1..];
            return SyntaxFacts.IsValidIdentifier(bare) ||
                   SyntaxFacts.GetKeywordKind(bare) != SyntaxKind.None;
        }

        if (!SyntaxFacts.IsValidIdentifier(name))
            return false;

        var keywordKind = SyntaxFacts.GetKeywordKind(name);
        return keywordKind == SyntaxKind.None || !SyntaxFacts.IsReservedKeyword(keywordKind);
    }
}
