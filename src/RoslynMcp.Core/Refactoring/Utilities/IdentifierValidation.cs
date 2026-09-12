namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared char-based identifier validation used by encapsulate_field and
/// extract_* name checks (letter-or-underscore start; letter/digit/underscore
/// body). Does not apply SyntaxFacts / verbatim @-keyword rules.
/// </summary>
internal static class IdentifierValidation
{
    /// <summary>
    /// True when <paramref name="name"/> is non-empty, starts with a letter or
    /// <c>_</c>, and every remaining character is a letter, digit, or <c>_</c>
    /// (same body as the EncapsulateField / ExtractConstant / ExtractInterface /
    /// ExtractVariable / ExtractBaseClass copies).
    /// </summary>
    internal static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}
