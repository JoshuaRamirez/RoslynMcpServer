using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Resolution;

/// <summary>
/// Shared method-declaration coverage helpers used when disambiguating
/// same-named methods by optional 1-based line / column (identifier preferred,
/// then containing method span via <see cref="SpanCoverage"/>).
/// </summary>
internal static class MethodCoverage
{
    /// <summary>
    /// True when the method's identifier or full declaration span covers
    /// <paramref name="line"/> / <paramref name="column"/> (exclusive-end
    /// column rules via <see cref="SpanCoverage.SpanCoversColumn"/>).
    /// </summary>
    internal static bool MethodCoversColumn(MethodDeclarationSyntax method, int line, int column) =>
        IdentifierCoversColumn(method, line, column) ||
        SpanCoverage.SpanCoversColumn(method.GetLocation().GetLineSpan(), line, column);

    /// <summary>
    /// True when the method's declaration identifier covers
    /// <paramref name="line"/> / <paramref name="column"/>.
    /// </summary>
    internal static bool IdentifierCoversColumn(MethodDeclarationSyntax method, int line, int column) =>
        SpanCoverage.SpanCoversColumn(method.Identifier.GetLocation().GetLineSpan(), line, column);
}
