namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the get_code_metrics query.
/// </summary>
public sealed class GetCodeMetricsParams
{
    /// <summary>
    /// Absolute path to the source file to analyze. If null with symbolName, searches entire solution.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// Name of a specific symbol (type or method) to compute metrics for.
    /// </summary>
    public string? SymbolName { get; init; }

    /// <summary>
    /// 1-based line number for position-based resolution.
    /// </summary>
    public int? Line { get; init; }
}
