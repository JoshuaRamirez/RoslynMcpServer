namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for searching symbols by name pattern across the solution.
/// </summary>
public sealed class SearchSymbolsParams
{
    /// <summary>
    /// Name pattern to search for. Supports substring matching.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Optional filter by symbol kind (e.g., "Class", "Method", "Property").
    /// </summary>
    public string? KindFilter { get; init; }

    /// <summary>
    /// Maximum number of results to return.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Absolute path to a source file to restrict results to. When set, only symbols
    /// whose in-source declaration location is that file are returned. When omitted,
    /// searches the whole solution.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// Whether <see cref="Query"/> is matched case-sensitively. Omit or <c>false</c> for today's
    /// case-insensitive substring match (<c>StringComparison.OrdinalIgnoreCase</c>); <c>true</c>
    /// uses <c>StringComparison.Ordinal</c>, applied before <see cref="MaxResults"/> so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the case-sensitive match set.
    /// </summary>
    public bool? CaseSensitive { get; init; }

    /// <summary>
    /// Whether <see cref="Query"/> must equal the whole symbol name. Omit or <c>false</c> for today's
    /// substring match; <c>true</c> keeps only symbols whose name equals <see cref="Query"/>
    /// (compared with the same case rule as <see cref="CaseSensitive"/>), applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the exact-match set.
    /// </summary>
    public bool? ExactMatch { get; init; }

    /// <summary>
    /// Optional namespace to restrict results to. When set, only symbols whose containing namespace
    /// (the namespace enclosing the symbol or its containing type) equals this value or is nested
    /// inside it are returned (e.g. <c>App.Services</c> matches <c>App.Services</c> and
    /// <c>App.Services.Orders</c>, not <c>App.ServicesExtra</c>). Compared with
    /// <c>StringComparison.Ordinal</c>; surrounding whitespace is trimmed and a leading
    /// <c>global::</c> is accepted. Applied before <see cref="MaxResults"/> so <c>TotalCount</c> /
    /// <c>Truncated</c> reflect the namespace-filtered set. Symbols in the global namespace never match a
    /// named filter. Omit, blank, or a bare <c>global::</c> (the root namespace) searches all namespaces.
    /// </summary>
    public string? NamespaceFilter { get; init; }

    /// <summary>
    /// Optional absolute path to a <c>.csproj</c> in the loaded workspace to restrict results to.
    /// Only projects whose file path equals it are compiled and searched, so only symbols declared in
    /// that project's sources are returned (every target-framework variant of a multi-targeted project
    /// shares that path, so all are included). Omit or leave blank for the whole solution. Applied
    /// together with <see cref="KindFilter"/>, <see cref="SourceFile"/>, <see cref="CaseSensitive"/>,
    /// <see cref="ExactMatch"/>, and <see cref="NamespaceFilter"/>, and before <see cref="MaxResults"/>
    /// so <c>TotalCount</c> / <c>Truncated</c> reflect the project-scoped set.
    /// </summary>
    public string? ProjectPath { get; init; }

    /// <summary>
    /// Optional declared accessibility to restrict results to: one of the C# spellings <c>public</c>,
    /// <c>internal</c>, <c>protected</c>, <c>private</c>, <c>protected internal</c>, or
    /// <c>private protected</c> (case-insensitive; surrounding whitespace trimmed and inner whitespace
    /// runs collapsed; the compound values are also accepted in the reversed modifier order C# allows,
    /// <c>internal protected</c> / <c>protected private</c>). When set, only symbols whose <c>ISymbol.DeclaredAccessibility</c> equals it are
    /// returned, so implicit defaults count (a top-level type with no modifier is <c>internal</c>, a
    /// class member with no modifier is <c>private</c>) and the match is exact (<c>protected internal</c>
    /// is not matched by <c>protected</c> or <c>internal</c>). Symbols without an accessibility
    /// (namespaces) never match. Applied together with the other filters and before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the accessibility-filtered
    /// set. Omit or leave blank for no accessibility filter.
    /// </summary>
    public string? AccessibilityFilter { get; init; }

    /// <summary>
    /// Optional static / instance filter. <c>true</c> keeps only symbols whose <c>ISymbol.IsStatic</c> is
    /// <c>true</c> (static classes, static methods / properties / fields / events, extension methods, and
    /// <c>const</c> fields and enum members, which Roslyn reports as static); <c>false</c> keeps only
    /// symbols whose <c>ISymbol.IsStatic</c> is <c>false</c> (instance members and every non-static type:
    /// classes, structs, interfaces, enums, delegates, records). Namespaces are returned when this is
    /// omitted but dropped when it is set to either value. <c>search_symbols</c> never returns operators
    /// (including conversion operators), constructors (including static constructors), indexers,
    /// property/event accessors, explicit interface implementations, or local functions, so this filter
    /// can never return them (operators and static constructors are static). Finalizers are returned (as
    /// instance methods named <c>Finalize</c>, so only when this is <c>false</c> or omitted). Nested types
    /// and enum members are returned and judged by <c>IsStatic</c> (a nested static class and enum members
    /// count as static). Like <c>get_symbol_info</c> / <c>get_document_outline</c> /
    /// <c>find_implementations</c> <c>staticFilter</c>, this judges each returned symbol itself, unlike
    /// <c>find_references</c> / <c>find_callers</c> / <c>get_diagnostics</c> <c>staticFilter</c>, which
    /// judge the enclosing site symbol of each usage / call / diagnostic. Applied together with the
    /// other filters (a symbol must
    /// satisfy all that are set) and before <see cref="MaxResults"/> so <c>TotalCount</c> /
    /// <c>Truncated</c> reflect the static-filtered set. No match gives empty <c>Symbols</c>,
    /// <c>TotalCount = 0</c>, <c>Truncated = false</c>. Omit for no static filter. A non-boolean value
    /// (e.g. <c>"yes"</c>, the string <c>"true"</c>, or <c>1</c>) is rejected by the
    /// <c>search_symbols</c> tool with <c>INTERNAL_ERROR</c>.
    /// </summary>
    public bool? StaticFilter { get; init; }

    /// <summary>
    /// Optional abstract / non-abstract filter. <c>true</c> keeps only symbols whose
    /// <c>ISymbol.IsAbstract</c> is <c>true</c>: abstract classes and abstract records, interfaces (Roslyn
    /// reports every interface as abstract), <c>abstract</c> and <c>abstract override</c> methods /
    /// properties / events, and interface members without a default body (instance and
    /// <c>static abstract</c>). <c>false</c> keeps only symbols whose <c>IsAbstract</c> is <c>false</c>:
    /// concrete, sealed and static classes (Roslyn reports <c>IsAbstract = false</c> for a static class),
    /// non-abstract records, structs, enums, delegates, members with bodies, <c>virtual</c> /
    /// <c>override</c> / <c>extern</c> members, default interface methods, non-abstract static interface
    /// members, fields, <c>const</c> fields and enum members. Namespaces are returned when this is
    /// omitted but dropped when it is set to either value (they are neither abstract nor concrete).
    /// Explicit interface implementations and re-abstractions (e.g. <c>abstract void IBase.M();</c>) are
    /// never returned by <c>search_symbols</c>, so this filter can never return them. Results are
    /// source-only: <c>search_symbols</c> never returns metadata symbols (a metadata name such as
    /// <c>IDisposable</c> gives an empty result for every value), whereas <c>get_type_hierarchy</c>
    /// <c>abstractFilter</c> lists metadata derived types and <c>find_implementations</c>
    /// <c>abstractFilter</c> counts metadata implementers. It has no target or root: it judges every
    /// name-matched type and member itself, including interfaces (which match <c>true</c>), whereas
    /// <c>get_type_hierarchy</c> judges only derived types (an interface root gives an empty result) and
    /// <c>find_implementations</c> judges only implementers (a class target gives an empty result).
    /// Unlike <see cref="StaticFilter"/>, a <c>static class</c> is static but not abstract, so
    /// <c>abstractFilter = true</c> drops it; both filters drop namespaces. Edge kinds: a generic type is
    /// returned only as its definition (an abstract <c>Base&lt;T&gt;</c> matches <c>true</c>; constructed
    /// generics such as <c>Base&lt;int&gt;</c> are never returned); abstract indexers and
    /// <c>static abstract</c> operators are never returned (they are not referenceable by name); an
    /// <c>abstract</c> event matches <c>true</c> and a field-like event <c>false</c>;
    /// <c>sealed override</c> members and <c>static virtual</c> interface members are <c>false</c>; an
    /// abstract class with no abstract members is <c>true</c>; nested abstract types are <c>true</c>;
    /// record structs and <c>readonly</c> structs are <c>false</c>. Combines with
    /// <see cref="StaticFilter"/> (a <c>static abstract</c> interface member matches
    /// <c>staticFilter = true</c> together with <c>abstractFilter = true</c>) and the other filters (a
    /// symbol must satisfy all that are set), and is applied before <see cref="MaxResults"/> so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the abstract-filtered set. No match gives empty
    /// <c>Symbols</c>, <c>TotalCount = 0</c>, <c>Truncated = false</c>. Omit for no abstract filter. A
    /// non-boolean value (e.g. <c>"yes"</c>, the string <c>"true"</c>, or <c>1</c>) is rejected by the
    /// <c>search_symbols</c> tool with <c>INTERNAL_ERROR</c>. CLI: a bare <c>--abstract-filter</c> means
    /// <c>true</c>, and a value other than <c>true</c> / <c>false</c> is rejected.
    /// </summary>
    public bool? AbstractFilter { get; init; }

    /// <summary>
    /// Optional <c>sealedFilter</c> (boolean) keeps only symbols whose <c>ISymbol.IsSealed</c> is <c>true</c> (sealed
    /// classes and sealed records, including nested and generic definitions; structs, including <c>readonly</c> structs
    /// and record structs; enums and delegates, which Roslyn reports as sealed; and <c>sealed override</c> methods /
    /// properties / events) or only symbols whose <c>IsSealed</c> is <c>false</c> (unsealed classes and records,
    /// including abstract and <c>static</c> classes; interfaces; every interface member, including an explicitly
    /// <c>sealed</c> default-bodied one, which Roslyn reports as non-virtual rather than <c>IsSealed</c>; ordinary /
    /// <c>virtual</c> / <c>abstract</c> / non-sealed <c>override</c> members; members of structs; fields, <c>const</c>
    /// fields and enum members). Namespaces are returned when <c>sealedFilter</c> is omitted but dropped when it is set
    /// to either value. Symbol kinds: <c>search_symbols</c> never returns indexers (so a <c>sealed override</c> indexer
    /// is never returned), operators and conversion operators, accessors, constructors, or explicit interface
    /// implementations, so <c>sealedFilter</c> can never return them; a finalizer is returned (as an instance method
    /// named <c>Finalize</c>) and counts as unsealed. Peer differences: <c>sealedFilter</c> exists only on
    /// <c>search_symbols</c>; <c>get_symbol_info</c>, <c>get_document_outline</c>, <c>get_type_hierarchy</c> and
    /// <c>find_implementations</c> have <c>abstractFilter</c> and <c>staticFilter</c> but no <c>sealedFilter</c>.
    /// Versus <c>abstractFilter</c> in <c>search_symbols</c>: interfaces and body-less interface members are abstract
    /// but not sealed; static classes are neither; structs, enums and delegates are sealed but not abstract; no symbol
    /// is both, so <c>abstractFilter</c> = <c>true</c> with <c>sealedFilter</c> = <c>true</c> is always empty.
    /// <c>search_symbols</c> returns a flat, source-only list from <c>GetSymbolsWithName</c>, so there is no container
    /// rule (unlike <c>get_document_outline</c>) and no target-only member filtering (unlike <c>get_symbol_info</c>); a
    /// metadata name such as <c>String</c> gives an empty result for every <c>sealedFilter</c> value. Unlike
    /// <c>staticFilter</c>, a static class is static but not sealed, so <c>sealedFilter</c> = <c>true</c> drops it; a
    /// struct is sealed but its members are not. Combined with the other filters (all must match) and applied before
    /// <c>maxResults</c> so <c>totalCount</c> / <c>truncated</c> reflect the filtered set; no match gives empty
    /// <c>symbols</c>, <c>totalCount</c> = 0, <c>truncated</c> = false. A non-boolean value (e.g. 'yes', the string
    /// 'true', or 1) is rejected with <c>INTERNAL_ERROR</c>. CLI: bare <c>--sealed-filter</c> = <c>true</c>, and a
    /// value other than true/false (case-insensitive) is rejected.
    /// </summary>
    public bool? SealedFilter { get; init; }
}
