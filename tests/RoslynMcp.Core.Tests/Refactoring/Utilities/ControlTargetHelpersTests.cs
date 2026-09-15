using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="ControlTargetHelpers.FindControlTarget"/> —
/// selection behavior previously covered on AddBraces/RemoveBraces private copies.
/// Feeds targets via <see cref="AddBracesOperation.CollectTargets"/>.
/// </summary>
public class ControlTargetHelpersTests
{
    private const string SameLineIfsSource = """
        namespace TestApp;

        public class SameLine
        {
            public string Pick(bool a, bool b)
            {
                if (a) if (b) return "both"; else return "a-only"; else return "none";
            }
        }
        """;

    private const string IndentedIfSource = """
        class C
        {
            void M(bool flag)
            {
                if (flag) return;
            }
        }
        """;

    [Fact]
    public void FindControlTarget_OmittedColumn_PicksFirstKeywordBySpanStart()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineIfsSource).GetRoot();
        var line = FindLine(SameLineIfsSource, "if (a) if (b)");

        var found = ControlTargetHelpers.FindControlTarget(
            AddBracesOperation.CollectTargets(root), line, column: null);

        Assert.NotNull(found);
        Assert.Equal("if", found.Value.Keyword.ValueText);
        var ownerIf = Assert.IsType<IfStatementSyntax>(found.Value.Owner);
        Assert.Equal("a", ownerIf.Condition.ToString());
    }

    [Fact]
    public void FindControlTarget_OmittedColumn_IndentedKeyword_DoesNotForceColumn1()
    {
        var root = CSharpSyntaxTree.ParseText(IndentedIfSource).GetRoot();
        var line = FindLine(IndentedIfSource, "if (flag)");
        var ifStmt = root.DescendantNodes().OfType<IfStatementSyntax>().Single();
        var startCol = ifStmt.IfKeyword.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
        Assert.True(startCol > 1);

        var found = ControlTargetHelpers.FindControlTarget(
            AddBracesOperation.CollectTargets(root), line, column: null);

        Assert.NotNull(found);
        var ownerIf = Assert.IsType<IfStatementSyntax>(found.Value.Owner);
        Assert.Equal("flag", ownerIf.Condition.ToString());
    }

    [Fact]
    public void FindControlTarget_ColumnSelectsInnerIfOnSameLine()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineIfsSource).GetRoot();
        var line = FindLine(SameLineIfsSource, "if (a) if (b)");

        var outer = ControlTargetHelpers.FindControlTarget(
            AddBracesOperation.CollectTargets(root), line, ColumnOf(SameLineIfsSource, "if (a)"));
        var inner = ControlTargetHelpers.FindControlTarget(
            AddBracesOperation.CollectTargets(root), line, ColumnOf(SameLineIfsSource, "if (b)"));

        Assert.NotNull(outer);
        var outerIf = Assert.IsType<IfStatementSyntax>(outer.Value.Owner);
        Assert.Equal("a", outerIf.Condition.ToString());
        Assert.NotNull(inner);
        var innerIf = Assert.IsType<IfStatementSyntax>(inner.Value.Owner);
        Assert.Equal("b", innerIf.Condition.ToString());
    }

    [Fact]
    public void FindControlTarget_AdjacentKeywords_ExclusiveEndDoesNotStealNext()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineIfsSource).GetRoot();
        var line = FindLine(SameLineIfsSource, "if (a) if (b)");
        var first = root.DescendantNodes().OfType<IfStatementSyntax>()
            .First(statement => statement.Condition.ToString() == "a");
        var firstKeywordEndCol = first.IfKeyword.GetLocation().GetLineSpan().EndLinePosition.Character + 1;
        var secondKeyword = ColumnOf(SameLineIfsSource, "if (b)");

        var atExclusiveEnd = ControlTargetHelpers.FindControlTarget(
            AddBracesOperation.CollectTargets(root), line, firstKeywordEndCol);
        var atSecond = ControlTargetHelpers.FindControlTarget(
            AddBracesOperation.CollectTargets(root), line, secondKeyword);

        Assert.False(SpanCoverage.SpanCoversColumn(
            first.IfKeyword.GetLocation().GetLineSpan(), line, firstKeywordEndCol));
        Assert.True(atExclusiveEnd == null
            || ((IfStatementSyntax)atExclusiveEnd.Value.Owner).Condition.ToString() != "a");
        Assert.NotNull(atSecond);
        var secondIf = Assert.IsType<IfStatementSyntax>(atSecond.Value.Owner);
        Assert.Equal("b", secondIf.Condition.ToString());
    }

    [Fact]
    public void FindControlTarget_NoMatch_ReturnsNull()
    {
        var root = CSharpSyntaxTree.ParseText(IndentedIfSource).GetRoot();
        var line = FindLine(IndentedIfSource, "if (flag)");

        var empty = ControlTargetHelpers.FindControlTarget([], line, column: null);
        Assert.Null(empty);

        var wrongLine = ControlTargetHelpers.FindControlTarget(
            AddBracesOperation.CollectTargets(root), line: 1, column: null);
        Assert.Null(wrongLine);

        var wrongColumn = ControlTargetHelpers.FindControlTarget(
            AddBracesOperation.CollectTargets(root), line, column: 1);
        Assert.Null(wrongColumn);
    }

    private static int ColumnOf(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var lineStart = source.LastIndexOf('\n', index) + 1;
        return index - lineStart + 1;
    }

    private static int FindLine(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
                line++;
        }

        return line;
    }
}
