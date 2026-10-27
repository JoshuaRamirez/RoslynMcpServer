namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the get_document_outline query.
/// </summary>
public sealed class GetDocumentOutlineParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Maximum number of outline nodes to return (DFS pre-order budget). Omit for no cap (return full tree).
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Maximum outline tree depth to return (1 = root entries only). Entries deeper than this are dropped
    /// before the <see cref="MaxResults"/> node budget is applied. Omit for no depth cap (return full tree).
    /// </summary>
    public int? MaxDepth { get; init; }

    /// <summary>
    /// Optional symbol kind to keep (same values and case-insensitive parsing as <c>search_symbols</c>
    /// <c>kindFilter</c>: Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field, Event,
    /// Constant, Local, Parameter, Namespace; <c>Local</c> and <c>Parameter</c> are accepted but never match an
    /// outline entry, so they return an empty outline). Only a single exact kind name is accepted. When set, an outline entry is kept if its kind matches or any descendant
    /// matches (non-matching ancestors are kept only as containers); subtrees with no match are dropped.
    /// Constructors match <c>Method</c> and enum members match <c>Constant</c>. Applied before
    /// <see cref="MaxDepth"/> and <see cref="MaxResults"/>, so <c>TotalCount</c> / <c>Truncated</c>
    /// reflect the kind-filtered tree. An unrecognized value (including numeric
    /// values such as <c>"6"</c> and comma-separated combinations) fails with <c>InvalidSymbolKind</c>.
    /// Omit, empty, or whitespace for no kind filter (return every declaration).
    /// </summary>
    public string? KindFilter { get; init; }

    /// <summary>
    /// Optional name substring to keep. When set, an outline entry is kept if its name contains this value
    /// (ordinal, case-insensitive; leading/trailing whitespace is ignored) or any descendant matches
    /// (non-matching ancestors are kept only as containers); subtrees with no match are dropped, exactly
    /// like <see cref="KindFilter"/>. Combined with <see cref="KindFilter"/>, an entry matches only when it
    /// satisfies both. Applied before <see cref="MaxDepth"/> and <see cref="MaxResults"/>, so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the name-filtered tree; no match returns an empty outline.
    /// Omit, empty, or whitespace for no name filter (return every declaration).
    /// </summary>
    public string? NameFilter { get; init; }

    /// <summary>
    /// Optional declared accessibility to keep (same values and parsing as <c>search_symbols</c>
    /// <c>accessibilityFilter</c>): one of the C# spellings <c>public</c>, <c>internal</c>, <c>protected</c>,
    /// <c>private</c>, <c>protected internal</c>, or <c>private protected</c> (case-insensitive; surrounding
    /// whitespace trimmed and inner whitespace runs collapsed; <c>internal protected</c> /
    /// <c>protected private</c> also accepted). When set, an outline entry is kept if its declared symbol's
    /// <c>ISymbol.DeclaredAccessibility</c> equals it exactly or any descendant matches (non-matching
    /// ancestors are kept only as containers); subtrees with no match are dropped, exactly like
    /// <see cref="KindFilter"/>. Semantic, so implicit defaults count (a top-level type with no modifier is
    /// <c>internal</c>, a class member with no modifier is <c>private</c>, an interface member or enum member
    /// is <c>public</c>) even though the entry's syntactic <c>Accessibility</c> stays <c>null</c>; the match is
    /// exact (<c>protected internal</c> is not matched by <c>protected</c> or <c>internal</c>); namespaces never
    /// match. Combined with <see cref="KindFilter"/> / <see cref="NameFilter"/>, an entry matches only when it
    /// satisfies all of them. Applied before <see cref="MaxDepth"/> and <see cref="MaxResults"/>, so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the filtered tree; no match returns an empty outline. An
    /// unrecognized value fails with <c>InvalidVisibility</c>. Omit, empty, or whitespace for no
    /// accessibility filter (return every declaration).
    /// </summary>
    public string? AccessibilityFilter { get; init; }

    /// <summary>
    /// Optional namespace to keep (same normalization and matching as <c>search_symbols</c> /
    /// <c>get_type_hierarchy</c> <c>namespaceFilter</c>). When set, an outline entry is kept if its declared
    /// symbol lives in this namespace or one nested inside it, or any descendant matches (non-matching
    /// ancestors are kept only as containers); subtrees with no match are dropped, exactly like
    /// <see cref="KindFilter"/>. Type and member entries match when their containing namespace equals the
    /// filter or is nested inside it; namespace entries match when the namespace itself does (e.g.
    /// <c>App.Services</c> matches the entries <c>App.Services</c> and <c>App.Services.Orders</c> and the
    /// types/members declared in them, not <c>App.ServicesExtra</c>). A block namespace declaration that
    /// syntactically encloses a match (<c>Outer</c> in <c>namespace Outer { namespace Inner { ... } }</c> for
    /// <c>Outer.Inner</c>) is kept as a container too, though nested namespaces remain sibling outline roots.
    /// Compared with <c>StringComparison.Ordinal</c>; surrounding whitespace is trimmed and a leading <c>global::</c> is
    /// accepted. Symbols in the global namespace never match a named filter. Combined with
    /// <see cref="KindFilter"/> / <see cref="NameFilter"/> / <see cref="AccessibilityFilter"/>, an entry matches
    /// only when it satisfies all of them. Applied before <see cref="MaxDepth"/> and <see cref="MaxResults"/>,
    /// so <c>TotalCount</c> / <c>Truncated</c> reflect the filtered tree; no match returns an empty outline.
    /// Omit, blank, or a bare <c>global::</c> (the root namespace) for no namespace filter (return every
    /// declaration).
    /// </summary>
    public string? NamespaceFilter { get; init; }

    /// <summary>
    /// Optional static filter. <c>true</c> keeps outline entries whose declared symbol has
    /// <c>ISymbol.IsStatic</c> true (static methods, properties, fields, events, static constructors and
    /// static classes; <c>const</c> fields and enum members count as static, as Roslyn reports them);
    /// <c>false</c> keeps entries whose declared symbol is not static (instance members, instance
    /// constructors, non-static types, delegates); <c>null</c> keeps every entry. Namespaces
    /// never match either value. A matching type keeps only its matching children; a non-matching type (or
    /// namespace) is kept only as a container for matching descendants, and subtrees with no match are dropped,
    /// exactly like <see cref="KindFilter"/>. The outline never lists operators (including conversion
    /// operators), indexers, finalizers, property/event accessors, local functions, or delegates declared
    /// inside a type, so <see cref="StaticFilter"/> can never return them (operators are always static).
    /// Explicit interface implementations are listed and judged by <c>ISymbol.IsStatic</c> (normally instance;
    /// a static abstract interface implementation counts as static), unlike <c>get_symbol_info</c>
    /// <c>Members</c>. Combined with <see cref="KindFilter"/> /
    /// <see cref="NameFilter"/> / <see cref="AccessibilityFilter"/> / <see cref="NamespaceFilter"/>, an entry
    /// matches only when it satisfies all of them. Applied before <see cref="MaxDepth"/> and
    /// <see cref="MaxResults"/>, so <c>TotalCount</c> / <c>Truncated</c> reflect the filtered tree; no match
    /// returns an empty outline. A non-boolean value (e.g. <c>"yes"</c>, the string <c>"true"</c>, or
    /// <c>1</c>) is rejected by the <c>get_document_outline</c> tool with <c>INTERNAL_ERROR</c>.
    /// </summary>
    public bool? StaticFilter { get; init; }
}
