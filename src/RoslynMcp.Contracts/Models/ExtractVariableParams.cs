namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the extract_variable tool.
/// </summary>
public sealed class ExtractVariableParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Start line of the expression (1-based).
    /// </summary>
    public required int StartLine { get; init; }

    /// <summary>
    /// Start column of the expression (1-based).
    /// </summary>
    public required int StartColumn { get; init; }

    /// <summary>
    /// End line of the expression (1-based).
    /// </summary>
    public required int EndLine { get; init; }

    /// <summary>
    /// End column of the expression (1-based).
    /// </summary>
    public required int EndColumn { get; init; }

    /// <summary>
    /// Name for the new variable.
    /// </summary>
    public required string VariableName { get; init; }

    /// <summary>
    /// Use var instead of explicit type. Default: true.
    /// </summary>
    public bool UseVar { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
