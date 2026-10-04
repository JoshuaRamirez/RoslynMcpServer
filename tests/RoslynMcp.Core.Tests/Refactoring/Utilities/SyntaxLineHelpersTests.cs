using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
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

    [Fact]
    public void StartLine_ReturnsOneBasedStartLine()
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
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        // Local starts on line 5; method on line 3 (1-based) in the snippet above.
        Assert.Equal(5, SyntaxLineHelpers.StartLine(local));
        Assert.Equal(3, SyntaxLineHelpers.StartLine(method));
    }

    [Fact]
    public void StartLine_RootStartsOnLineOne()
    {
        var root = Parse("class C { }");

        Assert.Equal(1, SyntaxLineHelpers.StartLine(root));
    }

    [Fact]
    public void StartsOnLine_MatchesStartLine()
    {
        var root = Parse("""
            class C
            {
                void M() { }
            }
            """);
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var line = SyntaxLineHelpers.StartLine(method);

        Assert.True(SyntaxLineHelpers.StartsOnLine(method, line));
        Assert.False(SyntaxLineHelpers.StartsOnLine(method, line + 1));
    }

    [Fact]
    public void GetSelectionSpan_MapsOneBasedLineColumnToSpan()
    {
        var text = SourceText.From("""
            class C
            {
                void M() { }
            }
            """);

        // "void" on line 3 starts at column 5 (1-based) in the indented snippet.
        var span = SyntaxLineHelpers.GetSelectionSpan(text, 3, 5, 3, 9);

        Assert.Equal("void", text.ToString(span));
    }

    [Fact]
    public void GetSelectionSpan_InvalidLine_ThrowsInvalidLineNumber()
    {
        var text = SourceText.From("class C { }");

        var ex = Assert.Throws<RefactoringException>(
            () => SyntaxLineHelpers.GetSelectionSpan(text, 99, 1, 99, 2));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void GetSelectionSpan_InvalidColumn_ThrowsInvalidColumnNumber()
    {
        var text = SourceText.From("class C { }");

        var ex = Assert.Throws<RefactoringException>(
            () => SyntaxLineHelpers.GetSelectionSpan(text, 1, 1, 1, 500));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public void GetSelectionSpan_EndBeforeStart_ThrowsInvalidSelectionRange()
    {
        var text = SourceText.From("class C { }");

        var ex = Assert.Throws<RefactoringException>(
            () => SyntaxLineHelpers.GetSelectionSpan(text, 1, 5, 1, 2));

        Assert.Equal(ErrorCodes.InvalidSelectionRange, ex.ErrorCode);
    }

    private static CompilationUnitSyntax Parse(string source) =>
        (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source).GetRoot();
}
