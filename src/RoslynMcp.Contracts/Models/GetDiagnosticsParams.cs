namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the get_diagnostics query.
/// </summary>
public sealed class GetDiagnosticsParams
{
    /// <summary>
    /// Absolute path to a source file to restrict diagnostics to. If null, returns all solution diagnostics.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// Minimum severity to include. Default: Warning (includes Error and Warning).
    /// Valid values: Error, Warning, Info, Hidden, All.
    /// </summary>
    public string? SeverityFilter { get; init; }

    /// <summary>
    /// Maximum number of diagnostics to return. Omit for no cap (return all matching diagnostics).
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Optional diagnostic IDs to restrict results to (e.g. CS0168, CS8019), matched case-insensitively.
    /// Omit or pass an empty list for no ID filter. Applied before <see cref="MaxResults"/>.
    /// </summary>
    public IReadOnlyList<string>? DiagnosticIds { get; init; }

    /// <summary>
    /// Optional diagnostic IDs to drop from results (e.g. CS1591, CS8019), matched case-insensitively.
    /// Omit or pass an empty list for no exclusion. Applied together with <see cref="DiagnosticIds"/>
    /// (exclusion wins when an ID is in both) and before <see cref="MaxResults"/>.
    /// </summary>
    public IReadOnlyList<string>? ExcludeDiagnosticIds { get; init; }

    /// <summary>
    /// Optional absolute path to a <c>.csproj</c> in the loaded workspace to restrict diagnostics to.
    /// Only projects whose file path equals it are compiled and scanned (every target-framework
    /// variant of a multi-targeted project shares that path, so all are included). Omit for the
    /// whole solution. Applied together with <see cref="SourceFile"/> and the ID / severity filters,
    /// and before <see cref="MaxResults"/>.
    /// </summary>
    public string? ProjectPath { get; init; }

    /// <summary>
    /// Optional namespace to restrict diagnostics to. When set, only diagnostics whose
    /// <b>diagnostic site</b> lives in a namespace that equals this value or is nested inside it are
    /// returned — the containing namespace of the code at the diagnostic location (the member, type,
    /// or namespace declaration enclosing it, via <c>SemanticModel.GetEnclosingSymbol</c>), e.g.
    /// <c>App.Services</c> matches sites in <c>App.Services</c> and <c>App.Services.Orders</c>, not
    /// <c>App.ServicesExtra</c>; same matching as <c>find_references</c> / <c>find_callers</c>
    /// <c>namespaceFilter</c>. Compared with <c>StringComparison.Ordinal</c>; surrounding whitespace is
    /// trimmed and a leading <c>global::</c> is accepted. Sites in the global namespace and
    /// diagnostics with no in-source location never match a named filter. Combined with
    /// <see cref="SourceFile"/>, <see cref="ProjectPath"/>, <see cref="SeverityFilter"/>,
    /// <see cref="DiagnosticIds"/>, <see cref="ExcludeDiagnosticIds"/>, <see cref="AccessibilityFilter"/>,
    /// <see cref="MessageFilter"/>, <see cref="KindFilter"/>, <see cref="NameFilter"/>, and <see cref="StaticFilter"/>, and applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the namespace-filtered
    /// set. Omit, blank, or a bare <c>global::</c> (the root namespace) returns diagnostics in all
    /// namespaces.
    /// </summary>
    public string? NamespaceFilter { get; init; }

    /// <summary>
    /// Optional declared accessibility to restrict diagnostics to: same values and parsing as
    /// <c>search_symbols</c> / <c>find_references</c> / <c>find_callers</c> / <c>get_document_outline</c>
    /// / <c>get_symbol_info</c> / <c>find_implementations</c> / <c>get_type_hierarchy</c>
    /// <c>accessibilityFilter</c> (<c>public</c>, <c>internal</c>, <c>protected</c>, <c>private</c>,
    /// <c>protected internal</c>, or <c>private protected</c>; case-insensitive; surrounding whitespace
    /// trimmed and inner whitespace runs collapsed; <c>internal protected</c> / <c>protected private</c>
    /// also accepted). When set, only diagnostics whose <b>diagnostic site</b> — the nearest declared
    /// member at the diagnostic span when the diagnostic is attached to a declaration (e.g. CS0169 on a
    /// field identifier; a field without an initializer does not establish a member semantic scope for
    /// <c>GetEnclosingSymbol</c>), otherwise the member / type / namespace declaration enclosing the
    /// location via <c>SemanticModel.GetEnclosingSymbol</c>, then the same declaring-site walk as
    /// <c>find_references</c> (look through lambdas / local functions / locals / params to the declaring
    /// member) — has an
    /// <c>ISymbol.DeclaredAccessibility</c> equal to it exactly are returned (semantic, so implicit
    /// defaults count: a class member with no modifier is <c>private</c>); the match is exact
    /// (<c>protected internal</c> is not matched by <c>protected</c> or <c>internal</c>). Namespace-level
    /// sites never match (Roslyn reports <c>INamespaceSymbol</c> as <c>Public</c>; rejected explicitly).
    /// Diagnostics with no in-source location never match. Combined with <see cref="SourceFile"/>,
    /// <see cref="ProjectPath"/>, <see cref="SeverityFilter"/>, <see cref="DiagnosticIds"/>,
    /// <see cref="ExcludeDiagnosticIds"/>, <see cref="NamespaceFilter"/>, <see cref="MessageFilter"/>,
    /// <see cref="KindFilter"/>, <see cref="NameFilter"/>, and <see cref="StaticFilter"/>,
    /// and applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the accessibility-filtered
    /// set. Omit or leave blank for no accessibility filter. An unrecognized value fails with
    /// <c>InvalidVisibility</c>.
    /// </summary>
    public string? AccessibilityFilter { get; init; }

    /// <summary>
    /// Optional message text to restrict diagnostics to: keep only diagnostics whose formatted message
    /// (<c>Diagnostic.GetMessage()</c>, the same text returned in each result's <c>Message</c>) contains
    /// this value as an ordinal, case-insensitive substring (same normalization and matching as the
    /// shared <c>nameFilter</c> of <c>get_document_outline</c> / <c>get_symbol_info</c> /
    /// <c>find_references</c>; surrounding whitespace is trimmed). Free text, never rejected. Applies to
    /// diagnostics with or without an in-source location. Combined with <see cref="SourceFile"/>,
    /// <see cref="ProjectPath"/>, <see cref="SeverityFilter"/>, <see cref="DiagnosticIds"/>,
    /// <see cref="ExcludeDiagnosticIds"/>, <see cref="NamespaceFilter"/>,
    /// <see cref="AccessibilityFilter"/>, <see cref="KindFilter"/>, <see cref="NameFilter"/>, and <see cref="StaticFilter"/> (a diagnostic must satisfy all that are set), and applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the message-filtered set.
    /// Omit, empty, or whitespace = no message filter.
    /// </summary>
    public string? MessageFilter { get; init; }

    /// <summary>
    /// Optional symbol kind to restrict diagnostics to: a single <c>SymbolKind</c> name shared with
    /// <c>search_symbols</c> / <c>get_document_outline</c> / <c>get_symbol_info</c> /
    /// <c>find_implementations</c> / <c>get_type_hierarchy</c> / <c>find_callers</c> / <c>find_references</c>
    /// <c>kindFilter</c> (Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field, Event,
    /// Constant, Local, Parameter, Namespace; case-insensitive; surrounding whitespace trimmed; numeric or
    /// comma-combined values rejected). When set, only diagnostics whose <b>diagnostic site</b> — the same
    /// site symbol <see cref="AccessibilityFilter"/> and <see cref="NamespaceFilter"/> resolve (the nearest
    /// declared member at a declaration-attached location such as CS0169 on a field identifier, otherwise
    /// the member / type / namespace declaration enclosing the location via <c>SemanticModel.GetEnclosingSymbol</c>,
    /// then the same declaring-site walk as <c>find_references</c>: look through lambdas / local functions /
    /// locals / params to the declaring member; a site inside a delegate's synthesized <c>Invoke</c> /
    /// <c>BeginInvoke</c> / <c>EndInvoke</c> / <c>.ctor</c> is classified as the delegate itself) — has that
    /// shared kind classification via <c>SymbolKindMapper.Map</c> are returned: methods, constructors,
    /// operators, and property/event accessors are <c>Method</c>; a field (including CS0169 / CS0414 on the
    /// field) is <c>Field</c> (<c>Constant</c> for a const); a property declaration site is <c>Property</c>.
    /// The body of an expression-bodied property / indexer (<c>int P => Missing;</c>) is its getter, so it is
    /// <c>Method</c> like a block-bodied or arrow-bodied <c>get</c> accessor; the property's type, name,
    /// parameters, and initializer stay <c>Property</c>.
    /// Namespace-level sites (unused usings, top-level statements enclosed by the synthesized
    /// <c>&lt;Main&gt;$</c>) never match a named kind filter, including <c>Namespace</c> and <c>Method</c>.
    /// Diagnostics with no in-source location never match. Combined with <see cref="SourceFile"/>,
    /// <see cref="ProjectPath"/>, <see cref="SeverityFilter"/>, <see cref="DiagnosticIds"/>,
    /// <see cref="ExcludeDiagnosticIds"/>, <see cref="NamespaceFilter"/>, <see cref="AccessibilityFilter"/>,
    /// <see cref="MessageFilter"/>, <see cref="NameFilter"/>, and <see cref="StaticFilter"/> (a diagnostic must satisfy all that are set), and applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the kind-filtered set. A kind
    /// no site has returns an empty success. Omit, empty, or whitespace for no kind filter. An unrecognized
    /// value fails with <c>InvalidSymbolKind</c>.
    /// </summary>
    public string? KindFilter { get; init; }

    /// <summary>
    /// Optional name substring to restrict diagnostics to: same normalization and matching as the
    /// shared <c>nameFilter</c> of <c>get_document_outline</c> / <c>get_symbol_info</c> /
    /// <c>find_implementations</c> / <c>get_type_hierarchy</c> / <c>find_callers</c> /
    /// <c>find_references</c> (ordinal, case-insensitive substring; surrounding whitespace trimmed; inner
    /// whitespace not collapsed; free text, never rejected). When set, only diagnostics whose
    /// <b>diagnostic site</b> — the same site symbol <see cref="AccessibilityFilter"/> and
    /// <see cref="KindFilter"/> judge (the nearest declared member at a declaration-attached location
    /// such as CS0169 on a field identifier, otherwise the member / type enclosing the location, with
    /// lambdas / local functions / locals / params looked through to the declaring member) — has a
    /// simple <c>ISymbol.Name</c> containing the value are returned, e.g. <c>Handle</c> for diagnostics
    /// inside <c>HandleOrder</c> or <c>OrderHandler</c>. The name is Roslyn's <c>ISymbol.Name</c>, as in
    /// <c>find_references</c> <c>nameFilter</c>: accessors read <c>get_X</c> / <c>set_X</c> (including the
    /// implicit getter of an expression-bodied property / indexer), constructors <c>.ctor</c>.
    /// Namespace-level sites (unused usings, top-level statements) and diagnostics with no in-source
    /// location never match. Combined with <see cref="SourceFile"/>, <see cref="ProjectPath"/>,
    /// <see cref="SeverityFilter"/>, <see cref="DiagnosticIds"/>, <see cref="ExcludeDiagnosticIds"/>,
    /// <see cref="NamespaceFilter"/>, <see cref="AccessibilityFilter"/>, <see cref="MessageFilter"/>,
    /// <see cref="KindFilter"/>, and <see cref="StaticFilter"/> (a diagnostic must satisfy all that are set), and applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the name-filtered set.
    /// No match returns an empty success. Omit, empty, or whitespace for no name filter.
    /// A diagnostic inside a delegate's synthesized <c>Invoke</c> / <c>BeginInvoke</c> / <c>EndInvoke</c> /
    /// <c>.ctor</c> site resolves to the delegate itself, so the filter matches the delegate's name and
    /// filters like <c>Invoke</c> or <c>.ctor</c> never match such a site.
    /// Matching is a plain ordinal, case-insensitive <c>Contains</c>, so wildcards such as <c>*</c> and
    /// <c>?</c> are not supported and are matched literally.
    /// </summary>
    public string? NameFilter { get; init; }

    /// <summary>
    /// Optional static / instance filter: when <c>true</c>, keep only diagnostics whose <b>diagnostic site</b>
    /// — the same declaring site symbol <see cref="AccessibilityFilter"/>, <see cref="KindFilter"/>, and
    /// <see cref="NameFilter"/> judge (the nearest declared member at a declaration-attached location such as
    /// CS0169 on a field identifier, otherwise the member / type enclosing the location) — has
    /// <c>ISymbol.IsStatic</c> <c>true</c> (static methods, static constructors, operators, static properties /
    /// events and their accessors, static and <c>const</c> fields — Roslyn reports <c>const</c> as static —
    /// static classes, and members of a static class); when <c>false</c>, keep only instance sites. Omit
    /// (<c>null</c>) for no static filter.
    /// <para>
    /// Edge rules: unlike <c>find_references</c>, a declaration-attached diagnostic is judged on the member or
    /// type it is attached to, not the containing type — a member's return or parameter type, an attribute on
    /// a member or on a type, and a field, <c>const</c>, or auto-property initializer follow that member's or
    /// type's own staticness, as does the body of an expression-bodied property (judged on its getter).
    /// Likewise unlike <c>find_references</c>, a top-level type's base list (<c>class B1 : MissingBase</c> is
    /// kept by <c>false</c>) and its type-parameter constraints are judged on the type (<c>find_references</c>
    /// treats both as namespace-level and never matches them), a method's type-parameter constraints are judged
    /// on the method, and doc-comment diagnostics (<c>CS1574</c> unresolved <c>cref</c>, <c>CS1591</c> missing
    /// XML comment) are judged on the documented member or type (<c>find_references</c> judges a member's
    /// <c>cref</c> on the containing type).
    /// Lambdas and local functions, including <c>static</c> lambdas, <c>static</c> local functions, and a
    /// non-static local function inside a static method, are looked through to the declaring member and judged
    /// on its staticness (unlike <c>find_callers</c>, which judges a local function as its own calling symbol).
    /// A finalizer, an indexer (C# has no static indexers), and a delegate type (including a diagnostic in its
    /// signature or synthesized <c>Invoke</c> / <c>BeginInvoke</c> / <c>EndInvoke</c> / <c>.ctor</c>) are
    /// instance; a custom event accessor follows the event; an extension method and a static interface member
    /// are static, a default interface member is instance; members of a nested type are judged on their own
    /// staticness, not the containing type's (instance members of a class nested in a static class are kept by
    /// <c>false</c>); records, record structs, and structs are judged like classes.
    /// Parser / missing-token errors are judged on the declaration containing the missing token (<c>CS1002</c>
    /// on the <c>}</c> of <c>static void M() { int x = 1 }</c> is kept by <c>true</c>). Enum members are
    /// static: a diagnostic on an enum member or its initializer (e.g. <c>CS1591</c> on <c>E.A</c>, or
    /// <c>B = Missing</c>) is kept by <c>true</c>, while one on the enum type itself is kept by <c>false</c>. Explicit
    /// implementations of instance interface members are instance, while an explicit implementation of a static
    /// abstract / static virtual member is static, as are static abstract / static virtual interface members
    /// themselves. Class and record primary-constructor parameters (and a primary constructor's base-type
    /// arguments) follow the instance constructor / type, so they are kept by <c>false</c>; a
    /// <c>: this(Missing)</c> initializer follows its constructor; default parameter values follow their member;
    /// field-like event initializers follow the event. A <c>#warning</c> / <c>#error</c> directive between
    /// members of a type is judged on that type, and one inside a member body on that member.
    /// </para>
    /// <para>
    /// Namespace-level sites — unused usings, <c>[assembly: X]</c> attributes, <c>#warning</c> / <c>#error</c>
    /// directives between namespace-level declarations (outside any type), and top-level statements, including
    /// a <c>static</c> lambda or
    /// <c>static</c> local function declared at top level (enclosed by the synthesized <c>&lt;Main&gt;$</c>,
    /// which <c>find_callers</c> counts as static) — never match either value, and diagnostics with no
    /// in-source location (e.g. CS5001) never match either value, so <c>true</c> and <c>false</c> together
    /// return every in-source, non-namespace-level diagnostic exactly once.
    /// </para>
    /// <para>
    /// Same <c>ISymbol.IsStatic</c> test as <c>get_symbol_info</c> / <c>get_document_outline</c> /
    /// <c>search_symbols</c> / <c>find_callers</c> / <c>find_references</c> / <c>find_implementations</c>
    /// <c>staticFilter</c>; there is no target symbol, so no all-or-nothing rule applies. Combined with
    /// <see cref="SourceFile"/>, <see cref="ProjectPath"/>, <see cref="SeverityFilter"/>,
    /// <see cref="DiagnosticIds"/>, <see cref="ExcludeDiagnosticIds"/>, <see cref="NamespaceFilter"/>,
    /// <see cref="AccessibilityFilter"/>, <see cref="MessageFilter"/>, <see cref="KindFilter"/>, and
    /// <see cref="NameFilter"/> (a diagnostic must satisfy all that are set), and applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the static-filtered set. No
    /// match returns an empty success. A non-boolean JSON value (e.g. <c>"yes"</c>, <c>"true"</c>, <c>1</c>)
    /// fails with <c>INTERNAL_ERROR</c> on the MCP tool; on the CLI, <c>--static-filter</c> alone means
    /// <c>true</c> and a value other than <c>true</c> / <c>false</c> is rejected.
    /// </para>
    /// </summary>
    public bool? StaticFilter { get; init; }
}
