using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared syntax line-position helpers used by convert operations when
/// selecting a target by 1-based start line, by extract operations when
/// mapping 1-based line/column selections to a <see cref="TextSpan"/>, and
/// by analyze_control_flow / analyze_data_flow when building an optional-column
/// region span.
/// </summary>
internal static class SyntaxLineHelpers
{
    /// <summary>
    /// 1-based start line of <paramref name="node"/> via
    /// <see cref="SyntaxNode.GetLocation"/>. Called directly by
    /// convert_expression_body / convert_property / inline_variable /
    /// add_null_checks (and via the typed forwarder on
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

    /// <summary>
    /// Builds an analysis region <see cref="TextSpan"/> from 1-based lines
    /// with optional columns. Same body as the identical private
    /// <c>BuildRegionSpan</c> copies on analyze_control_flow /
    /// analyze_data_flow. Omitted columns keep today's whole-line span
    /// (start of <paramref name="startLine"/> through <c>TextLine.End</c>
    /// of <paramref name="endLine"/>). Set <paramref name="startColumn"/>
    /// uses that 1-based column on the start line (Roslyn
    /// <c>Character = column - 1</c>). Set <paramref name="endColumn"/>
    /// uses that 1-based column on the end line. One omitted, the other
    /// set: omitted start stays start-of-line; omitted end stays
    /// end-of-line. Combined span must be start &lt;= end in absolute
    /// positions. Distinct from <see cref="GetSelectionSpan"/> (required
    /// columns + different error codes).
    /// </summary>
    internal static TextSpan BuildRegionSpan(
        SourceText text,
        int startLine,
        int endLine,
        int? startColumn,
        int? endColumn)
    {
        // Convert 1-based lines to 0-based
        var startLineIndex = startLine - 1;
        var endLineIndex = endLine - 1;

        if (startLineIndex >= text.Lines.Count || endLineIndex >= text.Lines.Count)
            throw new RefactoringException(ErrorCodes.InvalidRegion, "Line range exceeds file length.");

        var startLineInfo = text.Lines[startLineIndex];
        var endLineInfo = text.Lines[endLineIndex];

        // Omitted startColumn: today's start of startLine. Set: that
        // 1-based column on startLine (Character = column - 1). Do not
        // force column 1 when omitted. A column past TextLine.End would
        // leak into later lines — reject as InvalidColumnNumber (same
        // line-local bound as SymbolResolver / ExtractMethod).
        var startPosition = startLineInfo.Start;
        if (startColumn is int startCol)
            startPosition = GetColumnPosition(startLineInfo, startLine, startCol);

        // Omitted endColumn: today's TextLine.End of endLine (exclusive-ish
        // of the line break). Set: that 1-based column on endLine.
        var endPosition = endLineInfo.End;
        if (endColumn is int endCol)
            endPosition = GetColumnPosition(endLineInfo, endLine, endCol);

        if (startPosition < 0 || endPosition < 0 ||
            startPosition > text.Length || endPosition > text.Length ||
            startPosition > endPosition)
        {
            throw new RefactoringException(ErrorCodes.InvalidRegion, "Region start must be <= end.");
        }

        return TextSpan.FromBounds(startPosition, endPosition);
    }

    /// <summary>
    /// Converts a 1-based column on <paramref name="lineInfo"/> to an
    /// absolute position. Valid columns are 1 through
    /// <c>lineLength + 1</c> (the exclusive <see cref="TextLine.End"/>).
    /// Past that would cross the line break into later lines. Same body
    /// as the prior private copies on analyze_control_flow /
    /// analyze_data_flow.
    /// </summary>
    private static int GetColumnPosition(TextLine lineInfo, int lineNumber, int column)
    {
        var columnIndex = column - 1;
        var lineLength = lineInfo.End - lineInfo.Start;
        if (columnIndex < 0 || columnIndex > lineLength)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidColumnNumber,
                $"Column {column} is out of range for line {lineNumber} (line has {lineLength} characters).");
        }

        return lineInfo.Start + columnIndex;
    }
}
