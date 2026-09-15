using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Resolution;

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

    /// <summary>
    /// Finds a type by <paramref name="typeName"/>. Omitted
    /// <paramref name="column"/> keeps today's typeName + optional
    /// <paramref name="line"/> pick, including omitted-line
    /// <c>FirstOrDefault</c> and line-only exclusive-end coverage via
    /// <see cref="TypeCoverage"/>. Column without line keeps today's
    /// first-match after the typeName filter. When column is set with
    /// line, prefers identifier coverage then the smallest containing
    /// type; returns null when nothing covers that position. Same body
    /// as the four Generate copies (GenerateOverrides /
    /// GenerateEqualsHashCode / GenerateConstructor / ImplementInterface).
    /// </summary>
    internal static TypeDeclarationSyntax? FindTypeDeclaration(
        SyntaxNode root,
        string typeName,
        int? line,
        int? column = null)
    {
        var candidates = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == typeName)
            .ToList();

        if (candidates.Count == 0)
            return null;

        // Column without line is not a source position: substituting each
        // candidate's own start line would match every equally-aligned
        // same-name type and could silently pick the shortest. Keep
        // today's FirstOrDefault after the typeName filter.
        if (column.HasValue && !line.HasValue)
            return candidates.FirstOrDefault();

        if (column.HasValue)
        {
            // Do not require the declaration to start on `line` — a split
            // type's identifier may live on a continuation line whose
            // declaration span still covers that column. Prefer the
            // identifier hit, then the smallest containing type (nested
            // over outer). Do not silently pick the first when a covering
            // node exists elsewhere — scan every candidate. If nothing
            // covers this position, keep today's not-found (null) rather
            // than inventing a first-match.
            return candidates
                .Where(t => TypeCoverage.TypeCoversColumn(t, line!.Value, column.Value))
                .OrderBy(t => TypeCoverage.IdentifierCoversColumn(t, line!.Value, column.Value) ? 0 : 1)
                .ThenBy(t => t.Span.Length)
                .FirstOrDefault();
        }

        if (!line.HasValue)
            return candidates.FirstOrDefault();

        // Do not require the declaration to start on `line` — a split
        // type's identifier may live on a continuation line whose
        // declaration span still covers that line. Prefer the identifier
        // hit, then the smallest containing type (nested over outer).
        // Do not silently pick the first when a covering node exists
        // elsewhere — scan every candidate. If nothing covers this line,
        // keep today's first-match rather than inventing a not-found.
        return candidates
            .Where(t => TypeCoverage.TypeCoversLine(t, line.Value))
            .OrderBy(t => TypeCoverage.IdentifierCoversLine(t, line.Value) ? 0 : 1)
            .ThenBy(t => t.Span.Length)
            .FirstOrDefault()
            ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// Formats <paramref name="typeDecl"/>'s identifier for self-type
    /// references (Equals/GetHashCode constructor snippets), including
    /// open type-parameter names as <c>Name&lt;T1, T2&gt;</c> when a
    /// type-parameter list is present. Same body as the two Generate
    /// copies (GenerateEqualsHashCode / GenerateConstructor).
    /// </summary>
    internal static string GetSelfTypeName(TypeDeclarationSyntax typeDecl)
    {
        var identifier = typeDecl.Identifier.Text;
        if (typeDecl.TypeParameterList == null || typeDecl.TypeParameterList.Parameters.Count == 0)
            return identifier;

        var arguments = string.Join(", ", typeDecl.TypeParameterList.Parameters.Select(p => p.Identifier.Text));
        return $"{identifier}<{arguments}>";
    }
}
