using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared containing-namespace / full-namespace name helpers used by
/// convert_anonymous_to_class / convert_tuple_to_struct (and by
/// <see cref="TypeInsertionHelpers.FindNamespace"/>). Same bodies as the
/// two identical Convert copies and the former private TypeInsertionHelpers twin.
/// Named NamespaceNameHelpers (not NamespaceHelpers) to avoid clashing with
/// RenameNamespace FindNamespace / AddMissingUsings FindNamespacesForType.
/// </summary>
internal static class NamespaceNameHelpers
{
    /// <summary>
    /// Containing namespace of <paramref name="node"/> via enclosing symbol when
    /// available, otherwise syntax ancestors. Same body as the two Convert copies.
    /// </summary>
    internal static string? GetContainingNamespaceName(SemanticModel semanticModel, SyntaxNode node)
    {
        var enclosing = semanticModel.GetEnclosingSymbol(node.SpanStart);
        var fromSymbol = NamespaceEqualityHelpers.ToNamespaceName(enclosing?.ContainingNamespace);
        if (!string.IsNullOrEmpty(fromSymbol))
            return fromSymbol;

        return GetContainingNamespaceName(node);
    }

    /// <summary>
    /// Full name of the innermost enclosing namespace declaration of
    /// <paramref name="node"/>, or null when none / empty. Same body as the
    /// two Convert copies.
    /// </summary>
    internal static string? GetContainingNamespaceName(SyntaxNode node)
    {
        var name = GetFullNamespaceName(
            node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault());
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// Joins nested namespace declarations into the full enclosing name
    /// (e.g. <c>Outer.Inner</c>, not <c>Inner</c>). Same body as the two
    /// Convert copies and the former private TypeInsertionHelpers twin.
    /// </summary>
    internal static string? GetFullNamespaceName(BaseNamespaceDeclarationSyntax? ns)
    {
        if (ns == null)
            return null;

        var parts = ns.AncestorsAndSelf()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(n => n.Name.ToString())
            .Where(part => !string.IsNullOrEmpty(part));

        var joined = string.Join(".", parts);
        return string.IsNullOrEmpty(joined) ? null : joined;
    }
}
