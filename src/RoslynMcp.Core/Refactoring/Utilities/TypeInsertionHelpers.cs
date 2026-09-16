using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared type-declaration insertion helpers used by convert_anonymous_to_class /
/// convert_tuple_to_struct when placing a new named type into a namespace or
/// compilation unit. Same bodies as the two private Convert copies.
/// <see cref="FindNamespace"/> uses <see cref="NamespaceNameHelpers.GetFullNamespaceName"/>.
/// </summary>
internal static class TypeInsertionHelpers
{
    /// <summary>
    /// Inserts <paramref name="typeDeclaration"/> into <paramref name="insertionHost"/>
    /// (namespace members or compilation-unit members) and returns the updated root.
    /// Same body as the two private Convert copies.
    /// </summary>
    internal static SyntaxNode InsertTypeDeclaration(
        SyntaxNode root,
        SyntaxNode? insertionHost,
        TypeDeclarationSyntax typeDeclaration)
    {
        switch (insertionHost)
        {
            case BaseNamespaceDeclarationSyntax ns:
                return root.ReplaceNode(ns, ns.AddMembers(typeDeclaration));
            case CompilationUnitSyntax:
                return ((CompilationUnitSyntax)root).AddMembers(typeDeclaration);
            default:
                if (root is CompilationUnitSyntax compilationUnit)
                    return compilationUnit.AddMembers(typeDeclaration);
                throw new RefactoringException(
                    ErrorCodes.RoslynError,
                    "Could not find a compilable location for the new type.");
        }
    }

    /// <summary>
    /// Finds the last <see cref="BaseNamespaceDeclarationSyntax"/> under
    /// <paramref name="root"/> whose full name equals <paramref name="targetNamespace"/>,
    /// or null when empty / no match. Same body as the two private Convert copies.
    /// </summary>
    internal static BaseNamespaceDeclarationSyntax? FindNamespace(SyntaxNode root, string? targetNamespace)
    {
        if (string.IsNullOrEmpty(targetNamespace))
            return null;

        return root.DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .LastOrDefault(ns => string.Equals(NamespaceNameHelpers.GetFullNamespaceName(ns), targetNamespace, StringComparison.Ordinal));
    }

    /// <summary>
    /// Position at the end of the creation's enclosing namespace (block close-brace
    /// start, or file-scoped namespace end) or at the end of <paramref name="root"/>
    /// when there is no enclosing namespace. Same body as the two private Convert copies.
    /// </summary>
    internal static int GetTypeInsertionPosition(SyntaxNode root, SyntaxNode creation)
    {
        var ns = creation.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (ns is NamespaceDeclarationSyntax blockNamespace)
            return blockNamespace.CloseBraceToken.SpanStart;
        if (ns != null)
            return ns.Span.End;
        return root.Span.End;
    }
}
