using Microsoft.CodeAnalysis;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Applies the optional <c>namespaceFilter</c> of <c>find_references</c>, <c>find_callers</c>, and
/// <c>get_diagnostics</c> to reference / call / diagnostic sites: a site matches when the symbol enclosing it (member, type, or namespace
/// declaration) lives in the filter namespace or one nested inside it (see
/// <see cref="NamespaceFilter.IsSiteInNamespace"/>). The target symbol's own namespace is irrelevant.
/// Enclosing symbols come from a <see cref="SiteEnclosingSymbolResolver"/>, which caches semantic
/// models per syntax tree so each file is bound at most once per scope.
/// </summary>
internal sealed class NamespaceSiteScope
{
    private readonly SiteEnclosingSymbolResolver _resolver;
    private readonly string _normalizedFilter;

    private NamespaceSiteScope(SiteEnclosingSymbolResolver resolver, string normalizedFilter)
    {
        _resolver = resolver;
        _normalizedFilter = normalizedFilter;
    }

    /// <summary>
    /// Returns <c>null</c> (no namespace filtering) when <paramref name="namespaceFilter"/> normalizes to
    /// nothing (omitted, blank, or a bare <c>global::</c>; see <see cref="NamespaceFilter.Normalize"/>);
    /// otherwise a scope for the normalized filter. Pass <paramref name="resolver"/> to share its
    /// semantic-model cache with another site filter; when omitted the scope gets its own.
    /// </summary>
    public static NamespaceSiteScope? Create(
        WorkspaceContext context, string? namespaceFilter, SiteEnclosingSymbolResolver? resolver = null)
    {
        var normalizedFilter = NamespaceFilter.Normalize(namespaceFilter);
        return normalizedFilter == null
            ? null
            : new NamespaceSiteScope(resolver ?? new SiteEnclosingSymbolResolver(context), normalizedFilter);
    }

    /// <summary>
    /// Whether the site at <paramref name="position"/> in <paramref name="tree"/> (whose document is
    /// <paramref name="document"/>, or <c>null</c> for a tree that is not a regular document) lives in
    /// the filter namespace or one nested inside it. False when there is no tree, no semantic model,
    /// or no enclosing symbol.
    /// </summary>
    public async Task<bool> ContainsAsync(
        SyntaxTree? tree, Document? document, int position, CancellationToken cancellationToken)
    {
        var enclosingSymbol = await _resolver.GetEnclosingSymbolAsync(tree, document, position, cancellationToken);
        return enclosingSymbol != null && NamespaceFilter.IsSiteInNamespace(enclosingSymbol, _normalizedFilter);
    }
}
