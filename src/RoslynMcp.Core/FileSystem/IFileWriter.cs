namespace RoslynMcp.Core.FileSystem;

/// <summary>
/// Abstraction for file write operations.
/// </summary>
public interface IFileWriter
{
    /// <summary>
    /// Writes content to a file atomically.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    /// <param name="content">Content to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAsync(string filePath, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file if it exists.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    void Delete(string filePath);

    /// <summary>
    /// Ensures the directory for a file path exists.
    /// </summary>
    /// <param name="filePath">Absolute path to a file.</param>
    void EnsureDirectoryExists(string filePath);
}
