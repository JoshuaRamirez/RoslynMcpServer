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
    /// <see cref="ProjectPath"/>, and <see cref="AccessibilityFilter"/>, and applied before
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
    /// with <see cref="CallerFile"/>, <see cref="ProjectPath"/>, and <see cref="NamespaceFilter"/>,
    /// and applied before <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect
    /// the accessibility-filtered set. Omit or leave blank for no accessibility filter. An
    /// unrecognized value fails with <c>InvalidVisibility</c>.
    /// </summary>
    public string? AccessibilityFilter { get; init; }
}
