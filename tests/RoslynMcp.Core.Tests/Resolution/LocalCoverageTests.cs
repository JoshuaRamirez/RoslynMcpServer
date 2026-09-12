using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Resolution;
using Xunit;

namespace RoslynMcp.Core.Tests.Resolution;

public class LocalCoverageTests
{
    [Fact]
    public void GetLocalDeclaration_ReturnsLocal_NullForField()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                int _x = 1;
                void M() { int y = 2; }
            }
            """);
        var root = tree.GetRoot();
        var fieldDecl = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(d => d.Identifier.ValueText == "_x");
        var localDecl = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(d => d.Identifier.ValueText == "y");

        Assert.NotNull(LocalCoverage.GetLocalDeclaration(localDecl));
        Assert.IsType<LocalDeclarationStatementSyntax>(LocalCoverage.GetLocalDeclaration(localDecl));
        Assert.Null(LocalCoverage.GetLocalDeclaration(fieldDecl));
    }

    [Fact]
    public void IdentifierCoversLine_True_OnIdentifierLine_False_OnOtherLocalLine()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
                    int a = 1;

                    int b = 2;
                }
            }
            """);
        var root = tree.GetRoot();
        var a = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(d => d.Identifier.ValueText == "a");
        var b = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(d => d.Identifier.ValueText == "b");
        var aLine = a.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var bLine = b.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        Assert.True(LocalCoverage.IdentifierCoversLine(a, aLine));
        Assert.True(LocalCoverage.LocalCoversLine(a, aLine));
        Assert.False(LocalCoverage.IdentifierCoversLine(a, bLine));
        Assert.False(LocalCoverage.LocalCoversLine(a, bLine));
        Assert.False(LocalCoverage.LocalCoversLine(a, 0));
    }

    [Fact]
    public void LocalCoversColumn_True_OnIdentifier_False_AtExclusiveEnd()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M() { int x = 1; }
            }
            """);
        var root = tree.GetRoot();
        var decl = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();
        var idSpan = decl.Identifier.GetLocation().GetLineSpan();
        var line = idSpan.StartLinePosition.Line + 1;
        var startCol = idSpan.StartLinePosition.Character + 1;
        var endCol = idSpan.EndLinePosition.Character + 1;

        Assert.True(LocalCoverage.LocalCoversColumn(decl, line, startCol));
        Assert.True(LocalCoverage.IdentifierCoversColumn(decl, line, startCol));
        Assert.True(LocalCoverage.IdentifierCoversColumn(decl, line, endCol - 1));
        Assert.False(LocalCoverage.IdentifierCoversColumn(decl, line, endCol));
        // endCol is past the identifier exclusive end but still inside the local span.
        Assert.True(LocalCoverage.LocalCoversColumn(decl, line, endCol));
        Assert.False(LocalCoverage.IdentifierCoversColumn(decl, line, startCol - 1));
    }

    [Fact]
    public void LocalCoversLine_True_OnLocalSpanFallback_WhenPastIdentifier()
    {
        // Multi-line local: type on first line; identifier on continuation.
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
                    int
                        LongName = 1;
                }
            }
            """);
        var root = tree.GetRoot();
        var decl = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();
        var local = LocalCoverage.GetLocalDeclaration(decl)!;
        var localStartLine = local.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var idLine = decl.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        Assert.NotEqual(localStartLine, idLine);
        Assert.False(LocalCoverage.IdentifierCoversLine(decl, localStartLine));
        Assert.True(LocalCoverage.LocalCoversLine(decl, localStartLine));
        Assert.True(LocalCoverage.LocalCoversLine(decl, idLine));
    }

    [Fact]
    public void SmallestCoveringSpanLength_PrefersDeclaratorOverLocal()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M() { int x = 1, y = 2; }
            }
            """);
        var root = tree.GetRoot();
        var x = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(d => d.Identifier.ValueText == "x");
        var line = x.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var local = LocalCoverage.GetLocalDeclaration(x)!;

        var smallest = LocalCoverage.SmallestCoveringSpanLength(x, line);
        Assert.True(smallest < local.Span.Length);
        Assert.Equal(x.Span.Length, smallest);

        var col = x.Identifier.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
        Assert.Equal(x.Span.Length, LocalCoverage.SmallestCoveringSpanLength(x, line, col));
    }
}
