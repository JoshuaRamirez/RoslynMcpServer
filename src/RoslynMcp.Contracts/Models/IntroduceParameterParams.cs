namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the introduce_parameter tool.
/// </summary>
public sealed class IntroduceParameterParams
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
    /// Name of the local variable to promote to a parameter. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? VariableName { get; init; }

    /// <summary>
    /// 1-based line number where the variable is declared.
    /// Required when <see cref="AllFiles"/> is false. Omitted
    /// <see cref="Column"/> keeps today's start-line equality on the local
    /// declaration statement, then <see cref="VariableName"/>
    /// <c>FirstOrDefault</c>. Single-site only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set, selects the matching
    /// <c>VariableDeclaratorSyntax</c> whose identifier or declaration
    /// span covers that column (identifier preferred, then smallest
    /// covering declarator). A continuation-line identifier is eligible —
    /// do not require the declaration statement to start on
    /// <see cref="Line"/>. Omitted keeps today's start-line equality on
    /// the local declaration statement, then
    /// <see cref="VariableName"/> <c>FirstOrDefault</c>. Single-site only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}
