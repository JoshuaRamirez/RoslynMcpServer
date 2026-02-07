namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a data flow analysis.
/// </summary>
public sealed class AnalyzeDataFlowResult
{
    /// <summary>
    /// Variables read inside the region.
    /// </summary>
    public required IReadOnlyList<string> ReadInside { get; init; }

    /// <summary>
    /// Variables written inside the region.
    /// </summary>
    public required IReadOnlyList<string> WrittenInside { get; init; }

    /// <summary>
    /// Variables that flow data into the region.
    /// </summary>
    public required IReadOnlyList<string> DataFlowsIn { get; init; }

    /// <summary>
    /// Variables that flow data out of the region.
    /// </summary>
    public required IReadOnlyList<string> DataFlowsOut { get; init; }

    /// <summary>
    /// Variables captured by lambdas or local functions.
    /// </summary>
    public required IReadOnlyList<string> Captured { get; init; }

    /// <summary>
    /// Variables always assigned within the region.
    /// </summary>
    public required IReadOnlyList<string> AlwaysAssigned { get; init; }
}
