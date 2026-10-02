using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared hierarchy abstract/override modifier rewrites used by
/// <c>HierarchyAbstractMemberRewriter</c> and <c>push_members_down</c>.
/// Same core bodies as the prior private copies; intentional divergences
/// are expressed as flags (<see cref="ToAbstractModifiers"/> keep-override,
/// <see cref="ToOverrideModifiers"/> strip-sealed).
/// </summary>
internal static class HierarchyModifierHelpers
{
    /// <summary>
    /// Strips private / virtual / override / sealed / abstract / new / async,
    /// ensures a non-private accessibility (defaults to protected), then adds
    /// <c>abstract</c>. When <paramref name="keepOverrideWhenPresent"/> is
    /// true and the input had <c>override</c>, re-adds <c>override</c> after
    /// <c>abstract</c> (push leave-abstract path). Rewriter / pull path pass
    /// false.
    /// </summary>
    internal static SyntaxTokenList ToAbstractModifiers(
        SyntaxTokenList modifiers,
        bool keepOverrideWhenPresent = false)
    {
        var keepOverride = keepOverrideWhenPresent
            && modifiers.Any(SyntaxKind.OverrideKeyword);
        var tokens = AccessibilityModifiers.StripModifiers(
                modifiers,
                SyntaxKind.PrivateKeyword,
                SyntaxKind.VirtualKeyword,
                SyntaxKind.OverrideKeyword,
                SyntaxKind.SealedKeyword,
                SyntaxKind.AbstractKeyword,
                SyntaxKind.NewKeyword,
                SyntaxKind.AsyncKeyword)
            .ToList();

        if (!AccessibilityModifiers.HasAccessibility(tokens))
            tokens.Insert(0, SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));

        tokens.Add(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));
        if (keepOverride)
        {
            tokens.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword)
                .WithTrailingTrivia(SyntaxFactory.ElasticSpace));
        }

        return SyntaxFactory.TokenList(tokens);
    }

    /// <summary>
    /// Strips private / virtual / abstract / override / new (and optionally
    /// sealed), ensures accessibility (private → protected; missing →
    /// protected), then adds <c>override</c> with elastic trailing space.
    /// Push pass <paramref name="stripSealed"/> true; rewriter / pull pass
    /// false (sealed left in place when present).
    /// </summary>
    internal static SyntaxTokenList ToOverrideModifiers(
        SyntaxTokenList modifiers,
        bool stripSealed = false)
    {
        var tokens = (stripSealed
                ? AccessibilityModifiers.StripModifiers(
                    modifiers,
                    SyntaxKind.PrivateKeyword,
                    SyntaxKind.VirtualKeyword,
                    SyntaxKind.AbstractKeyword,
                    SyntaxKind.OverrideKeyword,
                    SyntaxKind.NewKeyword,
                    SyntaxKind.SealedKeyword)
                : AccessibilityModifiers.StripModifiers(
                    modifiers,
                    SyntaxKind.PrivateKeyword,
                    SyntaxKind.VirtualKeyword,
                    SyntaxKind.AbstractKeyword,
                    SyntaxKind.OverrideKeyword,
                    SyntaxKind.NewKeyword))
            .ToList();

        if (modifiers.Any(SyntaxKind.PrivateKeyword) || !AccessibilityModifiers.HasAccessibility(tokens))
            tokens.Insert(0, SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));

        tokens.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword)
            .WithTrailingTrivia(SyntaxFactory.ElasticSpace));
        return SyntaxFactory.TokenList(tokens);
    }
}
