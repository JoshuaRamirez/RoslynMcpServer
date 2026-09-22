using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Tests for ExtractVariableParams validation via ExtractVariableOperation.Validate.
/// </summary>
public class ExtractVariableParamsValidationTests
{
    private static string AbsoluteTestPath(string extension = ".cs") =>
        OperatingSystem.IsWindows()
            ? $"C:\\test\\file{extension}"
            : $"/test/file{extension}";

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrVariableName_DoesNotThrow()
    {
        ExtractVariableOperation.Validate(new ExtractVariableParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithVariableName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractVariableOperation.Validate(new ExtractVariableParams
            {
                AllFiles = true,
                VariableName = "extracted"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("variableName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithStartLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractVariableOperation.Validate(new ExtractVariableParams
            {
                AllFiles = true,
                StartLine = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithSpan_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractVariableOperation.Validate(new ExtractVariableParams
            {
                AllFiles = true,
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 5
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_RelativeSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractVariableOperation.Validate(new ExtractVariableParams
            {
                AllFiles = true,
                SourceFile = "relative.cs"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_MissingSourceFile_DoesNotThrow()
    {
        ExtractVariableOperation.Validate(new ExtractVariableParams
        {
            AllFiles = true,
            SourceFile = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractVariableMissingAllFiles.cs")
        });
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutStartLine_Throws()
    {
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractVariableAllFilesFalse.cs");
        File.WriteAllText(file, "// test");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractVariableOperation.Validate(new ExtractVariableParams
                {
                    AllFiles = false,
                    SourceFile = file,
                    StartColumn = 1,
                    EndLine = 1,
                    EndColumn = 5,
                    VariableName = "extracted"
                }));

            Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validate_InvalidEndLine_ThrowsException()
    {
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractVariableInvalidEndLine.cs");
        File.WriteAllText(file, "// test");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractVariableOperation.Validate(new ExtractVariableParams
                {
                    SourceFile = file,
                    StartLine = 1,
                    StartColumn = 1,
                    EndLine = 0,
                    EndColumn = 10,
                    VariableName = "extracted"
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
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractVariableInvalidEndColumn.cs");
        File.WriteAllText(file, "// test");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractVariableOperation.Validate(new ExtractVariableParams
                {
                    SourceFile = file,
                    StartLine = 1,
                    StartColumn = 1,
                    EndLine = 5,
                    EndColumn = 0,
                    VariableName = "extracted"
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
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractVariableSelectionOrder.cs");
        File.WriteAllText(file, "// test");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractVariableOperation.Validate(new ExtractVariableParams
                {
                    SourceFile = file,
                    StartLine = 5,
                    StartColumn = 1,
                    EndLine = 3,
                    EndColumn = 10,
                    VariableName = "extracted"
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
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractVariableExclusiveEnd.cs");
        File.WriteAllText(file, "// test");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractVariableOperation.Validate(new ExtractVariableParams
                {
                    SourceFile = file,
                    StartLine = 5,
                    StartColumn = 10,
                    EndLine = 5,
                    EndColumn = 10,
                    VariableName = "extracted"
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
        Assert.Equal("Extract variable", ExtractVariableOperation.BuildAllFilesDescription(1));
        Assert.Equal("Extract 2 variables", ExtractVariableOperation.BuildAllFilesDescription(2));
    }
    [Fact]
    public void DeriveVariableNameFromExpression_PrefersInvokedSimpleName()
    {
        var invocation = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression("GetValue()");
        Assert.Equal("getValue", ExtractVariableOperation.DeriveVariableNameFromExpression(invocation));
    }

    [Fact]
    public void DeriveVariableNameFromExpression_PrefersCreatedTypeName()
    {
        var creation = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression("new Item()");
        Assert.Equal("item", ExtractVariableOperation.DeriveVariableNameFromExpression(creation));
    }

    [Fact]
    public void DeriveVariableNameFromExpression_EscapesReservedKeyword()
    {
        var invocation = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression("Class()");
        Assert.Equal("@class", ExtractVariableOperation.DeriveVariableNameFromExpression(invocation));
    }
}
