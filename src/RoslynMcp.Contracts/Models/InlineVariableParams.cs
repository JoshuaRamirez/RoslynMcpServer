namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the inline_variable tool.
/// </summary>
public sealed class InlineVariableParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the variable to inline.
    /// </summary>
    public required string VariableName { get; init; }

    /// <summary>
    /// Line number where the variable is declared (1-based). Optional for disambiguation.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation when more than one matching
    /// local shares a line, or when the identifier lives on a
    /// continuation line of a split declaration. Optional. When set,
    /// selects the declaration whose identifier or declaration span
    /// covers that column. Omitted keeps today's variableName + optional
    /// line start-line pick.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
