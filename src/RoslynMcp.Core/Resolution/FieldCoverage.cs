using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Resolution;

/// <summary>
/// Shared field-declarator coverage helpers used when disambiguating
/// same-named fields by optional 1-based line / column (identifier preferred,
/// then declarator / containing field span via <see cref="SpanCoverage"/>).
/// </summary>
internal static class FieldCoverage
{
    /// <summary>
    /// True when the declarator's identifier, declarator span, or containing
    /// field declaration covers <paramref name="line"/> (exclusive-end line
    /// rules via <see cref="SpanCoverage.SpanCoversLine(FileLinePositionSpan, int)"/>).
    /// </summary>
    internal static bool FieldCoversLine(VariableDeclaratorSyntax declarator, int line) =>
        IdentifierCoversLine(declarator, line) ||
        SpanCoverage.SpanCoversLine(declarator.GetLocation().GetLineSpan(), line) ||
        (GetFieldDeclaration(declarator) is { } field &&
         SpanCoverage.SpanCoversLine(field.GetLocation().GetLineSpan(), line));

    /// <summary>
    /// True when the declarator's identifier covers <paramref name="line"/>.
    /// </summary>
    internal static bool IdentifierCoversLine(VariableDeclaratorSyntax declarator, int line) =>
        SpanCoverage.SpanCoversLine(declarator.Identifier.GetLocation().GetLineSpan(), line);

    /// <summary>
    /// True when the declarator's identifier, declarator span, or containing
    /// field declaration covers <paramref name="line"/> / <paramref name="column"/>
    /// (exclusive-end column rules via <see cref="SpanCoverage.SpanCoversColumn"/>).
    /// </summary>
    internal static bool FieldCoversColumn(VariableDeclaratorSyntax declarator, int line, int column) =>
        IdentifierCoversColumn(declarator, line, column) ||
        SpanCoverage.SpanCoversColumn(declarator.GetLocation().GetLineSpan(), line, column) ||
        (GetFieldDeclaration(declarator) is { } field &&
         SpanCoverage.SpanCoversColumn(field.GetLocation().GetLineSpan(), line, column));

    /// <summary>
    /// True when the declarator's identifier covers
    /// <paramref name="line"/> / <paramref name="column"/>.
    /// </summary>
    internal static bool IdentifierCoversColumn(VariableDeclaratorSyntax declarator, int line, int column) =>
        SpanCoverage.SpanCoversColumn(declarator.Identifier.GetLocation().GetLineSpan(), line, column);

    /// <summary>
    /// Smallest spanning length among the declarator and containing field that
    /// cover <paramref name="line"/>; <see cref="int.MaxValue"/> if none cover.
    /// </summary>
    internal static int SmallestCoveringSpanLength(VariableDeclaratorSyntax declarator, int line)
    {
        var smallest = int.MaxValue;
        if (SpanCoverage.SpanCoversLine(declarator.GetLocation().GetLineSpan(), line))
            smallest = Math.Min(smallest, declarator.Span.Length);

        if (GetFieldDeclaration(declarator) is { } field &&
            SpanCoverage.SpanCoversLine(field.GetLocation().GetLineSpan(), line))
        {
            smallest = Math.Min(smallest, field.Span.Length);
        }

        return smallest;
    }

    /// <summary>
    /// Smallest spanning length among the declarator and containing field that
    /// cover <paramref name="line"/> / <paramref name="column"/>;
    /// <see cref="int.MaxValue"/> if none cover.
    /// </summary>
    internal static int SmallestCoveringSpanLength(VariableDeclaratorSyntax declarator, int line, int column)
    {
        var smallest = int.MaxValue;
        if (SpanCoverage.SpanCoversColumn(declarator.GetLocation().GetLineSpan(), line, column))
            smallest = Math.Min(smallest, declarator.Span.Length);

        if (GetFieldDeclaration(declarator) is { } field &&
            SpanCoverage.SpanCoversColumn(field.GetLocation().GetLineSpan(), line, column))
        {
            smallest = Math.Min(smallest, field.Span.Length);
        }

        return smallest;
    }

    /// <summary>
    /// Containing <see cref="FieldDeclarationSyntax"/> for a field declarator, if any.
    /// </summary>
    internal static FieldDeclarationSyntax? GetFieldDeclaration(VariableDeclaratorSyntax declarator) =>
        declarator.Parent?.Parent as FieldDeclarationSyntax;
}
