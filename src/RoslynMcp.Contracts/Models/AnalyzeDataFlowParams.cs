namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the analyze_data_flow query.
/// </summary>
public sealed class AnalyzeDataFlowParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// 1-based start line of the region to analyze.
    /// </summary>
    public required int StartLine { get; init; }

    /// <summary>
    /// 1-based end line of the region to analyze.
    /// </summary>
    public required int EndLine { get; init; }
}
