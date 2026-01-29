namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Summary of file changes made by a refactoring operation.
/// </summary>
public sealed class FileChanges
{
    /// <summary>
    /// Paths of files that were modified.
    /// </summary>
    public required IReadOnlyList<string> FilesModified { get; init; }

    /// <summary>
    /// Paths of files that were created.
    /// </summary>
    public required IReadOnlyList<string> FilesCreated { get; init; }

    /// <summary>
    /// Paths of files that were deleted.
    /// </summary>
    public required IReadOnlyList<string> FilesDeleted { get; init; }

    /// <summary>
    /// Creates an empty FileChanges instance.
    /// </summary>
    public static FileChanges Empty => new()
    {
        FilesModified = [],
        FilesCreated = [],
        FilesDeleted = []
    };
}
