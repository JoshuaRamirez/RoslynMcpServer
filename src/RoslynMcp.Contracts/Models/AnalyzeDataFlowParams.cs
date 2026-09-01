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
    /// 1-based start line of the region to analyze. Required.
    /// Omitted <see cref="StartColumn"/> keeps today's start-of-line.
    /// </summary>
    public required int StartLine { get; init; }

    /// <summary>
    /// 1-based end line of the region to analyze. Required.
    /// Omitted <see cref="EndColumn"/> keeps today's end-of-line
    /// (<c>TextLine.End</c>).
    /// </summary>
    public required int EndLine { get; init; }

    /// <summary>
    /// Optional 1-based start column on <see cref="StartLine"/>.
    /// When set, the region starts at that column (Roslyn
    /// <c>Character = column - 1</c>). Omitted keeps today's
    /// start-of-line. Do not force column 1 when omitted.
    /// </summary>
    public int? StartColumn { get; init; }

    /// <summary>
    /// Optional 1-based end column on <see cref="EndLine"/>.
    /// When set, the region ends at that column (Roslyn
    /// <c>Character = column - 1</c>; exclusive-ish, same as
    /// <c>TextLine.End</c> / <c>FileLinePositionSpan.EndLinePosition</c>).
    /// Omitted keeps today's end-of-line. Do not force column 1 when omitted.
    /// </summary>
    public int? EndColumn { get; init; }
}
