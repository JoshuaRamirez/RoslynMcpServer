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
}
