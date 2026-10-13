using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Normalizes and applies the optional <c>namespaceFilter</c> of <c>search_symbols</c>,
/// <c>find_implementations</c>, and <c>find_references</c>: a symbol (or reference site) matches when
/// its containing namespace equals the filter or is nested inside it (ordinal).
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
    public static bool IsInNamespace(ISymbol symbol, string normalizedFilter) =>
        IsNamespaceInFilter(symbol.ContainingNamespace, normalizedFilter);

    /// <summary>
    /// Whether a reference site whose enclosing symbol (e.g. from
    /// <see cref="SemanticModel.GetEnclosingSymbol(int, System.Threading.CancellationToken)"/>) is
    /// <paramref name="enclosingSymbol"/> lives in a namespace that equals
    /// <paramref name="normalizedFilter"/> or is nested inside it (ordinal). When the enclosing symbol
    /// is itself a namespace (e.g. a using directive inside a namespace declaration),
    /// that namespace is used; otherwise its containing namespace. Sites in the global namespace never
    /// match.
    /// </summary>
    public static bool IsSiteInNamespace(ISymbol enclosingSymbol, string normalizedFilter) =>
        IsNamespaceInFilter(
            enclosingSymbol as INamespaceSymbol ?? enclosingSymbol.ContainingNamespace,
            normalizedFilter);

    private static bool IsNamespaceInFilter(INamespaceSymbol? containingNamespace, string normalizedFilter)
    {
        if (containingNamespace == null || containingNamespace.IsGlobalNamespace) return false;

        var name = containingNamespace.ToDisplayString();
        return name.Length == normalizedFilter.Length
            ? string.Equals(name, normalizedFilter, StringComparison.Ordinal)
            : name.Length > normalizedFilter.Length
              && name[normalizedFilter.Length] == '.'
              && name.StartsWith(normalizedFilter, StringComparison.Ordinal);
    }
}
