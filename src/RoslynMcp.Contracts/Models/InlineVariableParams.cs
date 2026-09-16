namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the inline_variable tool.
/// </summary>
public sealed class InlineVariableParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible local variable in every C# document
    /// (or the optional single <see cref="SourceFile"/>).
    /// When true, cannot be combined with <see cref="VariableName"/>,
    /// <see cref="Line"/>, or <see cref="Column"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Name of the variable to inline. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? VariableName { get; init; }

    /// <summary>
    /// Line number where the variable is declared (1-based). Optional for disambiguation.
    /// Single-site only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation when more than one matching
    /// local shares a line, or when the identifier lives on a
    /// continuation line of a split declaration. Optional. When set,
    /// selects the declaration whose identifier or declaration span
    /// covers that column. Omitted keeps today's variableName + optional
    /// line start-line pick. Single-site only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}
