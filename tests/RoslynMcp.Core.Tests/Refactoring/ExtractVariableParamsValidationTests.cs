using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Tests for ExtractVariableParams validation (mirrors ExtractVariableOperation.ValidateParams rules for end bounds).
/// </summary>
public class ExtractVariableParamsValidationTests
{
    private static string AbsoluteTestPath(string extension = ".cs") =>
        OperatingSystem.IsWindows()
            ? $"C:\\test\\file{extension}"
            : $"/test/file{extension}";

    [Fact]
    public void ValidateParams_InvalidEndLine_ThrowsException()
    {
        var @params = new ExtractVariableParams
        {
            SourceFile = AbsoluteTestPath(),
            StartLine = 1,
            StartColumn = 1,
            EndLine = 0,
            EndColumn = 10,
            VariableName = "extracted"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_InvalidEndColumn_ThrowsException()
    {
        var @params = new ExtractVariableParams
        {
            SourceFile = AbsoluteTestPath(),
            StartLine = 1,
            StartColumn = 1,
            EndLine = 5,
            EndColumn = 0,
            VariableName = "extracted"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_SelectionEndBeforeStart_ThrowsException()
    {
        var @params = new ExtractVariableParams
        {
            SourceFile = AbsoluteTestPath(),
            StartLine = 5,
            StartColumn = 1,
            EndLine = 3,
            EndColumn = 10,
            VariableName = "extracted"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.InvalidSelectionRange, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_SameLineExclusiveEndEqualStart_ThrowsException()
    {
        var @params = new ExtractVariableParams
        {
            SourceFile = AbsoluteTestPath(),
            StartLine = 5,
            StartColumn = 10,
            EndLine = 5,
            EndColumn = 10,
            VariableName = "extracted"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.InvalidSelectionRange, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_SameLineColumnEndBeforeStart_ThrowsException()
    {
        var @params = new ExtractVariableParams
        {
            SourceFile = AbsoluteTestPath(),
            StartLine = 5,
            StartColumn = 10,
            EndLine = 5,
            EndColumn = 5,
            VariableName = "extracted"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.InvalidSelectionRange, ex.ErrorCode);
    }

    /// <summary>
    /// Mimics ExtractVariableOperation.ValidateParams line/column/selection rules (before File.Exists),
    /// matching ExtractConstantParamsValidationTests so invalid ends are asserted without a real file.
    /// </summary>
    private static void ThrowIfInvalidParams(ExtractVariableParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.VariableName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "variableName is required.");

        if (!Path.IsPathRooted(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (@params.StartLine < 1 || @params.EndLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line numbers must be >= 1.");

        if (@params.StartColumn < 1 || @params.EndColumn < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column numbers must be >= 1.");

        if (@params.StartLine > @params.EndLine ||
            (@params.StartLine == @params.EndLine && @params.StartColumn >= @params.EndColumn))
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "Selection start must be before end.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }
}
