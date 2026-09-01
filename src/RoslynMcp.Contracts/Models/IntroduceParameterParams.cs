namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the introduce_parameter tool.
/// </summary>
public sealed class IntroduceParameterParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the local variable to promote to a parameter.
    /// </summary>
    public required string VariableName { get; init; }

    /// <summary>
    /// 1-based line number where the variable is declared.
    /// Required. Omitted <see cref="Column"/> keeps today's start-line
    /// equality on the local declaration statement, then
    /// <see cref="VariableName"/> <c>FirstOrDefault</c>.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set, selects the matching
    /// <c>VariableDeclaratorSyntax</c> whose identifier or declaration
    /// span covers that column (identifier preferred, then smallest
    /// covering declarator). A continuation-line identifier is eligible —
    /// do not require the declaration statement to start on
    /// <see cref="Line"/>. Omitted keeps today's start-line equality on
    /// the local declaration statement, then
    /// <see cref="VariableName"/> <c>FirstOrDefault</c>.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
