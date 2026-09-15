using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Resolution;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// A control statement (or else clause) that can receive or lose braces.
/// </summary>
/// <param name="Owner">The if/for/foreach/while/using statement, or the else clause, that owns the body.</param>
/// <param name="Body">The embedded statement that may lack or hold braces.</param>
/// <param name="Keyword">The keyword the user points at (if, else, for, foreach, while, using).</param>
internal readonly record struct ControlTarget(SyntaxNode Owner, StatementSyntax Body, SyntaxToken Keyword);

/// <summary>
/// Shared control-target selection used by add_braces / remove_braces.
/// Same body as the prior private FindControlTarget copies; CollectTargets
/// stays op-local (else Owner differs).
/// </summary>
internal static class ControlTargetHelpers
{
    /// <summary>
    /// Picks the control target on <paramref name="line"/> from pre-collected
    /// <paramref name="targets"/>. With a column, prefers the shortest keyword
    /// span that covers it (exclusive end); without a column, the earliest
    /// <see cref="SyntaxToken.SpanStart"/>. Returns null when none match.
    /// </summary>
    internal static ControlTarget? FindControlTarget(IEnumerable<ControlTarget> targets, int line, int? column)
    {
        var onLine = targets
            .Where(target => KeywordCoverage.KeywordIsOnLine(target.Keyword, line))
            .ToList();

        if (onLine.Count == 0)
            return null;

        if (column.HasValue)
        {
            var atColumn = onLine
                .Where(target => KeywordCoverage.KeywordCoversColumn(target.Keyword, line, column.Value))
                .OrderBy(target => target.Keyword.Span.Length)
                .ToList();
            return atColumn.Count == 0 ? null : atColumn[0];
        }

        return onLine.OrderBy(target => target.Keyword.SpanStart).First();
    }
}
