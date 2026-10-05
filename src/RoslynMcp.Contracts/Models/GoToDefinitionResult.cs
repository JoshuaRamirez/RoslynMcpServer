namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a go-to-definition query.
/// </summary>
public sealed class GoToDefinitionResult
{
    /// <summary>
    /// Definition locations (may have multiple for partial classes; may be truncated when maxResults is set).
    /// </summary>
    public required IReadOnlyList<DefinitionLocation> Definitions { get; init; }

    /// <summary>
    /// Total count of definition locations found (may exceed Definitions.Count if truncated).
    /// </summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// Whether Definitions was truncated due to maxResults.
    /// </summary>
    public bool Truncated { get; init; }
}
