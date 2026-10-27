namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the find_callers query.
/// </summary>
public sealed class FindCallersParams
{
    /// <summary>
    /// Absolute path to the source file containing the symbol.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the symbol to find callers for.
    /// </summary>
    public string? SymbolName { get; init; }

    /// <summary>
    /// 1-based line number for position-based resolution.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column number for position-based resolution.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Maximum number of callers to return.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Absolute path to a source file to restrict reported call sites to. When set, only
    /// call sites located in that file are returned (before <see cref="MaxResults"/>, so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the filtered set). When omitted, call
    /// sites across the whole solution are returned. Distinct from <see cref="SourceFile"/>,
    /// which locates the target symbol.
    /// </summary>
    public string? CallerFile { get; init; }

    /// <summary>
    /// Optional absolute path to a <c>.csproj</c> in the loaded workspace to restrict reported call
    /// sites to. When set, only call sites whose document belongs to a project whose file path equals
    /// it are returned (every target-framework variant of a multi-targeted project shares that path,
    /// so all are included); same validation and matching as <c>get_diagnostics</c> /
    /// <c>search_symbols</c> / <c>find_references</c> <c>projectPath</c>, and a path matching no
    /// project is an error rather than an empty result. Omit or leave blank for the whole solution.
    /// Applied together with <see cref="CallerFile"/> and before <see cref="MaxResults"/>, so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the project-scoped set. <see cref="SourceFile"/>
    /// still locates the target symbol and may live in another project.
    /// </summary>
    public string? ProjectPath { get; init; }

    /// <summary>
    /// Optional namespace to restrict reported call sites to. When set, only call sites whose
    /// call site lives in a namespace that equals this value or is nested inside it are returned —
    /// the containing namespace of the code at that location (the enclosing member, type, or
    /// namespace declaration), not the target symbol's namespace (e.g. <c>App.Services</c> matches
    /// sites in <c>App.Services</c> and <c>App.Services.Orders</c>, not <c>App.ServicesExtra</c>); same
    /// matching as <c>search_symbols</c> / <c>find_implementations</c> / <c>find_references</c>
    /// <c>namespaceFilter</c>. Compared with <c>StringComparison.Ordinal</c>; surrounding whitespace
    /// is trimmed and a leading <c>global::</c> is accepted. Combined with <see cref="CallerFile"/>,
    /// <see cref="ProjectPath"/>, <see cref="AccessibilityFilter"/>, <see cref="NameFilter"/>, and <see cref="KindFilter"/>, and applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the
    /// namespace-filtered set. Sites in the global namespace never match a named filter. Omit, blank,
    /// or a bare <c>global::</c> (the root namespace) reports call sites in all namespaces.
    /// <see cref="SourceFile"/> still locates the target symbol and may be in another namespace.
    /// </summary>
    public string? NamespaceFilter { get; init; }

    /// <summary>
    /// Optional declared accessibility to restrict reported call sites to: same values and parsing
    /// as <c>search_symbols</c> / <c>get_document_outline</c> / <c>get_symbol_info</c> /
    /// <c>find_implementations</c> / <c>get_type_hierarchy</c> <c>accessibilityFilter</c>
    /// (<c>public</c>, <c>internal</c>, <c>protected</c>, <c>private</c>, <c>protected internal</c>,
    /// or <c>private protected</c>; case-insensitive; surrounding whitespace trimmed and inner
    /// whitespace runs collapsed; <c>internal protected</c> / <c>protected private</c> also
    /// accepted). When set, only call sites whose <strong>calling symbol's</strong>
    /// <c>ISymbol.DeclaredAccessibility</c> equals it exactly are returned (semantic, so implicit
    /// defaults count: a class member with no modifier is <c>private</c>); the match is exact
    /// (<c>protected internal</c> is not matched by <c>protected</c> or <c>internal</c>). Combined
    /// with <see cref="CallerFile"/>, <see cref="ProjectPath"/>, <see cref="NamespaceFilter"/>,
    /// <see cref="NameFilter"/>, and <see cref="KindFilter"/>, and applied before <see cref="MaxResults"/> so <c>TotalCount</c> /
    /// <c>Truncated</c> reflect the accessibility-filtered set. Omit or leave blank for no accessibility
    /// filter. An unrecognized value fails with <c>InvalidVisibility</c>.
    /// </summary>
    public string? AccessibilityFilter { get; init; }

    /// <summary>
    /// Optional name substring to restrict reported call sites to: keep call sites whose
    /// <strong>calling symbol</strong> — the same symbol <see cref="AccessibilityFilter"/> judges
    /// (<c>caller.CallingSymbol</c>) — has a simple <c>ISymbol.Name</c> that contains this value,
    /// compared with <c>StringComparison.OrdinalIgnoreCase</c> (same matching as
    /// <c>get_document_outline</c> / <c>get_symbol_info</c> / <c>find_implementations</c> /
    /// <c>get_type_hierarchy</c> / <c>find_references</c> <c>nameFilter</c>), e.g. <c>Handle</c> for
    /// callers named <c>HandleOrder</c> / <c>OrderHandler</c>. The matched name is exactly the
    /// reported <c>CallerName</c> (both read <c>caller.CallingSymbol.Name</c>); for a call inside a
    /// property accessor body Roslyn reports the property itself, so it reads and matches as the
    /// property name (e.g. <c>Total</c>, not <c>get_Total</c>).
    /// Surrounding whitespace is trimmed; inner whitespace is not collapsed. Combined with
    /// <see cref="CallerFile"/>, <see cref="ProjectPath"/>, <see cref="NamespaceFilter"/>, and
    /// <see cref="AccessibilityFilter"/>, and <see cref="KindFilter"/> (a site must satisfy all that are
    /// set), and applied before <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect
    /// the name-filtered set.
    /// A value no calling-symbol name contains returns an empty success. Omit, empty, or whitespace
    /// for no name filter.
    /// </summary>
    public string? NameFilter { get; init; }

    /// <summary>
    /// Optional symbol kind to restrict reported call sites to: a single <c>SymbolKind</c> name shared
    /// with <c>search_symbols</c> / <c>get_document_outline</c> / <c>get_symbol_info</c> /
    /// <c>find_implementations</c> / <c>get_type_hierarchy</c> <c>kindFilter</c> (Class, Struct,
    /// Interface, Enum, Record, Delegate, Method, Property, Field, Event, Constant, Local, Parameter,
    /// Namespace; case-insensitive; surrounding whitespace trimmed; numeric or comma-combined values
    /// rejected). When set, only call sites whose <strong>calling symbol</strong> — the same
    /// <c>caller.CallingSymbol</c> that <see cref="AccessibilityFilter"/> and <see cref="NameFilter"/>
    /// judge — has that shared kind classification are returned: methods, constructors, operators,
    /// local functions, and lambdas are <c>Method</c>; a call inside a property accessor body is
    /// reported as the property itself, so it is <c>Property</c>; a field initializer is <c>Field</c>
    /// (<c>Constant</c> for a const). Combined with <see cref="CallerFile"/>, <see cref="ProjectPath"/>,
    /// <see cref="NamespaceFilter"/>, <see cref="AccessibilityFilter"/>, and <see cref="NameFilter"/>
    /// (a site must satisfy all that are set), and applied before <see cref="MaxResults"/> so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the kind-filtered set. A kind no calling symbol has
    /// (e.g. <c>Struct</c>, <c>Local</c>, <c>Namespace</c>) returns an empty success. Omit, empty, or
    /// whitespace for no kind filter. An unrecognized value fails with <c>InvalidSymbolKind</c>.
    /// </summary>
    public string? KindFilter { get; init; }

    /// <summary>
    /// Optional static / instance filter on the <strong>calling symbol</strong> — the same
    /// <c>caller.CallingSymbol</c> that <see cref="AccessibilityFilter"/>, <see cref="NameFilter"/>,
    /// and <see cref="KindFilter"/> judge. <c>true</c> keeps only call sites whose calling symbol has
    /// <c>ISymbol.IsStatic == true</c> (static methods, static constructors, operators, static
    /// properties — a call inside a property accessor body is reported as the property itself —
    /// static field initializers, and <c>const</c> initializers, which Roslyn reports as static);
    /// <c>false</c> keeps only call sites whose calling symbol has <c>IsStatic == false</c> (instance
    /// methods, instance constructors, instance properties, and instance field initializers). Same
    /// <c>IsStatic</c> test as <c>get_symbol_info</c> / <c>get_document_outline</c> /
    /// <c>search_symbols</c> <c>staticFilter</c>, but <c>find_callers</c> can judge callers those tools
    /// never list (local functions, indexers, event accessors, compiler backing fields, and
    /// <c>&lt;Main&gt;$</c> appear in none of them; finalizers never appear in
    /// <c>get_document_outline</c>); the target symbol's own staticness is irrelevant. Unlike
    /// <c>find_references</c> / <c>get_diagnostics</c> <c>staticFilter</c>, which also judge an enclosing
    /// site but look local functions through to the declaring member and never match top-level
    /// statements, <c>find_callers</c> judges a local function as its own calling symbol and counts
    /// top-level statements (<c>&lt;Main&gt;$</c>) as static.
    /// Edge callers: a non-static local function (even inside a static method) is its own calling
    /// symbol and counts as instance, while a <c>static</c> local function counts as static; lambdas,
    /// including <c>static</c> lambdas, are reported as and judged on the enclosing member; a finalizer
    /// is reported as <c>Finalize</c> and counts as instance; an indexer is reported as <c>this[]</c>
    /// and judged on the indexer; a call in a custom event accessor (<c>add</c> / <c>remove</c>) is
    /// reported as the event and judged on it; an auto-property initializer is reported as the backing
    /// field <c>&lt;P&gt;k__BackingField</c> and judged on it (static for a static property); members of
    /// a nested type are judged on their own staticness, not the containing type's; a <c>const</c>
    /// initializer that references another <c>const</c> is judged on the referencing <c>const</c>
    /// (static); top-level statements are reported as the synthesized entry point
    /// <c>&lt;Main&gt;$</c>, which is static.
    /// Combined with <see cref="CallerFile"/>, <see cref="ProjectPath"/>, <see cref="NamespaceFilter"/>,
    /// <see cref="AccessibilityFilter"/>, <see cref="NameFilter"/>, and <see cref="KindFilter"/> (a site
    /// must satisfy all that are set), and applied before <see cref="MaxResults"/> so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the static-filtered set. No match gives empty
    /// <c>Callers</c>, <c>TotalCount = 0</c>, <c>Truncated = false</c>. Omit for no static filter. A
    /// non-boolean value (e.g. <c>"yes"</c>, the string <c>"true"</c>, or <c>1</c>) is rejected by the
    /// <c>find_callers</c> tool with <c>INTERNAL_ERROR</c>.
    /// </summary>
    public bool? StaticFilter { get; init; }
}
