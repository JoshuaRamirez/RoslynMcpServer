namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for getting detailed information about a symbol.
/// </summary>
public sealed class GetSymbolInfoParams
{
    /// <summary>
    /// Absolute path to the source file containing the symbol.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the symbol to examine.
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
    /// Maximum number of members to return for a type symbol. Omit for no cap (return all members).
    /// Only caps <c>Members</c>; Interfaces, Parameters, Modifiers and other fields are not capped,
    /// and it has no effect on non-type symbols. Applied after <see cref="KindFilter"/>,
    /// <see cref="AccessibilityFilter"/>, <see cref="NameFilter"/>, <see cref="NamespaceFilter"/>, <see cref="StaticFilter"/>, and <see cref="AbstractFilter"/> so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the filtered set.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Optional member kind to restrict the <c>Members</c> list of a named type to: a single
    /// <c>SymbolKind</c> name shared with <c>search_symbols</c> / <c>get_document_outline</c>
    /// <c>kindFilter</c> (Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field,
    /// Event, Constant, Local, Parameter, Namespace; case-insensitive). Combined with
    /// <see cref="AccessibilityFilter"/>, <see cref="NameFilter"/>, and <see cref="NamespaceFilter"/> and
    /// applied before <see cref="MaxResults"/>. Omit, empty, or whitespace = all members. Interfaces,
    /// Parameters, and other fields are not filtered; no effect on non-type symbols.
    /// </summary>
    public string? KindFilter { get; init; }

    /// <summary>
    /// Optional declared accessibility to restrict the <c>Members</c> list of a named type to:
    /// <c>public</c>, <c>internal</c>, <c>protected</c>, <c>private</c>, <c>protected internal</c>, or
    /// <c>private protected</c> (case-insensitive; same values, parsing, and <c>InvalidVisibility</c>
    /// error as <c>search_symbols</c> / <c>get_document_outline</c> <c>accessibilityFilter</c>). A member
    /// is kept when its declared accessibility equals the filter exactly, so implicit defaults count
    /// (a class member with no modifier is <c>private</c>, an interface or enum member is <c>public</c>).
    /// Combined with <see cref="KindFilter"/>, <see cref="NameFilter"/>, and <see cref="NamespaceFilter"/>
    /// (a member must satisfy all that are set) and applied before <see cref="MaxResults"/>. Omit, empty,
    /// or whitespace = all members. Interfaces, Parameters, and other fields are not filtered; no effect
    /// on non-type symbols.
    /// </summary>
    public string? AccessibilityFilter { get; init; }

    /// <summary>
    /// Optional name substring to restrict the <c>Members</c> list of a named type to: keep members
    /// whose simple name contains this value (ordinal, case-insensitive; same matching as
    /// <c>get_document_outline</c> <c>nameFilter</c>). Surrounding whitespace is trimmed. Combined with
    /// <see cref="KindFilter"/>, <see cref="AccessibilityFilter"/>, and <see cref="NamespaceFilter"/> (a
    /// member must satisfy all that are set) and applied before <see cref="MaxResults"/>. Omit, empty, or
    /// whitespace = all members. Interfaces, Parameters, and other fields are not filtered; no effect on
    /// non-type symbols.
    /// </summary>
    public string? NameFilter { get; init; }

    /// <summary>
    /// Optional namespace to restrict the <c>Members</c> list of a named type to: keep members whose
    /// containing namespace equals this value or is nested inside it (ordinal, case-sensitive; same matching as
    /// <c>search_symbols</c> / <c>get_document_outline</c> / <c>find_implementations</c> /
    /// <c>get_type_hierarchy</c> / <c>find_references</c> / <c>find_callers</c> / <c>get_diagnostics</c>
    /// <c>namespaceFilter</c>). Surrounding whitespace is trimmed and a leading <c>global::</c> is
    /// accepted; bare <c>global::</c> filters nothing. Global-namespace members never match a named
    /// filter. Members of a type normally share the type's containing namespace (including nested types),
    /// so a named filter typically keeps all or none of that type's Members depending on whether the
    /// type's namespace matches or is nested under the filter — still valuable for API parity and for
    /// nested-namespace matching (e.g. <c>App</c> keeps members of a type in <c>App.Services</c>).
    /// For metadata types, each member is judged by its own containing namespace.
    /// Combined with <see cref="KindFilter"/>, <see cref="AccessibilityFilter"/>, and
    /// <see cref="NameFilter"/> (a member must satisfy all that are set); order is kindFilter →
    /// accessibilityFilter → nameFilter → namespaceFilter → maxResults, so <c>TotalCount</c> /
    /// <c>Truncated</c> reflect the filtered set. Omit, empty, or whitespace = all members. Interfaces,
    /// Parameters, and other fields are not filtered; no effect on non-type symbols (still accepted /
    /// normalized).
    /// </summary>
    public string? NamespaceFilter { get; init; }

    /// <summary>
    /// Optional static filter for Members of a type symbol. <c>true</c> keeps only members whose
    /// <c>ISymbol.IsStatic</c> is true (const fields count as static, as Roslyn reports them);
    /// <c>false</c> keeps only instance members; <c>null</c> keeps all members. Applied after
    /// <see cref="NamespaceFilter"/> and before <see cref="MaxResults"/> so <c>TotalCount</c> /
    /// <c>Truncated</c> reflect the filtered set. No effect on non-type symbols.
    /// Nested types are judged by <c>IsStatic</c> too: a nested <c>static class</c> counts as static;
    /// any other nested type (class, struct, enum, interface, delegate, record) counts as instance.
    /// Operators (including conversion operators), constructors (including static constructors),
    /// indexers, property / event accessors and explicit interface implementations never appear in
    /// <c>Members</c>, so <c>true</c> cannot return them (a finalizer <c>~T()</c> does appear and counts
    /// as instance). Like <c>get_document_outline</c> / <c>search_symbols</c> / <c>find_implementations</c>
    /// <c>staticFilter</c>, this judges each member's own symbol itself, unlike <c>find_references</c> /
    /// <c>find_callers</c> / <c>get_diagnostics</c> <c>staticFilter</c>, which judge the enclosing site
    /// symbol of each usage / call / diagnostic.
    /// No match gives empty <c>Members</c>, <c>TotalCount = 0</c>, <c>Truncated = false</c>.
    /// A non-boolean value (e.g. <c>"yes"</c>, the string <c>"true"</c>, or <c>1</c>) is rejected by the
    /// <c>get_symbol_info</c> tool with <c>INTERNAL_ERROR</c>.
    /// </summary>
    public bool? StaticFilter { get; init; }

    /// <summary>
    /// Optional abstract filter for Members of a type symbol. <c>true</c> keeps only members whose
    /// <c>ISymbol.IsAbstract</c> is true (<c>abstract</c> / <c>abstract override</c> methods, properties
    /// and events of a class; interface members without a default body, including <c>static abstract</c>
    /// members; nested abstract classes / abstract records and nested interfaces); <c>false</c> keeps only
    /// non-abstract members (members with bodies, <c>virtual</c> / <c>override</c> / <c>sealed override</c>,
    /// <c>extern</c>, fields, constants, default interface members and <c>static virtual</c> interface
    /// members with a body, nested concrete / sealed / static classes, structs, enums and delegates);
    /// <c>null</c> keeps all members. Judged on each member's own symbol (same <c>ISymbol.IsAbstract</c>
    /// test as <c>search_symbols</c> / <c>find_implementations</c> / <c>get_type_hierarchy</c>
    /// <c>abstractFilter</c>), independent of the target type: a concrete type's methods, properties,
    /// events and fields are never abstract, but its nested abstract classes / abstract records and nested
    /// interfaces still match <c>true</c>, so a concrete target is empty for <c>true</c> only when it
    /// declares no such nested types. A static class is static but not abstract.
    /// Abstract indexers, operators (including <c>static abstract</c> operators) and accessors never appear
    /// in <c>Members</c>, so <c>true</c> cannot return them. Combined with <see cref="KindFilter"/>,
    /// <see cref="AccessibilityFilter"/>, <see cref="NameFilter"/>, <see cref="NamespaceFilter"/>, and
    /// <see cref="StaticFilter"/> (a member must satisfy all that are set; e.g. <see cref="StaticFilter"/>
    /// true with <c>true</c> keeps <c>static abstract</c> interface members) and applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the filtered set. No
    /// effect on non-type symbols, Interfaces, or Parameters. No match gives empty <c>Members</c>,
    /// <c>TotalCount = 0</c>, <c>Truncated = false</c>. A non-boolean value (e.g. <c>"yes"</c>, the string
    /// <c>"true"</c>, or <c>1</c>) is rejected by the <c>get_symbol_info</c> tool with
    /// <c>INTERNAL_ERROR</c>; on the CLI a bare <c>--abstract-filter</c> means true.
    /// </summary>
    public bool? AbstractFilter { get; init; }
}
