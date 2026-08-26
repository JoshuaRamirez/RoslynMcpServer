namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the inline_constant tool.
/// </summary>
public sealed class InlineConstantParams
{
    /// <summary>
    /// Absolute path to the source file containing the constant.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the constant field to inline.
    /// </summary>
    public required string ConstantName { get; init; }

    /// <summary>
    /// Containing type name for disambiguation when multiple constants share a name.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// Remove the constant declaration after inlining when no remaining references exist.
    /// Default: true.
    /// </summary>
    public bool RemoveConstant { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
