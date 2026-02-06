namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a find references query.
/// </summary>
public sealed class FindReferencesResult
{
    /// <summary>
    /// Name of the symbol that was searched.
    /// </summary>
    public required string SymbolName { get; init; }

    /// <summary>
    /// Fully qualified name of the symbol.
    /// </summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>
    /// All reference locations found.
    /// </summary>
    public required IReadOnlyList<ReferenceLocationInfo> References { get; init; }

    /// <summary>
    /// Total number of references found (may exceed References.Count if truncated).
    /// </summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// Whether the result was truncated due to maxResults.
    /// </summary>
    public bool Truncated { get; init; }
}
