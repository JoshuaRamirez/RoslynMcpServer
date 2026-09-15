using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class GotoLabelHelpersTests
{
    [Fact]
    public void IsLabelAlreadyNestedInInnerBlock_LabelEqualsBody_ReturnsFalse()
    {
        var label = ParseLabeled("""
            class C
            {
                void M()
                {
                    label: ;
                }
            }
            """);
        Assert.False(GotoLabelHelpers.IsLabelAlreadyNestedInInnerBlock(label, label));
    }

    [Fact]
    public void IsLabelAlreadyNestedInInnerBlock_LabelNestedInInnerBlock_ReturnsTrue()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
                    if (true)
                    {
                        {
                            label: ;
                        }
                    }
                }
            }
            """);
        var root = tree.GetRoot();
        var ifStatement = root.DescendantNodes().OfType<IfStatementSyntax>().Single();
        var body = ifStatement.Statement;
        var label = root.DescendantNodes().OfType<LabeledStatementSyntax>().Single();
        Assert.True(GotoLabelHelpers.IsLabelAlreadyNestedInInnerBlock(label, body));
    }

    [Fact]
    public void IsLabelAlreadyNestedInInnerBlock_LabelNestedInSwitchSection_ReturnsTrue()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M(int x)
                {
                    switch (x)
                    {
                        case 1:
                            label: ;
                            break;
                    }
                }
            }
            """);
        var root = tree.GetRoot();
        var switchStatement = root.DescendantNodes().OfType<SwitchStatementSyntax>().Single();
        var label = root.DescendantNodes().OfType<LabeledStatementSyntax>().Single();
        Assert.True(GotoLabelHelpers.IsLabelAlreadyNestedInInnerBlock(label, switchStatement));
    }

    [Fact]
    public void IsLabelAlreadyNestedInInnerBlock_LabelDirectUnderBody_ReturnsFalse()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
                    if (true)
                    {
                        label: ;
                    }
                }
            }
            """);
        var root = tree.GetRoot();
        var ifStatement = root.DescendantNodes().OfType<IfStatementSyntax>().Single();
        var body = Assert.IsType<BlockSyntax>(ifStatement.Statement);
        var label = root.DescendantNodes().OfType<LabeledStatementSyntax>().Single();
        // Label is a direct statement of body (not body itself), so this hits
        // the ancestor loop's ancestor == body path rather than label == body.
        Assert.Same(label, body.Statements[0]);
        Assert.False(GotoLabelHelpers.IsLabelAlreadyNestedInInnerBlock(label, body));
    }

    [Fact]
    public void GetGotoLabelName_IdentifierName_ReturnsValueText()
    {
        var gotoStatement = ParseGoto("""
            class C
            {
                void M()
                {
                    goto @target;
                }
            }
            """);
        Assert.Equal("target", GotoLabelHelpers.GetGotoLabelName(gotoStatement));
    }

    [Fact]
    public void GetGotoLabelName_OtherExpression_ReturnsToString()
    {
        var gotoStatement = SyntaxFactory.GotoStatement(
            SyntaxKind.GotoStatement,
            SyntaxFactory.ParseExpression("1 + 2"));
        Assert.Equal("1 + 2", GotoLabelHelpers.GetGotoLabelName(gotoStatement));
    }

    [Fact]
    public void GetGotoLabelName_NullExpression_ReturnsNull()
    {
        var gotoStatement = SyntaxFactory.GotoStatement(
            SyntaxKind.GotoStatement,
            expression: null!);
        Assert.Null(GotoLabelHelpers.GetGotoLabelName(gotoStatement));
    }

    [Fact]
    public void GetLabelContainer_MethodDeclaration_ReturnsMethod()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
                    label: ;
                }
            }
            """);
        var root = tree.GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var label = root.DescendantNodes().OfType<LabeledStatementSyntax>().Single();
        Assert.Same(method, GotoLabelHelpers.GetLabelContainer(label));
    }

    [Fact]
    public void GetLabelContainer_LocalFunction_ReturnsLocalFunction()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
                    void Local()
                    {
                        label: ;
                    }
                }
            }
            """);
        var root = tree.GetRoot();
        var local = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single();
        var label = root.DescendantNodes().OfType<LabeledStatementSyntax>().Single();
        Assert.Same(local, GotoLabelHelpers.GetLabelContainer(label));
    }

    [Fact]
    public void GetLabelContainer_OutsideAnyContainer_ReturnsNull()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                int field = 1;
            }
            """);
        var field = tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>().Single();
        Assert.Null(GotoLabelHelpers.GetLabelContainer(field));
    }

    [Fact]
    public void WouldHideExternallyReferencedLabel_ExternalGoto_ReturnsTrue()
    {
        var method = CSharpSyntaxTree.ParseText("""
            class Loop
            {
                void Run(bool condition)
                {
                    goto retry;
                    if (condition)
                        retry: Work();
                }
                static void Work() {}
            }
            """).GetRoot()
            .DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Single();

        Assert.True(GotoLabelHelpers.WouldHideExternallyReferencedLabel(method.Statement));
    }

    [Fact]
    public void WouldHideExternallyReferencedLabel_GotoInsideBody_ReturnsFalse()
    {
        var ifStatement = CSharpSyntaxTree.ParseText("""
            class Loop
            {
                void Run(bool condition)
                {
                    if (condition)
                    {
                        goto retry;
                        retry: Work();
                    }
                }
                static void Work() {}
            }
            """).GetRoot()
            .DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Single();

        var body = Assert.IsType<BlockSyntax>(ifStatement.Statement);
        Assert.False(GotoLabelHelpers.WouldHideExternallyReferencedLabel(body));
    }

    private static LabeledStatementSyntax ParseLabeled(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return tree.GetRoot().DescendantNodes().OfType<LabeledStatementSyntax>().Single();
    }

    private static GotoStatementSyntax ParseGoto(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return tree.GetRoot().DescendantNodes().OfType<GotoStatementSyntax>().Single();
    }
}
