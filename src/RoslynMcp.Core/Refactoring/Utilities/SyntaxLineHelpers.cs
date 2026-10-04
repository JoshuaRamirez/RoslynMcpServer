using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared syntax line-position helpers used by convert operations when
/// selecting a target by 1-based start line, and by extract operations when
/// mapping 1-based line/column selections to a <see cref="TextSpan"/>.
/// </summary>
internal static class SyntaxLineHelpers
{
    /// <summary>
    /// 1-based start line of <paramref name="node"/> via
    /// <see cref="SyntaxNode.GetLocation"/>. Same body as the identical
    /// private copies on convert_to_async / convert_expression_body /
    /// convert_property / change_signature / inline_variable /
    /// add_null_checks (and the typed forwarder on
    /// <see cref="FindMethodHelpers.StartLine"/>).
    /// </summary>
    internal static int StartLine(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>
    /// True when <paramref name="node"/>'s start line (1-based) equals
    /// <paramref name="line"/>. Same body as the two private copies on
    /// convert_to_interpolated_string / convert_to_pattern_matching.
    /// </summary>
    internal static bool StartsOnLine(SyntaxNode node, int line) =>
        StartLine(node) == line;

    /// <summary>
    /// Maps a 1-based inclusive start / exclusive-end style line+column
    /// selection onto a <see cref="TextSpan"/> in <paramref name="sourceText"/>.
    /// Same body as the identical MakeStatic / MakeNonStatic / SafeDelete /
    /// IntroduceField private copies (line out of range →
    /// <see cref="ErrorCodes.InvalidLineNumber"/>; column out of range →
    /// <see cref="ErrorCodes.InvalidColumnNumber"/>; end before start →
    /// <see cref="ErrorCodes.InvalidSelectionRange"/>).
    /// </summary>
    internal static TextSpan GetSelectionSpan(
        SourceText sourceText,
        int startLineNumber,
        int startColumn,
        int endLineNumber,
        int endColumn)
    {
        if (startLineNumber > sourceText.Lines.Count || endLineNumber > sourceText.Lines.Count)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Selection is outside the file.");

        var startLine = sourceText.Lines[startLineNumber - 1];
        var endLine = sourceText.Lines[endLineNumber - 1];
        if (startColumn - 1 > startLine.Span.Length || endColumn - 1 > endLine.SpanIncludingLineBreak.Length)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Selection column is outside the line.");

        var startPosition = startLine.Start + startColumn - 1;
        var endPosition = endLine.Start + endColumn - 1;
        if (endPosition < startPosition)
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "End must be after start.");

        return TextSpan.FromBounds(startPosition, endPosition);
    }
}
