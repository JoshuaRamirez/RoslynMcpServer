using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Resolution;

/// <summary>
/// Shared variable-declarator coverage helpers used when disambiguating
/// same-named fields / locals by optional 1-based line / column (identifier
/// preferred, then declarator / containing declaration span via
/// <see cref="SpanCoverage"/>). The caller supplies the already-resolved
/// containing declaration (<see cref="FieldDeclarationSyntax"/> for
/// <see cref="FieldCoverage"/>, <see cref="LocalDeclarationStatementSyntax"/>
/// for <see cref="LocalCoverage"/>), or <see langword="null"/> when there is none.
/// </summary>
internal static class DeclaratorCoverage
{
    /// <summary>
    /// True when the declarator's identifier, declarator span, or
    /// <paramref name="containingDeclaration"/> covers <paramref name="line"/>
    /// (exclusive-end line rules via <see cref="SpanCoverage.SpanCoversLine(FileLinePositionSpan, int)"/>).
    /// </summary>
    internal static bool DeclaratorCoversLine(
        VariableDeclaratorSyntax declarator,
        SyntaxNode? containingDeclaration,
        int line) =>
        IdentifierCoversLine(declarator, line) ||
        SpanCoverage.SpanCoversLine(declarator.GetLocation().GetLineSpan(), line) ||
        (containingDeclaration != null &&
         SpanCoverage.SpanCoversLine(containingDeclaration.GetLocation().GetLineSpan(), line));

    /// <summary>
    /// True when the declarator's identifier covers <paramref name="line"/>.
    /// </summary>
    internal static bool IdentifierCoversLine(VariableDeclaratorSyntax declarator, int line) =>
        SpanCoverage.SpanCoversLine(declarator.Identifier.GetLocation().GetLineSpan(), line);

    /// <summary>
    /// True when the declarator's identifier, declarator span, or
    /// <paramref name="containingDeclaration"/> covers <paramref name="line"/> /
    /// <paramref name="column"/> (exclusive-end column rules via
    /// <see cref="SpanCoverage.SpanCoversColumn"/>).
    /// </summary>
    internal static bool DeclaratorCoversColumn(
        VariableDeclaratorSyntax declarator,
        SyntaxNode? containingDeclaration,
        int line,
        int column) =>
        IdentifierCoversColumn(declarator, line, column) ||
        SpanCoverage.SpanCoversColumn(declarator.GetLocation().GetLineSpan(), line, column) ||
        (containingDeclaration != null &&
         SpanCoverage.SpanCoversColumn(containingDeclaration.GetLocation().GetLineSpan(), line, column));

    /// <summary>
    /// True when the declarator's identifier covers
    /// <paramref name="line"/> / <paramref name="column"/>.
    /// </summary>
    internal static bool IdentifierCoversColumn(VariableDeclaratorSyntax declarator, int line, int column) =>
        SpanCoverage.SpanCoversColumn(declarator.Identifier.GetLocation().GetLineSpan(), line, column);

    /// <summary>
    /// Smallest spanning length among the declarator and
    /// <paramref name="containingDeclaration"/> that cover <paramref name="line"/>;
    /// <see cref="int.MaxValue"/> if none cover.
    /// </summary>
    internal static int SmallestCoveringSpanLength(
        VariableDeclaratorSyntax declarator,
        SyntaxNode? containingDeclaration,
        int line)
    {
        var smallest = int.MaxValue;
        if (SpanCoverage.SpanCoversLine(declarator.GetLocation().GetLineSpan(), line))
            smallest = Math.Min(smallest, declarator.Span.Length);

        if (containingDeclaration != null &&
            SpanCoverage.SpanCoversLine(containingDeclaration.GetLocation().GetLineSpan(), line))
        {
            smallest = Math.Min(smallest, containingDeclaration.Span.Length);
        }

        return smallest;
    }

    /// <summary>
    /// Smallest spanning length among the declarator and
    /// <paramref name="containingDeclaration"/> that cover <paramref name="line"/> /
    /// <paramref name="column"/>; <see cref="int.MaxValue"/> if none cover.
    /// </summary>
    internal static int SmallestCoveringSpanLength(
        VariableDeclaratorSyntax declarator,
        SyntaxNode? containingDeclaration,
        int line,
        int column)
    {
        var smallest = int.MaxValue;
        if (SpanCoverage.SpanCoversColumn(declarator.GetLocation().GetLineSpan(), line, column))
            smallest = Math.Min(smallest, declarator.Span.Length);

        if (containingDeclaration != null &&
            SpanCoverage.SpanCoversColumn(containingDeclaration.GetLocation().GetLineSpan(), line, column))
        {
            smallest = Math.Min(smallest, containingDeclaration.Span.Length);
        }

        return smallest;
    }
}
