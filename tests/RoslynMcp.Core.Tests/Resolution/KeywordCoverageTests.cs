using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Resolution;
using Xunit;

namespace RoslynMcp.Core.Tests.Resolution;

public class KeywordCoverageTests
{
    [Fact]
    public void KeywordCoversColumn_True_OnKeyword_False_Before_AtExclusiveEnd_WrongLine()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
                    if (true) { }
                    for (int i = 0; i < 1; i++) { }
                }
            }
            """);
        var root = tree.GetRoot();
        var ifKeyword = root.DescendantNodes().OfType<IfStatementSyntax>().Single().IfKeyword;
        var forKeyword = root.DescendantNodes().OfType<ForStatementSyntax>().Single().ForKeyword;

        var ifSpan = ifKeyword.GetLocation().GetLineSpan();
        var ifLine = ifSpan.StartLinePosition.Line + 1;
        var ifStartCol = ifSpan.StartLinePosition.Character + 1;
        var ifEndCol = ifSpan.EndLinePosition.Character + 1;

        Assert.True(KeywordCoverage.KeywordCoversColumn(ifKeyword, ifLine, ifStartCol));
        Assert.True(KeywordCoverage.KeywordCoversColumn(ifKeyword, ifLine, ifEndCol - 1));
        Assert.False(KeywordCoverage.KeywordCoversColumn(ifKeyword, ifLine, ifStartCol - 1));
        Assert.False(KeywordCoverage.KeywordCoversColumn(ifKeyword, ifLine, ifEndCol));
        Assert.False(KeywordCoverage.KeywordCoversColumn(ifKeyword, ifLine + 1, ifStartCol));

        var forSpan = forKeyword.GetLocation().GetLineSpan();
        var forLine = forSpan.StartLinePosition.Line + 1;
        var forStartCol = forSpan.StartLinePosition.Character + 1;
        Assert.True(KeywordCoverage.KeywordCoversColumn(forKeyword, forLine, forStartCol));
        Assert.False(KeywordCoverage.KeywordCoversColumn(forKeyword, ifLine, ifStartCol));
    }

    [Fact]
    public void KeywordIsOnLine_True_OnKeywordLine_False_OnWrongLine()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
                    if (true) { }
                    for (int i = 0; i < 1; i++) { }
                }
            }
            """);
        var root = tree.GetRoot();
        var ifKeyword = root.DescendantNodes().OfType<IfStatementSyntax>().Single().IfKeyword;
        var forKeyword = root.DescendantNodes().OfType<ForStatementSyntax>().Single().ForKeyword;

        var ifLine = ifKeyword.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var forLine = forKeyword.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        Assert.True(KeywordCoverage.KeywordIsOnLine(ifKeyword, ifLine));
        Assert.False(KeywordCoverage.KeywordIsOnLine(ifKeyword, ifLine + 1));
        Assert.True(KeywordCoverage.KeywordIsOnLine(forKeyword, forLine));
        Assert.False(KeywordCoverage.KeywordIsOnLine(forKeyword, ifLine));
    }
}
