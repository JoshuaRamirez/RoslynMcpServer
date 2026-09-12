using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Resolution;
using Xunit;

namespace RoslynMcp.Core.Tests.Resolution;

public class MethodCoverageTests
{
    [Fact]
    public void MethodCoversColumn_True_OnIdentifier_False_AtExclusiveEnd()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M() { int x = 1; }
            }
            """);
        var root = tree.GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var idSpan = method.Identifier.GetLocation().GetLineSpan();
        var line = idSpan.StartLinePosition.Line + 1;
        var startCol = idSpan.StartLinePosition.Character + 1;
        var endCol = idSpan.EndLinePosition.Character + 1;

        Assert.True(MethodCoverage.MethodCoversColumn(method, line, startCol));
        Assert.True(MethodCoverage.IdentifierCoversColumn(method, line, startCol));
        Assert.True(MethodCoverage.IdentifierCoversColumn(method, line, endCol - 1));
        Assert.False(MethodCoverage.IdentifierCoversColumn(method, line, endCol));
        // endCol is past the identifier exclusive end but still inside the method span.
        Assert.True(MethodCoverage.MethodCoversColumn(method, line, endCol));
        Assert.False(MethodCoverage.IdentifierCoversColumn(method, line, startCol - 1));
    }
}
