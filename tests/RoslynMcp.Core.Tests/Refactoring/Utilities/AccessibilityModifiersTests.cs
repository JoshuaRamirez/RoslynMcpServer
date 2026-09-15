using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class AccessibilityModifiersTests
{
    [Theory]
    [InlineData(SyntaxKind.PublicKeyword)]
    [InlineData(SyntaxKind.ProtectedKeyword)]
    [InlineData(SyntaxKind.InternalKeyword)]
    [InlineData(SyntaxKind.PrivateKeyword)]
    public void HasAccessibility_True_ForEachAccessibilityKeyword(SyntaxKind kind)
    {
        var modifiers = new[] { SyntaxFactory.Token(kind) };
        Assert.True(AccessibilityModifiers.HasAccessibility(modifiers));
    }

    [Fact]
    public void HasAccessibility_True_WhenMixedWithNonAccessibility()
    {
        var modifiers = new[]
        {
            SyntaxFactory.Token(SyntaxKind.StaticKeyword),
            SyntaxFactory.Token(SyntaxKind.ProtectedKeyword),
            SyntaxFactory.Token(SyntaxKind.VirtualKeyword),
        };
        Assert.True(AccessibilityModifiers.HasAccessibility(modifiers));
    }

    [Fact]
    public void HasAccessibility_False_WhenEmpty()
    {
        Assert.False(AccessibilityModifiers.HasAccessibility(Array.Empty<SyntaxToken>()));
    }

    [Fact]
    public void HasAccessibility_False_WhenOnlyNonAccessibilityModifiers()
    {
        var modifiers = new[]
        {
            SyntaxFactory.Token(SyntaxKind.StaticKeyword),
            SyntaxFactory.Token(SyntaxKind.AsyncKeyword),
            SyntaxFactory.Token(SyntaxKind.VirtualKeyword),
            SyntaxFactory.Token(SyntaxKind.OverrideKeyword),
            SyntaxFactory.Token(SyntaxKind.AbstractKeyword),
            SyntaxFactory.Token(SyntaxKind.SealedKeyword),
            SyntaxFactory.Token(SyntaxKind.NewKeyword),
            SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword),
        };
        Assert.False(AccessibilityModifiers.HasAccessibility(modifiers));
    }

    [Fact]
    public void HasNonPublicAccessibility_False_WhenNoAccessibilityModifiers()
    {
        var accessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        Assert.False(AccessibilityModifiers.HasNonPublicAccessibility(accessor));
    }

    [Theory]
    [InlineData(SyntaxKind.PrivateKeyword)]
    [InlineData(SyntaxKind.ProtectedKeyword)]
    [InlineData(SyntaxKind.InternalKeyword)]
    public void HasNonPublicAccessibility_True_ForNonPublicKeyword(SyntaxKind kind)
    {
        var accessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(kind)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        Assert.True(AccessibilityModifiers.HasNonPublicAccessibility(accessor));
    }

    [Fact]
    public void HasNonPublicAccessibility_False_WhenPublicOnly()
    {
        var accessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        Assert.False(AccessibilityModifiers.HasNonPublicAccessibility(accessor));
    }

    [Fact]
    public void HasNonPublicAccessibility_True_WhenPrivateMixedWithNonAccessibility()
    {
        var accessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.AsyncKeyword)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        Assert.True(AccessibilityModifiers.HasNonPublicAccessibility(accessor));
    }

    [Fact]
    public void IsPrivateOnlyAccessor_False_WhenNoModifiers()
    {
        var accessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        Assert.False(AccessibilityModifiers.IsPrivateOnlyAccessor(accessor));
    }

    [Fact]
    public void IsPrivateOnlyAccessor_True_WhenPrivateOnly()
    {
        var accessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        Assert.True(AccessibilityModifiers.IsPrivateOnlyAccessor(accessor));
    }

    [Theory]
    [InlineData(SyntaxKind.ProtectedKeyword)]
    [InlineData(SyntaxKind.InternalKeyword)]
    [InlineData(SyntaxKind.PublicKeyword)]
    public void IsPrivateOnlyAccessor_False_WhenNonPrivateAccessibilityAlone(SyntaxKind kind)
    {
        var accessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(kind)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        Assert.False(AccessibilityModifiers.IsPrivateOnlyAccessor(accessor));
    }

    [Theory]
    [InlineData(SyntaxKind.ProtectedKeyword)]
    [InlineData(SyntaxKind.InternalKeyword)]
    public void IsPrivateOnlyAccessor_False_WhenPrivateCombinedWithProtectedOrInternal(SyntaxKind other)
    {
        var accessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(other)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        Assert.False(AccessibilityModifiers.IsPrivateOnlyAccessor(accessor));
    }

    [Fact]
    public void IsPrivateOnlyAccessor_True_WhenPrivateMixedWithNonAccessibility()
    {
        var accessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.AsyncKeyword)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        Assert.True(AccessibilityModifiers.IsPrivateOnlyAccessor(accessor));
    }
}
