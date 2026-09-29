using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class SourceFilePathHelpersTests
{
    private static string AbsCs() =>
        OperatingSystem.IsWindows() ? @"C:\project\src\File.cs" : "/project/src/File.cs";

    private static string AbsTxt() =>
        OperatingSystem.IsWindows() ? @"C:\project\src\File.txt" : "/project/src/File.txt";

    [Fact]
    public void ValidateSourceFilePath_AcceptsAbsoluteCsPath()
    {
        SourceFilePathHelpers.ValidateSourceFilePath(AbsCs());
    }

    [Fact]
    public void ValidateSourceFilePath_RejectsRelativePath()
    {
        var ex = Assert.Throws<RefactoringException>(
            () => SourceFilePathHelpers.ValidateSourceFilePath("relative/File.cs"));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("sourceFile must be an absolute path.", ex.Message);
    }

    [Fact]
    public void ValidateSourceFilePath_RejectsAbsoluteNonCsPath()
    {
        var ex = Assert.Throws<RefactoringException>(
            () => SourceFilePathHelpers.ValidateSourceFilePath(AbsTxt()));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("sourceFile must be a .cs file.", ex.Message);
    }
}
