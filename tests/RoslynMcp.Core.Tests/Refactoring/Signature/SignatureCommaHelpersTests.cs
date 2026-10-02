using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

public class SignatureCommaHelpersTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DefaultCommaSeparators_Empty_WhenNodeCountAtMostOne(int nodeCount)
    {
        Assert.Empty(SignatureCommaHelpers.DefaultCommaSeparators(nodeCount));
    }

    [Fact]
    public void DefaultCommaSeparators_RepeatsCommaWithSpace()
    {
        var separators = SignatureCommaHelpers.DefaultCommaSeparators(3);
        Assert.Equal(2, separators.Count);
        foreach (var sep in separators)
        {
            Assert.Equal(SyntaxKind.CommaToken, sep.Kind());
            Assert.Equal(" ", sep.TrailingTrivia.ToFullString());
        }
    }

    [Fact]
    public void CommaWithSpace_IsCommaTokenWithTrailingSpace()
    {
        var token = SignatureCommaHelpers.CommaWithSpace();
        Assert.Equal(SyntaxKind.CommaToken, token.Kind());
        Assert.Equal(" ", token.TrailingTrivia.ToFullString());
        Assert.Equal(", ", token.ToFullString());
    }
}
