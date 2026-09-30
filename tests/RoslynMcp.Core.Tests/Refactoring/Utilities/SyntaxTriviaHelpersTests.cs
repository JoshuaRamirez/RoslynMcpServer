using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class SyntaxTriviaHelpersTests
{
    [Fact]
    public void NonWhitespaceTrivia_Drops_WhitespaceAndEndOfLine()
    {
        var list = SyntaxFactory.TriviaList(
            SyntaxFactory.Space,
            SyntaxFactory.CarriageReturnLineFeed,
            SyntaxFactory.Whitespace("\t"),
            SyntaxFactory.EndOfLine("\n"));

        Assert.Empty(SyntaxTriviaHelpers.NonWhitespaceTrivia(list));
    }

    [Fact]
    public void NonWhitespaceTrivia_Keeps_SingleLineComment()
    {
        var comment = SyntaxFactory.Comment("// keep");
        var list = SyntaxFactory.TriviaList(
            SyntaxFactory.Space,
            comment,
            SyntaxFactory.CarriageReturnLineFeed);

        var kept = SyntaxTriviaHelpers.NonWhitespaceTrivia(list).ToList();
        Assert.Single(kept);
        Assert.True(kept[0].IsKind(SyntaxKind.SingleLineCommentTrivia));
        Assert.Equal("// keep", kept[0].ToString());
    }

    [Fact]
    public void NonWhitespaceTrivia_Keeps_DisabledTextAndDirectives()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
            #if FALSE
                    int x = 1;
            #endif
                }
            }
            """);
        var root = tree.GetRoot();
        var trivia = root.DescendantTrivia()
            .Where(t => t.IsKind(SyntaxKind.DisabledTextTrivia)
                || t.IsDirective)
            .ToList();
        Assert.NotEmpty(trivia);

        foreach (var item in trivia)
        {
            var kept = SyntaxTriviaHelpers.NonWhitespaceTrivia(SyntaxFactory.TriviaList(item)).ToList();
            Assert.Single(kept);
            Assert.Equal(item.Kind(), kept[0].Kind());
        }
    }
}
