namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the convert_foreach_linq tool.
/// </summary>
public sealed class ConvertForeachLinqParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// 1-based line number of the foreach statement to convert.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
