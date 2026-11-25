using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Pins the <c>this.Member</c> shape that generate_tostring,
/// generate_equals_hashcode, and generate_constructor emit through
/// <see cref="SyntaxGenerationHelper.ThisMemberAccess"/>.
/// </summary>
public class SyntaxGenerationHelperThisMemberAccessTests
{
    [Theory]
    [InlineData("Name")]
    [InlineData("hash")]
    [InlineData("other")]
    [InlineData("_value")]
    public void ThisMemberAccess_BuildsSimpleMemberAccessOnThis(string memberName)
    {
        var access = SyntaxGenerationHelper.ThisMemberAccess(memberName);

        Assert.Equal(SyntaxKind.SimpleMemberAccessExpression, access.Kind());
        Assert.IsType<ThisExpressionSyntax>(access.Expression);
        var name = Assert.IsType<IdentifierNameSyntax>(access.Name);
        Assert.Equal(memberName, name.Identifier.Text);
        Assert.Equal($"this.{memberName}", access.ToString());
    }

    [Fact]
    public void ThisMemberAccess_MatchesInlineSyntaxFactoryShape()
    {
        var expected = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.ThisExpression(),
            SyntaxFactory.IdentifierName("Count"));

        var actual = SyntaxGenerationHelper.ThisMemberAccess("Count");

        Assert.True(actual.IsEquivalentTo(expected));
        Assert.Equal(expected.ToFullString(), actual.ToFullString());
    }

    [Theory]
    [InlineData("event")]
    [InlineData("class")]
    [InlineData("int")]
    public void ThisMemberAccess_ReservedKeywordName_KeepsVerbatimPrefix(string memberName)
    {
        var access = SyntaxGenerationHelper.ThisMemberAccess(memberName);

        var name = Assert.IsType<IdentifierNameSyntax>(access.Name);
        Assert.Equal($"@{memberName}", name.Identifier.Text);
        Assert.Equal(memberName, name.Identifier.ValueText);
        Assert.Equal($"this.@{memberName}", access.ToString());

        var reparsed = SyntaxFactory.ParseExpression($"{access} = default");
        Assert.False(reparsed.ContainsDiagnostics);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("var")]
    [InlineData("async")]
    public void ThisMemberAccess_ContextualKeywordName_StaysBare(string memberName)
    {
        var access = SyntaxGenerationHelper.ThisMemberAccess(memberName);

        Assert.Equal($"this.{memberName}", access.ToString());
    }

    [Theory]
    [InlineData("class", "@class")]
    [InlineData("event", "@event")]
    [InlineData("value", "value")]
    [InlineData("Class", "Class")]
    public void CreateEscapedIdentifierName_EscapesOnlyReservedKeywords(string name, string expectedText)
    {
        var identifier = SyntaxGenerationHelper.CreateEscapedIdentifierName(name);

        Assert.Equal(expectedText, identifier.Identifier.Text);
        Assert.Equal(name, identifier.Identifier.ValueText);
        Assert.Equal(expectedText, SyntaxGenerationHelper.EscapedIdentifierText(name));
    }
}
