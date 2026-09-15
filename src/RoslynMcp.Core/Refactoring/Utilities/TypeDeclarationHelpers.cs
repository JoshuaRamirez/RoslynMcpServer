using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared type-declaration walk used by Generate-family AllFiles / host
/// discovery paths. Same body as the six Generate copies
/// (ImplementAbstract / ImplementInterface / GenerateEqualsHashCode /
/// GenerateOverrides / GenerateToString / GenerateConstructor).
/// </summary>
internal static class TypeDeclarationHelpers
{
    /// <summary>
    /// Collects every <see cref="TypeDeclarationSyntax"/> under
    /// <paramref name="root"/> (classes, structs, interfaces, records,
    /// including nested), ordered by <see cref="SyntaxNode.SpanStart"/>
    /// then span length so same-start ties are stable.
    /// </summary>
    internal static IReadOnlyList<TypeDeclarationSyntax> CollectTypeDeclarations(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .OrderBy(type => type.SpanStart)
            .ThenBy(type => type.Span.Length)
            .ToList();
}
