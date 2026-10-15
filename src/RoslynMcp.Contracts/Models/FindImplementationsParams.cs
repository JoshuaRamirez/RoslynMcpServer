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
    /// <see cref="KindFilter"/>, and <see cref="NameFilter"/>, and applied before
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
    /// <see cref="KindFilter"/>, and <see cref="NameFilter"/>, and applied before
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
    /// <see cref="AccessibilityFilter"/>, and <see cref="NameFilter"/> (an implementation must satisfy
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
    /// <see cref="Transitive"/>, <see cref="NamespaceFilter"/>, <see cref="AccessibilityFilter"/>, and
    /// <see cref="KindFilter"/> (an implementation must satisfy all that are set), and applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the name-filtered set.
    /// A value no implementation name contains returns an empty success. Omit, empty, or whitespace
    /// for no name filter.
    /// </summary>
    public string? NameFilter { get; init; }
}
