using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Tests for query parameter validation across all 5 query operations.
/// Mirrors the validation logic without requiring a workspace.
/// </summary>
public class QueryParamsValidationTests
{
    private static string AbsoluteTestPath(string extension = ".cs") =>
        OperatingSystem.IsWindows()
            ? $"C:\\test\\file{extension}"
            : $"/test/file{extension}";

    #region FindReferencesParams Validation

    [Fact]
    public void FindReferences_MissingSourceFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateFindReferencesParams(new FindReferencesParams { SourceFile = "", SymbolName = "Foo" }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void FindReferences_RelativePath_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateFindReferencesParams(new FindReferencesParams { SourceFile = "file.cs", SymbolName = "Foo" }));
        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void FindReferences_NonCsFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateFindReferencesParams(new FindReferencesParams { SourceFile = AbsoluteTestPath(".txt"), SymbolName = "Foo" }));
        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void FindReferences_NoSymbolOrLine_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateFindReferencesParams(new FindReferencesParams { SourceFile = AbsoluteTestPath() }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void FindReferences_InvalidLineNumber_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateFindReferencesParams(new FindReferencesParams { SourceFile = AbsoluteTestPath(), Line = 0 }));
        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void FindReferences_InvalidColumnNumber_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateFindReferencesParams(new FindReferencesParams { SourceFile = AbsoluteTestPath(), SymbolName = "Foo", Column = 0 }));
        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public void FindReferences_ValidParamsWithName_PassesValidation()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateFindReferencesParams(new FindReferencesParams { SourceFile = AbsoluteTestPath(), SymbolName = "Foo" }));
        // Should fail on file not found, NOT on param validation
        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void FindReferences_ValidParamsWithLine_PassesValidation()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateFindReferencesParams(new FindReferencesParams { SourceFile = AbsoluteTestPath(), Line = 5, Column = 10 }));
        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region GoToDefinitionParams Validation

    [Fact]
    public void GoToDefinition_MissingSourceFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGoToDefinitionParams(new GoToDefinitionParams { SourceFile = "", SymbolName = "Foo" }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void GoToDefinition_NoSymbolOrLine_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGoToDefinitionParams(new GoToDefinitionParams { SourceFile = AbsoluteTestPath() }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void GoToDefinition_ValidParams_PassesValidation()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGoToDefinitionParams(new GoToDefinitionParams { SourceFile = AbsoluteTestPath(), SymbolName = "Foo" }));
        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region GetSymbolInfoParams Validation

    [Fact]
    public void GetSymbolInfo_MissingSourceFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGetSymbolInfoParams(new GetSymbolInfoParams { SourceFile = "", SymbolName = "Foo" }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void GetSymbolInfo_NoSymbolOrLine_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGetSymbolInfoParams(new GetSymbolInfoParams { SourceFile = AbsoluteTestPath() }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    #endregion

    #region FindImplementationsParams Validation

    [Fact]
    public void FindImplementations_MissingSourceFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateFindImplementationsParams(new FindImplementationsParams { SourceFile = "", SymbolName = "IFoo" }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void FindImplementations_NoSymbolOrLine_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateFindImplementationsParams(new FindImplementationsParams { SourceFile = AbsoluteTestPath() }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    #endregion

    #region SearchSymbolsParams Validation

    [Fact]
    public void SearchSymbols_MissingQuery_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateSearchSymbolsParams(new SearchSymbolsParams { Query = "" }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void SearchSymbols_InvalidMaxResults_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateSearchSymbolsParams(new SearchSymbolsParams { Query = "Foo", MaxResults = 0 }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void SearchSymbols_ValidParams_PassesValidation()
    {
        // SearchSymbols doesn't need sourceFile, so valid params should not throw here
        // (no file existence check)
        ValidateSearchSymbolsParams(new SearchSymbolsParams { Query = "Foo" });
    }

    #endregion

    #region Validation Helpers

    private static void ValidateFindReferencesParams(FindReferencesParams p)
    {
        if (string.IsNullOrWhiteSpace(p.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");
        if (!PathResolver.IsAbsolutePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");
        if (!PathResolver.IsValidCSharpFilePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
        if (!p.Line.HasValue && string.IsNullOrWhiteSpace(p.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either line/column or symbolName must be provided.");
        if (p.Line.HasValue && p.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");
        if (p.Column.HasValue && p.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");
        if (p.MaxResults.HasValue && p.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");
        if (!File.Exists(p.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {p.SourceFile}");
    }

    private static void ValidateGoToDefinitionParams(GoToDefinitionParams p)
    {
        if (string.IsNullOrWhiteSpace(p.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");
        if (!PathResolver.IsAbsolutePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");
        if (!PathResolver.IsValidCSharpFilePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
        if (!p.Line.HasValue && string.IsNullOrWhiteSpace(p.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either line/column or symbolName must be provided.");
        if (!File.Exists(p.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {p.SourceFile}");
    }

    private static void ValidateGetSymbolInfoParams(GetSymbolInfoParams p)
    {
        if (string.IsNullOrWhiteSpace(p.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");
        if (!PathResolver.IsAbsolutePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");
        if (!PathResolver.IsValidCSharpFilePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
        if (!p.Line.HasValue && string.IsNullOrWhiteSpace(p.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either line/column or symbolName must be provided.");
        if (!File.Exists(p.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {p.SourceFile}");
    }

    private static void ValidateFindImplementationsParams(FindImplementationsParams p)
    {
        if (string.IsNullOrWhiteSpace(p.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");
        if (!PathResolver.IsAbsolutePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");
        if (!PathResolver.IsValidCSharpFilePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
        if (!p.Line.HasValue && string.IsNullOrWhiteSpace(p.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either line/column or symbolName must be provided.");
        if (!File.Exists(p.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {p.SourceFile}");
    }

    private static void ValidateSearchSymbolsParams(SearchSymbolsParams p)
    {
        if (string.IsNullOrWhiteSpace(p.Query))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "query is required.");
        if (p.MaxResults.HasValue && p.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");
    }

    #endregion
}
