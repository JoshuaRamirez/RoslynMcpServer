using Xunit;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Tests.FileSystem;

public class AtomicFileWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AtomicFileWriter _writer;

    public AtomicFileWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AtomicFileWriterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _writer = new AtomicFileWriter();
    }

    [Fact]
    public async Task WriteAsync_CreatesNewFile()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "test.cs");
        var content = "public class Test { }";

        // Act
        await _writer.WriteAsync(filePath, content);

        // Assert
        Assert.True(File.Exists(filePath));
        Assert.Equal(content, await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task WriteAsync_OverwritesExistingFile()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "existing.cs");
        await File.WriteAllTextAsync(filePath, "old content");
        var newContent = "new content";

        // Act
        await _writer.WriteAsync(filePath, newContent);

        // Assert
        Assert.Equal(newContent, await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task WriteAsync_CreatesDirectoryIfNotExists()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "subdir", "nested", "test.cs");
        var content = "test content";

        // Act
        await _writer.WriteAsync(filePath, content);

        // Assert
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void Delete_RemovesExistingFile()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "todelete.cs");
        File.WriteAllText(filePath, "content");

        // Act
        _writer.Delete(filePath);

        // Assert
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void Delete_DoesNothingIfFileNotExists()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "nonexistent.cs");

        // Act & Assert (should not throw)
        _writer.Delete(filePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
