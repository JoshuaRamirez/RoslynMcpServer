namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the extract_constant tool.
/// </summary>
public sealed class ExtractConstantParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible compile-time literal in every C# document
    /// (or the optional single <see cref="SourceFile"/>).
    /// When true, cannot be combined with <see cref="StartLine"/>,
    /// <see cref="StartColumn"/>, <see cref="EndLine"/>,
    /// <see cref="EndColumn"/>, or <see cref="ConstantName"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Start line of the literal expression (1-based). Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? StartLine { get; init; }

    /// <summary>
    /// Start column of the literal expression (1-based). Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? StartColumn { get; init; }

    /// <summary>
    /// End line of the literal expression (1-based). Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? EndLine { get; init; }

    /// <summary>
    /// End column of the literal expression (1-based). Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? EndColumn { get; init; }

    /// <summary>
    /// Name for the new constant. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, each constant is named from its
    /// literal value text (sanitized to a valid identifier).
    /// </summary>
    public string? ConstantName { get; init; }

    /// <summary>
    /// Visibility of the constant. Default: "private".
    /// Valid with <see cref="AllFiles"/> (shared option; skip sites that cannot honor it).
    /// </summary>
    public string Visibility { get; init; } = "private";

    /// <summary>
    /// Replace all occurrences of the same literal in the class. Default: false.
    /// Valid with <see cref="AllFiles"/> (shared option where it applies).
    /// </summary>
    public bool ReplaceAll { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}
