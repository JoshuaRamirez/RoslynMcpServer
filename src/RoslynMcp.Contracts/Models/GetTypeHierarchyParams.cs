namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the get_type_hierarchy query.
/// </summary>
public sealed class GetTypeHierarchyParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type symbol.
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
    /// Direction: Ancestors, Descendants, or Both. Default: Both.
    /// </summary>
    public string? Direction { get; init; }

    /// <summary>
    /// Maximum number of derived types to return. Omit for no cap (return all derived types).
    /// Does not cap BaseTypes or Interfaces.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Absolute path to a source file to restrict reported derived types to. When set, only
    /// derived types declared (with an in-source location) in that file are returned in
    /// <c>DerivedTypes</c>, before <see cref="MaxResults"/>, so <c>TotalCount</c> /
    /// <c>Truncated</c> reflect the filtered set. Does not filter BaseTypes or Interfaces.
    /// When omitted, derived types across the whole solution are returned. Distinct from
    /// <see cref="SourceFile"/>, which locates the target type.
    /// </summary>
    public string? DerivedFile { get; init; }

    /// <summary>
    /// Optional absolute path to a <c>.csproj</c> in the loaded workspace to restrict reported
    /// derived types to. When set, only derived types with an in-source location whose document
    /// belongs to a project whose file path equals it are returned in <c>DerivedTypes</c>, reported
    /// at that location (every target-framework variant of a multi-targeted project shares that path,
    /// so all are included); same validation and matching as <c>get_diagnostics</c> /
    /// <c>search_symbols</c> / <c>find_references</c> / <c>find_callers</c> /
    /// <c>find_implementations</c> <c>projectPath</c>, and combined with <see cref="DerivedFile"/>.
    /// Applied before <see cref="MaxResults"/>, so <c>TotalCount</c> / <c>Truncated</c> reflect the
    /// project-scoped set. Does not filter BaseTypes or Interfaces. When omitted or blank, derived
    /// types across the whole solution are returned. <see cref="SourceFile"/> still locates the
    /// target type and may be in another project.
    /// </summary>
    public string? ProjectPath { get; init; }

    /// <summary>
    /// Whether <c>DerivedTypes</c> includes every descendant (<c>true</c>, the default) or only
    /// direct subclasses (<c>false</c>): types whose immediate base type is the target type, via
    /// Roslyn <c>SymbolFinder.FindDerivedClassesAsync(..., transitive: false)</c>. When
    /// <c>false</c>, <see cref="DerivedFile"/> and <see cref="ProjectPath"/> filter the direct set
    /// (so a type in the scoped project that derives only through an intermediate type is not
    /// reported) and <see cref="MaxResults"/> is applied afterwards, so <c>TotalCount</c> /
    /// <c>Truncated</c> reflect the direct set. Does not affect BaseTypes or Interfaces, and has no
    /// effect when <c>direction</c> is <c>Ancestors</c>. Omit or <c>true</c> for today's behavior.
    /// </summary>
    public bool? Transitive { get; init; }

    /// <summary>
    /// Optional namespace to restrict reported derived types to. When set, only derived types whose
    /// containing namespace equals this value or is nested inside it are returned in
    /// <c>DerivedTypes</c> (e.g. <c>App.Services</c> matches <c>App.Services</c> and
    /// <c>App.Services.Orders</c>, not <c>App.ServicesExtra</c>); same matching as
    /// <c>search_symbols</c> / <c>find_implementations</c> <c>namespaceFilter</c>. Compared with
    /// <c>StringComparison.Ordinal</c>; surrounding whitespace is trimmed and a leading
    /// <c>global::</c> is accepted. Combined with <see cref="DerivedFile"/>, <see cref="ProjectPath"/>,
    /// <see cref="Transitive"/>, <see cref="AccessibilityFilter"/>, and <see cref="KindFilter"/>, and
    /// applied before <see cref="MaxResults"/> so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the namespace-filtered set. Derived types in the
    /// global namespace never match a named filter. Does not filter BaseTypes or Interfaces. Omit,
    /// blank, or a bare <c>global::</c> (the root namespace) reports derived types in all namespaces.
    /// <see cref="SourceFile"/> still locates the target type and may be in another namespace.
    /// </summary>
    public string? NamespaceFilter { get; init; }

    /// <summary>
    /// Optional declared accessibility to restrict reported derived types to: <c>public</c>,
    /// <c>internal</c>, <c>protected</c>, <c>private</c>, <c>protected internal</c>, or
    /// <c>private protected</c> (case-insensitive; surrounding whitespace trimmed and inner
    /// whitespace runs collapsed; <c>internal protected</c> / <c>protected private</c> also
    /// accepted). Same values, parsing, and <c>InvalidVisibility</c> error as
    /// <c>search_symbols</c> / <c>get_document_outline</c> / <c>get_symbol_info</c> /
    /// <c>find_implementations</c> <c>accessibilityFilter</c>. When set, only derived types whose
    /// <c>DeclaredAccessibility</c> equals it exactly are returned in <c>DerivedTypes</c> (implicit
    /// defaults count: a top-level type with no modifier is <c>internal</c>, a nested type with no
    /// modifier is <c>private</c>; <c>protected internal</c> is not matched by <c>protected</c> or
    /// <c>internal</c>). Combined with <see cref="DerivedFile"/>, <see cref="ProjectPath"/>,
    /// <see cref="Transitive"/>, <see cref="NamespaceFilter"/>, and <see cref="KindFilter"/>, and applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the
    /// accessibility-filtered set. Does not filter BaseTypes or Interfaces. Omit or blank reports
    /// derived types of every accessibility.
    /// </summary>
    public string? AccessibilityFilter { get; init; }

    /// <summary>
    /// Optional symbol kind to restrict reported derived types to: a single <c>SymbolKind</c> name
    /// shared with <c>search_symbols</c> / <c>get_document_outline</c> / <c>get_symbol_info</c> /
    /// <c>find_implementations</c> <c>kindFilter</c> (Class, Struct, Interface, Enum, Record, Delegate,
    /// Method, Property, Field, Event, Constant, Local, Parameter, Namespace; case-insensitive;
    /// surrounding whitespace trimmed; numeric or comma-combined values rejected). When set, only
    /// derived types whose shared <c>SymbolKind</c> classification (the same one
    /// <c>find_implementations</c> reports in <c>ImplementationInfo.Kind</c>) equals it are returned in
    /// <c>DerivedTypes</c>: records match <c>Record</c>, not <c>Class</c>, even though
    /// <c>TypeHierarchyEntry.Kind</c> reports the type's <c>TypeKind</c> name (<c>Class</c> for a
    /// record class). Because C# only lets classes derive from classes and records from records, the
    /// derived types of one target are typically all <c>Class</c> or all <c>Record</c>. Combined with
    /// <see cref="DerivedFile"/>, <see cref="ProjectPath"/>, <see cref="Transitive"/>,
    /// <see cref="NamespaceFilter"/>, and <see cref="AccessibilityFilter"/> (a derived type must satisfy
    /// all that are set), and applied before <see cref="MaxResults"/> so <c>TotalCount</c> /
    /// <c>Truncated</c> reflect the kind-filtered set. A kind that no derived type has returns an empty
    /// success. Does not filter BaseTypes or Interfaces. Omit, empty, or whitespace for no kind filter.
    /// An unrecognized value fails with <c>InvalidSymbolKind</c>, even when <c>direction</c> is
    /// <c>Ancestors</c>.
    /// </summary>
    public string? KindFilter { get; init; }
}
