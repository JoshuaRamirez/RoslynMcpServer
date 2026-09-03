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
    /// When <see cref="Column"/> is omitted, matching stays today's line pick
    /// (single covering candidate returns; several on the line stay SymbolAmbiguous).
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// Name of the struct to create.
    /// </summary>
    public required string NewTypeName { get; init; }

    /// <summary>
    /// 1-based column on the tuple expression. When set with
    /// <see cref="Line"/>, selects the creation whose span covers that column
    /// (exclusive-end; today's unique covering match, else CannotConvert /
    /// SymbolAmbiguous). Omitted keeps today's line pick.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
