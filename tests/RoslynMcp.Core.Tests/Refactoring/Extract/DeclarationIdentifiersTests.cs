using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Refactoring.Extract;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

public class DeclarationIdentifiersTests
{
    [Fact]
    public void GetDeclarationIdentifier_ReturnsExpectedTokens()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                int f;
                int P { get; set; }
                event System.Action E { add { } remove { } }
                C() { }
                ~C() { }
                void M(int p) { void Local() { } }
                public static C operator +(C a, C b) => a;
                public static explicit operator int(C c) => 0;
            }
            """);
        var root = tree.GetRoot();

        var type = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        Assert.Equal("C", DeclarationIdentifiers.GetDeclarationIdentifier(type)!.Value.ValueText);

        var field = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(v => v.Identifier.Text == "f");
        Assert.Equal("f", DeclarationIdentifiers.GetDeclarationIdentifier(field)!.Value.ValueText);

        var property = root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Single();
        Assert.Equal("P", DeclarationIdentifiers.GetDeclarationIdentifier(property)!.Value.ValueText);

        var @event = root.DescendantNodes().OfType<EventDeclarationSyntax>().Single();
        Assert.Equal("E", DeclarationIdentifiers.GetDeclarationIdentifier(@event)!.Value.ValueText);

        var ctor = root.DescendantNodes().OfType<ConstructorDeclarationSyntax>().Single();
        Assert.Equal("C", DeclarationIdentifiers.GetDeclarationIdentifier(ctor)!.Value.ValueText);

        var dtor = root.DescendantNodes().OfType<DestructorDeclarationSyntax>().Single();
        Assert.Equal("C", DeclarationIdentifiers.GetDeclarationIdentifier(dtor)!.Value.ValueText);

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        Assert.Equal("M", DeclarationIdentifiers.GetDeclarationIdentifier(method)!.Value.ValueText);

        var parameter = method.ParameterList.Parameters.Single();
        Assert.Equal("p", DeclarationIdentifiers.GetDeclarationIdentifier(parameter)!.Value.ValueText);

        var local = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single();
        Assert.Equal("Local", DeclarationIdentifiers.GetDeclarationIdentifier(local)!.Value.ValueText);

        var op = root.DescendantNodes().OfType<OperatorDeclarationSyntax>().Single();
        Assert.Equal(op.OperatorToken, DeclarationIdentifiers.GetDeclarationIdentifier(op));

        var conversion = root.DescendantNodes().OfType<ConversionOperatorDeclarationSyntax>().Single();
        Assert.Equal(conversion.Type.GetLastToken(), DeclarationIdentifiers.GetDeclarationIdentifier(conversion));
    }

    [Fact]
    public void GetDeclarationIdentifier_Null_ForUnsupportedNode()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { void M() { int x = 1; } }");
        var literal = tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>().Single();
        Assert.Null(DeclarationIdentifiers.GetDeclarationIdentifier(literal));
    }

    [Fact]
    public void IdentifierOverlaps_True_OnIdentifier_False_Outside()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M() { }
            }
            """);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var idSpan = method.Identifier.Span;
        var bodySpan = method.Body!.Span;

        Assert.True(DeclarationIdentifiers.IdentifierOverlaps(method, idSpan));
        Assert.False(DeclarationIdentifiers.IdentifierOverlaps(method, bodySpan));
        Assert.False(DeclarationIdentifiers.IdentifierOverlaps(method, new TextSpan(bodySpan.End, 0)));
    }
}
