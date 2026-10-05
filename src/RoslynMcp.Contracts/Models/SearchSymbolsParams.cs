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
}
