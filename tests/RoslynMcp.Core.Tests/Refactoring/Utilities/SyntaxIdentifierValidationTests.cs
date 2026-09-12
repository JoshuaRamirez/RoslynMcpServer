using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class SyntaxIdentifierValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void IsValidIdentifier_False_ForWhitespaceOrEmpty(string? name)
    {
        Assert.False(SyntaxIdentifierValidation.IsValidIdentifier(name!));
    }

    [Theory]
    [InlineData("class")]
    [InlineData("namespace")]
    public void IsValidIdentifier_False_ForReservedKeyword(string name)
    {
        Assert.False(SyntaxIdentifierValidation.IsValidIdentifier(name));
    }

    [Theory]
    [InlineData("@class")]
    [InlineData("Δ")]
    [InlineData("MaxRetries")]
    [InlineData("DoWork")]
    [InlineData("Name")]
    public void IsValidIdentifier_True_ForVerbatimUnicodeAndNormal(string name)
    {
        Assert.True(SyntaxIdentifierValidation.IsValidIdentifier(name));
    }

    [Theory]
    [InlineData("123bad")]
    public void IsValidIdentifier_False_ForDigitStart(string name)
    {
        Assert.False(SyntaxIdentifierValidation.IsValidIdentifier(name));
    }
}
