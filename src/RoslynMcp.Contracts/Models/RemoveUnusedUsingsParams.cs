namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the remove_unused_usings tool.
/// </summary>
public sealed class RemoveUnusedUsingsParams
{
    /// <summary>
    /// Absolute path to the source file. Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional — limits the walk to that one file.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process all C# documents in the solution instead of a single file.
    /// When true, <see cref="SourceFile"/> is optional (omit to walk the whole solution;
    /// supply to limit the walk via <c>FilterAllFilesDocumentsBySourceFile</c>).
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
