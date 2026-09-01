namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the convert_to_interpolated_string tool.
/// </summary>
public sealed class ConvertToInterpolatedStringParams
{
    /// <summary>
    /// Absolute path to the source file. Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process all C# documents in the solution instead of a single expression.
    /// When true, <see cref="SourceFile"/> is optional. Cannot be combined with
    /// <see cref="Line"/> or <see cref="Column"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// 1-based line number of the string.Format or concatenation expression to convert.
    /// Required when <see cref="AllFiles"/> is false. Single-expression only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation when more than one convertible
    /// expression shares a line. Optional. When set, selects the
    /// <c>string.Format</c> invocation or concatenation whose span covers
    /// that column. Omitted keeps today's first-match on the line so
    /// indented expressions still convert. Spec default is 1; do not force
    /// that default when the parameter is omitted. Single-expression only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
