using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class SyntaxLineHelpersTests
{
    [Fact]
    public void StartsOnLine_True_WhenNodeStartsOnGivenLine()
    {
        var root = Parse("""
            class C
            {
                void M()
                {
                    var x = 1;
                }
            }
            """);
        var local = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().Single();

        // Local starts on line 5 (1-based) in the snippet above.
        Assert.True(SyntaxLineHelpers.StartsOnLine(local, 5));
    }

    [Fact]
    public void StartsOnLine_False_WhenNodeStartsOnDifferentLine()
    {
        var root = Parse("""
            class C
            {
                void M()
                {
                    var x = 1;
                }
            }
            """);
        var local = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().Single();

        Assert.False(SyntaxLineHelpers.StartsOnLine(local, 1));
        Assert.False(SyntaxLineHelpers.StartsOnLine(local, 4));
        Assert.False(SyntaxLineHelpers.StartsOnLine(local, 6));
    }

    [Fact]
    public void StartsOnLine_True_ForRootStartingOnLineOne()
    {
        var root = Parse("class C { }");

        Assert.True(SyntaxLineHelpers.StartsOnLine(root, 1));
        Assert.False(SyntaxLineHelpers.StartsOnLine(root, 2));
    }

    private static CompilationUnitSyntax Parse(string source) =>
        (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source).GetRoot();
}
