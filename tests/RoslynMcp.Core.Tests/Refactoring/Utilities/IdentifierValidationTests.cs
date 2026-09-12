using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class IdentifierValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsValidIdentifier_False_ForNullOrEmpty(string? name)
    {
        Assert.False(IdentifierValidation.IsValidIdentifier(name!));
    }

    [Theory]
    [InlineData("1abc")]
    [InlineData("9")]
    [InlineData("-name")]
    [InlineData(" name")]
    public void IsValidIdentifier_False_ForBadStart(string name)
    {
        Assert.False(IdentifierValidation.IsValidIdentifier(name));
    }

    [Theory]
    [InlineData("a-b")]
    [InlineData("foo.bar")]
    [InlineData("x y")]
    [InlineData("@class")]
    public void IsValidIdentifier_False_ForInvalidBodyOrVerbatim(string name)
    {
        Assert.False(IdentifierValidation.IsValidIdentifier(name));
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("_private")]
    [InlineData("a1")]
    [InlineData("MaxRetries")]
    [InlineData("_")]
    [InlineData("Δ")]
    public void IsValidIdentifier_True_ForLetterOrUnderscoreForms(string name)
    {
        Assert.True(IdentifierValidation.IsValidIdentifier(name));
    }
}
