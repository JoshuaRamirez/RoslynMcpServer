namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the convert_to_pattern_matching tool.
/// </summary>
public sealed class ConvertToPatternMatchingParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// 1-based line number of the if/is chain or switch statement to convert.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation when more than one convertible
    /// switch or if statement shares a line, or when the keyword lives on a
    /// continuation line of a split statement. Optional. When set, selects
    /// the smallest switch or if whose span covers that column (using
    /// <see cref="Line"/>). Do not require the statement to start on
    /// <see cref="Line"/> when this is set. Omitted keeps today's first
    /// start-line switch-then-if pick so indented statements still convert.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
