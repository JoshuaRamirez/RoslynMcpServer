using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class TargetFilePathHelpersTests
{
    private static string AbsCs() =>
        OperatingSystem.IsWindows() ? @"C:\project\src\File.cs" : "/project/src/File.cs";

    private static string AbsTxt() =>
        OperatingSystem.IsWindows() ? @"C:\project\src\File.txt" : "/project/src/File.txt";

    [Fact]
    public void ValidateTargetFilePath_AcceptsAbsoluteCsPath()
    {
        TargetFilePathHelpers.ValidateTargetFilePath(AbsCs());
    }

    [Fact]
    public void ValidateTargetFilePath_RejectsRelativePath()
    {
        var ex = Assert.Throws<RefactoringException>(
            () => TargetFilePathHelpers.ValidateTargetFilePath("relative/File.cs"));

        Assert.Equal(ErrorCodes.InvalidTargetPath, ex.ErrorCode);
        Assert.Equal("targetFile must be an absolute path.", ex.Message);
    }

    [Fact]
    public void ValidateTargetFilePath_RejectsAbsoluteNonCsPath()
    {
        var ex = Assert.Throws<RefactoringException>(
            () => TargetFilePathHelpers.ValidateTargetFilePath(AbsTxt()));

        Assert.Equal(ErrorCodes.InvalidTargetPath, ex.ErrorCode);
        Assert.Equal("targetFile must be a .cs file.", ex.Message);
    }

    private static string SourceDir() =>
        OperatingSystem.IsWindows() ? @"C:\project\src" : "/project/src";

    private static string Explicit() =>
        OperatingSystem.IsWindows() ? @"C:\other\Target.cs" : "/other/Target.cs";

    [Fact]
    public void ResolveTargetFile_ExplicitTargetFileWins()
    {
        var result = TargetFilePathHelpers.ResolveTargetFile(Explicit(), AbsCs(), separateFile: true, allFiles: true, "IFile");

        Assert.Equal(Explicit(), result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveTargetFile_InlineReturnsSourceFile(string? targetFile)
    {
        var result = TargetFilePathHelpers.ResolveTargetFile(targetFile, AbsCs(), separateFile: false, allFiles: false, "IFile");

        Assert.Equal(AbsCs(), result);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ResolveTargetFile_SeparateOrAllFilesReturnsSibling(bool separateFile, bool allFiles)
    {
        var result = TargetFilePathHelpers.ResolveTargetFile(null, AbsCs(), separateFile, allFiles, "FileBase");

        Assert.Equal(PathResolver.Combine(SourceDir(), "FileBase.cs"), result);
    }

    [Fact]
    public void ResolveTargetFile_NoParentDirectoryThrowsInvalidSourcePath()
    {
        var ex = Assert.Throws<RefactoringException>(
            () => TargetFilePathHelpers.ResolveTargetFile(null, "File.cs", separateFile: true, allFiles: false, "IFile"));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("sourceFile must have a parent directory.", ex.Message);
    }
}
