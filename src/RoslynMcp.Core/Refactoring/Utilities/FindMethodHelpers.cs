using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Resolution;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared method-declaration lookup used by Signature-family ops
/// (add/remove/reorder parameter + change return type). Includes
/// nullable <see cref="FindMethod"/> / <see cref="StartLine"/> and the
/// throwing <see cref="FindMethodDeclaration"/> gate.
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
    /// Finds a method declaration or throws. Requires <paramref name="line"/>
    /// when more than one method matches the name (even if column is set).
    /// When column is set, picks by identifier/declaration span coverage via
    /// <see cref="FindMethod"/> (continuation-line identifiers allowed).
    /// Omitted column keeps MethodName + optional Line start-line pick.
    /// </summary>
    /// <exception cref="RefactoringException">
    /// <see cref="ErrorCodes.MethodNotFound"/> or
    /// <see cref="ErrorCodes.SymbolAmbiguous"/>.
    /// </exception>
    internal static MethodDeclarationSyntax FindMethodDeclaration(
        SyntaxNode root,
        string methodName,
        int? line,
        int? column)
    {
        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList();

        if (methods.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                $"Method '{methodName}' not found.");
        }

        // Line is required when more than one method matches, even if
        // column is set. Column without Line is not a source position:
        // FindMethod would substitute each candidate's own start line and
        // could silently pick the shortest equally-aligned overload.
        // When both are set, pick by identifier/declaration span and do
        // not require the declaration to start on `line` (continuation-
        // line identifier).
        if (methods.Count > 1 && !line.HasValue)
        {
            var lines = methods
                .Select(StartLine)
                .ToList();
            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Multiple methods named '{methodName}' found. Provide line number. Options: {string.Join(", ", lines)}");
        }

        if (column.HasValue)
        {
            var covering = FindMethod(root, methodName, line, column);
            if (covering == null)
            {
                throw new RefactoringException(
                    ErrorCodes.MethodNotFound,
                    line.HasValue
                        ? $"Method '{methodName}' not found at line {line}."
                        : $"Method '{methodName}' not found.");
            }

            return covering;
        }

        // Omitted column keeps today's MethodName + optional Line start-line
        // pick exactly. Do not force column 1. Do not rewrite line-only to
        // covering-span. A single name match with no line is used as-is;
        // line filters declaration start-line; several start-line hits stay
        // SymbolAmbiguous (do not FirstOrDefault the first same-line overload).
        if (methods.Count == 1 && !line.HasValue)
            return methods[0];

        IEnumerable<MethodDeclarationSyntax> filtered = methods;
        if (line.HasValue)
        {
            filtered = filtered.Where(m => StartLine(m) == line.Value);
        }

        var matches = filtered.ToList();
        if (matches.Count == 1)
            return matches[0];

        if (matches.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MethodNotFound,
                line.HasValue
                    ? $"Method '{methodName}' not found at line {line}."
                    : $"Method '{methodName}' not found.");
        }

        var optionLines = matches
            .Select(StartLine)
            .ToList();
        throw new RefactoringException(
            ErrorCodes.SymbolAmbiguous,
            $"Multiple methods named '{methodName}' found. Provide line number. Options: {string.Join(", ", optionLines)}");
    }

    /// <summary>
    /// 1-based start line of the method declaration (exclusive of trivia
    /// that does not affect <see cref="SyntaxNode.GetLocation"/>).
    /// </summary>
    internal static int StartLine(MethodDeclarationSyntax method) =>
        method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
