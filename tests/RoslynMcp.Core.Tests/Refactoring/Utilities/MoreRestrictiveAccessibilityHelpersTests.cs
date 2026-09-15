using Microsoft.CodeAnalysis;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class MoreRestrictiveAccessibilityHelpersTests
{
    [Theory]
    [InlineData(Accessibility.Private, Accessibility.Public, Accessibility.Private)]
    [InlineData(Accessibility.Public, Accessibility.Private, Accessibility.Private)]
    [InlineData(Accessibility.Protected, Accessibility.Internal, Accessibility.Internal)]
    [InlineData(Accessibility.Internal, Accessibility.Protected, Accessibility.Internal)]
    [InlineData(Accessibility.Private, Accessibility.Private, Accessibility.Private)]
    [InlineData(Accessibility.Public, Accessibility.Public, Accessibility.Public)]
    [InlineData(Accessibility.ProtectedAndInternal, Accessibility.Internal, Accessibility.ProtectedAndInternal)]
    [InlineData(Accessibility.ProtectedOrInternal, Accessibility.Public, Accessibility.ProtectedOrInternal)]
    [InlineData(Accessibility.NotApplicable, Accessibility.Public, Accessibility.NotApplicable)]
    [InlineData(Accessibility.Public, Accessibility.NotApplicable, Accessibility.NotApplicable)]
    [InlineData(Accessibility.NotApplicable, Accessibility.Private, Accessibility.NotApplicable)]
    [InlineData(Accessibility.Private, Accessibility.NotApplicable, Accessibility.Private)]
    public void MoreRestrictive_ReturnsMoreRestrictive(
        Accessibility left,
        Accessibility right,
        Accessibility expected)
    {
        Assert.Equal(expected, MoreRestrictiveAccessibilityHelpers.MoreRestrictive(left, right));
    }

    [Theory]
    [InlineData(Accessibility.Private, 0)]
    [InlineData(Accessibility.ProtectedAndInternal, 1)]
    [InlineData(Accessibility.Internal, 2)]
    [InlineData(Accessibility.Protected, 3)]
    [InlineData(Accessibility.ProtectedOrInternal, 4)]
    [InlineData(Accessibility.Public, 5)]
    [InlineData(Accessibility.NotApplicable, 0)]
    public void AccessibilityRank_KnownValues(Accessibility accessibility, int expected)
    {
        Assert.Equal(expected, MoreRestrictiveAccessibilityHelpers.AccessibilityRank(accessibility));
    }
}
