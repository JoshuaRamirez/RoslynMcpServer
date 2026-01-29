namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the move_type_to_file tool.
/// </summary>
public sealed class MoveTypeToFileParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to move (simple name or fully qualified).
    /// </summary>
    public required string SymbolName { get; init; }

    /// <summary>
    /// 1-based line number where symbol is declared (for disambiguation).
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Absolute path to the target file.
    /// </summary>
    public required string TargetFile { get; init; }

    /// <summary>
    /// Create target file if it does not exist. Default: true.
    /// </summary>
    public bool CreateTargetFile { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
