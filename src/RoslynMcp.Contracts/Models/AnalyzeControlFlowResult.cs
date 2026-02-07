namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a control flow analysis.
/// </summary>
public sealed class AnalyzeControlFlowResult
{
    /// <summary>
    /// Whether the start point of the region is reachable.
    /// </summary>
    public required bool StartPointReachable { get; init; }

    /// <summary>
    /// Whether the end point of the region is reachable (can fall through).
    /// </summary>
    public required bool EndPointReachable { get; init; }

    /// <summary>
    /// Return statements within the region.
    /// </summary>
    public required IReadOnlyList<ControlFlowStatement> ReturnStatements { get; init; }

    /// <summary>
    /// All exit points from the region (return, break, continue, goto, throw).
    /// </summary>
    public required IReadOnlyList<ControlFlowStatement> ExitPoints { get; init; }
}

/// <summary>
/// A control flow statement found in the analyzed region.
/// </summary>
public sealed class ControlFlowStatement
{
    /// <summary>
    /// The kind of statement (Return, Break, Continue, Goto, Throw).
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// 1-based line number.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// 1-based column number.
    /// </summary>
    public required int Column { get; init; }

    /// <summary>
    /// The statement text.
    /// </summary>
    public string? Text { get; init; }
}
