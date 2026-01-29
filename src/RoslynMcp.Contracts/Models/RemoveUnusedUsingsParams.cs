namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the remove_unused_usings tool.
/// </summary>
public sealed class RemoveUnusedUsingsParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
