using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;

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

    /// <summary>
    /// Recovers a type declaration after a per-execution annotation rewrite:
    /// prefer the annotated node, else rematch by SpanStart + Identifier, else
    /// throw <see cref="ErrorCodes.TypeNotFound"/>. Same body as the prior
    /// PullMembersUp / PushMembersDown private copies.
    /// </summary>
    internal static TypeDeclarationSyntax RecoverAnnotatedType(
        SyntaxNode root,
        SyntaxAnnotation typeAnnotation,
        TypeDeclarationSyntax original,
        string typeName)
    {
        var annotated = root.GetAnnotatedNodes(typeAnnotation)
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();
        if (annotated != null)
            return annotated;

        return RematchTypeDeclaration(root, original)
            ?? throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found in file.");
    }
}
