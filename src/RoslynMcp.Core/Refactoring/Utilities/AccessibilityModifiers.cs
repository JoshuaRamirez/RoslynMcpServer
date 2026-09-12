using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared modifier-accessibility helpers used by hierarchy pull/push and
/// override emission when deciding whether an explicit accessibility keyword
/// is already present on a member.
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
}
