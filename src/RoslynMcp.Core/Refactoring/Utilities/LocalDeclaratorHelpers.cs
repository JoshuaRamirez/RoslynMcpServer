using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared local-declarator collection used by
/// <c>InlineVariableOperation</c> and <c>IntroduceParameterOperation</c>.
/// Same body as the prior private <c>CollectLocalDeclarators</c> /
/// <c>IsLocalDeclarator</c> copies.
/// </summary>
internal static class LocalDeclaratorHelpers
{
    /// <summary>
    /// Collects every local <see cref="VariableDeclaratorSyntax"/> in
    /// <paramref name="root"/> whose parent is a
    /// <see cref="LocalDeclarationStatementSyntax"/> (fields and for-loop
    /// declarators stay excluded; <c>using</c> / <c>await using</c>
    /// declarators stay excluded). Deterministic <c>SpanStart</c> then
    /// span-length order. Same body as the prior private copies on
    /// InlineVariable / IntroduceParameter.
    /// </summary>
    internal static IReadOnlyList<VariableDeclaratorSyntax> CollectLocalDeclarators(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(IsLocalDeclarator)
            .OrderBy(declarator => declarator.SpanStart)
            .ThenBy(declarator => declarator.Span.Length)
            .ToList();

    /// <summary>
    /// True when <paramref name="declarator"/> is a non-using local
    /// declaration (parent is <see cref="LocalDeclarationStatementSyntax"/>
    /// and not a using declaration). Same body as the prior private copies.
    /// </summary>
    internal static bool IsLocalDeclarator(VariableDeclaratorSyntax declarator) =>
        declarator.Parent is VariableDeclarationSyntax
        {
            Parent: LocalDeclarationStatementSyntax
        } &&
        !UsingDeclarationHelpers.IsUsingDeclaration(declarator);
}
