using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Shared declaration-identifier helpers used by make_static / make_non_static
/// when matching a text span to a declaration name token (method, local
/// function, constructor, destructor, operator, conversion, property, event,
/// variable, type, or parameter).
/// </summary>
internal static class DeclarationIdentifiers
{
    /// <summary>
    /// True when the node's declaration identifier overlaps
    /// <paramref name="span"/> (either direction of
    /// <see cref="TextSpan.OverlapsWith(TextSpan)"/>).
    /// </summary>
    internal static bool IdentifierOverlaps(SyntaxNode node, TextSpan span)
    {
        var identifier = GetDeclarationIdentifier(node);
        return identifier != null &&
            (identifier.Value.Span.OverlapsWith(span) || span.OverlapsWith(identifier.Value.Span));
    }

    /// <summary>
    /// Declaration name token for supported declaration shapes; otherwise
    /// <see langword="null"/>. Conversion operators use the last token of
    /// the return type (same as the MakeStatic / MakeNonStatic copies).
    /// </summary>
    internal static SyntaxToken? GetDeclarationIdentifier(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => method.Identifier,
        LocalFunctionStatementSyntax localFunction => localFunction.Identifier,
        ConstructorDeclarationSyntax constructor => constructor.Identifier,
        DestructorDeclarationSyntax destructor => destructor.Identifier,
        OperatorDeclarationSyntax @operator => @operator.OperatorToken,
        ConversionOperatorDeclarationSyntax conversion => conversion.Type.GetLastToken(),
        PropertyDeclarationSyntax property => property.Identifier,
        EventDeclarationSyntax @event => @event.Identifier,
        VariableDeclaratorSyntax variable => variable.Identifier,
        TypeDeclarationSyntax type => type.Identifier,
        ParameterSyntax parameter => parameter.Identifier,
        _ => null
    };
}
