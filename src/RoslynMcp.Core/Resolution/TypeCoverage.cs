using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Resolution;

/// <summary>
/// Shared type-declaration coverage helpers used when disambiguating
/// same-named types by optional 1-based line / column (identifier preferred,
/// then containing type span via <see cref="SpanCoverage"/>).
/// </summary>
internal static class TypeCoverage
{
    /// <summary>
    /// True when the type's identifier or full declaration span covers
    /// <paramref name="line"/> (exclusive-end line rules via
    /// <see cref="SpanCoverage.SpanCoversLine(FileLinePositionSpan, int)"/>).
    /// </summary>
    internal static bool TypeCoversLine(MemberDeclarationSyntax type, int line) =>
        IdentifierCoversLine(type, line) ||
        SpanCoverage.SpanCoversLine(type.GetLocation().GetLineSpan(), line);

    /// <summary>
    /// True when the type's declaration identifier covers <paramref name="line"/>.
    /// </summary>
    internal static bool IdentifierCoversLine(MemberDeclarationSyntax type, int line)
    {
        var identifier = GetTypeIdentifier(type);
        return identifier != default
            && SpanCoverage.SpanCoversLine(identifier.GetLocation().GetLineSpan(), line);
    }

    /// <summary>
    /// True when the type's identifier or full declaration span covers
    /// <paramref name="line"/> / <paramref name="column"/> (exclusive-end
    /// column rules via <see cref="SpanCoverage.SpanCoversColumn"/>).
    /// </summary>
    internal static bool TypeCoversColumn(MemberDeclarationSyntax type, int line, int column) =>
        IdentifierCoversColumn(type, line, column) ||
        SpanCoverage.SpanCoversColumn(type.GetLocation().GetLineSpan(), line, column);

    /// <summary>
    /// True when the type's declaration identifier covers
    /// <paramref name="line"/> / <paramref name="column"/>.
    /// </summary>
    internal static bool IdentifierCoversColumn(MemberDeclarationSyntax type, int line, int column)
    {
        var identifier = GetTypeIdentifier(type);
        return identifier != default
            && SpanCoverage.SpanCoversColumn(identifier.GetLocation().GetLineSpan(), line, column);
    }

    /// <summary>
    /// Declaration name token for a named type or delegate; otherwise default.
    /// </summary>
    internal static SyntaxToken GetTypeIdentifier(MemberDeclarationSyntax type) => type switch
    {
        BaseTypeDeclarationSyntax named => named.Identifier,
        DelegateDeclarationSyntax del => del.Identifier,
        _ => default
    };
}
