using Microsoft.CodeAnalysis;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class AccessibilityRankHelpersTests
{
    [Theory]
    [InlineData(Accessibility.Private, Accessibility.Public, Accessibility.Private)]
    [InlineData(Accessibility.Public, Accessibility.Private, Accessibility.Private)]
    [InlineData(Accessibility.Protected, Accessibility.Internal, Accessibility.Protected)]
    [InlineData(Accessibility.Internal, Accessibility.Protected, Accessibility.Protected)]
    [InlineData(Accessibility.Private, Accessibility.Private, Accessibility.Private)]
    [InlineData(Accessibility.Public, Accessibility.Public, Accessibility.Public)]
    [InlineData(Accessibility.ProtectedAndInternal, Accessibility.Protected, Accessibility.ProtectedAndInternal)]
    [InlineData(Accessibility.ProtectedOrInternal, Accessibility.Public, Accessibility.ProtectedOrInternal)]
    [InlineData(Accessibility.NotApplicable, Accessibility.Private, Accessibility.Private)]
    [InlineData(Accessibility.Private, Accessibility.NotApplicable, Accessibility.Private)]
    public void MinAccessibility_ReturnsLessAccessible(
        Accessibility left,
        Accessibility right,
        Accessibility expected)
    {
        Assert.Equal(expected, AccessibilityRankHelpers.MinAccessibility(left, right));
    }

    [Theory]
    [InlineData(Accessibility.Private, 0)]
    [InlineData(Accessibility.ProtectedAndInternal, 1)]
    [InlineData(Accessibility.Protected, 2)]
    [InlineData(Accessibility.Internal, 3)]
    [InlineData(Accessibility.ProtectedOrInternal, 4)]
    [InlineData(Accessibility.Public, 5)]
    [InlineData(Accessibility.NotApplicable, 5)]
    public void AccessibilityRank_KnownValues(Accessibility accessibility, int expected)
    {
        Assert.Equal(expected, AccessibilityRankHelpers.AccessibilityRank(accessibility));
    }
}
