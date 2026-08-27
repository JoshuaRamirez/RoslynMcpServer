namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the convert_tuple_to_struct tool.
/// </summary>
public sealed class ConvertTupleToStructParams
{
    /// <summary>
    /// Absolute path to the source file containing the tuple expression.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// 1-based line number of the tuple expression (<c>(1, 2)</c> / <c>(X: 1, Y: 2)</c>).
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// Name of the struct to create.
    /// </summary>
    public required string NewTypeName { get; init; }

    /// <summary>
    /// 1-based column number for disambiguation when multiple tuple expressions share a line.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
