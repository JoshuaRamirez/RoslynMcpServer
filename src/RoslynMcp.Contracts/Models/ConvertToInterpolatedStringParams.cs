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
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
