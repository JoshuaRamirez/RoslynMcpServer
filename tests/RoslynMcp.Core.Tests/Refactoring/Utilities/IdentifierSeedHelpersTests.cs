using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="IdentifierSeedHelpers"/>: PreferInvokedName /
/// PreferInvokedNameFromExpression for simple, member-access, generic,
/// parenthesized and unsupported expressions, PreferTypeName for
/// identifier, qualified, generic, nullable, alias-qualified, fallback and null
/// types, and SanitizeIdentifierSeed in both casing modes for blank, symbol-only
/// and digit inputs.
/// </summary>
public class IdentifierSeedHelpersTests
{
    private static TypeSyntax Type(string text) => SyntaxFactory.ParseTypeName(text);

    private static ExpressionSyntax Expr(string text) => SyntaxFactory.ParseExpression(text);

    [Theory]
    [InlineData("Compute()", "Compute")]
    [InlineData("order.GetTotal()", "GetTotal")]
    [InlineData("a.b.c.Load(1, 2)", "Load")]
    [InlineData("Create<int>()", "Create")]
    [InlineData("factory.Create<Widget>()", "Create")]
    [InlineData("((x.Run))()", "Run")]
    [InlineData("this.Save()", "Save")]
    public void PreferInvokedName_ReturnsInvokedSimpleName(string text, string expected)
    {
        var invocation = Assert.IsType<InvocationExpressionSyntax>(Expr(text));
        Assert.Equal(expected, IdentifierSeedHelpers.PreferInvokedName(invocation));
    }

    [Theory]
    [InlineData("GetItems()()")]
    [InlineData("handlers[0]()")]
    [InlineData("((Func<int>)f)()")]
    public void PreferInvokedName_UnsupportedTarget_ReturnsNull(string text)
    {
        var invocation = Assert.IsType<InvocationExpressionSyntax>(Expr(text));
        Assert.Null(IdentifierSeedHelpers.PreferInvokedName(invocation));
    }

    [Theory]
    [InlineData("items", "items")]
    [InlineData("order.Lines", "Lines")]
    [InlineData("Cache<string>", "Cache")]
    [InlineData("(items)", "items")]
    [InlineData("(((order.Lines)))", "Lines")]
    public void PreferInvokedNameFromExpression_SimpleShapes_ReturnSimpleName(string text, string expected)
    {
        Assert.Equal(expected, IdentifierSeedHelpers.PreferInvokedNameFromExpression(Expr(text)));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("a + b")]
    [InlineData("items[0]")]
    [InlineData("Load()")]
    [InlineData("(a + b)")]
    [InlineData("\"text\"")]
    public void PreferInvokedNameFromExpression_OtherShapes_ReturnNull(string text)
    {
        Assert.Null(IdentifierSeedHelpers.PreferInvokedNameFromExpression(Expr(text)));
    }

    [Theory]
    [InlineData("Widget", "Widget")]
    [InlineData("System.Text.StringBuilder", "StringBuilder")]
    [InlineData("List<int>", "List")]
    [InlineData("Dictionary<string, Widget>", "Dictionary")]
    [InlineData("Widget?", "Widget")]
    [InlineData("global::System.Uri", "Uri")]
    [InlineData("global::Widget", "Widget")]
    [InlineData("Ns.List<int>", "List")]
    public void PreferTypeName_NamedTypes_ReturnSimpleNameVerbatim(string typeText, string expected)
    {
        Assert.Equal(expected, IdentifierSeedHelpers.PreferTypeName(Type(typeText), capitalizeFirstWord: true));
        Assert.Equal(expected, IdentifierSeedHelpers.PreferTypeName(Type(typeText), capitalizeFirstWord: false));
    }

    [Fact]
    public void PreferTypeName_LowercaseIdentifier_IsNotRecased()
    {
        Assert.Equal("widget", IdentifierSeedHelpers.PreferTypeName(Type("widget"), capitalizeFirstWord: true));
        Assert.Equal("Widget", IdentifierSeedHelpers.PreferTypeName(Type("Widget"), capitalizeFirstWord: false));
    }

    [Theory]
    [InlineData("int[]", true, "Int")]
    [InlineData("int[]", false, "int")]
    [InlineData("int?", true, "Int")]
    [InlineData("int?", false, "int")]
    [InlineData("(int count, string name)", true, "IntCountStringName")]
    [InlineData("(int count, string name)", false, "intCountStringName")]
    public void PreferTypeName_OtherTypes_FallBackToSanitizedText(string typeText, bool capitalize, string expected)
    {
        Assert.Equal(expected, IdentifierSeedHelpers.PreferTypeName(Type(typeText), capitalize));
    }

    [Fact]
    public void PreferTypeName_Null_ReturnsNull()
    {
        Assert.Null(IdentifierSeedHelpers.PreferTypeName(null, capitalizeFirstWord: true));
        Assert.Null(IdentifierSeedHelpers.PreferTypeName(null, capitalizeFirstWord: false));
    }

    [Theory]
    [InlineData("order.GetTotal()", true, "OrderGetTotal")]
    [InlineData("order.GetTotal()", false, "orderGetTotal")]
    [InlineData("Count + 1", true, "Count1")]
    [InlineData("Count + 1", false, "count1")]
    [InlineData("x_y z", true, "XYZ")]
    [InlineData("x_y z", false, "xYZ")]
    [InlineData("a1b", true, "A1b")]
    [InlineData("a1b", false, "a1b")]
    [InlineData("1abc", true, "1abc")]
    [InlineData("1abc", false, "1abc")]
    [InlineData("1 abc", true, "1Abc")]
    [InlineData("1 abc", false, "1Abc")]
    [InlineData("42", true, "42")]
    [InlineData("42", false, "42")]
    public void SanitizeIdentifierSeed_KeepsLettersAndDigits_WithCasingMode(string text, bool capitalize, string expected)
    {
        Assert.Equal(expected, IdentifierSeedHelpers.SanitizeIdentifierSeed(text, capitalize));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData("()")]
    [InlineData("+ - * /")]
    public void SanitizeIdentifierSeed_BlankOrSymbolOnly_ReturnsNull(string text)
    {
        Assert.Null(IdentifierSeedHelpers.SanitizeIdentifierSeed(text, capitalizeFirstWord: true));
        Assert.Null(IdentifierSeedHelpers.SanitizeIdentifierSeed(text, capitalizeFirstWord: false));
    }
}
