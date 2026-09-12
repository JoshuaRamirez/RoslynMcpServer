using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class EventFieldDeclaratorTests
{
    [Fact]
    public void TryGet_True_ForVariableDeclaratorUnderEventField()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                event System.Action E;
            }
            """);
        var declarator = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();
        var field = tree.GetRoot().DescendantNodes().OfType<EventFieldDeclarationSyntax>().Single();

        Assert.True(EventFieldDeclarator.TryGet(declarator, out var eventField, out var matched));
        Assert.Same(field, eventField);
        Assert.Same(declarator, matched);
        Assert.Equal("E", matched.Identifier.Text);
    }

    [Fact]
    public void TryGet_False_ForVariableDeclaratorUnderFieldDeclaration()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                int f;
            }
            """);
        var declarator = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();

        Assert.False(EventFieldDeclarator.TryGet(declarator, out var eventField, out var matched));
        Assert.Null(eventField);
        Assert.Null(matched);
    }

    [Fact]
    public void TryGet_False_ForOtherSyntaxNode()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                event System.Action E { add { } remove { } }
                void M() { }
            }
            """);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var eventDecl = tree.GetRoot().DescendantNodes().OfType<EventDeclarationSyntax>().Single();

        Assert.False(EventFieldDeclarator.TryGet(method, out _, out _));
        Assert.False(EventFieldDeclarator.TryGet(eventDecl, out _, out _));
    }

    [Fact]
    public void TryGet_True_ForMatchingDeclaratorInMultiVariableEventField()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                event System.Action A, B;
            }
            """);
        var field = tree.GetRoot().DescendantNodes().OfType<EventFieldDeclarationSyntax>().Single();
        var a = field.Declaration.Variables[0];
        var b = field.Declaration.Variables[1];

        Assert.True(EventFieldDeclarator.TryGet(a, out var fieldForA, out var matchedA));
        Assert.Same(field, fieldForA);
        Assert.Same(a, matchedA);
        Assert.Equal("A", matchedA.Identifier.Text);

        Assert.True(EventFieldDeclarator.TryGet(b, out var fieldForB, out var matchedB));
        Assert.Same(field, fieldForB);
        Assert.Same(b, matchedB);
        Assert.Equal("B", matchedB.Identifier.Text);
    }
}
