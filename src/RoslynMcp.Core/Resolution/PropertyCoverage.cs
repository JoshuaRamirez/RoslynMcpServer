using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Resolution;

/// <summary>
/// Shared property-declaration coverage helpers used when disambiguating
/// same-named properties by optional 1-based line / column (identifier preferred,
/// then containing property span via <see cref="SpanCoverage"/>).
/// </summary>
internal static class PropertyCoverage
{
    /// <summary>
    /// True when the property's identifier or full declaration span covers
    /// <paramref name="line"/> / <paramref name="column"/> (exclusive-end
    /// column rules via <see cref="SpanCoverage.SpanCoversColumn"/>).
    /// </summary>
    internal static bool PropertyCoversColumn(PropertyDeclarationSyntax property, int line, int column) =>
        IdentifierCoversColumn(property, line, column) ||
        SpanCoverage.SpanCoversColumn(property.GetLocation().GetLineSpan(), line, column);

    /// <summary>
    /// True when the property's declaration identifier covers
    /// <paramref name="line"/> / <paramref name="column"/>.
    /// </summary>
    internal static bool IdentifierCoversColumn(PropertyDeclarationSyntax property, int line, int column) =>
        SpanCoverage.SpanCoversColumn(property.Identifier.GetLocation().GetLineSpan(), line, column);
}
