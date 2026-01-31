namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the extract_constant tool.
/// </summary>
public sealed class ExtractConstantParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Start line of the literal expression (1-based).
    /// </summary>
    public required int StartLine { get; init; }

    /// <summary>
    /// Start column of the literal expression (1-based).
    /// </summary>
    public required int StartColumn { get; init; }

    /// <summary>
    /// End line of the literal expression (1-based).
    /// </summary>
    public required int EndLine { get; init; }

    /// <summary>
    /// End column of the literal expression (1-based).
    /// </summary>
    public required int EndColumn { get; init; }

    /// <summary>
    /// Name for the new constant.
    /// </summary>
    public required string ConstantName { get; init; }

    /// <summary>
    /// Visibility of the constant. Default: "private".
    /// </summary>
    public string Visibility { get; init; } = "private";

    /// <summary>
    /// Replace all occurrences of the same literal in the class. Default: false.
    /// </summary>
    public bool ReplaceAll { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
