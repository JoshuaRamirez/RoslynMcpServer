using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Resolution;

/// <summary>
/// Shared member-declaration coverage helpers used when disambiguating
/// members by optional 1-based line / column (identifier preferred,
/// then containing member span via <see cref="SpanCoverage"/>).
/// </summary>
internal static class MemberCoverage
{
    /// <summary>
    /// True when the member's identifier token or full declaration span covers
    /// <paramref name="line"/> / <paramref name="column"/> (exclusive-end
    /// column rules via <see cref="SpanCoverage.SpanCoversColumn"/>).
    /// </summary>
    internal static bool MemberCoversColumn(SyntaxNode member, int line, int column) =>
        IdentifierCoversColumn(member, line, column) ||
        SpanCoverage.SpanCoversColumn(member.GetLocation().GetLineSpan(), line, column);

    /// <summary>
    /// True when the member's declaration identifier (or equivalent name token)
    /// covers <paramref name="line"/> / <paramref name="column"/>.
    /// </summary>
    internal static bool IdentifierCoversColumn(SyntaxNode member, int line, int column)
    {
        var token = GetMemberIdentifier(member);
        return token != default &&
               SpanCoverage.SpanCoversColumn(token.GetLocation().GetLineSpan(), line, column);
    }

    /// <summary>
    /// Name token used for column disambiguation: method/property/event/type
    /// identifier, indexer <c>this</c>, operator token, conversion return-type
    /// first token, constructor/destructor/local-function identifier, or first
    /// field/event-field variable identifier; otherwise default.
    /// </summary>
    internal static SyntaxToken GetMemberIdentifier(SyntaxNode member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier,
        PropertyDeclarationSyntax property => property.Identifier,
        IndexerDeclarationSyntax indexer => indexer.ThisKeyword,
        OperatorDeclarationSyntax op => op.OperatorToken,
        ConversionOperatorDeclarationSyntax conversion => conversion.Type.GetFirstToken(),
        ConstructorDeclarationSyntax constructor => constructor.Identifier,
        DestructorDeclarationSyntax destructor => destructor.Identifier,
        LocalFunctionStatementSyntax localFunction => localFunction.Identifier,
        EventDeclarationSyntax @event => @event.Identifier,
        FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier ?? default,
        EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables.FirstOrDefault()?.Identifier ?? default,
        TypeDeclarationSyntax type => type.Identifier,
        _ => default
    };
}
