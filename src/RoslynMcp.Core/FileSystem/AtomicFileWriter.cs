using System.Text;

namespace RoslynMcp.Core.FileSystem;

/// <summary>
/// Writes files atomically using temp-then-rename pattern.
/// Prevents partial writes on crash or cancellation.
/// </summary>
public sealed class AtomicFileWriter : IFileWriter
{
    /// <inheritdoc />
    public async Task WriteAsync(string filePath, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        EnsureDirectoryExists(filePath);

        // Generate temp file in same directory for atomic rename
        var directory = Path.GetDirectoryName(filePath)!;
        var tempFileName = $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp";
        var tempFilePath = Path.Combine(directory, tempFileName);

        try
        {
            // Write to temp file
            await File.WriteAllTextAsync(tempFilePath, content, Encoding.UTF8, cancellationToken);

            // Atomic rename (same filesystem)
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            File.Move(tempFilePath, filePath);
        }
        catch
        {
            // Clean up temp file on any failure
            TryDeleteFile(tempFilePath);
            throw;
        }
    }

    /// <inheritdoc />
    public void Delete(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    /// <inheritdoc />
    public void EnsureDirectoryExists(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
