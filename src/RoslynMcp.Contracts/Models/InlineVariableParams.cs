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
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
