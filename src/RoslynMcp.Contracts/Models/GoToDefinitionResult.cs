namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a go-to-definition query.
/// </summary>
public sealed class GoToDefinitionResult
{
    /// <summary>
    /// Definition locations (may have multiple for partial classes).
    /// </summary>
    public required IReadOnlyList<DefinitionLocation> Definitions { get; init; }
}
