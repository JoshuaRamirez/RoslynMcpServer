using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared modifier-accessibility helpers used by hierarchy pull/push and
/// override emission when deciding whether an explicit accessibility keyword
/// is already present on a member, and whether an accessor is non-public.
/// </summary>
internal static class AccessibilityModifiers
{
    /// <summary>
    /// True when <paramref name="modifiers"/> contains any of
    /// <c>public</c>, <c>protected</c>, <c>internal</c>, or <c>private</c>.
    /// </summary>
    internal static bool HasAccessibility(IEnumerable<SyntaxToken> modifiers) =>
        modifiers.Any(token =>
            token.IsKind(SyntaxKind.PublicKeyword) ||
            token.IsKind(SyntaxKind.ProtectedKeyword) ||
            token.IsKind(SyntaxKind.InternalKeyword) ||
            token.IsKind(SyntaxKind.PrivateKeyword));

    /// <summary>
    /// True when <paramref name="accessor"/> has a <c>private</c>,
    /// <c>protected</c>, or <c>internal</c> modifier. Used to skip non-public
    /// accessors when emitting interface indexer declarations.
    /// </summary>
    internal static bool HasNonPublicAccessibility(AccessorDeclarationSyntax accessor) =>
        accessor.Modifiers.Any(token =>
            token.IsKind(SyntaxKind.PrivateKeyword)
            || token.IsKind(SyntaxKind.ProtectedKeyword)
            || token.IsKind(SyntaxKind.InternalKeyword));

    /// <summary>
    /// True when <paramref name="accessor"/> has <c>private</c> and does not
    /// also have <c>protected</c> or <c>internal</c>. Used when deciding
    /// whether an indexer can be left as an abstract member.
    /// </summary>
    internal static bool IsPrivateOnlyAccessor(AccessorDeclarationSyntax accessor) =>
        accessor.Modifiers.Any(SyntaxKind.PrivateKeyword)
        && !accessor.Modifiers.Any(SyntaxKind.ProtectedKeyword)
        && !accessor.Modifiers.Any(SyntaxKind.InternalKeyword);
}
