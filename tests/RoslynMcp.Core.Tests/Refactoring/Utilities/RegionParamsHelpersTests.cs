using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="RegionParamsHelpers.ValidateRegionParams"/> —
/// each error code / message, check order, the success path, and that the
/// analyze_control_flow / analyze_data_flow <c>Validate</c> forwarders keep
/// the same behavior.
/// </summary>
public class RegionParamsHelpersTests
{
    private static string MissingPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpRegionParams_" + Guid.NewGuid().ToString("N") + ".cs");

    private static RefactoringException Throws(
        string? sourceFile,
        int startLine,
        int endLine,
        int? startColumn = null,
        int? endColumn = null) =>
        Assert.Throws<RefactoringException>(() =>
            RegionParamsHelpers.ValidateRegionParams(sourceFile, startLine, endLine, startColumn, endColumn));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingSourceFile_ThrowsMissingRequiredParam(string? sourceFile)
    {
        var ex = Throws(sourceFile, startLine: 0, endLine: 0, startColumn: 0, endColumn: 0);

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("sourceFile is required.", ex.Message);
    }

    [Fact]
    public void RelativeSourceFile_ThrowsInvalidSourcePath_BeforeLineChecks()
    {
        var ex = Throws("relative/File.cs", startLine: 0, endLine: 0);

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("sourceFile must be an absolute path.", ex.Message);
    }

    [Fact]
    public void NonCsSourceFile_ThrowsInvalidSourcePath()
    {
        var ex = Throws(Path.Combine(Path.GetTempPath(), "File.txt"), startLine: 1, endLine: 1);

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("sourceFile must be a .cs file.", ex.Message);
    }

    [Fact]
    public void StartLineBelowOne_ThrowsInvalidLineNumber_BeforeEndLine()
    {
        var ex = Throws(MissingPath(), startLine: 0, endLine: 0);

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("startLine must be >= 1.", ex.Message);
    }

    [Fact]
    public void EndLineBelowOne_ThrowsInvalidLineNumber()
    {
        var ex = Throws(MissingPath(), startLine: 1, endLine: 0);

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("endLine must be >= 1.", ex.Message);
    }

    [Fact]
    public void StartLineAfterEndLine_ThrowsInvalidRegion_BeforeColumnChecks()
    {
        var ex = Throws(MissingPath(), startLine: 5, endLine: 2, startColumn: 0, endColumn: 0);

        Assert.Equal(ErrorCodes.InvalidRegion, ex.ErrorCode);
        Assert.Equal("startLine must be <= endLine.", ex.Message);
    }

    [Fact]
    public void StartColumnBelowOne_ThrowsInvalidColumnNumber_BeforeEndColumn()
    {
        var ex = Throws(MissingPath(), startLine: 1, endLine: 2, startColumn: 0, endColumn: 0);

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("startColumn must be >= 1.", ex.Message);
    }

    [Fact]
    public void EndColumnBelowOne_ThrowsInvalidColumnNumber()
    {
        var ex = Throws(MissingPath(), startLine: 1, endLine: 2, endColumn: 0);

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("endColumn must be >= 1.", ex.Message);
    }

    [Fact]
    public void SameLineStartColumnAfterEndColumn_ThrowsInvalidRegion()
    {
        var ex = Throws(MissingPath(), startLine: 3, endLine: 3, startColumn: 9, endColumn: 4);

        Assert.Equal(ErrorCodes.InvalidRegion, ex.ErrorCode);
        Assert.Equal("startColumn must be <= endColumn when both are set on the same line.", ex.Message);
    }

    [Fact]
    public void DifferentLinesStartColumnAfterEndColumn_ReachesFileCheck()
    {
        var path = MissingPath();
        var ex = Throws(path, startLine: 3, endLine: 4, startColumn: 9, endColumn: 4);

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
        Assert.Equal($"Source file not found: {path}", ex.Message);
    }

    [Fact]
    public void MissingFile_ThrowsSourceFileNotFound()
    {
        var path = MissingPath();
        var ex = Throws(path, startLine: 1, endLine: 1);

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
        Assert.Equal($"Source file not found: {path}", ex.Message);
    }

    [Fact]
    public void ExistingFile_ValidRegion_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpRegionParams_" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(path, "class C { }");
        try
        {
            RegionParamsHelpers.ValidateRegionParams(path, 1, 1, null, null);
            RegionParamsHelpers.ValidateRegionParams(path, 1, 1, 2, 2);
            RegionParamsHelpers.ValidateRegionParams(path, 1, 3, 7, 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnalyzeOperationForwarders_MatchHelper()
    {
        var path = MissingPath();

        var control = Assert.Throws<RefactoringException>(() =>
            AnalyzeControlFlowOperation.Validate(new AnalyzeControlFlowParams
            {
                SourceFile = path,
                StartLine = 2,
                EndLine = 2,
                StartColumn = 5,
                EndColumn = 3
            }));
        var data = Assert.Throws<RefactoringException>(() =>
            AnalyzeDataFlowOperation.Validate(new AnalyzeDataFlowParams
            {
                SourceFile = path,
                StartLine = 2,
                EndLine = 2,
                StartColumn = 5,
                EndColumn = 3
            }));
        var helper = Throws(path, startLine: 2, endLine: 2, startColumn: 5, endColumn: 3);

        Assert.Equal(helper.ErrorCode, control.ErrorCode);
        Assert.Equal(helper.Message, control.Message);
        Assert.Equal(helper.ErrorCode, data.ErrorCode);
        Assert.Equal(helper.Message, data.Message);
    }
}
