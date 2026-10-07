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
}
