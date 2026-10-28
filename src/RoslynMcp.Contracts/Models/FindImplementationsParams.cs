namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for finding implementations of an interface or abstract member.
/// </summary>
public sealed class FindImplementationsParams
{
    /// <summary>
    /// Absolute path to the source file containing the symbol.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the interface, abstract class, or virtual member.
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
    /// Maximum number of implementations to return.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Absolute path to a source file to restrict reported implementations to. When set, only
    /// implementations declared (with an in-source location) in that file are returned, before
    /// <see cref="MaxResults"/>, so <c>TotalCount</c> / <c>Truncated</c> reflect the filtered set.
    /// When omitted, implementations across the whole solution are returned. Distinct from
    /// <see cref="SourceFile"/>, which locates the target symbol.
    /// </summary>
    public string? ImplementationFile { get; init; }

    /// <summary>
    /// Optional absolute path to a <c>.csproj</c> in the loaded workspace to restrict reported
    /// implementations to. When set, only implementations with an in-source location whose document
    /// belongs to a project whose file path equals it are returned, reported at that location (every
    /// target-framework variant of a multi-targeted project shares that path, so all are included);
    /// same validation and matching as <c>get_diagnostics</c> / <c>search_symbols</c> /
    /// <c>find_references</c> / <c>find_callers</c> <c>projectPath</c>, and combined with
    /// <see cref="ImplementationFile"/>. Applied before <see cref="MaxResults"/>, so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the project-scoped set. When omitted or blank,
    /// implementations across the whole solution are returned. <see cref="SourceFile"/> still
    /// locates the target symbol and may be in another project.
    /// </summary>
    public string? ProjectPath { get; init; }

    /// <summary>
    /// Optional: when the target symbol is an interface type, whether to report every implementing
    /// class / struct (<c>true</c>, the default and today's behavior) or only those that implement the
    /// interface directly (<c>false</c>), via Roslyn's
    /// <c>SymbolFinder.FindImplementationsAsync(INamedTypeSymbol, Solution, transitive: false, ...)</c>:
    /// types that only inherit the implementation from a base class, or that implement it through a
    /// derived interface, are then omitted. Same idea as <c>get_type_hierarchy</c> <c>transitive</c>.
    /// <see cref="ImplementationFile"/> / <see cref="ProjectPath"/> filter, and
    /// <see cref="MaxResults"/> caps, the resulting set, so <c>TotalCount</c> / <c>Truncated</c>
    /// reflect it. No effect for member targets or non-interface types. When omitted, <c>true</c>.
    /// </summary>
    public bool? Transitive { get; init; }

    /// <summary>
    /// Optional namespace to restrict reported implementations to. When set, only implementations whose
    /// containing namespace (the namespace enclosing the implementing type, or the member's containing
    /// type for member targets) equals this value or is nested inside it are returned (e.g.
    /// <c>App.Services</c> matches <c>App.Services</c> and <c>App.Services.Orders</c>, not
    /// <c>App.ServicesExtra</c>); same matching as <c>search_symbols</c> <c>namespaceFilter</c>.
    /// Compared with <c>StringComparison.Ordinal</c>; surrounding whitespace is trimmed and a leading
    /// <c>global::</c> is accepted. Combined with <see cref="ImplementationFile"/>,
    /// <see cref="ProjectPath"/>, <see cref="Transitive"/>, <see cref="AccessibilityFilter"/>,
    /// <see cref="KindFilter"/>, <see cref="NameFilter"/>, <see cref="StaticFilter"/>, and <see cref="AbstractFilter"/>, and applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the namespace-filtered set. Implementations in the global namespace never match a named filter. Omit, blank, or a bare
    /// <c>global::</c> (the root namespace) reports implementations in all namespaces.
    /// <see cref="SourceFile"/> still locates the target symbol and may be in another namespace.
    /// </summary>
    public string? NamespaceFilter { get; init; }

    /// <summary>
    /// Optional declared accessibility to restrict reported implementations to: same values and parsing
    /// as <c>search_symbols</c> / <c>get_document_outline</c> / <c>get_symbol_info</c>
    /// <c>accessibilityFilter</c> (<c>public</c>, <c>internal</c>, <c>protected</c>, <c>private</c>,
    /// <c>protected internal</c>, or <c>private protected</c>; case-insensitive; surrounding whitespace
    /// trimmed and inner whitespace runs collapsed; <c>internal protected</c> / <c>protected private</c>
    /// also accepted). When set, only implementations whose <c>ISymbol.DeclaredAccessibility</c> equals
    /// it exactly are returned (semantic, so implicit defaults count: a top-level type with no modifier
    /// is <c>internal</c>, a nested type with no modifier is <c>private</c>); the match is exact
    /// (<c>protected internal</c> is not matched by <c>protected</c> or <c>internal</c>). For member
    /// targets, the implementing member's declared accessibility is used (explicit interface
    /// implementations are <c>private</c>). Combined with <see cref="ImplementationFile"/>,
    /// <see cref="ProjectPath"/>, <see cref="Transitive"/>, <see cref="NamespaceFilter"/>,
    /// <see cref="KindFilter"/>, <see cref="NameFilter"/>, <see cref="StaticFilter"/>, and <see cref="AbstractFilter"/>, and applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the
    /// accessibility-filtered set. Omit or leave blank for no accessibility filter. An unrecognized
    /// value fails with <c>InvalidVisibility</c>.
    /// </summary>
    public string? AccessibilityFilter { get; init; }

    /// <summary>
    /// Optional symbol kind to restrict reported implementations to: a single <c>SymbolKind</c> name
    /// shared with <c>search_symbols</c> / <c>get_document_outline</c> / <c>get_symbol_info</c>
    /// <c>kindFilter</c> (Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field,
    /// Event, Constant, Local, Parameter, Namespace; case-insensitive; surrounding whitespace trimmed;
    /// numeric or comma-combined values rejected). When set, only implementations whose reported
    /// <c>Kind</c> (the same value returned in <c>ImplementationInfo.Kind</c>; records map to
    /// <c>Record</c>, not <c>Class</c>) equals it are returned, e.g. <c>Struct</c> for "which structs
    /// implement this interface?". Combined with <see cref="ImplementationFile"/>,
    /// <see cref="ProjectPath"/>, <see cref="Transitive"/>, <see cref="NamespaceFilter"/>,
    /// <see cref="AccessibilityFilter"/>, <see cref="NameFilter"/>, <see cref="StaticFilter"/>, and <see cref="AbstractFilter"/> (an implementation must satisfy
    /// all that are set), and applied
    /// before <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the
    /// kind-filtered set. A kind that no implementation has (e.g. <c>Local</c>, <c>Parameter</c>,
    /// <c>Namespace</c>) returns an empty success. Omit, empty, or whitespace for no kind filter. An
    /// unrecognized value fails with <c>InvalidSymbolKind</c>.
    /// </summary>
    public string? KindFilter { get; init; }

    /// <summary>
    /// Optional name substring to restrict reported implementations to: keep implementations whose
    /// reported <c>Name</c> (the same value returned in <c>ImplementationInfo.Name</c>, i.e.
    /// <c>ISymbol.Name</c>) contains this value, compared with
    /// <c>StringComparison.OrdinalIgnoreCase</c> (same matching as <c>get_document_outline</c> /
    /// <c>get_symbol_info</c> <c>nameFilter</c>), e.g. <c>Sql</c> for the <c>SqlOrderRepository</c> /
    /// <c>SqlUserRepository</c> implementers of <c>IRepository</c>. Surrounding whitespace is trimmed.
    /// For member targets the implementing member's own name is used: overrides and implicit
    /// implementations usually share the target's name, and explicit interface implementations report
    /// Roslyn's qualified metadata name (e.g. <c>App.IRunner.Run</c>), so this filter is mainly useful
    /// for type targets. Combined with <see cref="ImplementationFile"/>, <see cref="ProjectPath"/>,
    /// <see cref="Transitive"/>, <see cref="NamespaceFilter"/>, <see cref="AccessibilityFilter"/>,
    /// <see cref="KindFilter"/>, <see cref="StaticFilter"/>, and <see cref="AbstractFilter"/> (an implementation must satisfy all that are set), and applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the name-filtered set.
    /// A value no implementation name contains returns an empty success. Omit, empty, or whitespace
    /// for no name filter.
    /// </summary>
    public string? NameFilter { get; init; }

    /// <summary>
    /// Optional static-ness to restrict reported implementations to: <c>true</c> keeps only
    /// implementations whose <c>ISymbol.IsStatic</c> is <c>true</c> (e.g. the static implementations
    /// of a C# 11 <c>static abstract</c> / <c>static virtual</c> interface member), <c>false</c> keeps
    /// only instance implementations. Same <c>ISymbol.IsStatic</c> test as <c>get_symbol_info</c> /
    /// <c>get_document_outline</c> / <c>search_symbols</c> / <c>find_callers</c> / <c>find_references</c> /
    /// <c>get_diagnostics</c> <c>staticFilter</c>. Implementing types are
    /// never static (a static class cannot implement an interface or derive from a class), so for type
    /// targets <c>true</c> returns an empty success while <c>false</c> or <c>null</c> keeps every
    /// implementing type. Unlike <c>find_references</c> / <c>find_callers</c> / <c>get_diagnostics</c>
    /// <c>staticFilter</c>, which judge the enclosing site symbol of each usage / call / diagnostic,
    /// this judges each returned implementation symbol itself. C# requires an implementing member's staticness to equal its
    /// target's, so for a member target the filter is all-or-nothing (the target's staticness
    /// decides). Explicit interface implementations (static or instance) are judged on their own
    /// <c>IsStatic</c>; a default interface member re-implemented explicitly in a derived interface is
    /// returned and judged on that re-implementation (the derived interface itself is not returned for
    /// a type target); records, record structs, structs, nested types, partial members (reported once,
    /// at the defining declaration), generic implementers, and implementers of a self-referencing
    /// generic <c>static abstract</c> member (<c>ISelf&lt;TSelf&gt;</c>) are judged like any other.
    /// Implementations in other projects of the solution are returned and filtered; implementations
    /// declared only in metadata are never listed (for a metadata target they still count toward
    /// <c>TotalCount</c>, so <c>Truncated</c> can be <c>true</c>). Current behavior: static-class
    /// targets, abstract or virtual class targets, and abstract / virtual class members with overrides
    /// (including a generic base such as <c>GB&lt;T&gt;.GM</c>) return no implementations even without
    /// a filter.
    /// Combined with <see cref="ImplementationFile"/>, <see cref="ProjectPath"/>,
    /// <see cref="Transitive"/>, <see cref="NamespaceFilter"/>, <see cref="AccessibilityFilter"/>,
    /// <see cref="KindFilter"/>, <see cref="NameFilter"/>, and <see cref="AbstractFilter"/> (an
    /// implementation must satisfy all that are set), and applied before <see cref="MaxResults"/> so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the static-filtered set. Omit (<c>null</c>) for no static filter. A
    /// non-boolean JSON value fails the <c>find_implementations</c> tool with <c>INTERNAL_ERROR</c>.
    /// </summary>
    public bool? StaticFilter { get; init; }

    /// <summary>
    /// Optional abstractness to restrict reported implementations to: <c>true</c> keeps only
    /// implementations whose <c>ISymbol.IsAbstract</c> is <c>true</c>, <c>false</c> keeps only
    /// non-abstract ones, judged on each returned implementation symbol itself. <c>get_type_hierarchy</c>
    /// <c>abstractFilter</c> tests the same property but the two differ: <c>get_type_hierarchy</c> judges
    /// only types (derived classes and records), while this filter also judges implementing members;
    /// <c>get_type_hierarchy</c> lists derived types declared in metadata (e.g. <c>DbException</c> under
    /// <c>System.Exception</c>), while <c>find_implementations</c> never lists metadata implementations
    /// but still counts them toward <c>TotalCount</c> (e.g. a <c>System.IDisposable</c> target in a
    /// net9.0 project counts 365 / 71 / 294 for omitted / <c>true</c> / <c>false</c> while listing only
    /// the source implementers, so <c>Truncated</c> is <c>true</c>); <c>get_type_hierarchy</c> returns
    /// empty <c>DerivedTypes</c> for an interface root, while <c>find_implementations</c> returns
    /// nothing for a class target and lists the implementers of an interface target, including a
    /// derived interface. For an interface
    /// type target, implementing types are judged: abstract classes count as abstract even with no
    /// abstract members (including abstract records, nested and generic abstract classes, and abstract
    /// classes that implement the interface only explicitly); sealed classes, structs, classes with
    /// only virtual or extern members, and concrete subclasses of an abstract implementer are not.
    /// Filtering does not prune: with <see cref="Transitive"/> <c>true</c> (default), concrete
    /// subclasses of a filtered-out abstract implementer are still listed for <c>false</c>
    /// (with <see cref="Transitive"/> <c>false</c> they are dropped because they do not list the
    /// interface directly). Derived interfaces are never returned for a type target. For an interface
    /// member target (method, property, event), implementing members are judged on their own
    /// <c>IsAbstract</c>, independent of their containing type: an abstract member and an
    /// <c>abstract</c> re-abstraction in a derived interface (<c>abstract void IRunner.Run();</c>) are
    /// abstract; a member with a body inside an abstract class, an explicit implementation, a default
    /// body re-implementation in a derived interface, and virtual members are not. Overrides of an
    /// abstract implementing member are not implementations and are never listed (current
    /// behavior). Implementations of a C# 11 <c>static abstract</c> interface member are static and
    /// never abstract, so <c>true</c> returns an empty success for such a member target (the
    /// implementing type itself is still judged on its own <c>IsAbstract</c> for a type target).
    /// Implementations in other projects of the solution are returned and filtered; implementations
    /// declared only in metadata are never listed (for a metadata target such as
    /// <c>System.IDisposable</c> they are still judged and counted toward <c>TotalCount</c>, so
    /// <c>Truncated</c> can be <c>true</c>). Current behavior: class targets (abstract, virtual, or
    /// concrete) and abstract / virtual class members with overrides return no implementations even
    /// without a filter. A derived-interface target (e.g. <c>IFastRunner : IRunner</c>) is an interface
    /// target like any other: its implementing types are returned and judged the same way. Targeting a
    /// constructed generic interface such as <c>IGen&lt;int&gt;</c> (in a base list or as a field type)
    /// reports the implementers of every construction of <c>IGen&lt;T&gt;</c>, each judged on its own
    /// <c>IsAbstract</c>: omitted gives <c>IntG : IGen&lt;int&gt;</c>, <c>OpenG&lt;T&gt;</c>,
    /// <c>AbsOpenG&lt;T&gt;</c>, and <c>AbsStrG : IGen&lt;string&gt;</c>; <c>true</c> gives
    /// <c>AbsOpenG&lt;T&gt;</c> and <c>AbsStrG</c>; <c>false</c> gives <c>IntG</c> and
    /// <c>OpenG&lt;T&gt;</c> (<see cref="Transitive"/> <c>false</c> keeps the same set). With
    /// <see cref="StaticFilter"/> both filters must match (AND): implementing types are never static, so
    /// <see cref="StaticFilter"/> <c>true</c> returns nothing for a type target whatever this filter is;
    /// implementations of a <c>static abstract</c> interface member are static and never abstract, so for
    /// that member target <see cref="StaticFilter"/> <c>true</c> with this filter <c>false</c> (or
    /// omitted) keeps them, <c>true</c> here is always empty, and <see cref="StaticFilter"/>
    /// <c>false</c> is always empty.
    /// Combined with <see cref="ImplementationFile"/>, <see cref="ProjectPath"/>,
    /// <see cref="Transitive"/>, <see cref="NamespaceFilter"/>, <see cref="AccessibilityFilter"/>,
    /// <see cref="KindFilter"/>, <see cref="NameFilter"/>, and <see cref="StaticFilter"/> (an
    /// implementation must satisfy all that are set), and applied before <see cref="MaxResults"/> so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the abstract-filtered set. Omit (<c>null</c>) for
    /// no abstract filter. A non-boolean JSON value fails the <c>find_implementations</c> tool with
    /// <c>INTERNAL_ERROR</c>. On the CLI a bare <c>--abstract-filter</c> means <c>true</c> and a value
    /// other than <c>true</c> / <c>false</c> (case-insensitive) is rejected.
    /// </summary>
    public bool? AbstractFilter { get; init; }
}
