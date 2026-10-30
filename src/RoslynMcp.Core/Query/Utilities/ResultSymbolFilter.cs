using Microsoft.CodeAnalysis;
using RoslynMcp.Core.Resolution;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// The optional <c>namespaceFilter</c> / <c>accessibilityFilter</c> / <c>kindFilter</c> / <c>nameFilter</c> /
/// <c>staticFilter</c> / <c>abstractFilter</c> (and, for <c>search_symbols</c> only, <c>sealedFilter</c>) combination that <c>find_implementations</c>,
/// <c>get_type_hierarchy</c>, <c>get_symbol_info</c>, <c>find_callers</c> and <c>search_symbols</c> apply to each
/// returned symbol (an implementation, a derived type, a member of the target type, a call site's calling symbol,
/// or a found declaration) before <c>maxResults</c>. <c>search_symbols</c> passes no nameFilter (its query is the
/// name match) and additionally rejects namespaces when <see cref="ConstrainsModifiers"/>; <c>find_callers</c>
/// passes neither namespaceFilter (it scopes call-site locations via <see cref="NamespaceSiteScope"/>) nor
/// abstractFilter. Built once per request from the raw parameters; invalid accessibility / kind values are
/// rejected earlier in each operation's <c>ValidateParams</c>.
/// </summary>
internal sealed class ResultSymbolFilter
{
    private readonly string? _namespace;
    private readonly Accessibility? _accessibility;
    private readonly Contracts.Enums.SymbolKind? _kind;
    private readonly string? _name;
    private readonly bool? _isStatic;
    private readonly bool? _isAbstract;
    private readonly bool? _isSealed;

    private ResultSymbolFilter(
        string? @namespace,
        Accessibility? accessibility,
        Contracts.Enums.SymbolKind? kind,
        string? name,
        bool? isStatic,
        bool? isAbstract,
        bool? isSealed)
    {
        _namespace = @namespace;
        _accessibility = accessibility;
        _kind = kind;
        _name = name;
        _isStatic = isStatic;
        _isAbstract = isAbstract;
        _isSealed = isSealed;
    }

    /// <summary>
    /// Parses the raw filter values: <paramref name="namespaceFilter"/> via
    /// <see cref="NamespaceFilter.Normalize"/>, <paramref name="accessibilityFilter"/> via
    /// <see cref="AccessibilityFilterParser.Parse"/>, <paramref name="kindFilter"/> via
    /// <see cref="SymbolKindFilterParser.Parse"/>, <paramref name="nameFilter"/> via
    /// <see cref="NameFilterMatcher.Normalize"/>; <paramref name="staticFilter"/> and
    /// <paramref name="abstractFilter"/> / <paramref name="sealedFilter"/> are used as-is. Omitted / blank values impose no filter.
    /// </summary>
    public static ResultSymbolFilter Create(
        string? namespaceFilter,
        string? accessibilityFilter,
        string? kindFilter,
        string? nameFilter,
        bool? staticFilter,
        bool? abstractFilter,
        bool? sealedFilter = null) =>
        new(
            NamespaceFilter.Normalize(namespaceFilter),
            AccessibilityFilterParser.Parse(accessibilityFilter),
            SymbolKindFilterParser.Parse(kindFilter),
            NameFilterMatcher.Normalize(nameFilter),
            staticFilter,
            abstractFilter,
            sealedFilter);

    /// <summary>
    /// Whether an accessibilityFilter, staticFilter, abstractFilter or sealedFilter is set — the filters on declaration
    /// modifiers that a namespace (no modifiers) can never satisfy in <c>search_symbols</c>.
    /// </summary>
    public bool ConstrainsModifiers => _accessibility.HasValue || _isStatic.HasValue || _isAbstract.HasValue || _isSealed.HasValue;

    /// <summary>
    /// Whether <paramref name="symbol"/> satisfies every set filter: containing namespace equal to or nested
    /// in the filter (<see cref="NamespaceFilter.IsInNamespace"/>), exact
    /// <see cref="ISymbol.DeclaredAccessibility"/> equality (implicit defaults count; protected internal ≠
    /// protected / internal), <see cref="SymbolKindMapper.Map"/> equality, <see cref="NameFilterMatcher.Matches"/>
    /// on the simple <see cref="ISymbol.Name"/>, <see cref="ISymbol.IsStatic"/> equality and
    /// <see cref="ISymbol.IsAbstract"/> equality and <see cref="ISymbol.IsSealed"/> equality. Always <c>true</c> when no filter is set.
    /// </summary>
    public bool Matches(ISymbol symbol) =>
        (_namespace == null || NamespaceFilter.IsInNamespace(symbol, _namespace))
        && (!_accessibility.HasValue || symbol.DeclaredAccessibility == _accessibility.Value)
        && (!_kind.HasValue || SymbolKindMapper.Map(symbol) == _kind.Value)
        && NameFilterMatcher.Matches(symbol.Name, _name)
        && (!_isStatic.HasValue || symbol.IsStatic == _isStatic.Value)
        && (!_isAbstract.HasValue || symbol.IsAbstract == _isAbstract.Value)
        && (!_isSealed.HasValue || symbol.IsSealed == _isSealed.Value);
}
