using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared partial-type rematch helpers used when applying Generate / Hierarchy /
/// ExtractBaseClass rewrites across syntax trees (SameSyntaxTree by reference or
/// case-insensitive FilePath; RematchTypeDeclaration by SpanStart + Identifier).
/// </summary>
internal static class TypePartRematch
{
    /// <summary>
    /// True when <paramref name="left"/> and <paramref name="right"/> are the
    /// same tree by reference, or share a non-empty FilePath compared
    /// ordinal-ignore-case (same body as the Generate-family copies).
    /// </summary>
    internal static bool SameSyntaxTree(SyntaxTree left, SyntaxTree right) =>
        left == right
        || (!string.IsNullOrEmpty(left.FilePath)
            && string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Rematches a type declaration in <paramref name="root"/> by SpanStart +
    /// Identifier.Text (same body as the Generate / Pull / Push copies).
    /// </summary>
    internal static TypeDeclarationSyntax? RematchTypeDeclaration(
        SyntaxNode root,
        TypeDeclarationSyntax original) =>
        root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.SpanStart == original.SpanStart && t.Identifier.Text == original.Identifier.Text);
}
