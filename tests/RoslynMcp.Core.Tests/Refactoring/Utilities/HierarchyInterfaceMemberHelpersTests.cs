using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class HierarchyInterfaceMemberHelpersTests
{
    private static T ParseMember<T>(string typeBody) where T : MemberDeclarationSyntax
    {
        var tree = CSharpSyntaxTree.ParseText($$"""
            class C
            {
                {{typeBody}}
            }
            """);
        return tree.GetRoot().DescendantNodes().OfType<T>().Single();
    }

    private static void AssertSignatureOnly(AccessorDeclarationSyntax accessor)
    {
        Assert.Empty(accessor.Modifiers);
        Assert.Null(accessor.Body);
        Assert.Null(accessor.ExpressionBody);
        Assert.Equal(SyntaxKind.SemicolonToken, accessor.SemicolonToken.Kind());
    }

    [Fact]
    public void ToSignatureOnlyAccessors_NullList_YieldsSingleGet()
    {
        var accessors = HierarchyInterfaceMemberHelpers.ToSignatureOnlyAccessors(null, skipNonPublic: true);

        var accessor = Assert.Single(accessors);
        Assert.Equal(SyntaxKind.GetAccessorDeclaration, accessor.Kind());
        Assert.Equal("get;", accessor.ToString());
    }

    [Fact]
    public void ToSignatureOnlyAccessors_StripsBodiesAndModifiers_WhenNotSkipping()
    {
        var property = ParseMember<PropertyDeclarationSyntax>(
            "public int P { get { return 1; } private set => _ = value; }");

        var accessors = HierarchyInterfaceMemberHelpers.ToSignatureOnlyAccessors(property.AccessorList, skipNonPublic: false);

        Assert.Equal(new[] { "get", "set" }, accessors.Select(a => a.Keyword.Text));
        Assert.All(accessors, AssertSignatureOnly);
    }

    [Fact]
    public void ToSignatureOnlyAccessors_SkipsNonPublic_WhenRequested()
    {
        var indexer = ParseMember<IndexerDeclarationSyntax>(
            "public int this[int i] { get => i; protected set { } }");

        var accessors = HierarchyInterfaceMemberHelpers.ToSignatureOnlyAccessors(indexer.AccessorList, skipNonPublic: true);

        Assert.Equal(new[] { "get" }, accessors.Select(a => a.Keyword.Text));
        Assert.All(accessors, AssertSignatureOnly);
    }

    [Fact]
    public void ToSignatureOnlyInterfaceProperty_AutoProperty()
    {
        var property = ParseMember<PropertyDeclarationSyntax>("public virtual int P { get; set; }");

        var result = HierarchyInterfaceMemberHelpers.ToSignatureOnlyInterfaceProperty(property);

        Assert.Equal("int P { get; set; }", result.ToFullString());
    }

    [Fact]
    public void ToSignatureOnlyInterfaceProperty_KeepsNonPublicAccessorAsPublicSignature()
    {
        var property = ParseMember<PropertyDeclarationSyntax>("public int P { get; private set; }");

        var result = HierarchyInterfaceMemberHelpers.ToSignatureOnlyInterfaceProperty(property);

        Assert.Equal("int P { get; set; }", result.ToFullString());
    }

    [Fact]
    public void ToSignatureOnlyInterfaceProperty_ExpressionBodied_BecomesGetOnly()
    {
        var property = ParseMember<PropertyDeclarationSyntax>("public int P => 42;");

        var result = HierarchyInterfaceMemberHelpers.ToSignatureOnlyInterfaceProperty(property);

        Assert.Null(result.ExpressionBody);
        Assert.Equal("int P { get; }", result.ToFullString());
    }

    [Fact]
    public void ToSignatureOnlyInterfaceIndexer_DropsNonPublicAccessor()
    {
        var indexer = ParseMember<IndexerDeclarationSyntax>(
            "public int this[int i] { get => i; private set { } }");

        var result = HierarchyInterfaceMemberHelpers.ToSignatureOnlyInterfaceIndexer(indexer, "unused");

        Assert.Equal("int this[int i] { get; }", result.ToFullString());
    }

    [Fact]
    public void ToSignatureOnlyInterfaceIndexer_ExpressionBodied_BecomesGetOnly()
    {
        var indexer = ParseMember<IndexerDeclarationSyntax>("public int this[int i] => i;");

        var result = HierarchyInterfaceMemberHelpers.ToSignatureOnlyInterfaceIndexer(indexer, "unused");

        Assert.Null(result.ExpressionBody);
        Assert.Equal("int this[int i] { get; }", result.ToFullString());
    }

    [Fact]
    public void ToSignatureOnlyInterfaceIndexer_StripsExplicitInterfaceSpecifier()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            interface I { int this[int i] { get; } }
            class C : I { int I.this[int i] { get => i; } }
            """);
        var indexer = tree.GetRoot().DescendantNodes().OfType<IndexerDeclarationSyntax>()
            .Single(i => i.ExplicitInterfaceSpecifier != null);

        var result = HierarchyInterfaceMemberHelpers.ToSignatureOnlyInterfaceIndexer(indexer, "unused");

        Assert.Null(result.ExplicitInterfaceSpecifier);
        Assert.Equal("int this[int i] { get; }", result.ToFullString());
    }

    [Fact]
    public void ToSignatureOnlyInterfaceIndexer_NoPublicAccessors_ThrowsWithPassedMessage()
    {
        var indexer = ParseMember<IndexerDeclarationSyntax>(
            "public int this[int i] { private get => i; protected set { } }");

        var ex = Assert.Throws<RefactoringException>(() =>
            HierarchyInterfaceMemberHelpers.ToSignatureOnlyInterfaceIndexer(indexer, "custom message"));

        Assert.Equal(ErrorCodes.MemberNotInterfaceCompatible, ex.ErrorCode);
        Assert.Equal("custom message", ex.Message);
    }
}
