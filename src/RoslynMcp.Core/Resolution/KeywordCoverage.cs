using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Resolution;

/// <summary>
/// Shared keyword-token coverage helpers used when disambiguating
/// control-statement keywords by optional 1-based line / column via
/// <see cref="SpanCoverage"/>.
/// </summary>
internal static class KeywordCoverage
{
    /// <summary>
    /// True when <paramref name="keyword"/> starts on 1-based <paramref name="line"/>.
    /// </summary>
    internal static bool KeywordIsOnLine(SyntaxToken keyword, int line)
    {
        var span = keyword.GetLocation().GetLineSpan();
        return span.StartLinePosition.Line + 1 == line;
    }

    /// <summary>
    /// True when <paramref name="keyword"/>'s location covers
    /// <paramref name="line"/> / <paramref name="column"/> (exclusive-end
    /// column rules via <see cref="SpanCoverage.SpanCoversColumn"/>).
    /// </summary>
    internal static bool KeywordCoversColumn(SyntaxToken keyword, int line, int column) =>
        SpanCoverage.SpanCoversColumn(keyword.GetLocation().GetLineSpan(), line, column);
}
