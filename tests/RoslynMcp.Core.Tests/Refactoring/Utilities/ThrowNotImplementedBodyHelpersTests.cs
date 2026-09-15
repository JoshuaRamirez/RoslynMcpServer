using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="ThrowNotImplementedBodyHelpers.CreateThrowNotImplementedBody"/> —
/// global:: throw shape previously duplicated on ImplementAbstract /
/// GenerateMethodStub.
/// </summary>
public class ThrowNotImplementedBodyHelpersTests
{
    [Fact]
    public void CreateThrowNotImplementedBody_IsSingleStatementThrowBlock()
    {
        var body = ThrowNotImplementedBodyHelpers.CreateThrowNotImplementedBody();

        Assert.Single(body.Statements);
        Assert.IsType<ThrowStatementSyntax>(body.Statements[0]);
    }

    [Fact]
    public void CreateThrowNotImplementedBody_ThrowsGlobalSystemNotImplementedException()
    {
        var body = ThrowNotImplementedBodyHelpers.CreateThrowNotImplementedBody();
        var throwStmt = Assert.IsType<ThrowStatementSyntax>(body.Statements[0]);
        var creation = Assert.IsType<ObjectCreationExpressionSyntax>(throwStmt.Expression);

        Assert.Equal("global::System.NotImplementedException", creation.Type.ToString());
        Assert.NotNull(creation.ArgumentList);
        Assert.Empty(creation.ArgumentList.Arguments);
    }

    [Fact]
    public void CreateThrowNotImplementedBody_NormalizedText_MatchesExpectedThrow()
    {
        var body = ThrowNotImplementedBodyHelpers.CreateThrowNotImplementedBody();
        var normalized = body.NormalizeWhitespace().ToFullString();

        Assert.Contains("throw new global::System.NotImplementedException()", normalized);
    }
}
