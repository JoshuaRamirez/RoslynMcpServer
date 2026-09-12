using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Resolution;

/// <summary>
/// Shared local-declarator coverage helpers used when disambiguating
/// same-named locals by optional 1-based line / column (identifier preferred,
/// then declarator / containing local declaration span via <see cref="SpanCoverage"/>).
/// </summary>
internal static class LocalCoverage
{
    /// <summary>
    /// True when the declarator's identifier, declarator span, or containing
    /// local declaration covers <paramref name="line"/> (exclusive-end line
    /// rules via <see cref="SpanCoverage.SpanCoversLine(FileLinePositionSpan, int)"/>).
    /// </summary>
    internal static bool LocalCoversLine(VariableDeclaratorSyntax declarator, int line) =>
        IdentifierCoversLine(declarator, line) ||
        SpanCoverage.SpanCoversLine(declarator.GetLocation().GetLineSpan(), line) ||
        (GetLocalDeclaration(declarator) is { } local &&
         SpanCoverage.SpanCoversLine(local.GetLocation().GetLineSpan(), line));

    /// <summary>
    /// True when the declarator's identifier covers <paramref name="line"/>.
    /// </summary>
    internal static bool IdentifierCoversLine(VariableDeclaratorSyntax declarator, int line) =>
        SpanCoverage.SpanCoversLine(declarator.Identifier.GetLocation().GetLineSpan(), line);

    /// <summary>
    /// True when the declarator's identifier, declarator span, or containing
    /// local declaration covers <paramref name="line"/> / <paramref name="column"/>
    /// (exclusive-end column rules via <see cref="SpanCoverage.SpanCoversColumn"/>).
    /// </summary>
    internal static bool LocalCoversColumn(VariableDeclaratorSyntax declarator, int line, int column) =>
        IdentifierCoversColumn(declarator, line, column) ||
        SpanCoverage.SpanCoversColumn(declarator.GetLocation().GetLineSpan(), line, column) ||
        (GetLocalDeclaration(declarator) is { } local &&
         SpanCoverage.SpanCoversColumn(local.GetLocation().GetLineSpan(), line, column));

    /// <summary>
    /// True when the declarator's identifier covers
    /// <paramref name="line"/> / <paramref name="column"/>.
    /// </summary>
    internal static bool IdentifierCoversColumn(VariableDeclaratorSyntax declarator, int line, int column) =>
        SpanCoverage.SpanCoversColumn(declarator.Identifier.GetLocation().GetLineSpan(), line, column);

    /// <summary>
    /// Smallest spanning length among the declarator and containing local declaration that
    /// cover <paramref name="line"/>; <see cref="int.MaxValue"/> if none cover.
    /// </summary>
    internal static int SmallestCoveringSpanLength(VariableDeclaratorSyntax declarator, int line)
    {
        var smallest = int.MaxValue;
        if (SpanCoverage.SpanCoversLine(declarator.GetLocation().GetLineSpan(), line))
            smallest = Math.Min(smallest, declarator.Span.Length);

        if (GetLocalDeclaration(declarator) is { } local &&
            SpanCoverage.SpanCoversLine(local.GetLocation().GetLineSpan(), line))
        {
            smallest = Math.Min(smallest, local.Span.Length);
        }

        return smallest;
    }

    /// <summary>
    /// Smallest spanning length among the declarator and containing local declaration that
    /// cover <paramref name="line"/> / <paramref name="column"/>;
    /// <see cref="int.MaxValue"/> if none cover.
    /// </summary>
    internal static int SmallestCoveringSpanLength(VariableDeclaratorSyntax declarator, int line, int column)
    {
        var smallest = int.MaxValue;
        if (SpanCoverage.SpanCoversColumn(declarator.GetLocation().GetLineSpan(), line, column))
            smallest = Math.Min(smallest, declarator.Span.Length);

        if (GetLocalDeclaration(declarator) is { } local &&
            SpanCoverage.SpanCoversColumn(local.GetLocation().GetLineSpan(), line, column))
        {
            smallest = Math.Min(smallest, local.Span.Length);
        }

        return smallest;
    }

    /// <summary>
    /// Containing <see cref="LocalDeclarationStatementSyntax"/> for a local declarator, if any.
    /// </summary>
    internal static LocalDeclarationStatementSyntax? GetLocalDeclaration(VariableDeclaratorSyntax declarator) =>
        declarator.Parent?.Parent as LocalDeclarationStatementSyntax;
}
