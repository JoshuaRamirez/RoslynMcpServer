using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class VisibilityTokenHelpersTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ParseVisibilityTokens_EmptyOrWhitespace_DefaultsToPublic(string visibility)
    {
        var tokens = VisibilityTokenHelpers.ParseVisibilityTokens(visibility).ToArray();
        Assert.Single(tokens);
        Assert.Equal(SyntaxKind.PublicKeyword, tokens[0].Kind());
    }

    [Theory]
    [InlineData("public", SyntaxKind.PublicKeyword)]
    [InlineData("private", SyntaxKind.PrivateKeyword)]
    [InlineData("protected", SyntaxKind.ProtectedKeyword)]
    [InlineData("internal", SyntaxKind.InternalKeyword)]
    [InlineData("PUBLIC", SyntaxKind.PublicKeyword)]
    [InlineData("Private", SyntaxKind.PrivateKeyword)]
    [InlineData("PROTECTED", SyntaxKind.ProtectedKeyword)]
    [InlineData("Internal", SyntaxKind.InternalKeyword)]
    public void ParseVisibilityTokens_SingleKeyword_CaseInsensitive(string visibility, SyntaxKind expected)
    {
        var tokens = VisibilityTokenHelpers.ParseVisibilityTokens(visibility).ToArray();
        Assert.Single(tokens);
        Assert.Equal(expected, tokens[0].Kind());
    }

    [Fact]
    public void ParseVisibilityTokens_ProtectedInternal_EmitsBothTokens()
    {
        var tokens = VisibilityTokenHelpers.ParseVisibilityTokens("protected internal").ToArray();
        Assert.Equal(2, tokens.Length);
        Assert.Equal(SyntaxKind.ProtectedKeyword, tokens[0].Kind());
        Assert.Equal(SyntaxKind.InternalKeyword, tokens[1].Kind());
    }

    [Fact]
    public void ParseVisibilityTokens_PrivateProtected_EmitsBothTokens()
    {
        var tokens = VisibilityTokenHelpers.ParseVisibilityTokens("private protected").ToArray();
        Assert.Equal(2, tokens.Length);
        Assert.Equal(SyntaxKind.PrivateKeyword, tokens[0].Kind());
        Assert.Equal(SyntaxKind.ProtectedKeyword, tokens[1].Kind());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("foo")]
    [InlineData("pubic")]
    public void ParseVisibilityTokens_UnknownKeyword_DefaultsToPublic(string visibility)
    {
        var tokens = VisibilityTokenHelpers.ParseVisibilityTokens(visibility).ToArray();
        Assert.Single(tokens);
        Assert.Equal(SyntaxKind.PublicKeyword, tokens[0].Kind());
    }

    [Theory]
    [InlineData("public", SyntaxKind.PublicKeyword)]
    [InlineData("PRIVATE", SyntaxKind.PrivateKeyword)]
    [InlineData("Protected", SyntaxKind.ProtectedKeyword)]
    [InlineData("INTERNAL", SyntaxKind.InternalKeyword)]
    [InlineData("nope", SyntaxKind.PublicKeyword)]
    public void ParseVisibilityKeyword_MapsKnownAndUnknown(string keyword, SyntaxKind expected)
    {
        Assert.Equal(expected, VisibilityTokenHelpers.ParseVisibilityKeyword(keyword).Kind());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown")]
    public void ParseVisibilityTokens_EmptyOrUnknown_PrivateDefault(string visibility)
    {
        var tokens = VisibilityTokenHelpers.ParseVisibilityTokens(
            visibility,
            SyntaxKind.PrivateKeyword).ToArray();
        Assert.Single(tokens);
        Assert.Equal(SyntaxKind.PrivateKeyword, tokens[0].Kind());
        // No forced Space trivia (extract path); SyntaxFactory may still attach elastic markers.
        Assert.DoesNotContain(tokens[0].TrailingTrivia, static t => t.ToString() == " ");
    }

    [Fact]
    public void ParseVisibilityTokens_TrailingSpace_AppliesToEachToken()
    {
        var tokens = VisibilityTokenHelpers.ParseVisibilityTokens(
            "protected internal",
            SyntaxKind.PrivateKeyword,
            trailingSpace: true).ToArray();
        Assert.Equal(2, tokens.Length);
        Assert.Equal(SyntaxKind.ProtectedKeyword, tokens[0].Kind());
        Assert.Equal(SyntaxKind.InternalKeyword, tokens[1].Kind());
        Assert.Equal(" ", tokens[0].TrailingTrivia.ToString());
        Assert.Equal(" ", tokens[1].TrailingTrivia.ToString());
    }

    [Fact]
    public void ParseVisibilityKeyword_Unknown_UsesCallerDefault()
    {
        Assert.Equal(
            SyntaxKind.PrivateKeyword,
            VisibilityTokenHelpers.ParseVisibilityKeyword("nope", SyntaxKind.PrivateKeyword).Kind());
    }
}
