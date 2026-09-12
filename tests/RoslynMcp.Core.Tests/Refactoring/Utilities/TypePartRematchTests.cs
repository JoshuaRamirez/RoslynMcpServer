using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class TypePartRematchTests
{
    [Fact]
    public void SameSyntaxTree_True_ForSameReference()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { }");
        Assert.True(TypePartRematch.SameSyntaxTree(tree, tree));
    }

    [Fact]
    public void SameSyntaxTree_True_ForMatchingFilePathIgnoreCase()
    {
        var left = CSharpSyntaxTree.ParseText("class C { }", path: "A.cs");
        var right = CSharpSyntaxTree.ParseText("class C { }", path: "a.cs");
        Assert.True(TypePartRematch.SameSyntaxTree(left, right));
    }

    [Fact]
    public void SameSyntaxTree_False_ForDifferentPaths()
    {
        var left = CSharpSyntaxTree.ParseText("class C { }", path: "A.cs");
        var right = CSharpSyntaxTree.ParseText("class C { }", path: "B.cs");
        Assert.False(TypePartRematch.SameSyntaxTree(left, right));
    }

    [Fact]
    public void RematchTypeDeclaration_FindsBySpanStartAndName()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class A { }
            class B { }
            """);
        var root = tree.GetRoot();
        var original = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Single(t => t.Identifier.Text == "A");

        var rematched = TypePartRematch.RematchTypeDeclaration(root, original);

        Assert.NotNull(rematched);
        Assert.Same(original, rematched);
        Assert.Equal("A", rematched!.Identifier.Text);
    }

    [Fact]
    public void RematchTypeDeclaration_Null_WhenNameMismatch()
    {
        var originalTree = CSharpSyntaxTree.ParseText("class A { } class B { }");
        var original = originalTree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Single(t => t.Identifier.Text == "A");

        var otherRoot = CSharpSyntaxTree.ParseText("class B { }").GetRoot();

        Assert.Null(TypePartRematch.RematchTypeDeclaration(otherRoot, original));
    }

    [Fact]
    public void RematchTypeDeclaration_PrefersMatchingSpanStartAndName()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Outer
            {
                class Inner { }
            }
            class Inner { }
            """);
        var root = tree.GetRoot();
        var nested = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Single(t => t.Identifier.Text == "Inner" && t.Parent is TypeDeclarationSyntax);
        var topLevel = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Single(t => t.Identifier.Text == "Inner" && t.Parent is not TypeDeclarationSyntax);

        Assert.Same(nested, TypePartRematch.RematchTypeDeclaration(root, nested));
        Assert.Same(topLevel, TypePartRematch.RematchTypeDeclaration(root, topLevel));
        Assert.NotSame(nested, topLevel);
    }
}
