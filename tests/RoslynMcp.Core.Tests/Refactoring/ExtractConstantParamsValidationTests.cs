using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Tests for ExtractConstantParams validation via ExtractConstantOperation.Validate.
/// </summary>
public class ExtractConstantParamsValidationTests
{
    private static string AbsoluteTestPath(string extension = ".cs") =>
        OperatingSystem.IsWindows()
            ? $"C:\\test\\file{extension}"
            : $"/test/file{extension}";

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrConstantName_DoesNotThrow()
    {
        ExtractConstantOperation.Validate(new ExtractConstantParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithConstantName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractConstantOperation.Validate(new ExtractConstantParams
            {
                AllFiles = true,
                ConstantName = "MaxRetries"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("constantName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithStartLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractConstantOperation.Validate(new ExtractConstantParams
            {
                AllFiles = true,
                StartLine = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("startLine", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithSpan_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractConstantOperation.Validate(new ExtractConstantParams
            {
                AllFiles = true,
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_RelativeSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractConstantOperation.Validate(new ExtractConstantParams
            {
                AllFiles = true,
                SourceFile = "Types.cs"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractConstantOperation.Validate(new ExtractConstantParams
            {
                AllFiles = true,
                SourceFile = AbsoluteTestPath()
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutStartLine_Throws()
    {
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractConstantAllFilesFalse.cs");
        File.WriteAllText(file, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractConstantOperation.Validate(new ExtractConstantParams
                {
                    AllFiles = false,
                    SourceFile = file,
                    ConstantName = "MaxRetries",
                    StartColumn = 1,
                    EndLine = 1,
                    EndColumn = 2
                }));

            Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
            Assert.Contains("startLine", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validate_InvalidEndLine_ThrowsException()
    {
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractConstantInvalidEnd.cs");
        File.WriteAllText(file, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractConstantOperation.Validate(new ExtractConstantParams
                {
                    SourceFile = file,
                    StartLine = 1,
                    StartColumn = 1,
                    EndLine = 0,
                    EndColumn = 10,
                    ConstantName = "ExtractedConstant"
                }));

            Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validate_InvalidEndColumn_ThrowsException()
    {
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractConstantInvalidEndCol.cs");
        File.WriteAllText(file, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractConstantOperation.Validate(new ExtractConstantParams
                {
                    SourceFile = file,
                    StartLine = 1,
                    StartColumn = 1,
                    EndLine = 5,
                    EndColumn = 0,
                    ConstantName = "ExtractedConstant"
                }));

            Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validate_SelectionEndBeforeStart_ThrowsException()
    {
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractConstantBadRange.cs");
        File.WriteAllText(file, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractConstantOperation.Validate(new ExtractConstantParams
                {
                    SourceFile = file,
                    StartLine = 5,
                    StartColumn = 1,
                    EndLine = 3,
                    EndColumn = 10,
                    ConstantName = "ExtractedConstant"
                }));

            Assert.Equal(ErrorCodes.InvalidSelectionRange, ex.ErrorCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validate_SameLineExclusiveEndEqualStart_ThrowsException()
    {
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractConstantEqualEnd.cs");
        File.WriteAllText(file, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractConstantOperation.Validate(new ExtractConstantParams
                {
                    SourceFile = file,
                    StartLine = 5,
                    StartColumn = 10,
                    EndLine = 5,
                    EndColumn = 10,
                    ConstantName = "ExtractedConstant"
                }));

            Assert.Equal(ErrorCodes.InvalidSelectionRange, ex.ErrorCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Extract constant", ExtractConstantOperation.BuildAllFilesDescription(1));
        Assert.Equal("Extract 2 constants", ExtractConstantOperation.BuildAllFilesDescription(2));
    }

    [Theory]
    [InlineData("42", "_42")]
    [InlineData("hello world", "HelloWorld")]
    [InlineData("true", "True")]
    [InlineData("hi", "Hi")]
    public void DeriveConstantNameFromLiteral_SanitizesValueText(string valueText, string expected)
    {
        var literal = valueText switch
        {
            "true" => Microsoft.CodeAnalysis.CSharp.SyntaxFactory.LiteralExpression(
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.TrueLiteralExpression),
            "42" => Microsoft.CodeAnalysis.CSharp.SyntaxFactory.LiteralExpression(
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression,
                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Literal(42)),
            _ => Microsoft.CodeAnalysis.CSharp.SyntaxFactory.LiteralExpression(
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression,
                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Literal(valueText))
        };

        Assert.Equal(expected, ExtractConstantOperation.DeriveConstantNameFromLiteral(literal));
    }
}
