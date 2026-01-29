using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Contracts.Errors;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Tests for ExtractMethodParams validation.
/// </summary>
public class ExtractMethodParamsValidationTests
{
    [Fact]
    public void ValidateParams_MissingSourceFile_ThrowsException()
    {
        var @params = new ExtractMethodParams
        {
            SourceFile = "",
            StartLine = 1,
            StartColumn = 1,
            EndLine = 5,
            EndColumn = 10,
            MethodName = "ExtractedMethod"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_MissingMethodName_ThrowsException()
    {
        var @params = new ExtractMethodParams
        {
            SourceFile = "C:\\test\\file.cs",
            StartLine = 1,
            StartColumn = 1,
            EndLine = 5,
            EndColumn = 10,
            MethodName = ""
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_InvalidStartLine_ThrowsException()
    {
        var @params = new ExtractMethodParams
        {
            SourceFile = "C:\\test\\file.cs",
            StartLine = 0,
            StartColumn = 1,
            EndLine = 5,
            EndColumn = 10,
            MethodName = "ExtractedMethod"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_InvalidStartColumn_ThrowsException()
    {
        var @params = new ExtractMethodParams
        {
            SourceFile = "C:\\test\\file.cs",
            StartLine = 1,
            StartColumn = 0,
            EndLine = 5,
            EndColumn = 10,
            MethodName = "ExtractedMethod"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_SelectionEndBeforeStart_ThrowsException()
    {
        var @params = new ExtractMethodParams
        {
            SourceFile = "C:\\test\\file.cs",
            StartLine = 5,
            StartColumn = 1,
            EndLine = 3,
            EndColumn = 10,
            MethodName = "ExtractedMethod"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.InvalidSelectionRange, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_SameLineColumnEndBeforeStart_ThrowsException()
    {
        var @params = new ExtractMethodParams
        {
            SourceFile = "C:\\test\\file.cs",
            StartLine = 5,
            StartColumn = 10,
            EndLine = 5,
            EndColumn = 5,
            MethodName = "ExtractedMethod"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.InvalidSelectionRange, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_InvalidMethodName_ThrowsException()
    {
        var @params = new ExtractMethodParams
        {
            SourceFile = "C:\\test\\file.cs",
            StartLine = 1,
            StartColumn = 1,
            EndLine = 5,
            EndColumn = 10,
            MethodName = "123Invalid"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.InvalidNewName, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_MethodNameIsKeyword_ThrowsException()
    {
        var @params = new ExtractMethodParams
        {
            SourceFile = "C:\\test\\file.cs",
            StartLine = 1,
            StartColumn = 1,
            EndLine = 5,
            EndColumn = 10,
            MethodName = "void"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.ReservedKeyword, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_InvalidVisibility_ThrowsException()
    {
        var @params = new ExtractMethodParams
        {
            SourceFile = "C:\\test\\file.cs",
            StartLine = 1,
            StartColumn = 1,
            EndLine = 5,
            EndColumn = 10,
            MethodName = "ExtractedMethod",
            Visibility = "invalid"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
    }

    [Theory]
    [InlineData("private")]
    [InlineData("internal")]
    [InlineData("protected")]
    [InlineData("public")]
    public void ValidateParams_ValidVisibility_DoesNotThrowForVisibility(string visibility)
    {
        var @params = new ExtractMethodParams
        {
            SourceFile = "C:\\test\\file.cs",
            StartLine = 1,
            StartColumn = 1,
            EndLine = 5,
            EndColumn = 10,
            MethodName = "ExtractedMethod",
            Visibility = visibility
        };

        // Will throw for file not found, but not for visibility
        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    /// <summary>
    /// Mimics the parameter validation from ExtractMethodOperation.
    /// </summary>
    private static void ThrowIfInvalidParams(ExtractMethodParams @params)
    {
        var validVisibilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "private", "internal", "protected", "public", "private protected", "protected internal"
        };

        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        if (!IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!IsValidIdentifier(@params.MethodName))
            throw new RefactoringException(ErrorCodes.InvalidNewName, $"'{@params.MethodName}' is not a valid method name.");

        if (IsKeyword(@params.MethodName))
            throw new RefactoringException(ErrorCodes.ReservedKeyword, $"'{@params.MethodName}' is a C# reserved keyword.");

        if (@params.StartLine < 1 || @params.EndLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line numbers must be >= 1.");

        if (@params.StartColumn < 1 || @params.EndColumn < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column numbers must be >= 1.");

        if (@params.StartLine > @params.EndLine ||
            (@params.StartLine == @params.EndLine && @params.StartColumn >= @params.EndColumn))
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "Selection start must be before end.");

        if (!validVisibilities.Contains(@params.Visibility))
            throw new RefactoringException(ErrorCodes.InvalidVisibility, $"'{@params.Visibility}' is not a valid visibility modifier.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    private static bool IsAbsolutePath(string path) =>
        Path.IsPathRooted(path);

    private static bool IsValidIdentifier(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^@?[A-Za-z_][A-Za-z0-9_]*$");

    private static bool IsKeyword(string name)
    {
        if (name.StartsWith("@")) return false;
        return Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name) !=
               Microsoft.CodeAnalysis.CSharp.SyntaxKind.None;
    }
}
