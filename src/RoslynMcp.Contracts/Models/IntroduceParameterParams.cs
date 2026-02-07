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
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
