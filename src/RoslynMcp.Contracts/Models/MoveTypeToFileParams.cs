namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the move_type_to_file tool.
/// </summary>
public sealed class MoveTypeToFileParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// Required when <see cref="AllFiles"/> is false. When
    /// <see cref="AllFiles"/> is true, optional: limits the walk to that
    /// one file, or omit to walk the whole solution.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process all C# documents in the solution instead of a single type.
    /// When true, <see cref="SourceFile"/> is optional. Cannot be combined with
    /// <see cref="SymbolName"/>, <see cref="TargetFile"/>, <see cref="Line"/>,
    /// or <see cref="Column"/>. Bulk extracts every eligible top-level type
    /// into <c>{directory}/{TypeName}.cs</c>; it does not broaden search
    /// for one <see cref="SymbolName"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Name of the type to move (simple name or fully qualified).
    /// Single-site only. Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? SymbolName { get; init; }

    /// <summary>
    /// 1-based line number where symbol is declared (for disambiguation).
    /// When <see cref="Column"/> is omitted, matching stays today's
    /// start-line equality among top-level types that share
    /// <see cref="SymbolName"/>. A single match ignores line. Multiple
    /// matches with omitted line is <c>SymbolAmbiguous</c>.
    /// Single-site only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set with <see cref="Line"/>,
    /// selects the top-level type whose identifier or declaration span
    /// covers that column (identifier preferred, then smallest covering
    /// type). Omitted keeps today's symbolName + optional line pick.
    /// Column without line keeps today's omitted-line path (start-line
    /// equality / <c>SymbolAmbiguous</c> / single-match ignores line).
    /// Nested types stay unmoveable. Single-site only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Absolute path to the target file. Single-site only.
    /// Required when <see cref="AllFiles"/> is false. Bulk derives each
    /// destination as <c>{currentFileDirectory}/{TypeName}.cs</c>.
    /// </summary>
    public string? TargetFile { get; init; }

    /// <summary>
    /// Create target file if it does not exist. Default: true.
    /// With <see cref="AllFiles"/>, when false and the derived destination
    /// is missing, that type is skipped.
    /// </summary>
    public bool CreateTargetFile { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
