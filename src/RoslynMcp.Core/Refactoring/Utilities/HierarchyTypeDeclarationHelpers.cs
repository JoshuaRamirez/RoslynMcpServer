using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Resolution;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared type-declaration lookup used by
/// <c>PullMembersUpOperation</c> and <c>PushMembersDownOperation</c>.
/// Same body as the prior <c>FindTypeDeclaration</c> copies on those
/// operations (FQN simpleName strip; enum/delegate in the line set for
/// InvalidSymbolKind). Intentionally separate from
/// <see cref="TypeDeclarationHelpers.FindTypeDeclaration"/>, which stays
/// <see cref="TypeDeclarationSyntax"/>-only.
/// </summary>
internal static class HierarchyTypeDeclarationHelpers
{
    /// <summary>
    /// Finds a type by <paramref name="typeName"/>. Omitted
    /// <paramref name="column"/> keeps today's typeName + optional
    /// <paramref name="line"/> pick, including omitted-line
    /// <c>TypeDeclarationSyntax</c> <c>FirstOrDefault</c> (enum and
    /// <c>DelegateDeclarationSyntax</c> do not participate) and
    /// line-only exclusive-end coverage (<see cref="SpanCoverage.SpanCoversLine(FileLinePositionSpan, int)"/>).
    /// Do not force column 1 when omitted. Do not change
    /// omitted-line/omitted-column to <c>BaseTypeDeclarationSyntax</c>
    /// FirstOrDefault. Do not add enums or delegates to the omitted-line
    /// set. Column without line keeps today's first-match after the
    /// typeName filter (<c>TypeDeclarationSyntax</c> only) rather than
    /// substituting each candidate's own start line. When column is set
    /// with line, picks the type whose identifier or declaration span
    /// covers that 1-based column (same exclusive-end coverage as
    /// <c>SpanCoverage.SpanCoversColumn</c>). Prefer the
    /// identifier hit, then the smallest containing type. Nested types,
    /// enums, and <c>DelegateDeclarationSyntax</c> participate when line
    /// is set so a covering enum or delegate still reaches
    /// <c>InvalidSymbolKind</c> rather than retargeting a later class. Do
    /// not require the declaration to start on <paramref name="line"/>
    /// when column is set — a split declaration may put the identifier on
    /// a continuation line. If column is set with line and nothing covers
    /// that position, return null (TypeNotFound) rather than falling back
    /// to first-match. After a hierarchy rewrite (pull: derived rewrite +
    /// members added to the target; push: source rewrite + members added
    /// to derived types), recover the selected type from the per-execution
    /// syntax annotation — do not reuse a pre-rewrite SpanStart or line.
    /// </summary>
    internal static MemberDeclarationSyntax? FindTypeDeclaration(
        SyntaxNode root,
        string typeName,
        int? line,
        int? column = null)
    {
        var simpleName = typeName.Contains('.')
            ? typeName[(typeName.LastIndexOf('.') + 1)..]
            : typeName;

        var typeCandidates = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == simpleName)
            .ToList();

        // Line set (including column+line) uses BaseTypeDeclarationSyntax
        // (enum) plus DelegateDeclarationSyntax so a covering enum or
        // delegate reaches InvalidSymbolKind rather than retargeting a
        // later class. Omitted-line / column-without-line stay
        // TypeDeclarationSyntax only — do not switch that set to
        // BaseTypeDeclarationSyntax (that would add enums).
        var lineCandidates = line.HasValue
            ? root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .Where(t => t.Identifier.Text == simpleName)
                .Cast<MemberDeclarationSyntax>()
                .Concat(root.DescendantNodes()
                    .OfType<DelegateDeclarationSyntax>()
                    .Where(d => d.Identifier.Text == simpleName))
                .ToList()
            : typeCandidates.Cast<MemberDeclarationSyntax>().ToList();

        // Column without line is not a source position: substituting each
        // candidate's own start line would match every equally-aligned
        // same-name type and could silently pick the shortest. Keep
        // today's FirstOrDefault after the typeName filter
        // (TypeDeclarationSyntax only).
        if (column.HasValue && !line.HasValue)
            return typeCandidates.FirstOrDefault();

        if (column.HasValue)
        {
            // Do not require the declaration to start on `line` — a split
            // type's identifier may live on a continuation line whose
            // declaration span still covers that column. Prefer the
            // identifier hit, then the smallest containing type (nested
            // over outer). Include enum and delegate candidates so a
            // covering enum or delegate still reaches InvalidSymbolKind.
            // Do not silently pick the first when a covering node exists
            // elsewhere — scan every candidate. If nothing covers this
            // position, keep today's not-found (null) rather than
            // inventing a first-match.
            return lineCandidates
                .Where(t => TypeCoverage.TypeCoversColumn(t, line!.Value, column.Value))
                .OrderBy(t => TypeCoverage.IdentifierCoversColumn(t, line!.Value, column.Value) ? 0 : 1)
                .ThenBy(t => t.Span.Length)
                .FirstOrDefault();
        }

        if (!line.HasValue)
            return typeCandidates.FirstOrDefault();

        // Line set: include BaseTypeDeclarationSyntax (enum) and
        // DelegateDeclarationSyntax in the covering-line set. Do not
        // require the declaration to start on `line` — a split type's
        // identifier may live on a continuation line whose declaration
        // span still covers that line. Prefer the identifier hit, then
        // the smallest containing type (nested over outer). Include enum
        // and delegate candidates. Do not silently pick the first when a
        // covering node exists elsewhere — scan every candidate. If
        // nothing covers this line, keep today's TypeDeclarationSyntax
        // first-match rather than inventing a not-found (enums and
        // delegates stay out of that omitted-line fallback).
        if (lineCandidates.Count == 0)
            return null;

        return lineCandidates
            .Where(t => TypeCoverage.TypeCoversLine(t, line.Value))
            .OrderBy(t => TypeCoverage.IdentifierCoversLine(t, line.Value) ? 0 : 1)
            .ThenBy(t => t.Span.Length)
            .FirstOrDefault()
            ?? typeCandidates.FirstOrDefault();
    }
}
