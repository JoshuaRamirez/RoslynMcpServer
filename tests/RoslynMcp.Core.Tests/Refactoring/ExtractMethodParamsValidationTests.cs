using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Tests for ExtractMethodParams validation via ExtractMethodOperation.Validate.
/// </summary>
public class ExtractMethodParamsValidationTests
{

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrMethodName_DoesNotThrow()
    {
        ExtractMethodOperation.Validate(new ExtractMethodParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithEmptyMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractMethodOperation.Validate(new ExtractMethodParams
            {
                AllFiles = true,
                MethodName = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractMethodOperation.Validate(new ExtractMethodParams
            {
                AllFiles = true,
                MethodName = "Extracted"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithStartLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractMethodOperation.Validate(new ExtractMethodParams
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
            ExtractMethodOperation.Validate(new ExtractMethodParams
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
            ExtractMethodOperation.Validate(new ExtractMethodParams
            {
                AllFiles = true,
                SourceFile = "relative.cs"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_MissingSourceFile_DoesNotThrow()
    {
        ExtractMethodOperation.Validate(new ExtractMethodParams
        {
            AllFiles = true,
            SourceFile = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractMethodMissingAllFiles.cs")
        });
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutStartLine_Throws()
    {
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractMethodAllFilesFalse.cs");
        File.WriteAllText(file, "// test");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractMethodOperation.Validate(new ExtractMethodParams
                {
                    AllFiles = false,
                    SourceFile = file,
                    StartColumn = 1,
                    EndLine = 1,
                    EndColumn = 5,
                    MethodName = "Extracted"
                }));

            Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractMethodOperation.Validate(new ExtractMethodParams
            {
                AllFiles = false,
                StartLine = 1,
                StartColumn = 1,
                EndLine = 5,
                EndColumn = 10,
                MethodName = "Extracted"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutMethodName_Throws()
    {
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractMethodMissingName.cs");
        File.WriteAllText(file, "// test");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractMethodOperation.Validate(new ExtractMethodParams
                {
                    SourceFile = file,
                    StartLine = 1,
                    StartColumn = 1,
                    EndLine = 5,
                    EndColumn = 10,
                    MethodName = ""
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
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractMethodInvalidEndLine.cs");
        File.WriteAllText(file, "// test");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractMethodOperation.Validate(new ExtractMethodParams
                {
                    SourceFile = file,
                    StartLine = 1,
                    StartColumn = 1,
                    EndLine = 0,
                    EndColumn = 10,
                    MethodName = "Extracted"
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
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractMethodInvalidEndColumn.cs");
        File.WriteAllText(file, "// test");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractMethodOperation.Validate(new ExtractMethodParams
                {
                    SourceFile = file,
                    StartLine = 1,
                    StartColumn = 1,
                    EndLine = 5,
                    EndColumn = 0,
                    MethodName = "Extracted"
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
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractMethodSelectionOrder.cs");
        File.WriteAllText(file, "// test");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractMethodOperation.Validate(new ExtractMethodParams
                {
                    SourceFile = file,
                    StartLine = 5,
                    StartColumn = 1,
                    EndLine = 3,
                    EndColumn = 10,
                    MethodName = "Extracted"
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
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractMethodExclusiveEnd.cs");
        File.WriteAllText(file, "// test");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractMethodOperation.Validate(new ExtractMethodParams
                {
                    SourceFile = file,
                    StartLine = 5,
                    StartColumn = 10,
                    EndLine = 5,
                    EndColumn = 10,
                    MethodName = "Extracted"
                }));

            Assert.Equal(ErrorCodes.InvalidSelectionRange, ex.ErrorCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validate_InvalidMethodName_ThrowsException()
    {
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractMethodInvalidName.cs");
        File.WriteAllText(file, "// test");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractMethodOperation.Validate(new ExtractMethodParams
                {
                    SourceFile = file,
                    StartLine = 1,
                    StartColumn = 1,
                    EndLine = 5,
                    EndColumn = 10,
                    MethodName = "123Invalid"
                }));

            Assert.Equal(ErrorCodes.InvalidNewName, ex.ErrorCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validate_MethodNameIsKeyword_ThrowsException()
    {
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractMethodKeyword.cs");
        File.WriteAllText(file, "// test");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ExtractMethodOperation.Validate(new ExtractMethodParams
                {
                    SourceFile = file,
                    StartLine = 1,
                    StartColumn = 1,
                    EndLine = 5,
                    EndColumn = 10,
                    MethodName = "void"
                }));

            Assert.Equal(ErrorCodes.ReservedKeyword, ex.ErrorCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validate_InvalidVisibility_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractMethodOperation.Validate(new ExtractMethodParams
            {
                AllFiles = true,
                Visibility = "invalid"
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
    }

    [Theory]
    [InlineData("private")]
    [InlineData("internal")]
    [InlineData("protected")]
    [InlineData("public")]
    public void Validate_ValidVisibility_WithAllFiles_DoesNotThrow(string visibility)
    {
        ExtractMethodOperation.Validate(new ExtractMethodParams
        {
            AllFiles = true,
            Visibility = visibility
        });
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Extract method", ExtractMethodOperation.BuildAllFilesDescription(1));
        Assert.Equal("Extract 2 methods", ExtractMethodOperation.BuildAllFilesDescription(2));
    }

    [Fact]
    public void DeriveMethodNameFromStatements_PrefersInvokedSimpleName()
    {
        var statements = new[]
        {
            SyntaxFactory.ParseStatement("DoWork();"),
            SyntaxFactory.ParseStatement("Log();")
        };
        Assert.Equal("DoWork", ExtractMethodOperation.DeriveMethodNameFromStatements(statements));
    }

    [Fact]
    public void DeriveMethodNameFromStatements_PrefersCreatedTypeName()
    {
        var statements = new[]
        {
            SyntaxFactory.ParseStatement("var x = new Item();"),
            SyntaxFactory.ParseStatement("Use(x);")
        };
        Assert.Equal("Item", ExtractMethodOperation.DeriveMethodNameFromStatements(statements));
    }

    [Fact]
    public void DeriveMethodNameFromStatements_PascalCasesInvokedName()
    {
        var statements = new[]
        {
            SyntaxFactory.ParseStatement("doWork();"),
            SyntaxFactory.ParseStatement("Log();")
        };
        Assert.Equal("DoWork", ExtractMethodOperation.DeriveMethodNameFromStatements(statements));
    }
}
