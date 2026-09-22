namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the extract_variable tool.
/// </summary>
public sealed class ExtractVariableParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible non-trivial expression in every C# document
    /// (or the optional single <see cref="SourceFile"/>).
    /// When true, cannot be combined with <see cref="StartLine"/>,
    /// <see cref="StartColumn"/>, <see cref="EndLine"/>,
    /// <see cref="EndColumn"/>, or <see cref="VariableName"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Start line of the expression (1-based). Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? StartLine { get; init; }

    /// <summary>
    /// Start column of the expression (1-based). Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? StartColumn { get; init; }

    /// <summary>
    /// End line of the expression (1-based). Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? EndLine { get; init; }

    /// <summary>
    /// End column of the expression (1-based). Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? EndColumn { get; init; }

    /// <summary>
    /// Name for the new variable. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, each variable is named from its
    /// expression text (sanitized to a valid identifier).
    /// </summary>
    public string? VariableName { get; init; }

    /// <summary>
    /// Use var instead of explicit type. Default: true.
    /// Valid with <see cref="AllFiles"/> (shared option).
    /// </summary>
    public bool UseVar { get; init; } = true;

    /// <summary>
    /// Replace all equivalent occurrences in the same containing method or block.
    /// Default: false (replace only the selected expression).
    /// Valid with <see cref="AllFiles"/> (shared option; skip sites that cannot honor it).
    /// </summary>
    public bool ReplaceAll { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}
