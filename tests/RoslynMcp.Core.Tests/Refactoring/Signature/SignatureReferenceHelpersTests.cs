using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

public class SignatureReferenceHelpersTests
{
    [Fact]
    public void IsDeclarationName_True_WhenSpanHitsMethodIdentifier()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M() { }
            }
            """);
        var root = tree.GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var idSpan = method.Identifier.Span;

        Assert.True(SignatureReferenceHelpers.IsDeclarationName(method, idSpan));
        // Body node still walks ancestors to the method identifier.
        Assert.True(SignatureReferenceHelpers.IsDeclarationName(method.Body!, idSpan));
    }

    [Fact]
    public void IsDeclarationName_False_WhenSpanMissesMethodIdentifier()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M() { int x = 1; }
            }
            """);
        var root = tree.GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var local = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();

        Assert.False(SignatureReferenceHelpers.IsDeclarationName(local, local.Identifier.Span));
        Assert.False(SignatureReferenceHelpers.IsDeclarationName(method, local.Identifier.Span));
    }

    [Fact]
    public void IsInvokedMethodName_True_OnIdentifierAndMemberName_False_InsideArgs()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
                    Foo(1);
                    this.Bar(2);
                }
            }
            """);
        var root = tree.GetRoot();
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        Assert.Equal(2, invocations.Count);

        var foo = invocations[0];
        var fooName = ((IdentifierNameSyntax)foo.Expression).Identifier;
        Assert.True(SignatureReferenceHelpers.IsInvokedMethodName(foo, fooName.Span));
        var fooArg = foo.ArgumentList.Arguments[0];
        Assert.False(SignatureReferenceHelpers.IsInvokedMethodName(foo, fooArg.Span));

        var bar = invocations[1];
        var barName = ((MemberAccessExpressionSyntax)bar.Expression).Name;
        Assert.True(SignatureReferenceHelpers.IsInvokedMethodName(bar, barName.Span));
        var barArg = bar.ArgumentList.Arguments[0];
        Assert.False(SignatureReferenceHelpers.IsInvokedMethodName(bar, barArg.Span));
    }

    [Fact]
    public void IsNameOfArgument_True_InsideNameof_False_Otherwise()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
                    var a = nameof(C);
                    var b = Foo(C);
                }
            }
            """);
        var root = tree.GetRoot();
        var nameOfId = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(n => n.Identifier.Text == "C" && n.Parent is ArgumentSyntax);
        var otherId = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(n => n.Identifier.Text == "C" && n.Parent is ArgumentSyntax);

        Assert.True(SignatureReferenceHelpers.IsNameOfArgument(nameOfId));
        Assert.False(SignatureReferenceHelpers.IsNameOfArgument(otherId));
    }
}
