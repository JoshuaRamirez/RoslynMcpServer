using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class MethodRematchHelpersTests
{
    [Fact]
    public void RematchInvocation_FindsInvocation_ViaFindNodeAndAncestors()
    {
        var root = Parse("""
            class C
            {
                void M()
                {
                    Helper(1, 2);
                }

                void Helper(int a, int b) { }
            }
            """);
        var invocation = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();

        var rematched = MethodRematchHelpers.RematchInvocation(root, invocation.Span);

        Assert.Same(invocation, rematched);
    }

    [Fact]
    public void RematchInvocation_FindsInvocation_WhenSpanIsInnerArgumentOnly()
    {
        var root = Parse("""
            class C
            {
                void M()
                {
                    Helper(1, 2);
                }

                void Helper(int a, int b) { }
            }
            """);
        var invocation = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var argument = invocation.ArgumentList.Arguments[0];

        var rematched = MethodRematchHelpers.RematchInvocation(root, argument.Span);

        Assert.Same(invocation, rematched);
    }

    [Fact]
    public void RematchInvocation_UsesOverlapFallback_WhenFindNodeMissesInvocation()
    {
        var root = Parse("""
            class C
            {
                void M()
                {
                    Helper();
                    Other();
                }

                void Helper() { }
                void Other() { }
            }
            """);
        var helper = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(i => i.Expression is IdentifierNameSyntax { Identifier.Text: "Helper" });

        // Span that overlaps Helper's span but is not nested inside FindNode's
        // ancestor chain starting at an unrelated token (shift one past start).
        var overlapping = TextSpan.FromBounds(helper.SpanStart + 1, helper.Span.End + 1);
        Assert.True(overlapping.End <= root.FullSpan.End || overlapping.OverlapsWith(helper.Span));

        var rematched = MethodRematchHelpers.RematchInvocation(root, overlapping);

        Assert.NotNull(rematched);
        Assert.Equal("Helper", ((IdentifierNameSyntax)rematched!.Expression).Identifier.Text);
    }

    [Fact]
    public void RematchInvocation_OutOfRange_PicksNearestBySpanStartThenLength()
    {
        var root = Parse("""
            class C
            {
                void M()
                {
                    A();
                    BBB();
                }

                void A() { }
                void BBB() { }
            }
            """);
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        Assert.Equal(2, invocations.Count);

        // Past end of root → out-of-range branch; nearest to a large start is BBB.
        var pastEnd = new TextSpan(root.FullSpan.End + 10, 1);
        var rematched = MethodRematchHelpers.RematchInvocation(root, pastEnd);

        Assert.NotNull(rematched);
        Assert.Equal("BBB", ((IdentifierNameSyntax)rematched!.Expression).Identifier.Text);
    }

    [Fact]
    public void RematchInvocation_ReturnsNull_WhenNoInvocationsAndOutOfRange()
    {
        var root = Parse("class C { int X => 1; }");
        var pastEnd = new TextSpan(root.FullSpan.End + 5, 1);

        Assert.Null(MethodRematchHelpers.RematchInvocation(root, pastEnd));
    }

    [Fact]
    public void RematchMethod_FindsSingleMethodByIdentifier()
    {
        var root = Parse("""
            class C
            {
                void Target() { }
                void Other() { }
            }
            """);
        var original = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "Target");

        var rematched = MethodRematchHelpers.RematchMethod(root, original);

        Assert.Same(original, rematched);
    }

    [Fact]
    public void RematchMethod_DisambiguatesSameName_ByClosestSpanStart()
    {
        var root = Parse("""
            class C
            {
                void Target(int x) { }
                void Target(string s) { }
            }
            """);
        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == "Target")
            .OrderBy(m => m.SpanStart)
            .ToList();
        Assert.Equal(2, methods.Count);

        var second = methods[1];
        var rematched = MethodRematchHelpers.RematchMethod(root, second);

        Assert.Same(second, rematched);
    }

    private static CompilationUnitSyntax Parse(string source) =>
        (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source).GetRoot();
}
