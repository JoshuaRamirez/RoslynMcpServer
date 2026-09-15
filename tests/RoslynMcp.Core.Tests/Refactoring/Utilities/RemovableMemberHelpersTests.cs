using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="RemovableMemberHelpers.AsRemovableMember"/> —
/// declaring-syntax → removable member mapping previously duplicated on
/// ImplementInterface / ImplementAbstract.
/// </summary>
public class RemovableMemberHelpersTests
{
    [Fact]
    public void AsRemovableMember_DirectMethod_ReturnsMethod()
    {
        var method = SyntaxFactory.ParseCompilationUnit(
            "class C { void M() { } }").DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        var result = RemovableMemberHelpers.AsRemovableMember(method);

        Assert.Same(method, result);
    }

    [Fact]
    public void AsRemovableMember_DirectProperty_ReturnsProperty()
    {
        var property = SyntaxFactory.ParseCompilationUnit(
            "class C { int P { get; set; } }").DescendantNodes().OfType<PropertyDeclarationSyntax>().Single();

        var result = RemovableMemberHelpers.AsRemovableMember(property);

        Assert.Same(property, result);
    }

    [Fact]
    public void AsRemovableMember_DirectIndexer_ReturnsIndexer()
    {
        var indexer = SyntaxFactory.ParseCompilationUnit(
            "class C { int this[int i] => i; }").DescendantNodes().OfType<IndexerDeclarationSyntax>().Single();

        var result = RemovableMemberHelpers.AsRemovableMember(indexer);

        Assert.Same(indexer, result);
    }

    [Fact]
    public void AsRemovableMember_DirectEvent_ReturnsEvent()
    {
        var evt = SyntaxFactory.ParseCompilationUnit(
            "class C { event System.EventHandler E { add { } remove { } } }")
            .DescendantNodes().OfType<EventDeclarationSyntax>().Single();

        var result = RemovableMemberHelpers.AsRemovableMember(evt);

        Assert.Same(evt, result);
    }

    [Fact]
    public void AsRemovableMember_DirectEventField_ReturnsEventField()
    {
        var eventField = SyntaxFactory.ParseCompilationUnit(
            "class C { event System.EventHandler E; }")
            .DescendantNodes().OfType<EventFieldDeclarationSyntax>().Single();

        var result = RemovableMemberHelpers.AsRemovableMember(eventField);

        Assert.Same(eventField, result);
    }

    [Fact]
    public void AsRemovableMember_NestedStatementInMethod_ReturnsAncestorMethod()
    {
        var tree = SyntaxFactory.ParseCompilationUnit("class C { void M() { int x = 1; } }");
        var localDecl = tree.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().Single();
        var method = tree.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        var result = RemovableMemberHelpers.AsRemovableMember(localDecl);

        Assert.Same(method, result);
    }

    [Fact]
    public void AsRemovableMember_TypeDeclaration_ReturnsNull()
    {
        var type = SyntaxFactory.ParseCompilationUnit(
            "class C { }").DescendantNodes().OfType<ClassDeclarationSyntax>().Single();

        var result = RemovableMemberHelpers.AsRemovableMember(type);

        Assert.Null(result);
    }

    [Fact]
    public void AsRemovableMember_FieldDeclaration_ReturnsNull()
    {
        var field = SyntaxFactory.ParseCompilationUnit(
            "class C { int f; }").DescendantNodes().OfType<FieldDeclarationSyntax>().Single();

        var result = RemovableMemberHelpers.AsRemovableMember(field);

        Assert.Null(result);
    }
}
