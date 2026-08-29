namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the invert_if tool.
/// </summary>
public sealed class InvertIfParams
{
    /// <summary>
    /// Absolute path to the source file containing the if statement.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// 1-based line number of the if keyword.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// 1-based column on the if keyword. Optional; used to disambiguate when
    /// multiple if statements share a line.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
