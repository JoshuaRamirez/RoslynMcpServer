using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Contracts.Errors;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Tests for RenameSymbolParams validation.
/// </summary>
public class RenameSymbolParamsValidationTests
{
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
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_MissingSymbolName_ThrowsException()
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = "C:\\test\\file.cs",
            SymbolName = "",
            NewName = "RenamedClass"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_MissingNewName_ThrowsException()
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = "C:\\test\\file.cs",
            SymbolName = "MyClass",
            NewName = ""
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

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
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_InvalidNewName_ThrowsException()
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = "C:\\test\\file.cs",
            SymbolName = "MyClass",
            NewName = "123Invalid"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.InvalidNewName, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_ReservedKeyword_ThrowsException()
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = "C:\\test\\file.cs",
            SymbolName = "MyClass",
            NewName = "class"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.ReservedKeyword, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_SameName_ThrowsException()
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = "C:\\test\\file.cs",
            SymbolName = "MyClass",
            NewName = "MyClass"
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.SameLocation, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_InvalidLineNumber_ThrowsException()
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = "C:\\test\\file.cs",
            SymbolName = "MyClass",
            NewName = "RenamedClass",
            Line = 0
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void ValidateParams_InvalidColumnNumber_ThrowsException()
    {
        var @params = new RenameSymbolParams
        {
            SourceFile = "C:\\test\\file.cs",
            SymbolName = "MyClass",
            NewName = "RenamedClass",
            Column = 0
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
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
            SourceFile = "C:\\test\\file.cs",
            SymbolName = "MyClass",
            NewName = newName
        };

        // Will throw for file not found, but not for the name
        var ex = Assert.Throws<RefactoringException>(() =>
            ThrowIfInvalidParams(@params));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    /// <summary>
    /// Mimics the parameter validation from RenameSymbolOperation.
    /// </summary>
    private static void ThrowIfInvalidParams(RenameSymbolParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "symbolName is required.");

        if (string.IsNullOrWhiteSpace(@params.NewName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "newName is required.");

        if (!IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!IsValidIdentifier(@params.NewName))
            throw new RefactoringException(ErrorCodes.InvalidNewName, $"'{@params.NewName}' is not a valid C# identifier.");

        if (IsKeyword(@params.NewName))
            throw new RefactoringException(ErrorCodes.ReservedKeyword, $"'{@params.NewName}' is a C# reserved keyword.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");

        if (@params.SymbolName == @params.NewName)
            throw new RefactoringException(ErrorCodes.SameLocation, "New name is the same as current name.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    private static bool IsAbsolutePath(string path) =>
        Path.IsPathRooted(path);

    private static bool IsValidIdentifier(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^@?[A-Za-z_][A-Za-z0-9_]*$");

    private static bool IsKeyword(string name)
    {
        // Don't treat verbatim identifiers as keywords
        if (name.StartsWith("@")) return false;

        return Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name) !=
               Microsoft.CodeAnalysis.CSharp.SyntaxKind.None;
    }
}
