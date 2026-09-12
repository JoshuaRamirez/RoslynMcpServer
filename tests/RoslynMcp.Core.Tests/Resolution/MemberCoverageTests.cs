using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Resolution;
using Xunit;

namespace RoslynMcp.Core.Tests.Resolution;

public class MemberCoverageTests
{
    [Fact]
    public void MemberCoversColumn_Method_True_OnIdentifier_False_AtExclusiveEnd()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M() { }
            }
            """);
        var root = tree.GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var idSpan = method.Identifier.GetLocation().GetLineSpan();
        var line = idSpan.StartLinePosition.Line + 1;
        var startCol = idSpan.StartLinePosition.Character + 1;
        var endCol = idSpan.EndLinePosition.Character + 1;

        Assert.True(MemberCoverage.MemberCoversColumn(method, line, startCol));
        Assert.True(MemberCoverage.IdentifierCoversColumn(method, line, startCol));
        Assert.True(MemberCoverage.IdentifierCoversColumn(method, line, endCol - 1));
        Assert.False(MemberCoverage.IdentifierCoversColumn(method, line, endCol));
        // endCol is past the identifier exclusive end but still inside the method span.
        Assert.True(MemberCoverage.MemberCoversColumn(method, line, endCol));
        Assert.False(MemberCoverage.IdentifierCoversColumn(method, line, startCol - 1));
    }

    [Fact]
    public void MemberCoversColumn_Constructor_True_OnIdentifier_False_AtExclusiveEnd()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                public C() { }
            }
            """);
        var root = tree.GetRoot();
        var ctor = root.DescendantNodes().OfType<ConstructorDeclarationSyntax>().Single();
        var idSpan = ctor.Identifier.GetLocation().GetLineSpan();
        var line = idSpan.StartLinePosition.Line + 1;
        var startCol = idSpan.StartLinePosition.Character + 1;
        var endCol = idSpan.EndLinePosition.Character + 1;

        Assert.True(MemberCoverage.MemberCoversColumn(ctor, line, startCol));
        Assert.True(MemberCoverage.IdentifierCoversColumn(ctor, line, startCol));
        Assert.True(MemberCoverage.IdentifierCoversColumn(ctor, line, endCol - 1));
        Assert.False(MemberCoverage.IdentifierCoversColumn(ctor, line, endCol));
        Assert.True(MemberCoverage.MemberCoversColumn(ctor, line, endCol));
        Assert.False(MemberCoverage.IdentifierCoversColumn(ctor, line, startCol - 1));
    }
}
