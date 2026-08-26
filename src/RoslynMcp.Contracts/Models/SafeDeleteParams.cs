namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the safe_delete tool.
/// </summary>
public sealed class SafeDeleteParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Start line of the selected symbol (1-based).
    /// </summary>
    public required int StartLine { get; init; }

    /// <summary>
    /// Start column of the selected symbol (1-based).
    /// </summary>
    public required int StartColumn { get; init; }

    /// <summary>
    /// End line of the selected symbol (1-based).
    /// </summary>
    public required int EndLine { get; init; }

    /// <summary>
    /// End column of the selected symbol (1-based).
    /// </summary>
    public required int EndColumn { get; init; }

    /// <summary>
    /// Optional symbol name used to confirm the selection.
    /// </summary>
    public string? SymbolName { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
