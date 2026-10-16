using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Resolution;

/// <summary>
/// Shared field-declarator coverage helpers used when disambiguating
/// same-named fields by optional 1-based line / column (identifier preferred,
/// then declarator / containing field span via <see cref="SpanCoverage"/>).
/// Bodies live on <see cref="DeclaratorCoverage"/>; this class supplies the
/// containing <see cref="FieldDeclarationSyntax"/>.
/// </summary>
internal static class FieldCoverage
{
    /// <summary>
    /// True when the declarator's identifier, declarator span, or containing
    /// field covers <paramref name="line"/> (exclusive-end line
    /// rules via <see cref="SpanCoverage.SpanCoversLine(FileLinePositionSpan, int)"/>).
    /// </summary>
    internal static bool FieldCoversLine(VariableDeclaratorSyntax declarator, int line) =>
        DeclaratorCoverage.DeclaratorCoversLine(declarator, GetFieldDeclaration(declarator), line);

    /// <summary>
    /// True when the declarator's identifier covers <paramref name="line"/>.
    /// </summary>
    internal static bool IdentifierCoversLine(VariableDeclaratorSyntax declarator, int line) =>
        DeclaratorCoverage.IdentifierCoversLine(declarator, line);

    /// <summary>
    /// True when the declarator's identifier, declarator span, or containing
    /// field covers <paramref name="line"/> / <paramref name="column"/>
    /// (exclusive-end column rules via <see cref="SpanCoverage.SpanCoversColumn"/>).
    /// </summary>
    internal static bool FieldCoversColumn(VariableDeclaratorSyntax declarator, int line, int column) =>
        DeclaratorCoverage.DeclaratorCoversColumn(declarator, GetFieldDeclaration(declarator), line, column);

    /// <summary>
    /// True when the declarator's identifier covers
    /// <paramref name="line"/> / <paramref name="column"/>.
    /// </summary>
    internal static bool IdentifierCoversColumn(VariableDeclaratorSyntax declarator, int line, int column) =>
        DeclaratorCoverage.IdentifierCoversColumn(declarator, line, column);

    /// <summary>
    /// Smallest spanning length among the declarator and containing field that
    /// cover <paramref name="line"/>; <see cref="int.MaxValue"/> if none cover.
    /// </summary>
    internal static int SmallestCoveringSpanLength(VariableDeclaratorSyntax declarator, int line) =>
        DeclaratorCoverage.SmallestCoveringSpanLength(declarator, GetFieldDeclaration(declarator), line);

    /// <summary>
    /// Smallest spanning length among the declarator and containing field that
    /// cover <paramref name="line"/> / <paramref name="column"/>;
    /// <see cref="int.MaxValue"/> if none cover.
    /// </summary>
    internal static int SmallestCoveringSpanLength(VariableDeclaratorSyntax declarator, int line, int column) =>
        DeclaratorCoverage.SmallestCoveringSpanLength(declarator, GetFieldDeclaration(declarator), line, column);

    /// <summary>
    /// Containing <see cref="FieldDeclarationSyntax"/> for a field declarator, if any.
    /// </summary>
    internal static FieldDeclarationSyntax? GetFieldDeclaration(VariableDeclaratorSyntax declarator) =>
        declarator.Parent?.Parent as FieldDeclarationSyntax;
}
