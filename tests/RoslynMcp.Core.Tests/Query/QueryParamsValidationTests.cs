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

    [Fact]
    public void SearchSymbols_InvalidKindFilter_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateSearchSymbolsParams(new SearchSymbolsParams { Query = "Foo", KindFilter = "BogusKind" }));
        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
    }

    [Fact]
    public void SearchSymbols_ValidKindFilter_PassesValidation()
    {
        ValidateSearchSymbolsParams(new SearchSymbolsParams { Query = "Foo", KindFilter = "Class" });
    }

    [Fact]
    public void SearchSymbols_CaseInsensitiveKindFilter_PassesValidation()
    {
        ValidateSearchSymbolsParams(new SearchSymbolsParams { Query = "Foo", KindFilter = "method" });
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
        if (!string.IsNullOrWhiteSpace(p.KindFilter) &&
            !System.Enum.TryParse<RoslynMcp.Contracts.Enums.SymbolKind>(p.KindFilter, ignoreCase: true, out _))
        {
            var validKinds = string.Join(", ", System.Enum.GetNames<RoslynMcp.Contracts.Enums.SymbolKind>());
            throw new RefactoringException(ErrorCodes.InvalidSymbolKind, $"Invalid kindFilter '{p.KindFilter}'. Valid values: {validKinds}");
        }
    }

    #endregion

    #region GetDiagnosticsParams Validation

    [Fact]
    public void GetDiagnostics_RelativePath_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGetDiagnosticsParams(new GetDiagnosticsParams { SourceFile = "file.cs" }));
        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void GetDiagnostics_NonCsFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGetDiagnosticsParams(new GetDiagnosticsParams { SourceFile = AbsoluteTestPath(".txt") }));
        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void GetDiagnostics_NoSourceFile_PassesValidation()
    {
        // No sourceFile is valid — returns all solution diagnostics
        ValidateGetDiagnosticsParams(new GetDiagnosticsParams());
    }

    [Fact]
    public void GetDiagnostics_InvalidSeverityFilter_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGetDiagnosticsParams(new GetDiagnosticsParams { SeverityFilter = "BogusLevel" }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void GetDiagnostics_ValidSeverityFilter_PassesValidation()
    {
        ValidateGetDiagnosticsParams(new GetDiagnosticsParams { SeverityFilter = "Error" });
    }

    [Fact]
    public void GetDiagnostics_CaseInsensitiveSeverityFilter_PassesValidation()
    {
        ValidateGetDiagnosticsParams(new GetDiagnosticsParams { SeverityFilter = "warning" });
    }

    #endregion

    #region GetCodeMetricsParams Validation

    [Fact]
    public void GetCodeMetrics_MissingSourceFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGetCodeMetricsParams(new GetCodeMetricsParams()));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void GetCodeMetrics_RelativePath_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGetCodeMetricsParams(new GetCodeMetricsParams { SourceFile = "file.cs" }));
        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void GetCodeMetrics_InvalidLine_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGetCodeMetricsParams(new GetCodeMetricsParams { SourceFile = AbsoluteTestPath(), Line = 0 }));
        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void GetCodeMetrics_ValidParams_PassesValidation()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGetCodeMetricsParams(new GetCodeMetricsParams { SourceFile = AbsoluteTestPath() }));
        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region AnalyzeControlFlowParams Validation

    [Fact]
    public void AnalyzeControlFlow_MissingSourceFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateAnalyzeControlFlowParams(new AnalyzeControlFlowParams { SourceFile = "", StartLine = 1, EndLine = 5 }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void AnalyzeControlFlow_RelativePath_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateAnalyzeControlFlowParams(new AnalyzeControlFlowParams { SourceFile = "file.cs", StartLine = 1, EndLine = 5 }));
        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void AnalyzeControlFlow_InvalidStartLine_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateAnalyzeControlFlowParams(new AnalyzeControlFlowParams { SourceFile = AbsoluteTestPath(), StartLine = 0, EndLine = 5 }));
        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void AnalyzeControlFlow_StartAfterEnd_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateAnalyzeControlFlowParams(new AnalyzeControlFlowParams { SourceFile = AbsoluteTestPath(), StartLine = 10, EndLine = 5 }));
        Assert.Equal(ErrorCodes.InvalidRegion, ex.ErrorCode);
    }

    [Fact]
    public void AnalyzeControlFlow_ValidParams_PassesValidation()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateAnalyzeControlFlowParams(new AnalyzeControlFlowParams { SourceFile = AbsoluteTestPath(), StartLine = 1, EndLine = 5 }));
        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region FindCallersParams Validation

    [Fact]
    public void FindCallers_MissingSourceFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateFindCallersParams(new FindCallersParams { SourceFile = "", SymbolName = "Foo" }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void FindCallers_NoSymbolOrLine_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateFindCallersParams(new FindCallersParams { SourceFile = AbsoluteTestPath() }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void FindCallers_ValidParams_PassesValidation()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateFindCallersParams(new FindCallersParams { SourceFile = AbsoluteTestPath(), SymbolName = "Foo" }));
        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region GetTypeHierarchyParams Validation

    [Fact]
    public void GetTypeHierarchy_MissingSourceFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGetTypeHierarchyParams(new GetTypeHierarchyParams { SourceFile = "", SymbolName = "Foo" }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void GetTypeHierarchy_NoSymbolOrLine_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGetTypeHierarchyParams(new GetTypeHierarchyParams { SourceFile = AbsoluteTestPath() }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void GetTypeHierarchy_InvalidDirection_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGetTypeHierarchyParams(new GetTypeHierarchyParams { SourceFile = AbsoluteTestPath(), SymbolName = "Foo", Direction = "Sideways" }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void GetTypeHierarchy_ValidDirection_PassesValidation()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGetTypeHierarchyParams(new GetTypeHierarchyParams { SourceFile = AbsoluteTestPath(), SymbolName = "Foo", Direction = "Ancestors" }));
        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region GetDocumentOutlineParams Validation

    [Fact]
    public void GetDocumentOutline_MissingSourceFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGetDocumentOutlineParams(new GetDocumentOutlineParams { SourceFile = "" }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void GetDocumentOutline_RelativePath_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGetDocumentOutlineParams(new GetDocumentOutlineParams { SourceFile = "file.cs" }));
        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void GetDocumentOutline_ValidParams_PassesValidation()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateGetDocumentOutlineParams(new GetDocumentOutlineParams { SourceFile = AbsoluteTestPath() }));
        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region New Validation Helpers

    private static void ValidateGetDiagnosticsParams(GetDiagnosticsParams p)
    {
        if (!string.IsNullOrWhiteSpace(p.SourceFile))
        {
            if (!PathResolver.IsAbsolutePath(p.SourceFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");
            if (!PathResolver.IsValidCSharpFilePath(p.SourceFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
            if (!File.Exists(p.SourceFile))
                throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {p.SourceFile}");
        }
        if (!string.IsNullOrWhiteSpace(p.SeverityFilter) &&
            !Enum.TryParse<RoslynMcp.Contracts.Enums.DiagnosticSeverityFilter>(p.SeverityFilter, ignoreCase: true, out _))
        {
            var valid = string.Join(", ", Enum.GetNames<RoslynMcp.Contracts.Enums.DiagnosticSeverityFilter>());
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, $"Invalid severityFilter. Valid values: {valid}");
        }
    }

    private static void ValidateGetCodeMetricsParams(GetCodeMetricsParams p)
    {
        if (string.IsNullOrWhiteSpace(p.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");
        if (!PathResolver.IsAbsolutePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");
        if (!PathResolver.IsValidCSharpFilePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
        if (p.Line.HasValue && p.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");
        if (!File.Exists(p.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {p.SourceFile}");
    }

    private static void ValidateAnalyzeControlFlowParams(AnalyzeControlFlowParams p)
    {
        if (string.IsNullOrWhiteSpace(p.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");
        if (!PathResolver.IsAbsolutePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");
        if (!PathResolver.IsValidCSharpFilePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
        if (p.StartLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "startLine must be >= 1.");
        if (p.EndLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "endLine must be >= 1.");
        if (p.StartLine > p.EndLine)
            throw new RefactoringException(ErrorCodes.InvalidRegion, "startLine must be <= endLine.");
        if (!File.Exists(p.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {p.SourceFile}");
    }

    private static void ValidateFindCallersParams(FindCallersParams p)
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

    private static void ValidateGetTypeHierarchyParams(GetTypeHierarchyParams p)
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
        if (!string.IsNullOrWhiteSpace(p.Direction) &&
            !Enum.TryParse<RoslynMcp.Contracts.Enums.HierarchyDirection>(p.Direction, ignoreCase: true, out _))
        {
            var valid = string.Join(", ", Enum.GetNames<RoslynMcp.Contracts.Enums.HierarchyDirection>());
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, $"Invalid direction. Valid values: {valid}");
        }
        if (!File.Exists(p.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {p.SourceFile}");
    }

    private static void ValidateGetDocumentOutlineParams(GetDocumentOutlineParams p)
    {
        if (string.IsNullOrWhiteSpace(p.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");
        if (!PathResolver.IsAbsolutePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");
        if (!PathResolver.IsValidCSharpFilePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
        if (!File.Exists(p.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {p.SourceFile}");
    }

    #endregion

    #region AnalyzeDataFlowParams Validation

    [Fact]
    public void AnalyzeDataFlow_MissingSourceFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateAnalyzeDataFlowParams(new AnalyzeDataFlowParams { SourceFile = "", StartLine = 1, EndLine = 5 }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void AnalyzeDataFlow_RelativePath_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateAnalyzeDataFlowParams(new AnalyzeDataFlowParams { SourceFile = "file.cs", StartLine = 1, EndLine = 5 }));
        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void AnalyzeDataFlow_InvalidStartLine_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateAnalyzeDataFlowParams(new AnalyzeDataFlowParams { SourceFile = AbsoluteTestPath(), StartLine = 0, EndLine = 5 }));
        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void AnalyzeDataFlow_StartAfterEnd_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateAnalyzeDataFlowParams(new AnalyzeDataFlowParams { SourceFile = AbsoluteTestPath(), StartLine = 10, EndLine = 5 }));
        Assert.Equal(ErrorCodes.InvalidRegion, ex.ErrorCode);
    }

    [Fact]
    public void AnalyzeDataFlow_ValidParams_PassesValidation()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateAnalyzeDataFlowParams(new AnalyzeDataFlowParams { SourceFile = AbsoluteTestPath(), StartLine = 1, EndLine = 5 }));
        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region ConvertExpressionBodyParams Validation

    [Fact]
    public void ConvertExpressionBody_MissingSourceFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertExpressionBodyParams(new ConvertExpressionBodyParams { SourceFile = "", Direction = "ToBlockBody" }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ConvertExpressionBody_MissingDirection_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertExpressionBodyParams(new ConvertExpressionBodyParams { SourceFile = AbsoluteTestPath(), Direction = "" }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ConvertExpressionBody_NoMemberOrLine_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertExpressionBodyParams(new ConvertExpressionBodyParams { SourceFile = AbsoluteTestPath(), Direction = "ToBlockBody" }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ConvertExpressionBody_InvalidDirection_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertExpressionBodyParams(new ConvertExpressionBodyParams { SourceFile = AbsoluteTestPath(), MemberName = "Foo", Direction = "Invalid" }));
        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
    }

    [Fact]
    public void ConvertExpressionBody_InvalidLine_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertExpressionBodyParams(new ConvertExpressionBodyParams { SourceFile = AbsoluteTestPath(), Line = 0, Direction = "ToBlockBody" }));
        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void ConvertExpressionBody_ValidParams_PassesValidation()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertExpressionBodyParams(new ConvertExpressionBodyParams { SourceFile = AbsoluteTestPath(), MemberName = "Foo", Direction = "ToExpressionBody" }));
        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region ConvertPropertyParams Validation

    [Fact]
    public void ConvertProperty_MissingSourceFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertPropertyParams(new ConvertPropertyParams { SourceFile = "", Direction = "ToFullProperty" }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ConvertProperty_MissingDirection_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertPropertyParams(new ConvertPropertyParams { SourceFile = AbsoluteTestPath(), Direction = "" }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ConvertProperty_InvalidDirection_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertPropertyParams(new ConvertPropertyParams { SourceFile = AbsoluteTestPath(), PropertyName = "Foo", Direction = "Bogus" }));
        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
    }

    [Fact]
    public void ConvertProperty_ValidParams_PassesValidation()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertPropertyParams(new ConvertPropertyParams { SourceFile = AbsoluteTestPath(), PropertyName = "Foo", Direction = "ToAutoProperty" }));
        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region IntroduceParameterParams Validation

    [Fact]
    public void IntroduceParameter_MissingSourceFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateIntroduceParameterParams(new IntroduceParameterParams { SourceFile = "", VariableName = "x", Line = 5 }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void IntroduceParameter_MissingVariableName_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateIntroduceParameterParams(new IntroduceParameterParams { SourceFile = AbsoluteTestPath(), VariableName = "", Line = 5 }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void IntroduceParameter_InvalidLine_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateIntroduceParameterParams(new IntroduceParameterParams { SourceFile = AbsoluteTestPath(), VariableName = "x", Line = 0 }));
        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void IntroduceParameter_ValidParams_PassesValidation()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateIntroduceParameterParams(new IntroduceParameterParams { SourceFile = AbsoluteTestPath(), VariableName = "x", Line = 5 }));
        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region M4 Validation Helpers

    private static void ValidateAnalyzeDataFlowParams(AnalyzeDataFlowParams p)
    {
        if (string.IsNullOrWhiteSpace(p.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");
        if (!PathResolver.IsAbsolutePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");
        if (!PathResolver.IsValidCSharpFilePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
        if (p.StartLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "startLine must be >= 1.");
        if (p.EndLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "endLine must be >= 1.");
        if (p.StartLine > p.EndLine)
            throw new RefactoringException(ErrorCodes.InvalidRegion, "startLine must be <= endLine.");
        if (!File.Exists(p.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {p.SourceFile}");
    }

    private static void ValidateConvertExpressionBodyParams(ConvertExpressionBodyParams p)
    {
        if (string.IsNullOrWhiteSpace(p.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");
        if (string.IsNullOrWhiteSpace(p.Direction))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "direction is required.");
        if (!PathResolver.IsAbsolutePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");
        if (!PathResolver.IsValidCSharpFilePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
        if (!p.Line.HasValue && string.IsNullOrWhiteSpace(p.MemberName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either memberName or line must be provided.");
        if (p.Line.HasValue && p.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");
        if (!Enum.TryParse<RoslynMcp.Contracts.Enums.ConversionDirection>(p.Direction, ignoreCase: true, out var dir) ||
            (dir != RoslynMcp.Contracts.Enums.ConversionDirection.ToExpressionBody && dir != RoslynMcp.Contracts.Enums.ConversionDirection.ToBlockBody))
        {
            throw new RefactoringException(ErrorCodes.CannotConvert, "direction must be 'ToExpressionBody' or 'ToBlockBody'.");
        }
        if (!File.Exists(p.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {p.SourceFile}");
    }

    private static void ValidateConvertPropertyParams(ConvertPropertyParams p)
    {
        if (string.IsNullOrWhiteSpace(p.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");
        if (string.IsNullOrWhiteSpace(p.Direction))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "direction is required.");
        if (!PathResolver.IsAbsolutePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");
        if (!PathResolver.IsValidCSharpFilePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
        if (!p.Line.HasValue && string.IsNullOrWhiteSpace(p.PropertyName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either propertyName or line must be provided.");
        if (p.Line.HasValue && p.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");
        if (!Enum.TryParse<RoslynMcp.Contracts.Enums.ConversionDirection>(p.Direction, ignoreCase: true, out var dir) ||
            (dir != RoslynMcp.Contracts.Enums.ConversionDirection.ToAutoProperty && dir != RoslynMcp.Contracts.Enums.ConversionDirection.ToFullProperty))
        {
            throw new RefactoringException(ErrorCodes.CannotConvert, "direction must be 'ToAutoProperty' or 'ToFullProperty'.");
        }
        if (!File.Exists(p.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {p.SourceFile}");
    }

    private static void ValidateIntroduceParameterParams(IntroduceParameterParams p)
    {
        if (string.IsNullOrWhiteSpace(p.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");
        if (string.IsNullOrWhiteSpace(p.VariableName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "variableName is required.");
        if (!PathResolver.IsAbsolutePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");
        if (!PathResolver.IsValidCSharpFilePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
        if (p.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");
        if (!File.Exists(p.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {p.SourceFile}");
    }

    #endregion

    #region ConvertForeachLinqParams Validation

    [Fact]
    public void ConvertForeachLinq_MissingSourceFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertForeachLinqParams(new ConvertForeachLinqParams { SourceFile = "", Line = 1 }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ConvertForeachLinq_RelativePath_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertForeachLinqParams(new ConvertForeachLinqParams { SourceFile = "file.cs", Line = 1 }));
        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void ConvertForeachLinq_InvalidLine_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertForeachLinqParams(new ConvertForeachLinqParams { SourceFile = AbsoluteTestPath(), Line = 0 }));
        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void ConvertForeachLinq_NonCsFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertForeachLinqParams(new ConvertForeachLinqParams { SourceFile = AbsoluteTestPath(".txt"), Line = 1 }));
        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void ConvertForeachLinq_ValidParams_FileNotFoundExpected()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertForeachLinqParams(new ConvertForeachLinqParams { SourceFile = AbsoluteTestPath(), Line = 5 }));
        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region ConvertToPatternMatchingParams Validation

    [Fact]
    public void ConvertToPatternMatching_MissingSourceFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertToPatternMatchingParams(new ConvertToPatternMatchingParams { SourceFile = "", Line = 1 }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ConvertToPatternMatching_RelativePath_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertToPatternMatchingParams(new ConvertToPatternMatchingParams { SourceFile = "file.cs", Line = 1 }));
        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void ConvertToPatternMatching_InvalidLine_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertToPatternMatchingParams(new ConvertToPatternMatchingParams { SourceFile = AbsoluteTestPath(), Line = -1 }));
        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void ConvertToPatternMatching_NonCsFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertToPatternMatchingParams(new ConvertToPatternMatchingParams { SourceFile = AbsoluteTestPath(".vb"), Line = 1 }));
        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void ConvertToPatternMatching_ValidParams_FileNotFoundExpected()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertToPatternMatchingParams(new ConvertToPatternMatchingParams { SourceFile = AbsoluteTestPath(), Line = 10 }));
        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region ConvertToInterpolatedStringParams Validation

    [Fact]
    public void ConvertToInterpolatedString_MissingSourceFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertToInterpolatedStringParams(new ConvertToInterpolatedStringParams { SourceFile = "", Line = 1 }));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void ConvertToInterpolatedString_RelativePath_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertToInterpolatedStringParams(new ConvertToInterpolatedStringParams { SourceFile = "file.cs", Line = 1 }));
        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void ConvertToInterpolatedString_InvalidLine_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertToInterpolatedStringParams(new ConvertToInterpolatedStringParams { SourceFile = AbsoluteTestPath(), Line = 0 }));
        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void ConvertToInterpolatedString_NonCsFile_ThrowsException()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertToInterpolatedStringParams(new ConvertToInterpolatedStringParams { SourceFile = AbsoluteTestPath(".json"), Line = 1 }));
        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void ConvertToInterpolatedString_ValidParams_FileNotFoundExpected()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ValidateConvertToInterpolatedStringParams(new ConvertToInterpolatedStringParams { SourceFile = AbsoluteTestPath(), Line = 3 }));
        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region M5 Validation Helpers

    private static void ValidateConvertForeachLinqParams(ConvertForeachLinqParams p)
    {
        if (string.IsNullOrWhiteSpace(p.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");
        if (!PathResolver.IsAbsolutePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");
        if (!PathResolver.IsValidCSharpFilePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
        if (p.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");
        if (!File.Exists(p.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {p.SourceFile}");
    }

    private static void ValidateConvertToPatternMatchingParams(ConvertToPatternMatchingParams p)
    {
        if (string.IsNullOrWhiteSpace(p.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");
        if (!PathResolver.IsAbsolutePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");
        if (!PathResolver.IsValidCSharpFilePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
        if (p.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");
        if (!File.Exists(p.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {p.SourceFile}");
    }

    private static void ValidateConvertToInterpolatedStringParams(ConvertToInterpolatedStringParams p)
    {
        if (string.IsNullOrWhiteSpace(p.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");
        if (!PathResolver.IsAbsolutePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");
        if (!PathResolver.IsValidCSharpFilePath(p.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
        if (p.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");
        if (!File.Exists(p.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {p.SourceFile}");
    }

    #endregion
}
