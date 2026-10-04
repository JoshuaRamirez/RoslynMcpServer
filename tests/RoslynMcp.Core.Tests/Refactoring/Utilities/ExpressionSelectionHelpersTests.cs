using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class ExpressionSelectionHelpersTests
{
    [Fact]
    public void FindEnclosingExpression_SelectsSmallestContainingExpression()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
                    var x = a + b * c;
                }
            }
            """);
        var root = tree.GetRoot();
        var multiply = root.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Single(b => b.IsKind(SyntaxKind.MultiplyExpression));
        var span = multiply.Right.Span; // `c`
        var node = root.FindNode(span, getInnermostNodeForTie: true);

        var found = ExpressionSelectionHelpers.FindEnclosingExpression(node, span);

        Assert.NotNull(found);
        Assert.Equal(multiply.Right.Span, found!.Span);
        Assert.Equal("c", found.ToString());
    }

    [Fact]
    public void FindEnclosingExpression_WholeBinarySelectsThatExpression()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
                    var x = a + b;
                }
            }
            """);
        var root = tree.GetRoot();
        var add = root.DescendantNodes().OfType<BinaryExpressionSyntax>().Single();
        var span = add.Span;
        var node = root.FindNode(span, getInnermostNodeForTie: true);

        var found = ExpressionSelectionHelpers.FindEnclosingExpression(node, span);

        Assert.NotNull(found);
        Assert.Equal(add.Span, found!.Span);
    }

    [Fact]
    public void FindEnclosingExpression_NoContainingExpression_ReturnsNull()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M() { }
            }
            """);
        var root = tree.GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        // Span on the method identifier — walk from that node never hits ExpressionSyntax containing it
        var span = method.Identifier.Span;
        var node = root.FindNode(span, getInnermostNodeForTie: true);

        var found = ExpressionSelectionHelpers.FindEnclosingExpression(node, span);

        Assert.Null(found);
    }

    [Fact]
    public void FindEnclosingExpression_NestedMemberAccess_PrefersSmallerMatch()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
                    var x = foo.Bar.Baz;
                }
            }
            """);
        var root = tree.GetRoot();
        var bar = root.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Single(id => id.Identifier.ValueText == "Bar");
        var span = bar.Span;
        var node = root.FindNode(span, getInnermostNodeForTie: true);

        var found = ExpressionSelectionHelpers.FindEnclosingExpression(node, span);

        Assert.NotNull(found);
        Assert.Equal(bar.Span, found!.Span);
        Assert.Equal("Bar", found.ToString());
    }
}
