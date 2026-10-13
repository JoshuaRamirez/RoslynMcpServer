using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Normalizes and applies the optional <c>namespaceFilter</c> of <c>search_symbols</c> and
/// <c>find_implementations</c>: a symbol matches when its containing namespace equals the filter or
/// is nested inside it (ordinal).
/// </summary>
internal static class NamespaceFilter
{
    private const string GlobalPrefix = "global::";

    /// <summary>
    /// Trims the namespace filter and strips an optional leading <c>global::</c>.
    /// Returns <c>null</c> (no namespace filtering) when the filter is omitted, blank, or a bare
    /// <c>global::</c>, since every namespace is nested in the root namespace.
    /// </summary>
    public static string? Normalize(string? namespaceFilter)
    {
        if (string.IsNullOrWhiteSpace(namespaceFilter)) return null;

        var trimmed = namespaceFilter.Trim();
        if (trimmed.StartsWith(GlobalPrefix, StringComparison.Ordinal))
            trimmed = trimmed[GlobalPrefix.Length..].Trim();

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Whether the symbol's containing namespace equals <paramref name="normalizedFilter"/> (a value
    /// returned by <see cref="Normalize"/>) or is nested inside it (ordinal). Symbols in the global
    /// namespace never match.
    /// </summary>
    public static bool IsInNamespace(ISymbol symbol, string normalizedFilter)
    {
        var containingNamespace = symbol.ContainingNamespace;
        if (containingNamespace == null || containingNamespace.IsGlobalNamespace) return false;

        var name = containingNamespace.ToDisplayString();
        return name.Length == normalizedFilter.Length
            ? string.Equals(name, normalizedFilter, StringComparison.Ordinal)
            : name.Length > normalizedFilter.Length
              && name[normalizedFilter.Length] == '.'
              && name.StartsWith(normalizedFilter, StringComparison.Ordinal);
    }
}
