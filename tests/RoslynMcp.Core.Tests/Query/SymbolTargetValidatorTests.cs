using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Unit tests for <see cref="SymbolTargetValidator.Validate"/>, the shared line / column / symbolName
/// target gate of <c>find_references</c>, <c>find_implementations</c>, <c>find_callers</c>,
/// <c>get_type_hierarchy</c>, <c>get_symbol_info</c>, and <c>go_to_definition</c>: each error code /
/// message, check order, and the success paths.
/// </summary>
public class SymbolTargetValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoLineAndBlankSymbolName_ThrowsMissingRequiredParam(string? symbolName)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SymbolTargetValidator.Validate(line: null, column: null, symbolName));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("Either line/column or symbolName must be provided.", ex.Message);
    }

    [Fact]
    public void ColumnWithoutLineOrSymbolName_ThrowsMissingRequiredParam()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SymbolTargetValidator.Validate(line: null, column: 5, symbolName: null));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void MissingTarget_IsReportedBeforeInvalidColumn()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SymbolTargetValidator.Validate(line: null, column: 0, symbolName: " "));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void LineBelowOne_ThrowsInvalidLineNumber(int line)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SymbolTargetValidator.Validate(line, column: null, symbolName: null));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("Line number must be >= 1.", ex.Message);
    }

    [Fact]
    public void InvalidLine_IsReportedBeforeInvalidColumn()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SymbolTargetValidator.Validate(line: 0, column: 0, symbolName: "Foo"));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Theory]
    [InlineData(1, 0, null)]
    [InlineData(3, -2, null)]
    [InlineData(null, 0, "Foo")]
    public void ColumnBelowOne_ThrowsInvalidColumnNumber(int? line, int column, string? symbolName)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SymbolTargetValidator.Validate(line, column, symbolName));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("Column number must be >= 1.", ex.Message);
    }

    [Theory]
    [InlineData(1, null, null)]
    [InlineData(1, 1, null)]
    [InlineData(10, 4, "Foo")]
    [InlineData(null, null, "Foo")]
    [InlineData(null, 2, "Foo")]
    public void ValidTarget_DoesNotThrow(int? line, int? column, string? symbolName)
    {
        SymbolTargetValidator.Validate(line, column, symbolName);
    }
}
