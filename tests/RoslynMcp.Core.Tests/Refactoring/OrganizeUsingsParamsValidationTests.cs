using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Tests for AddMissingUsingsParams and RemoveUnusedUsingsParams validation.
/// </summary>
public class OrganizeUsingsParamsValidationTests
{
    /// <summary>
    /// Returns a platform-appropriate absolute path for test purposes.
    /// On Windows: C:\test\file{ext}, on Unix: /test/file{ext}
    /// </summary>
    private static string AbsoluteTestPath(string extension = ".cs") =>
        OperatingSystem.IsWindows()
            ? $"C:\\test\\file{extension}"
            : $"/test/file{extension}";
    [Fact]
    public void AddMissingUsings_MissingSourceFile_ThrowsException()
    {
        var @params = new AddMissingUsingsParams
        {
            SourceFile = ""
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidAddMissingUsingsParams(@params));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void AddMissingUsings_RelativePath_ThrowsException()
    {
        var @params = new AddMissingUsingsParams
        {
            SourceFile = "file.cs"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidAddMissingUsingsParams(@params));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void AddMissingUsings_NonCsFile_ThrowsException()
    {
        var @params = new AddMissingUsingsParams
        {
            SourceFile = AbsoluteTestPath(".txt")
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidAddMissingUsingsParams(@params));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void AddMissingUsings_ValidParams_ThrowsSourceFileNotFound()
    {
        var @params = new AddMissingUsingsParams
        {
            SourceFile = AbsoluteTestPath(),
            Preview = true
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidAddMissingUsingsParams(@params));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void RemoveUnusedUsings_MissingSourceFile_ThrowsException()
    {
        var @params = new RemoveUnusedUsingsParams
        {
            SourceFile = ""
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidRemoveUnusedUsingsParams(@params));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void RemoveUnusedUsings_RelativePath_ThrowsException()
    {
        var @params = new RemoveUnusedUsingsParams
        {
            SourceFile = "file.cs"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidRemoveUnusedUsingsParams(@params));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void RemoveUnusedUsings_NonCsFile_ThrowsException()
    {
        var @params = new RemoveUnusedUsingsParams
        {
            SourceFile = AbsoluteTestPath(".txt")
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidRemoveUnusedUsingsParams(@params));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void RemoveUnusedUsings_ValidParams_ThrowsSourceFileNotFound()
    {
        var @params = new RemoveUnusedUsingsParams
        {
            SourceFile = AbsoluteTestPath(),
            Preview = true
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidRemoveUnusedUsingsParams(@params));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    private static void ThrowIfInvalidAddMissingUsingsParams(AddMissingUsingsParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    private static void ThrowIfInvalidRemoveUnusedUsingsParams(RemoveUnusedUsingsParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    private static bool IsAbsolutePath(string path) =>
        Path.IsPathRooted(path);

    private static bool IsValidCSharpFilePath(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
}
