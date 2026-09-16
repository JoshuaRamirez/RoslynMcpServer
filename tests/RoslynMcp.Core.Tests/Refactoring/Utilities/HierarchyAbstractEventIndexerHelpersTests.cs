using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class HierarchyAbstractEventIndexerHelpersTests
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

    private static SyntaxTokenList ProtectedAbstract() =>
        SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.ProtectedKeyword),
            SyntaxFactory.Token(SyntaxKind.AbstractKeyword));

    [Fact]
    public void CanMakeIndexerAbstract_True_ForOrdinaryIndexer()
    {
        var indexer = ParseMember<IndexerDeclarationSyntax>("public int this[int i] { get => i; set { } }");
        Assert.True(HierarchyAbstractEventIndexerHelpers.CanMakeIndexerAbstract(indexer));
    }

    [Fact]
    public void CanMakeIndexerAbstract_False_WhenStatic()
    {
        var indexer = ParseMember<IndexerDeclarationSyntax>("public int this[int i] { get; set; }")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)));
        Assert.False(HierarchyAbstractEventIndexerHelpers.CanMakeIndexerAbstract(indexer));
    }

    [Fact]
    public void CanMakeIndexerAbstract_False_WhenExplicitInterface()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            interface I { int this[int i] { get; } }
            class C : I { int I.this[int i] => i; }
            """);
        var indexer = tree.GetRoot().DescendantNodes().OfType<IndexerDeclarationSyntax>()
            .Single(i => i.ExplicitInterfaceSpecifier != null);
        Assert.False(HierarchyAbstractEventIndexerHelpers.CanMakeIndexerAbstract(indexer));
    }

    [Fact]
    public void CanMakeIndexerAbstract_False_WhenPrivateOnlyAccessor()
    {
        var indexer = ParseMember<IndexerDeclarationSyntax>(
            "public int this[int i] { get; private set; }");
        Assert.False(HierarchyAbstractEventIndexerHelpers.CanMakeIndexerAbstract(indexer));
    }

    [Fact]
    public void CanMakeEventAbstract_True_ForEventDeclaration()
    {
        var evt = ParseMember<EventDeclarationSyntax>("public event System.Action E { add { } remove { } }");
        Assert.True(HierarchyAbstractEventIndexerHelpers.CanMakeEventAbstract(evt));
    }

    [Fact]
    public void CanMakeEventAbstract_False_WhenEventDeclarationStatic()
    {
        var evt = ParseMember<EventDeclarationSyntax>("public static event System.Action E { add { } remove { } }");
        Assert.False(HierarchyAbstractEventIndexerHelpers.CanMakeEventAbstract(evt));
    }

    [Fact]
    public void CanMakeEventAbstract_False_WhenEventDeclarationExplicitInterface()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            interface I { event System.Action E; }
            class C : I { event System.Action I.E { add { } remove { } } }
            """);
        var evt = tree.GetRoot().DescendantNodes().OfType<EventDeclarationSyntax>().Single();
        Assert.False(HierarchyAbstractEventIndexerHelpers.CanMakeEventAbstract(evt));
    }

    [Fact]
    public void CanMakeEventAbstract_True_ForEventField()
    {
        var field = ParseMember<EventFieldDeclarationSyntax>("public event System.Action E;");
        Assert.True(HierarchyAbstractEventIndexerHelpers.CanMakeEventAbstract(field));
    }

    [Fact]
    public void CanMakeEventAbstract_False_WhenEventFieldStatic()
    {
        var field = ParseMember<EventFieldDeclarationSyntax>("public static event System.Action E;");
        Assert.False(HierarchyAbstractEventIndexerHelpers.CanMakeEventAbstract(field));
    }

    [Fact]
    public void ToAbstractEvent_EventDeclaration_DropsAccessorsAndUsesSuppliedModifiers()
    {
        var evt = ParseMember<EventDeclarationSyntax>("public virtual event System.Action E { add { } remove { } }");
        var abstractEvt = HierarchyAbstractEventIndexerHelpers.ToAbstractEvent(evt, ProtectedAbstract());

        Assert.True(abstractEvt.Modifiers.Any(SyntaxKind.ProtectedKeyword));
        Assert.True(abstractEvt.Modifiers.Any(SyntaxKind.AbstractKeyword));
        Assert.False(abstractEvt.Modifiers.Any(SyntaxKind.PublicKeyword));
        Assert.False(abstractEvt.Modifiers.Any(SyntaxKind.VirtualKeyword));
        Assert.Null(abstractEvt.AccessorList);
        Assert.False(abstractEvt.SemicolonToken.IsKind(SyntaxKind.None));
        Assert.Equal("E", abstractEvt.Identifier.Text);
    }

    [Fact]
    public void ToAbstractEvent_EventField_UsesFirstVariableAndSuppliedModifiers()
    {
        var field = ParseMember<EventFieldDeclarationSyntax>("public event System.Action A, B;");
        var abstractEvt = HierarchyAbstractEventIndexerHelpers.ToAbstractEvent(field, ProtectedAbstract());

        Assert.Equal("A", abstractEvt.Identifier.Text);
        Assert.True(abstractEvt.Modifiers.Any(SyntaxKind.ProtectedKeyword));
        Assert.True(abstractEvt.Modifiers.Any(SyntaxKind.AbstractKeyword));
        Assert.Null(abstractEvt.AccessorList);
        Assert.False(abstractEvt.SemicolonToken.IsKind(SyntaxKind.None));
    }

    [Fact]
    public void ToAbstractIndexer_ConvertsAccessorsToSemicolonBodies()
    {
        var indexer = ParseMember<IndexerDeclarationSyntax>(
            "public virtual int this[int i] { get => i; set { _ = value; } }");
        var abstractIndexer = HierarchyAbstractEventIndexerHelpers.ToAbstractIndexer(indexer, ProtectedAbstract());

        Assert.True(abstractIndexer.Modifiers.Any(SyntaxKind.ProtectedKeyword));
        Assert.True(abstractIndexer.Modifiers.Any(SyntaxKind.AbstractKeyword));
        Assert.False(abstractIndexer.Modifiers.Any(SyntaxKind.VirtualKeyword));
        Assert.Null(abstractIndexer.ExpressionBody);
        Assert.NotNull(abstractIndexer.AccessorList);
        Assert.Equal(2, abstractIndexer.AccessorList!.Accessors.Count);
        Assert.All(abstractIndexer.AccessorList.Accessors, a =>
        {
            Assert.Null(a.Body);
            Assert.Null(a.ExpressionBody);
            Assert.False(a.SemicolonToken.IsKind(SyntaxKind.None));
        });
    }

    [Fact]
    public void ToAbstractIndexer_WhenNoAccessorList_AddsGetAccessor()
    {
        var indexer = SyntaxFactory.IndexerDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(SyntaxFactory.BracketedParameterList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("i"))
                        .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword))))));

        Assert.Null(indexer.AccessorList);
        var abstractIndexer = HierarchyAbstractEventIndexerHelpers.ToAbstractIndexer(indexer, ProtectedAbstract());
        Assert.NotNull(abstractIndexer.AccessorList);
        Assert.Single(abstractIndexer.AccessorList!.Accessors);
        Assert.True(abstractIndexer.AccessorList.Accessors[0].IsKind(SyntaxKind.GetAccessorDeclaration));
    }
}
