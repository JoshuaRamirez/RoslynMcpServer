namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the sort_usings tool.
/// </summary>
public sealed class SortUsingsParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

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
