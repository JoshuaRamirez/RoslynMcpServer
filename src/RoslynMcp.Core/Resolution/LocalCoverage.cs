using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Resolution;

/// <summary>
/// Shared local-declarator coverage helpers used when disambiguating
/// same-named locals by optional 1-based line / column (identifier preferred,
/// then declarator / containing local declaration span via <see cref="SpanCoverage"/>).
/// Bodies live on <see cref="DeclaratorCoverage"/>; this class supplies the
/// containing <see cref="LocalDeclarationStatementSyntax"/>.
/// </summary>
internal static class LocalCoverage
{
    /// <summary>
    /// True when the declarator's identifier, declarator span, or containing
    /// local declaration covers <paramref name="line"/> (exclusive-end line
    /// rules via <see cref="SpanCoverage.SpanCoversLine(FileLinePositionSpan, int)"/>).
    /// </summary>
    internal static bool LocalCoversLine(VariableDeclaratorSyntax declarator, int line) =>
        DeclaratorCoverage.DeclaratorCoversLine(declarator, GetLocalDeclaration(declarator), line);

    /// <summary>
    /// True when the declarator's identifier covers <paramref name="line"/>.
    /// </summary>
    internal static bool IdentifierCoversLine(VariableDeclaratorSyntax declarator, int line) =>
        DeclaratorCoverage.IdentifierCoversLine(declarator, line);

    /// <summary>
    /// True when the declarator's identifier, declarator span, or containing
    /// local declaration covers <paramref name="line"/> / <paramref name="column"/>
    /// (exclusive-end column rules via <see cref="SpanCoverage.SpanCoversColumn"/>).
    /// </summary>
    internal static bool LocalCoversColumn(VariableDeclaratorSyntax declarator, int line, int column) =>
        DeclaratorCoverage.DeclaratorCoversColumn(declarator, GetLocalDeclaration(declarator), line, column);

    /// <summary>
    /// True when the declarator's identifier covers
    /// <paramref name="line"/> / <paramref name="column"/>.
    /// </summary>
    internal static bool IdentifierCoversColumn(VariableDeclaratorSyntax declarator, int line, int column) =>
        DeclaratorCoverage.IdentifierCoversColumn(declarator, line, column);

    /// <summary>
    /// Smallest spanning length among the declarator and containing local declaration that
    /// cover <paramref name="line"/>; <see cref="int.MaxValue"/> if none cover.
    /// </summary>
    internal static int SmallestCoveringSpanLength(VariableDeclaratorSyntax declarator, int line) =>
        DeclaratorCoverage.SmallestCoveringSpanLength(declarator, GetLocalDeclaration(declarator), line);

    /// <summary>
    /// Smallest spanning length among the declarator and containing local declaration that
    /// cover <paramref name="line"/> / <paramref name="column"/>;
    /// <see cref="int.MaxValue"/> if none cover.
    /// </summary>
    internal static int SmallestCoveringSpanLength(VariableDeclaratorSyntax declarator, int line, int column) =>
        DeclaratorCoverage.SmallestCoveringSpanLength(declarator, GetLocalDeclaration(declarator), line, column);

    /// <summary>
    /// Containing <see cref="LocalDeclarationStatementSyntax"/> for a local declarator, if any.
    /// </summary>
    internal static LocalDeclarationStatementSyntax? GetLocalDeclaration(VariableDeclaratorSyntax declarator) =>
        declarator.Parent?.Parent as LocalDeclarationStatementSyntax;
}
