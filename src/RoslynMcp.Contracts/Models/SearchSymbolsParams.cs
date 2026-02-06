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
}
