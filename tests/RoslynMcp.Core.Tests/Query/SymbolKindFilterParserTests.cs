using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Unit tests for <see cref="SymbolKindFilterParser"/>, the <c>kindFilter</c> parsing shared by
/// <c>search_symbols</c> and <c>get_document_outline</c>.
/// </summary>
public class SymbolKindFilterParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_OmittedOrBlank_ReturnsNull(string? kindFilter)
    {
        Assert.Null(SymbolKindFilterParser.Parse(kindFilter));
    }

    [Theory]
    [InlineData("Method", SymbolKind.Method)]
    [InlineData("method", SymbolKind.Method)]
    [InlineData("CLASS", SymbolKind.Class)]
    [InlineData("Namespace", SymbolKind.Namespace)]
    [InlineData("constant", SymbolKind.Constant)]
    [InlineData("  pRoPeRtY  ", SymbolKind.Property)]
    [InlineData("local", SymbolKind.Local)]
    [InlineData("Parameter", SymbolKind.Parameter)]
    public void Parse_KnownKind_IsCaseInsensitive(string kindFilter, SymbolKind expected)
    {
        Assert.Equal(expected, SymbolKindFilterParser.Parse(kindFilter));
    }

    [Fact]
    public void Parse_UnknownKind_ThrowsInvalidSymbolKindListingValidValues()
    {
        var ex = Assert.Throws<RefactoringException>(() => SymbolKindFilterParser.Parse("BogusKind"));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal(
            $"Invalid kindFilter 'BogusKind'. Valid values: {string.Join(", ", Enum.GetNames<SymbolKind>())}",
            ex.Message);
    }

    [Theory]
    [InlineData("6")]
    [InlineData("0")]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("+6")]
    [InlineData("Class, Method")]
    [InlineData("Class,Method")]
    [InlineData("Methods")]
    public void Parse_NumericUndefinedOrCombinedValue_ThrowsInvalidSymbolKind(string kindFilter)
    {
        var ex = Assert.Throws<RefactoringException>(() => SymbolKindFilterParser.Parse(kindFilter));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal(
            $"Invalid kindFilter '{kindFilter}'. Valid values: {string.Join(", ", Enum.GetNames<SymbolKind>())}",
            ex.Message);
    }
}
