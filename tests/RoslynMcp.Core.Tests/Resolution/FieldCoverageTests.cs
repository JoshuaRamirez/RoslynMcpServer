using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Resolution;
using Xunit;

namespace RoslynMcp.Core.Tests.Resolution;

public class FieldCoverageTests
{
    [Fact]
    public void GetFieldDeclaration_ReturnsField_NullForLocal()
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

        Assert.NotNull(FieldCoverage.GetFieldDeclaration(fieldDecl));
        Assert.IsType<FieldDeclarationSyntax>(FieldCoverage.GetFieldDeclaration(fieldDecl));
        Assert.Null(FieldCoverage.GetFieldDeclaration(localDecl));
    }

    [Fact]
    public void IdentifierCoversLine_True_OnIdentifierLine_False_OnOtherFieldLine()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                int _a = 1;

                int _b = 2;
            }
            """);
        var root = tree.GetRoot();
        var a = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(d => d.Identifier.ValueText == "_a");
        var b = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(d => d.Identifier.ValueText == "_b");
        var aLine = a.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var bLine = b.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        Assert.True(FieldCoverage.IdentifierCoversLine(a, aLine));
        Assert.True(FieldCoverage.FieldCoversLine(a, aLine));
        Assert.False(FieldCoverage.IdentifierCoversLine(a, bLine));
        Assert.False(FieldCoverage.FieldCoversLine(a, bLine));
        Assert.False(FieldCoverage.FieldCoversLine(a, 0));
    }

    [Fact]
    public void FieldCoversColumn_True_OnIdentifier_False_AtExclusiveEnd()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                int _x = 1;
            }
            """);
        var root = tree.GetRoot();
        var decl = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();
        var idSpan = decl.Identifier.GetLocation().GetLineSpan();
        var line = idSpan.StartLinePosition.Line + 1;
        var startCol = idSpan.StartLinePosition.Character + 1;
        var endCol = idSpan.EndLinePosition.Character + 1;

        Assert.True(FieldCoverage.FieldCoversColumn(decl, line, startCol));
        Assert.True(FieldCoverage.IdentifierCoversColumn(decl, line, startCol));
        Assert.True(FieldCoverage.IdentifierCoversColumn(decl, line, endCol - 1));
        Assert.False(FieldCoverage.IdentifierCoversColumn(decl, line, endCol));
        // endCol is past the identifier exclusive end but still inside the field span.
        Assert.True(FieldCoverage.FieldCoversColumn(decl, line, endCol));
        Assert.False(FieldCoverage.IdentifierCoversColumn(decl, line, startCol - 1));
    }

    [Fact]
    public void FieldCoversLine_True_OnFieldSpanFallback_WhenPastIdentifier()
    {
        // Multi-line field: identifier on first line; type/modifiers line still covers via field span.
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                private static readonly int
                    LongName = 1;
            }
            """);
        var root = tree.GetRoot();
        var decl = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();
        var field = FieldCoverage.GetFieldDeclaration(decl)!;
        var fieldStartLine = field.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var idLine = decl.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        Assert.NotEqual(fieldStartLine, idLine);
        Assert.False(FieldCoverage.IdentifierCoversLine(decl, fieldStartLine));
        Assert.True(FieldCoverage.FieldCoversLine(decl, fieldStartLine));
        Assert.True(FieldCoverage.FieldCoversLine(decl, idLine));
    }

    [Fact]
    public void SmallestCoveringSpanLength_PrefersDeclaratorOverField()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                int _x = 1, _y = 2;
            }
            """);
        var root = tree.GetRoot();
        var x = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(d => d.Identifier.ValueText == "_x");
        var line = x.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var field = FieldCoverage.GetFieldDeclaration(x)!;

        var smallest = FieldCoverage.SmallestCoveringSpanLength(x, line);
        Assert.True(smallest < field.Span.Length);
        Assert.Equal(x.Span.Length, smallest);

        var col = x.Identifier.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
        Assert.Equal(x.Span.Length, FieldCoverage.SmallestCoveringSpanLength(x, line, col));
    }
}
