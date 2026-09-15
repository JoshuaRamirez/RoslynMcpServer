using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Resolution;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared method-declaration lookup used by Signature-family ops
/// (add/remove/reorder parameter + change return type).
/// </summary>
internal static class FindMethodHelpers
{
    /// <summary>
    /// Finds a method. Omitted <paramref name="column"/> keeps today's
    /// MethodName + optional Line start-line pick (a single name match
    /// with no line is used as-is; line uses start-line equality; several
    /// start-line hits stay ambiguous at the caller). When set, picks the
    /// smallest method whose identifier or declaration span covers that
    /// 1-based column. Do not require the declaration to start on
    /// <paramref name="line"/> when column is set — a split signature may
    /// put the identifier on a continuation line.
    /// </summary>
    internal static MethodDeclarationSyntax? FindMethod(
        SyntaxNode root,
        string methodName,
        int? line,
        int? column)
    {
        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList();

        if (column.HasValue)
        {
            // When column is set, do not require the declaration to start
            // on `line` — a split signature's identifier may live on a
            // continuation line whose declaration span still covers that
            // column. Prefer the identifier hit, then the smallest
            // containing declaration. Do not silently pick the first when
            // a covering node exists elsewhere — scan every candidate,
            // including those that do not start on `line`. If nothing
            // covers this position, keep today's not-found (null).
            return methods
                .Where(m => MethodCoverage.MethodCoversColumn(m, line ?? StartLine(m), column.Value))
                .OrderBy(m => MethodCoverage.IdentifierCoversColumn(m, line ?? StartLine(m), column.Value) ? 0 : 1)
                .ThenBy(m => m.Span.Length)
                .FirstOrDefault();
        }

        if (methods.Count == 1 && !line.HasValue)
            return methods[0];

        if (!line.HasValue)
            return methods.Count == 1 ? methods[0] : null;

        var startLineMatches = methods.Where(m => StartLine(m) == line.Value).ToList();
        return startLineMatches.Count == 1 ? startLineMatches[0] : null;
    }

    /// <summary>
    /// 1-based start line of the method declaration (exclusive of trivia
    /// that does not affect <see cref="SyntaxNode.GetLocation"/>).
    /// </summary>
    internal static int StartLine(MethodDeclarationSyntax method) =>
        method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
