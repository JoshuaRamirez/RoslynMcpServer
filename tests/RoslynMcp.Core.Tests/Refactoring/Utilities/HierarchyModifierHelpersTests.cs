using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="HierarchyModifierHelpers"/> —
/// abstract/override modifier rewrites previously duplicated on
/// HierarchyAbstractMemberRewriter / PushMembersDown.
/// </summary>
public class HierarchyModifierHelpersTests
{
    [Fact]
    public void ToAbstractModifiers_DefaultsProtectedAndAddsAbstract()
    {
        var modifiers = SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.VirtualKeyword));

        var result = HierarchyModifierHelpers.ToAbstractModifiers(modifiers)
            .Select(t => t.Kind())
            .ToList();

        Assert.Equal(
            new[] { SyntaxKind.ProtectedKeyword, SyntaxKind.AbstractKeyword },
            result);
    }

    [Fact]
    public void ToAbstractModifiers_StripsPrivateVirtualOverrideSealedAbstractNewAsync()
    {
        var modifiers = SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
            SyntaxFactory.Token(SyntaxKind.VirtualKeyword),
            SyntaxFactory.Token(SyntaxKind.OverrideKeyword),
            SyntaxFactory.Token(SyntaxKind.SealedKeyword),
            SyntaxFactory.Token(SyntaxKind.AbstractKeyword),
            SyntaxFactory.Token(SyntaxKind.NewKeyword),
            SyntaxFactory.Token(SyntaxKind.AsyncKeyword),
            SyntaxFactory.Token(SyntaxKind.StaticKeyword));

        var result = HierarchyModifierHelpers.ToAbstractModifiers(modifiers)
            .Select(t => t.Kind())
            .ToList();

        Assert.Equal(
            new[]
            {
                SyntaxKind.ProtectedKeyword,
                SyntaxKind.StaticKeyword,
                SyntaxKind.AbstractKeyword,
            },
            result);
    }

    [Fact]
    public void ToAbstractModifiers_PreservesExistingNonPrivateAccessibility()
    {
        var modifiers = SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            SyntaxFactory.Token(SyntaxKind.VirtualKeyword));

        var result = HierarchyModifierHelpers.ToAbstractModifiers(modifiers)
            .Select(t => t.Kind())
            .ToList();

        Assert.Equal(
            new[] { SyntaxKind.PublicKeyword, SyntaxKind.AbstractKeyword },
            result);
    }

    [Fact]
    public void ToAbstractModifiers_KeepOverrideWhenPresent_ReaddsOverride()
    {
        var modifiers = SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            SyntaxFactory.Token(SyntaxKind.OverrideKeyword));

        var result = HierarchyModifierHelpers
            .ToAbstractModifiers(modifiers, keepOverrideWhenPresent: true)
            .Select(t => t.Kind())
            .ToList();

        Assert.Equal(
            new[]
            {
                SyntaxKind.PublicKeyword,
                SyntaxKind.AbstractKeyword,
                SyntaxKind.OverrideKeyword,
            },
            result);
    }

    [Fact]
    public void ToAbstractModifiers_KeepOverrideWhenPresent_False_DoesNotReadd()
    {
        var modifiers = SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            SyntaxFactory.Token(SyntaxKind.OverrideKeyword));

        var result = HierarchyModifierHelpers
            .ToAbstractModifiers(modifiers, keepOverrideWhenPresent: false)
            .Select(t => t.Kind())
            .ToList();

        Assert.Equal(
            new[] { SyntaxKind.PublicKeyword, SyntaxKind.AbstractKeyword },
            result);
    }

    [Fact]
    public void ToOverrideModifiers_DefaultsProtectedAndAddsOverride()
    {
        var modifiers = SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.VirtualKeyword));

        var result = HierarchyModifierHelpers.ToOverrideModifiers(modifiers)
            .Select(t => t.Kind())
            .ToList();

        Assert.Equal(
            new[] { SyntaxKind.ProtectedKeyword, SyntaxKind.OverrideKeyword },
            result);
    }

    [Fact]
    public void ToOverrideModifiers_PrivateBecomesProtected()
    {
        var modifiers = SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
            SyntaxFactory.Token(SyntaxKind.VirtualKeyword));

        var result = HierarchyModifierHelpers.ToOverrideModifiers(modifiers)
            .Select(t => t.Kind())
            .ToList();

        Assert.Equal(
            new[] { SyntaxKind.ProtectedKeyword, SyntaxKind.OverrideKeyword },
            result);
    }

    [Fact]
    public void ToOverrideModifiers_StripSealedFalse_PreservesSealed()
    {
        var modifiers = SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            SyntaxFactory.Token(SyntaxKind.SealedKeyword),
            SyntaxFactory.Token(SyntaxKind.OverrideKeyword));

        var result = HierarchyModifierHelpers
            .ToOverrideModifiers(modifiers, stripSealed: false)
            .Select(t => t.Kind())
            .ToList();

        Assert.Equal(
            new[]
            {
                SyntaxKind.PublicKeyword,
                SyntaxKind.SealedKeyword,
                SyntaxKind.OverrideKeyword,
            },
            result);
    }

    [Fact]
    public void ToOverrideModifiers_StripSealedTrue_RemovesSealed()
    {
        var modifiers = SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            SyntaxFactory.Token(SyntaxKind.SealedKeyword),
            SyntaxFactory.Token(SyntaxKind.OverrideKeyword));

        var result = HierarchyModifierHelpers
            .ToOverrideModifiers(modifiers, stripSealed: true)
            .Select(t => t.Kind())
            .ToList();

        Assert.Equal(
            new[] { SyntaxKind.PublicKeyword, SyntaxKind.OverrideKeyword },
            result);
    }
}
