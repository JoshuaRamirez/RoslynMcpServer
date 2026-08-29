namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the convert_to_interpolated_string tool.
/// </summary>
public sealed class ConvertToInterpolatedStringParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// 1-based line number of the string.Format or concatenation expression to convert.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation when more than one convertible
    /// expression shares a line. Optional. When set, selects the
    /// <c>string.Format</c> invocation or concatenation whose span covers
    /// that column. Omitted keeps today's first-match on the line so
    /// indented expressions still convert. Spec default is 1; do not force
    /// that default when the parameter is omitted.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
