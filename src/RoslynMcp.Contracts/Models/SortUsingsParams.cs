namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the sort_usings tool.
/// </summary>
public sealed class SortUsingsParams
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
    /// Place System / System.* namespaces first within regular and static groups. Default: true.
    /// Alias usings remain alphabetical by alias name regardless of this flag.
    /// </summary>
    public bool SystemFirst { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
