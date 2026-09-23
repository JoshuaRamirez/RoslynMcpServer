using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Tests for RenameSymbolParams validation.
/// </summary>
public class RenameSymbolParamsValidationTests
{
    /// <summary>
    /// Returns a platform-appropriate absolute path for test purposes.
    /// On Windows: C:\test\file.cs, on Unix: /test/file.cs
    /// </summary>
    private static string AbsoluteTestPath(string extension = ".cs") =>
        OperatingSystem.IsWindows()
            ? $"C:\\test\\file{extension}"
            : $"/test/file{extension}";

    [Fact]
    public void ValidateParams_MissingSourceFile_ThrowsException()
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = "",
            SymbolName = "MyClass",
            NewName = "RenamedClass"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            RenameSymbolOperation.Validate(@params));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_MissingSymbolName_ThrowsException()
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = AbsoluteTestPath(),
            SymbolName = "",
            NewName = "RenamedClass"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            RenameSymbolOperation.Validate(@params));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_MissingNewName_ThrowsException()
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = AbsoluteTestPath(),
            SymbolName = "MyClass",
            NewName = ""
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            RenameSymbolOperation.Validate(@params));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_RelativePath_ThrowsException()
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = "file.cs",
            SymbolName = "MyClass",
            NewName = "RenamedClass"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            RenameSymbolOperation.Validate(@params));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_InvalidNewName_ThrowsException()
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = AbsoluteTestPath(),
            SymbolName = "MyClass",
            NewName = "123Invalid"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            RenameSymbolOperation.Validate(@params));

        Assert.Equal(ErrorCodes.InvalidNewName, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_ReservedKeyword_ThrowsException()
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = AbsoluteTestPath(),
            SymbolName = "MyClass",
            NewName = "class"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            RenameSymbolOperation.Validate(@params));

        Assert.Equal(ErrorCodes.ReservedKeyword, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_SameName_ThrowsException()
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = AbsoluteTestPath(),
            SymbolName = "MyClass",
            NewName = "MyClass"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            RenameSymbolOperation.Validate(@params));

        Assert.Equal(ErrorCodes.SameLocation, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_InvalidLineNumber_ThrowsException()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameSymbolInvalidLine.cs");
        File.WriteAllText(path, "// test");
        try
        {
            var @params = new RenameSymbolParams
            {
                SourceFile = path,
                SymbolName = "MyClass",
                NewName = "RenamedClass",
                Line = 0
            };

            var ex = Assert.Throws<RefactoringException>(() =>
                RenameSymbolOperation.Validate(@params));

            Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ValidateParams_InvalidColumnNumber_ThrowsException()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameSymbolInvalidColumn.cs");
        File.WriteAllText(path, "// test");
        try
        {
            var @params = new RenameSymbolParams
            {
                SourceFile = path,
                SymbolName = "MyClass",
                NewName = "RenamedClass",
                Column = 0
            };

            var ex = Assert.Throws<RefactoringException>(() =>
                RenameSymbolOperation.Validate(@params));

            Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("ValidName")]
    [InlineData("_underscore")]
    [InlineData("camelCase")]
    [InlineData("PascalCase")]
    [InlineData("Name123")]
    [InlineData("@class")] // Verbatim identifier - escapes keyword
    public void ValidateParams_ValidNewNames_DoesNotThrowForName(string newName)
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = AbsoluteTestPath(),
            SymbolName = "MyClass",
            NewName = newName
        };

        // Will throw for file not found, but not for the name
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameSymbolOperation.Validate(@params));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameSymbolOperation.Validate(new RenameSymbolParams
            {
                AllFiles = false,
                SymbolName = "Foo",
                NewName = "Bar"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFile_DoesNotThrow()
    {
        RenameSymbolOperation.Validate(new RenameSymbolParams
        {
            AllFiles = true,
            SymbolName = "Foo",
            NewName = "Bar"
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithRelativeSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameSymbolOperation.Validate(new RenameSymbolParams
            {
                AllFiles = true,
                SourceFile = "relative.cs",
                SymbolName = "Foo",
                NewName = "Bar"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithNonCSharpSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameSymbolOperation.Validate(new RenameSymbolParams
            {
                AllFiles = true,
                SourceFile = AbsoluteTestPath(".txt"),
                SymbolName = "Foo",
                NewName = "Bar"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMissingSourceFile_DoesNotThrow()
    {
        RenameSymbolOperation.Validate(new RenameSymbolParams
        {
            AllFiles = true,
            SourceFile = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameSymbolMissingAllFiles.cs"),
            SymbolName = "Foo",
            NewName = "Bar"
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameSymbolOperation.Validate(new RenameSymbolParams
            {
                AllFiles = true,
                SymbolName = "Foo",
                NewName = "Bar",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameSymbolOperation.Validate(new RenameSymbolParams
            {
                AllFiles = true,
                SymbolName = "Foo",
                NewName = "Bar",
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_MissingSymbolName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameSymbolOperation.Validate(new RenameSymbolParams
            {
                AllFiles = true,
                SymbolName = "",
                NewName = "Bar"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("symbolName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal(
            "Rename 'Foo' to 'Bar'",
            RenameSymbolOperation.BuildAllFilesDescription(1, "Foo", "Bar"));
        Assert.Equal(
            "Rename 2 symbols 'Foo' to 'Bar'",
            RenameSymbolOperation.BuildAllFilesDescription(2, "Foo", "Bar"));
    }
}
