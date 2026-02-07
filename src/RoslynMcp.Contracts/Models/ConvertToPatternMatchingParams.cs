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
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
