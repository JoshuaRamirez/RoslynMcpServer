using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Resolution;
using Xunit;

namespace RoslynMcp.Core.Tests.Resolution;

public class TypeCoverageTests
{
    [Fact]
    public void GetTypeIdentifier_ReturnsClassAndDelegateIdentifiers_DefaultForMethod()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C { void M() { } }
            delegate void D();
            """);
        var root = tree.GetRoot();
        var type = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        var del = root.DescendantNodes().OfType<DelegateDeclarationSyntax>().Single();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        Assert.Equal("C", TypeCoverage.GetTypeIdentifier(type).ValueText);
        Assert.Equal("D", TypeCoverage.GetTypeIdentifier(del).ValueText);
        Assert.Equal(default, TypeCoverage.GetTypeIdentifier(method));
    }

    [Fact]
    public void TypeCoversLine_True_OnIdentifierLine_False_Outside()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M() { }
            }
            """);
        var root = tree.GetRoot();
        var type = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        var idLine = type.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var methodLine = method.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        Assert.True(TypeCoverage.TypeCoversLine(type, idLine));
        Assert.True(TypeCoverage.IdentifierCoversLine(type, idLine));
        // Method line is still inside the type span.
        Assert.True(TypeCoverage.TypeCoversLine(type, methodLine));
        Assert.False(TypeCoverage.IdentifierCoversLine(type, methodLine));
        Assert.False(TypeCoverage.TypeCoversLine(type, 0));
    }

    [Fact]
    public void TypeCoversColumn_True_OnIdentifier_False_AtExclusiveEnd()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C { }
            """);
        var root = tree.GetRoot();
        var type = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        var idSpan = type.Identifier.GetLocation().GetLineSpan();
        var line = idSpan.StartLinePosition.Line + 1;
        var startCol = idSpan.StartLinePosition.Character + 1;
        var endCol = idSpan.EndLinePosition.Character + 1;

        Assert.True(TypeCoverage.TypeCoversColumn(type, line, startCol));
        Assert.True(TypeCoverage.IdentifierCoversColumn(type, line, startCol));
        Assert.True(TypeCoverage.IdentifierCoversColumn(type, line, endCol - 1));
        Assert.False(TypeCoverage.IdentifierCoversColumn(type, line, endCol));
        Assert.False(TypeCoverage.IdentifierCoversColumn(type, line, startCol - 1));
    }
}
