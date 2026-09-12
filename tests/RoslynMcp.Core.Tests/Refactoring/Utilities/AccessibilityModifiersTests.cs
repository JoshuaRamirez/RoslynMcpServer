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
}
