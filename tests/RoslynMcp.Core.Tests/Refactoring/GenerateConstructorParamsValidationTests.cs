using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Contracts.Errors;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Tests for GenerateConstructorParams validation.
/// </summary>
public class GenerateConstructorParamsValidationTests
{
    /// <summary>
    /// Returns a platform-appropriate absolute path for test purposes.
    /// On Windows: C:\test\file.cs, on Unix: /test/file.cs
    /// </summary>
    private static string AbsoluteTestPath(string extension = ".cs") =>
        OperatingSystem.IsWindows()
            ? $"C:\\test\\file{extension}"
            : $"/test/file{extension}";
    [Fact]
    public void ValidateParams_MissingSourceFile_ThrowsException()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = "",
            TypeName = "MyClass"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_MissingTypeName_ThrowsException()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = ""
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_RelativePath_ThrowsException()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = "file.cs",
            TypeName = "MyClass"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_ValidParams_ThrowsSourceFileNotFound()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass",
            Members = new List<string> { "Field1", "Field2" },
            AddNullChecks = true
        };

        // Should only fail on file not found
        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_NullMembers_AcceptsNull()
    {
        var @params = new GenerateConstructorParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "MyClass",
            Members = null
        };

        // Should only fail on file not found, not on null members
        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    /// <summary>
    /// Mimics the parameter validation from GenerateConstructorOperation.
    /// </summary>
    private static void ThrowIfInvalidParams(GenerateConstructorParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (!IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    private static bool IsAbsolutePath(string path) =>
        Path.IsPathRooted(path);
}
