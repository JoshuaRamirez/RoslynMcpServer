using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared type-declaration helpers used by Generate-family operations.
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

    /// <summary>
    /// Appends <paramref name="newMembers"/> onto
    /// <paramref name="typeDeclaration"/> with a blank-line leading trivia
    /// and trailing CRLF per member. Same body as the three Generate copies
    /// (GenerateOverrides / ImplementInterface / ImplementAbstract).
    /// </summary>
    internal static TypeDeclarationSyntax AddMembers(
        TypeDeclarationSyntax typeDeclaration,
        IReadOnlyList<MemberDeclarationSyntax> newMembers)
    {
        var members = typeDeclaration.Members.ToList();

        foreach (var member in newMembers)
        {
            members.Add(member
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed)
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
        }

        return typeDeclaration.WithMembers(SyntaxFactory.List(members));
    }
}
