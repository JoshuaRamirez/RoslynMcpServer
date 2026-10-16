using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Resolution;
using Xunit;

namespace RoslynMcp.Core.Tests.Resolution;

public class DeclaratorCoverageTests
{
    private static VariableDeclaratorSyntax Declarator(Microsoft.CodeAnalysis.SyntaxNode root, string name) =>
        root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(d => d.Identifier.ValueText == name);

    private static int StartLine(Microsoft.CodeAnalysis.SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    [Fact]
    public void DeclaratorCoversLine_UsesContainingDeclaration_OnlyWhenSupplied()
    {
        // Field declaration starts on the attribute line; declarator is on the next line.
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                [System.Obsolete]
                int _x = 1;
            }
            """);
        var root = tree.GetRoot();
        var decl = Declarator(root, "_x");
        var field = FieldCoverage.GetFieldDeclaration(decl);
        Assert.NotNull(field);
        var attributeLine = StartLine(field!);
        Assert.NotEqual(attributeLine, StartLine(decl));

        Assert.True(DeclaratorCoverage.DeclaratorCoversLine(decl, field, attributeLine));
        Assert.False(DeclaratorCoverage.DeclaratorCoversLine(decl, containingDeclaration: null, attributeLine));
        Assert.True(DeclaratorCoverage.DeclaratorCoversLine(decl, containingDeclaration: null, StartLine(decl)));
        Assert.Equal(
            FieldCoverage.FieldCoversLine(decl, attributeLine),
            DeclaratorCoverage.DeclaratorCoversLine(decl, field, attributeLine));
    }

    [Fact]
    public void DeclaratorCoversColumn_MatchesLocalCoverage_AndIdentifierExclusiveEnd()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M() { int x = 1; }
            }
            """);
        var root = tree.GetRoot();
        var decl = Declarator(root, "x");
        var local = LocalCoverage.GetLocalDeclaration(decl);
        var idSpan = decl.Identifier.GetLocation().GetLineSpan();
        var line = idSpan.StartLinePosition.Line + 1;
        var startCol = idSpan.StartLinePosition.Character + 1;
        var endCol = idSpan.EndLinePosition.Character + 1;

        Assert.True(DeclaratorCoverage.IdentifierCoversColumn(decl, line, startCol));
        Assert.False(DeclaratorCoverage.IdentifierCoversColumn(decl, line, endCol));
        Assert.True(DeclaratorCoverage.DeclaratorCoversColumn(decl, local, line, endCol));

        // 'int' keyword column: only the containing local declaration covers it.
        var localStartCol = local!.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
        Assert.True(DeclaratorCoverage.DeclaratorCoversColumn(decl, local, line, localStartCol));
        Assert.False(DeclaratorCoverage.DeclaratorCoversColumn(decl, containingDeclaration: null, line, localStartCol));
        Assert.Equal(
            LocalCoverage.LocalCoversColumn(decl, line, localStartCol),
            DeclaratorCoverage.DeclaratorCoversColumn(decl, local, line, localStartCol));
    }

    [Fact]
    public void SmallestCoveringSpanLength_PrefersDeclarator_FallsBackToContainer_MaxValueWhenNone()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                int _x = 1;
            }
            """);
        var root = tree.GetRoot();
        var decl = Declarator(root, "_x");
        var field = FieldCoverage.GetFieldDeclaration(decl)!;
        var line = StartLine(decl);
        var idCol = decl.Identifier.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
        var fieldCol = field.GetLocation().GetLineSpan().StartLinePosition.Character + 1;

        Assert.Equal(decl.Span.Length, DeclaratorCoverage.SmallestCoveringSpanLength(decl, field, line));
        Assert.Equal(decl.Span.Length, DeclaratorCoverage.SmallestCoveringSpanLength(decl, field, line, idCol));
        Assert.Equal(field.Span.Length, DeclaratorCoverage.SmallestCoveringSpanLength(decl, field, line, fieldCol));
        Assert.Equal(int.MaxValue, DeclaratorCoverage.SmallestCoveringSpanLength(decl, containingDeclaration: null, line, fieldCol));
        Assert.Equal(int.MaxValue, DeclaratorCoverage.SmallestCoveringSpanLength(decl, field, line: 0));
        Assert.Equal(
            FieldCoverage.SmallestCoveringSpanLength(decl, line, fieldCol),
            DeclaratorCoverage.SmallestCoveringSpanLength(decl, field, line, fieldCol));
    }

    [Fact]
    public void IdentifierCoversLine_TrueOnIdentifierLine_FalseElsewhere()
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
        var a = Declarator(root, "a");
        var b = Declarator(root, "b");

        Assert.True(DeclaratorCoverage.IdentifierCoversLine(a, StartLine(a)));
        Assert.False(DeclaratorCoverage.IdentifierCoversLine(a, StartLine(b)));
        Assert.False(DeclaratorCoverage.IdentifierCoversLine(a, 0));
    }
}
