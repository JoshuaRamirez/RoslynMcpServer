using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class StaticMethodHelpersTests
{
    [Fact]
    public void PlanConflictsWithClaimedSpans_False_WhenClaimedEmpty()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { void M() { } }");
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        var claimed = new HashSet<(SyntaxTree Tree, TextSpan Span)>();
        var declarations = new[] { (tree, method.Span) };
        var callSites = Array.Empty<(SyntaxTree, TextSpan)>();

        Assert.False(StaticMethodHelpers.PlanConflictsWithClaimedSpans(declarations, callSites, claimed));
    }

    [Fact]
    public void PlanConflictsWithClaimedSpans_True_WhenDeclarationAlreadyClaimed()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { void M() { } void N() { } }");
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var m = methods.Single(x => x.Identifier.Text == "M");
        var n = methods.Single(x => x.Identifier.Text == "N");

        var claimed = new HashSet<(SyntaxTree Tree, TextSpan Span)> { (tree, m.Span) };
        var declarations = new[] { (tree, m.Span), (tree, n.Span) };
        var callSites = Array.Empty<(SyntaxTree, TextSpan)>();

        Assert.True(StaticMethodHelpers.PlanConflictsWithClaimedSpans(declarations, callSites, claimed));
    }

    [Fact]
    public void PlanConflictsWithClaimedSpans_True_WhenCallSiteAlreadyClaimed()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { void M() { N(); } void N() { } }");
        var root = tree.GetRoot();
        var m = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(x => x.Identifier.Text == "M");
        var invocation = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();

        var claimed = new HashSet<(SyntaxTree Tree, TextSpan Span)> { (tree, invocation.Span) };
        var declarations = new[] { (tree, m.Span) };
        var callSites = new[] { (tree, invocation.Span) };

        Assert.True(StaticMethodHelpers.PlanConflictsWithClaimedSpans(declarations, callSites, claimed));
    }

    [Fact]
    public void PlanConflictsWithClaimedSpans_False_WhenSpansDistinct()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { void M() { N(); } void N() { } }");
        var root = tree.GetRoot();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var m = methods.Single(x => x.Identifier.Text == "M");
        var n = methods.Single(x => x.Identifier.Text == "N");
        var invocation = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();

        var claimed = new HashSet<(SyntaxTree Tree, TextSpan Span)> { (tree, n.Span) };
        var declarations = new[] { (tree, m.Span) };
        var callSites = new[] { (tree, invocation.Span) };

        Assert.False(StaticMethodHelpers.PlanConflictsWithClaimedSpans(declarations, callSites, claimed));
    }

    [Fact]
    public void PlanConflictsWithClaimedSpans_False_WhenSameSpanDifferentTree()
    {
        var left = CSharpSyntaxTree.ParseText("class C { void M() { } }", path: "A.cs");
        var right = CSharpSyntaxTree.ParseText("class C { void M() { } }", path: "B.cs");
        var leftMethod = left.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var rightMethod = right.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        // Same TextSpan offsets, different SyntaxTree — must not conflict.
        Assert.Equal(leftMethod.Span, rightMethod.Span);

        var claimed = new HashSet<(SyntaxTree Tree, TextSpan Span)> { (left, leftMethod.Span) };
        var declarations = new[] { (right, rightMethod.Span) };
        var callSites = Array.Empty<(SyntaxTree, TextSpan)>();

        Assert.False(StaticMethodHelpers.PlanConflictsWithClaimedSpans(declarations, callSites, claimed));
    }
}
