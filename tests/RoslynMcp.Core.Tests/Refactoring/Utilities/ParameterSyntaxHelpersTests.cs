using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class ParameterSyntaxHelpersTests
{
    [Fact]
    public void IsParams_True_WhenParamsKeywordPresent()
    {
        var parameter = ParseParameter("params int[] values");

        Assert.True(ParameterSyntaxHelpers.IsParams(parameter));
    }

    [Fact]
    public void IsParams_False_WhenNoParamsKeyword()
    {
        var parameter = ParseParameter("int value");

        Assert.False(ParameterSyntaxHelpers.IsParams(parameter));
    }

    [Fact]
    public void IsOptional_True_WhenDefaultClausePresent()
    {
        var parameter = ParseParameter("int value = 0");

        Assert.True(ParameterSyntaxHelpers.IsOptional(parameter));
    }

    [Fact]
    public void IsOptional_False_WhenNoDefaultClause()
    {
        var parameter = ParseParameter("int value");

        Assert.False(ParameterSyntaxHelpers.IsOptional(parameter));
    }

    [Fact]
    public void IsParams_And_IsOptional_Independent()
    {
        var paramsOnly = ParseParameter("params string[] items");
        var optionalOnly = ParseParameter("string? name = null");
        var neither = ParseParameter("string name");

        Assert.True(ParameterSyntaxHelpers.IsParams(paramsOnly));
        Assert.False(ParameterSyntaxHelpers.IsOptional(paramsOnly));

        Assert.False(ParameterSyntaxHelpers.IsParams(optionalOnly));
        Assert.True(ParameterSyntaxHelpers.IsOptional(optionalOnly));

        Assert.False(ParameterSyntaxHelpers.IsParams(neither));
        Assert.False(ParameterSyntaxHelpers.IsOptional(neither));
    }

    [Fact]
    public void IsDefaultValueExpression_True_ForDefaultLiteral()
    {
        var expression = SyntaxFactory.ParseExpression("default");

        Assert.True(ParameterSyntaxHelpers.IsDefaultValueExpression(expression));
    }

    [Fact]
    public void IsDefaultValueExpression_True_ForDefaultTypedExpression()
    {
        var expression = SyntaxFactory.ParseExpression("default(int)");

        Assert.True(ParameterSyntaxHelpers.IsDefaultValueExpression(expression));
    }

    [Fact]
    public void IsDefaultValueExpression_False_ForNonDefaultExpression()
    {
        var expression = SyntaxFactory.ParseExpression("0");

        Assert.False(ParameterSyntaxHelpers.IsDefaultValueExpression(expression));
    }

    private static ParameterSyntax ParseParameter(string text)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(
            $"class C {{ void M({text}) {{ }} }}").GetRoot();
        return root.DescendantNodes().OfType<ParameterSyntax>().Single();
    }
}
