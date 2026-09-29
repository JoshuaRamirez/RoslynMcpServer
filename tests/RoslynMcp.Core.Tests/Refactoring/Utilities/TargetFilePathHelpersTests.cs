using RoslynMcp.Contracts.Errors;
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
}
