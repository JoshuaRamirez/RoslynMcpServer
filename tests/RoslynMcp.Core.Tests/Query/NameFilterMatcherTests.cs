using RoslynMcp.Core.Query.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Unit tests for <see cref="NameFilterMatcher"/>, the <c>nameFilter</c> normalization and matching
/// shared by <c>get_document_outline</c>, <c>get_symbol_info</c>, <c>find_implementations</c>,
/// <c>get_type_hierarchy</c>, <c>find_callers</c>, and <c>find_references</c>.
/// </summary>
public class NameFilterMatcherTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Normalize_OmittedOrBlank_ReturnsNull(string? nameFilter)
    {
        Assert.Null(NameFilterMatcher.Normalize(nameFilter));
    }

    [Theory]
    [InlineData("Get", "Get")]
    [InlineData("  Get  ", "Get")]
    [InlineData("\tget Value\n", "get Value")]
    public void Normalize_NonBlank_ReturnsTrimmedValue(string nameFilter, string expected)
    {
        Assert.Equal(expected, NameFilterMatcher.Normalize(nameFilter));
    }

    [Theory]
    [InlineData("GetValue")]
    [InlineData("")]
    public void Matches_NullFilter_AlwaysTrue(string name)
    {
        Assert.True(NameFilterMatcher.Matches(name, null));
    }

    [Theory]
    [InlineData("GetValue", "Value", true)]
    [InlineData("GetValue", "value", true)]
    [InlineData("GetValue", "GETVALUE", true)]
    [InlineData("GetValue", "tVa", true)]
    [InlineData("GetValue", "Set", false)]
    [InlineData("GetValue", "GetValues", false)]
    public void Matches_Filter_IsOrdinalCaseInsensitiveSubstring(string name, string filter, bool expected)
    {
        Assert.Equal(expected, NameFilterMatcher.Matches(name, filter));
    }
}
