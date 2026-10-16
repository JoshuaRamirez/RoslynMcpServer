namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for finding all references to a symbol.
/// </summary>
public sealed class FindReferencesParams
{
    /// <summary>
    /// Absolute path to the source file containing the symbol.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the symbol to find references for.
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
    /// Maximum number of references to return.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Whether to include the symbol's own definition locations (<c>IsDefinition = true</c>).
    /// Omit or <c>true</c> for today's behavior; <c>false</c> returns usages only, applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the usage-only set.
    /// </summary>
    public bool? IncludeDeclaration { get; init; }

    /// <summary>
    /// Absolute path to a source file to restrict reported reference locations to. When set, only
    /// locations (usages and, when <see cref="IncludeDeclaration"/> is on, definition locations)
    /// located in that file are returned, before <see cref="MaxResults"/>, so <c>TotalCount</c> /
    /// <c>Truncated</c> reflect the filtered set. When omitted, locations across the whole solution
    /// are returned. Distinct from <see cref="SourceFile"/>, which locates the target symbol.
    /// </summary>
    public string? ReferenceFile { get; init; }

    /// <summary>
    /// Optional absolute path to a <c>.csproj</c> in the loaded workspace to restrict reported reference
    /// locations to. When set, only locations (usages and, when <see cref="IncludeDeclaration"/> is on,
    /// definition locations) whose document belongs to a project whose file path equals it are returned
    /// (every target-framework variant of a multi-targeted project shares that path, so all are
    /// included); same validation and matching as <c>get_diagnostics</c> / <c>search_symbols</c>
    /// <c>projectPath</c>, and a path matching no project is an error rather than an empty result.
    /// Omit or leave blank for the whole solution. Applied together with <see cref="ReferenceFile"/>
    /// and before <see cref="MaxResults"/>, so <c>TotalCount</c> / <c>Truncated</c> reflect the
    /// project-scoped set. <see cref="SourceFile"/> still locates the target symbol and may live in
    /// another project.
    /// </summary>
    public string? ProjectPath { get; init; }

    /// <summary>
    /// Optional namespace to restrict reported reference locations to. When set, only locations
    /// (usages and, when <see cref="IncludeDeclaration"/> is on, definition locations) whose
    /// reference site lives in a namespace that equals this value or is nested inside it are
    /// returned — the containing namespace of the code at that location (the enclosing member, type,
    /// or namespace declaration), not the target symbol's namespace (e.g. <c>App.Services</c> matches
    /// sites in <c>App.Services</c> and <c>App.Services.Orders</c>, not <c>App.ServicesExtra</c>); same
    /// matching as <c>search_symbols</c> / <c>find_implementations</c> <c>namespaceFilter</c>.
    /// Compared with <c>StringComparison.Ordinal</c>; surrounding whitespace is trimmed and a leading
    /// <c>global::</c> is accepted. Combined with <see cref="ReferenceFile"/>, <see cref="ProjectPath"/>,
    /// <see cref="IncludeDeclaration"/>, <see cref="AccessibilityFilter"/>, <see cref="NameFilter"/>, and <see cref="KindFilter"/>, and applied before <see cref="MaxResults"/> so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the namespace-filtered set. Sites in the global
    /// namespace (top-level statements, usings outside any namespace) never match a named filter.
    /// Omit, blank, or a bare <c>global::</c> (the root namespace) reports locations in all namespaces.
    /// <see cref="SourceFile"/> still locates the target symbol and may be in another namespace.
    /// </summary>
    public string? NamespaceFilter { get; init; }

    /// <summary>
    /// Optional declared accessibility to restrict reported reference locations to: same values and
    /// parsing as <c>search_symbols</c> / <c>get_document_outline</c> / <c>get_symbol_info</c> /
    /// <c>find_implementations</c> / <c>get_type_hierarchy</c> / <c>find_callers</c>
    /// <c>accessibilityFilter</c> (<c>public</c>, <c>internal</c>, <c>protected</c>, <c>private</c>,
    /// <c>protected internal</c>, or <c>private protected</c>; case-insensitive; surrounding whitespace
    /// trimmed and inner whitespace runs collapsed; <c>internal protected</c> / <c>protected private</c>
    /// also accepted). When set, only usage locations whose reference site's <strong>enclosing
    /// symbol</strong> — the member, type, or namespace declaration enclosing the location, the same
    /// enclosing-symbol notion <see cref="NamespaceFilter"/> uses, not the target symbol — has an
    /// <c>ISymbol.DeclaredAccessibility</c> equal to it exactly are returned (semantic, so implicit
    /// defaults count: a class member with no modifier is <c>private</c>); the match is exact
    /// (<c>protected internal</c> is not matched by <c>protected</c> or <c>internal</c>). Lambdas /
    /// anonymous methods and local functions are looked through to the member declaring them, and
    /// namespace-level sites never match. Definition locations (when <see cref="IncludeDeclaration"/>
    /// is on) are judged by the declared symbol itself (an <c>internal</c> method on a <c>public</c>
    /// type is an <c>internal</c> site), or for a local / parameter / local function by the member
    /// declaring it. Combined with
    /// <see cref="ReferenceFile"/>, <see cref="ProjectPath"/>, <see cref="NamespaceFilter"/>,
    /// <see cref="NameFilter"/>, <see cref="KindFilter"/>, and <see cref="IncludeDeclaration"/>, and applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the
    /// accessibility-filtered set. Omit or leave blank
    /// for no accessibility filter. An unrecognized value fails with <c>InvalidVisibility</c>.
    /// </summary>
    public string? AccessibilityFilter { get; init; }

    /// <summary>
    /// Optional name substring to restrict reported reference locations to: keep locations whose
    /// reference site's <strong>enclosing symbol</strong> — the same site symbol
    /// <see cref="AccessibilityFilter"/> judges (the member or type enclosing each usage, not the target
    /// symbol; lambdas / anonymous methods and local functions are looked through to the member
    /// declaring them) — has a simple <c>ISymbol.Name</c> that contains this value, compared with
    /// <c>StringComparison.OrdinalIgnoreCase</c> (same matching as <c>get_document_outline</c> /
    /// <c>get_symbol_info</c> / <c>find_implementations</c> / <c>get_type_hierarchy</c>
    /// <c>nameFilter</c>), e.g. <c>Handle</c> for the usages inside <c>HandleOrder</c> /
    /// <c>OrderHandler</c>. The name is Roslyn's metadata name of that symbol, so property / indexer /
    /// event accessors read <c>get_X</c> / <c>set_X</c> / <c>add_X</c> / <c>remove_X</c> (still containing
    /// the property or event name), constructors <c>.ctor</c>, static constructors <c>.cctor</c>, and
    /// operators <c>op_Addition</c> and similar. Definition locations (when <see cref="IncludeDeclaration"/>
    /// is on) are judged by the declared symbol itself, or for a local / parameter / local function by the
    /// member declaring it. Namespace-level sites (using aliases, a top-level type's base list, top-level
    /// statements, a namespace's own declaration) never match. Surrounding whitespace is trimmed; inner whitespace is not collapsed.
    /// Combined with <see cref="ReferenceFile"/>, <see cref="ProjectPath"/>, <see cref="NamespaceFilter"/>,
    /// <see cref="AccessibilityFilter"/>, <see cref="KindFilter"/>, and <see cref="IncludeDeclaration"/> (a location must satisfy all
    /// that are set), and applied before <see cref="MaxResults"/> so <c>TotalCount</c> /
    /// <c>Truncated</c> reflect the name-filtered set. A value no site name contains returns an empty
    /// success. Omit, empty, or whitespace for no name filter.
    /// </summary>
    public string? NameFilter { get; init; }

    /// <summary>
    /// Optional symbol kind to restrict reported reference locations to: a single <c>SymbolKind</c> name shared
    /// with <c>search_symbols</c> / <c>get_document_outline</c> / <c>get_symbol_info</c> /
    /// <c>find_implementations</c> / <c>get_type_hierarchy</c> / <c>find_callers</c> <c>kindFilter</c> (Class, Struct,
    /// Interface, Enum, Record, Delegate, Method, Property, Field, Event, Constant, Local, Parameter,
    /// Namespace; case-insensitive; surrounding whitespace trimmed; numeric or comma-combined values
    /// rejected). When set, only locations whose reference site's <strong>enclosing symbol</strong> — the same
    /// site symbol <see cref="AccessibilityFilter"/> and <see cref="NameFilter"/> judge (the member or type
    /// enclosing each usage, not the target symbol; lambdas / anonymous methods and local functions are looked
    /// through to the member declaring them) — has that shared kind classification are returned via
    /// <c>SymbolKindMapper.Map</c>: methods, constructors, operators, and property/event accessors are
    /// <c>Method</c>; a field initializer is <c>Field</c> (<c>Constant</c> for a const); a property declaration
    /// site (definition) is <c>Property</c>. Definition locations (when <see cref="IncludeDeclaration"/> is on)
    /// are judged by the declared symbol itself, or for a local / parameter / local function by the member
    /// declaring it. Namespace-level sites never match a named kind filter; top-level statements, enclosed by the
    /// synthesized <c>&lt;Main&gt;$</c> entry point, are namespace-level sites and never match any kind, including
    /// <c>Method</c>. A site inside a delegate's synthesized <c>Invoke</c> / <c>BeginInvoke</c> / <c>EndInvoke</c> /
    /// <c>.ctor</c> is classified as the delegate itself, so it matches <c>Delegate</c>, not <c>Method</c>. Combined with
    /// <see cref="ReferenceFile"/>, <see cref="ProjectPath"/>, <see cref="NamespaceFilter"/>,
    /// <see cref="AccessibilityFilter"/>, <see cref="NameFilter"/>, and <see cref="IncludeDeclaration"/>
    /// (a location must satisfy all that are set), and applied before <see cref="MaxResults"/> so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the kind-filtered set. A kind no site has returns an empty
    /// success. Omit, empty, or whitespace for no kind filter. An unrecognized value fails with
    /// <c>InvalidSymbolKind</c>.
    /// </summary>
    public string? KindFilter { get; init; }
}
