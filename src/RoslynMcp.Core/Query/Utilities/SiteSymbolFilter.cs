using Microsoft.CodeAnalysis;
using RoslynMcp.Core.Resolution;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// The optional <c>accessibilityFilter</c> / <c>nameFilter</c> / <c>kindFilter</c> / <c>staticFilter</c>
/// combination that <c>find_references</c> and <c>get_diagnostics</c> apply to a result's declaring site
/// symbol (see <see cref="DeclaringSiteSymbol"/>). Built once per request from the raw parameters; invalid
/// accessibility / kind values are rejected earlier in each operation's <c>ValidateParams</c>.
/// </summary>
internal sealed class SiteSymbolFilter
{
    private readonly Accessibility? _accessibility;
    private readonly string? _name;
    private readonly Contracts.Enums.SymbolKind? _kind;
    private readonly bool? _isStatic;

    private SiteSymbolFilter(
        Accessibility? accessibility, string? name, Contracts.Enums.SymbolKind? kind, bool? isStatic)
    {
        _accessibility = accessibility;
        _name = name;
        _kind = kind;
        _isStatic = isStatic;
    }

    /// <summary>
    /// Parses the raw filter values: <paramref name="accessibilityFilter"/> via
    /// <see cref="AccessibilityFilterParser.Parse"/>, <paramref name="nameFilter"/> via
    /// <see cref="NameFilterMatcher.Normalize"/>, <paramref name="kindFilter"/> via
    /// <see cref="SymbolKindFilterParser.Parse"/>; <paramref name="staticFilter"/> is used as-is. Omitted /
    /// blank values impose no filter.
    /// </summary>
    public static SiteSymbolFilter Create(
        string? accessibilityFilter, string? nameFilter, string? kindFilter, bool? staticFilter) =>
        new(
            AccessibilityFilterParser.Parse(accessibilityFilter),
            NameFilterMatcher.Normalize(nameFilter),
            SymbolKindFilterParser.Parse(kindFilter),
            staticFilter);

    /// <summary>Whether any of the four filters is set (so the site symbol needs resolving at all).</summary>
    public bool IsActive =>
        _accessibility.HasValue || _name != null || _kind.HasValue || _isStatic.HasValue;

    /// <summary>
    /// Whether <paramref name="siteSymbol"/> satisfies every set filter: exact
    /// <see cref="ISymbol.DeclaredAccessibility"/> equality, <see cref="NameFilterMatcher.Matches"/> on the
    /// simple <see cref="ISymbol.Name"/>, <see cref="SymbolKindMapper.Map"/> equality, and
    /// <see cref="ISymbol.IsStatic"/> equality. Always <c>true</c> when no filter is set. Namespaces have no
    /// accessibility modifier but Roslyn reports <see cref="INamespaceSymbol"/> as <c>Public</c>, so a
    /// <c>null</c> or namespace-level site never matches any set filter.
    /// </summary>
    public bool Matches(ISymbol? siteSymbol)
    {
        if (!IsActive)
            return true;

        if (siteSymbol is null or INamespaceSymbol)
            return false;

        return (!_accessibility.HasValue || siteSymbol.DeclaredAccessibility == _accessibility.Value)
            && NameFilterMatcher.Matches(siteSymbol.Name, _name)
            && (!_kind.HasValue || SymbolKindMapper.Map(siteSymbol) == _kind.Value)
            && (!_isStatic.HasValue || siteSymbol.IsStatic == _isStatic.Value);
    }
}
