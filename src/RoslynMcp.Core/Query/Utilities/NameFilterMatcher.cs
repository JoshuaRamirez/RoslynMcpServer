namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Normalizes and applies the optional <c>nameFilter</c> string shared by <c>get_document_outline</c>,
/// <c>get_symbol_info</c>, <c>find_implementations</c>, <c>get_type_hierarchy</c>, <c>find_callers</c>,
/// and <c>find_references</c>: an ordinal, case-insensitive substring match on a simple name. Also backs
/// <c>get_diagnostics</c> <c>messageFilter</c>, which applies the same match to a diagnostic's message.
/// </summary>
internal static class NameFilterMatcher
{
    /// <summary>
    /// Returns <c>null</c> when <paramref name="nameFilter"/> is omitted, empty, or whitespace (no name
    /// filtering); otherwise the value with surrounding whitespace trimmed. Free text — never rejected.
    /// </summary>
    public static string? Normalize(string? nameFilter) =>
        string.IsNullOrWhiteSpace(nameFilter) ? null : nameFilter.Trim();

    /// <summary>
    /// Whether <paramref name="name"/> satisfies <paramref name="normalizedFilter"/> (a value returned by
    /// <see cref="Normalize"/>): always <c>true</c> when the filter is <c>null</c>, otherwise whether
    /// <paramref name="name"/> contains it as an ordinal, case-insensitive substring.
    /// </summary>
    public static bool Matches(string name, string? normalizedFilter) =>
        normalizedFilter == null || name.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase);
}
