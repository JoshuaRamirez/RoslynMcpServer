using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Unit tests for <see cref="ResultFileFilter.Validate"/>, the shared optional result-file filter
/// gate of <c>go_to_definition</c> (<c>definitionFile</c>), <c>find_implementations</c>
/// (<c>implementationFile</c>), <c>find_references</c> (<c>referenceFile</c>), <c>find_callers</c>
/// (<c>callerFile</c>), and <c>get_type_hierarchy</c> (<c>derivedFile</c>): each error code /
/// message, check order, the omitted / blank no-op, and the success path.
/// </summary>
public class ResultFileFilterTests
{
    private static string MissingCsPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpResultFileFilter_" + Guid.NewGuid().ToString("N") + ".cs");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OmittedOrBlank_IsNoOp(string? filePath)
    {
        ResultFileFilter.Validate(filePath, "callerFile");
    }

    [Theory]
    [InlineData("definitionFile")]
    [InlineData("implementationFile")]
    [InlineData("referenceFile")]
    [InlineData("callerFile")]
    [InlineData("derivedFile")]
    public void RelativePath_ThrowsInvalidSourcePath_NamingParam(string paramName)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ResultFileFilter.Validate("relative/File.cs", paramName));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal($"{paramName} must be an absolute path.", ex.Message);
    }

    [Fact]
    public void RelativeNonCsPath_ReportsAbsolutePathFirst()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ResultFileFilter.Validate("relative/File.txt", "referenceFile"));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("referenceFile must be an absolute path.", ex.Message);
    }

    [Theory]
    [InlineData("definitionFile")]
    [InlineData("derivedFile")]
    public void AbsoluteNonCsPath_ThrowsInvalidSourcePath_NamingParam(string paramName)
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpResultFileFilter_" + Guid.NewGuid().ToString("N") + ".txt");

        var ex = Assert.Throws<RefactoringException>(() => ResultFileFilter.Validate(path, paramName));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal($"{paramName} must be a .cs file.", ex.Message);
    }

    [Fact]
    public void MissingCsFile_ThrowsSourceFileNotFound_WithPath()
    {
        var path = MissingCsPath();

        var ex = Assert.Throws<RefactoringException>(() => ResultFileFilter.Validate(path, "implementationFile"));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
        Assert.Equal($"Source file not found: {path}", ex.Message);
    }

    [Fact]
    public void ExistingCsFile_Passes()
    {
        var path = MissingCsPath();
        File.WriteAllText(path, "class C { }");
        try
        {
            ResultFileFilter.Validate(path, "callerFile");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExistingCsFile_UpperCaseExtension_Passes()
    {
        var path = Path.ChangeExtension(MissingCsPath(), ".CS");
        File.WriteAllText(path, "class C { }");
        try
        {
            ResultFileFilter.Validate(path, "derivedFile");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
