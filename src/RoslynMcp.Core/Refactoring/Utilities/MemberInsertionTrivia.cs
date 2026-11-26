using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Layout helpers for a member or statement inserted into existing code: the file's own line ending
/// and the indentation of its new siblings, instead of a hard-coded CRLF at column 0 (#3123).
/// </summary>
internal static class MemberInsertionTrivia
{
    /// <summary>
    /// The first end-of-line trivia in <paramref name="node"/> (LF or CRLF); CRLF when it has none.
    /// </summary>
    public static SyntaxTrivia DetectEndOfLine(SyntaxNode node)
    {
        foreach (var trivia in node.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                return SyntaxFactory.EndOfLine(trivia.ToString());
        }

        return SyntaxFactory.CarriageReturnLineFeed;
    }

    /// <summary>
    /// Inserts <paramref name="member"/> (a node without line breaks of its own, or one laid out by
    /// <see cref="Reindent"/>) at <paramref name="index"/> of <paramref name="members"/>, on its own line
    /// with the members' indentation and the file's line ending.
    /// </summary>
    public static TMember WithInsertionTrivia<TMember>(
        TMember member,
        TypeDeclarationSyntax typeDeclaration,
        IReadOnlyList<MemberDeclarationSyntax> members,
        int index)
        where TMember : MemberDeclarationSyntax
    {
        var endOfLine = DetectEndOfLine(typeDeclaration);
        var indentation = MemberIndentation(typeDeclaration, members);
        var previous = index > 0 ? members[index - 1].GetLastToken() : typeDeclaration.OpenBraceToken;

        var leading = new List<SyntaxTrivia>();
        if (!EndsWithEndOfLine(previous.TrailingTrivia))
            leading.Add(endOfLine);
        if (indentation.Length > 0)
            leading.Add(SyntaxFactory.Whitespace(indentation));

        return member.WithLeadingTrivia(leading).WithTrailingTrivia(endOfLine);
    }

    /// <summary>
    /// The leading whitespace of the type's first member that starts its own line, else the close
    /// brace's indentation plus one indent unit (a tab when the brace is tab-indented, else 4 spaces).
    /// </summary>
    public static string MemberIndentation(TypeDeclarationSyntax typeDeclaration, IReadOnlyList<MemberDeclarationSyntax> members)
    {
        foreach (var member in members)
        {
            if (TryGetLineIndentation(member.GetFirstToken(), out var indentation))
                return indentation;
        }

        if (!TryGetLineIndentation(typeDeclaration.CloseBraceToken, out var braceIndentation))
            TryGetLineIndentation(typeDeclaration.GetFirstToken(), out braceIndentation);
        return braceIndentation + (braceIndentation.Contains('\t') ? "\t" : "    ");
    }

    /// <summary>
    /// Re-lays out a multi-line member built with <c>NormalizeWhitespace</c>
    /// so every line carries <paramref name="indentation"/> and ends with <paramref name="endOfLine"/>.
    /// </summary>
    public static MemberDeclarationSyntax Reindent(MemberDeclarationSyntax member, string indentation, SyntaxTrivia endOfLine)
    {
        var eol = endOfLine.ToString();
        var unit = indentation.Contains('\t') ? "\t" : "    ";
        var lines = member.NormalizeWhitespace(unit, eol).ToFullString().Split(eol);
        var text = string.Join(eol, lines.Select((line, i) => i == 0 || line.Length == 0 ? line : indentation + line));
        return SyntaxFactory.ParseMemberDeclaration(text)!;
    }

    private static bool EndsWithEndOfLine(SyntaxTriviaList trivia) =>
        trivia.Count > 0 && trivia[^1].IsKind(SyntaxKind.EndOfLineTrivia);

    /// <summary>
    /// The whitespace between the previous line break and <paramref name="token"/>, when the token
    /// is the first thing on its line.
    /// </summary>
    private static bool TryGetLineIndentation(SyntaxToken token, out string indentation)
    {
        indentation = string.Empty;
        if (token.IsKind(SyntaxKind.None))
            return false;

        var leading = token.LeadingTrivia;
        var i = leading.Count - 1;
        if (i >= 0 && leading[i].IsKind(SyntaxKind.WhitespaceTrivia))
        {
            indentation = leading[i].ToString();
            i--;
        }

        if (i >= 0)
            return leading[i].IsKind(SyntaxKind.EndOfLineTrivia) || leading[i].IsDirective || leading[i].IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia);

        var previous = token.GetPreviousToken();
        return previous.IsKind(SyntaxKind.None) || EndsWithEndOfLine(previous.TrailingTrivia);
    }
}
