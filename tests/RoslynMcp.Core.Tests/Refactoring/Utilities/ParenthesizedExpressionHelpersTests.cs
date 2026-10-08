using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class ParenthesizedExpressionHelpersTests
{
    [Fact]
    public void SkipParentheses_NoParentheses_ReturnsSameInstance()
    {
        var expression = SyntaxFactory.ParseExpression("a + b");

        var result = ParenthesizedExpressionHelpers.SkipParentheses(expression);

        Assert.Same(expression, result);
    }

    [Fact]
    public void SkipParentheses_SingleLayer_ReturnsInnerInstance()
    {
        var expression = (ParenthesizedExpressionSyntax)SyntaxFactory.ParseExpression("(x)");

        var result = ParenthesizedExpressionHelpers.SkipParentheses(expression);

        Assert.Same(expression.Expression, result);
        Assert.IsType<IdentifierNameSyntax>(result);
    }

    [Fact]
    public void SkipParentheses_NestedLayers_ReturnsInnermostNonParenthesizedInstance()
    {
        var expression = SyntaxFactory.ParseExpression("(((x => x)))");
        var innermost = expression.DescendantNodesAndSelf()
            .OfType<SimpleLambdaExpressionSyntax>()
            .Single();

        var result = ParenthesizedExpressionHelpers.SkipParentheses(expression);

        Assert.Same(innermost, result);
    }

    [Fact]
    public void SkipParentheses_ParenthesesBelowNonParenthesizedNode_AreLeftAlone()
    {
        var expression = SyntaxFactory.ParseExpression("((a) + (b))");
        var binary = expression.DescendantNodes()
            .OfType<BinaryExpressionSyntax>()
            .Single();

        var result = ParenthesizedExpressionHelpers.SkipParentheses(expression);

        Assert.Same(binary, result);
        Assert.Equal("(a) + (b)", result.ToString());
        Assert.IsType<ParenthesizedExpressionSyntax>(((BinaryExpressionSyntax)result).Left);
    }

    [Fact]
    public void SkipParentheses_CastIsNotStripped()
    {
        var expression = SyntaxFactory.ParseExpression("((int)(x))");

        var result = ParenthesizedExpressionHelpers.SkipParentheses(expression);

        Assert.IsType<CastExpressionSyntax>(result);
        Assert.Equal("(int)(x)", result.ToString());
    }
}
