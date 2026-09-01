namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the move_type_to_namespace tool.
/// </summary>
public sealed class MoveTypeToNamespaceParams
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
    /// When <see cref="Column"/> is omitted, matching stays today's
    /// start-line equality among top-level types that share
    /// <see cref="SymbolName"/>. A single match ignores line. Multiple
    /// matches with omitted line is <c>SymbolAmbiguous</c>.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set with <see cref="Line"/>,
    /// selects the top-level type whose identifier or declaration span
    /// covers that column (identifier preferred, then smallest covering
    /// type). Omitted keeps today's symbolName + optional line pick.
    /// Column without line keeps today's omitted-line path (start-line
    /// equality / <c>SymbolAmbiguous</c> / single-match ignores line).
    /// Nested types stay unmoveable.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Target namespace (e.g., MyApp.Services).
    /// </summary>
    public required string TargetNamespace { get; init; }

    /// <summary>
    /// Also move file to match namespace folder structure. Default: false.
    /// </summary>
    public bool UpdateFileLocation { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
