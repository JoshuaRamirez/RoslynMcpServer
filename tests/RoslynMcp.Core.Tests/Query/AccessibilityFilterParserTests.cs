using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Unit tests for <see cref="AccessibilityFilterParser"/>, the <c>search_symbols</c>
/// <c>accessibilityFilter</c> parsing.
/// </summary>
public class AccessibilityFilterParserTests
{
    private const string ValidValues =
        "public, internal, protected, private, protected internal, private protected";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Parse_OmittedOrBlank_ReturnsNull(string? accessibilityFilter)
    {
        Assert.Null(AccessibilityFilterParser.Parse(accessibilityFilter));
    }

    [Theory]
    [InlineData("public", Accessibility.Public)]
    [InlineData("internal", Accessibility.Internal)]
    [InlineData("protected", Accessibility.Protected)]
    [InlineData("private", Accessibility.Private)]
    [InlineData("protected internal", Accessibility.ProtectedOrInternal)]
    [InlineData("private protected", Accessibility.ProtectedAndInternal)]
    public void Parse_CSharpSpelling_MapsToRoslynAccessibility(string accessibilityFilter, Accessibility expected)
    {
        Assert.Equal(expected, AccessibilityFilterParser.Parse(accessibilityFilter));
    }

    [Theory]
    [InlineData("PUBLIC", Accessibility.Public)]
    [InlineData("  Internal  ", Accessibility.Internal)]
    [InlineData("Protected  Internal", Accessibility.ProtectedOrInternal)]
    [InlineData(" PRIVATE\tprotected ", Accessibility.ProtectedAndInternal)]
    [InlineData("internal protected", Accessibility.ProtectedOrInternal)]
    [InlineData("Protected Private", Accessibility.ProtectedAndInternal)]
    public void Parse_IsCaseInsensitiveAndNormalizesWhitespace(string accessibilityFilter, Accessibility expected)
    {
        Assert.Equal(expected, AccessibilityFilterParser.Parse(accessibilityFilter));
    }

    [Theory]
    [InlineData("friend")]
    [InlineData("ProtectedOrInternal")]
    [InlineData("ProtectedAndInternal")]
    [InlineData("NotApplicable")]
    [InlineData("protected internal protected")]
    [InlineData("public internal")]
    [InlineData("public, internal")]
    [InlineData("6")]
    [InlineData("pub")]
    public void Parse_UnknownValue_ThrowsInvalidVisibilityListingValidValues(string accessibilityFilter)
    {
        var ex = Assert.Throws<RefactoringException>(() => AccessibilityFilterParser.Parse(accessibilityFilter));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        Assert.Equal(
            $"Invalid accessibilityFilter '{accessibilityFilter}'. Valid values: {ValidValues}",
            ex.Message);
    }
}
