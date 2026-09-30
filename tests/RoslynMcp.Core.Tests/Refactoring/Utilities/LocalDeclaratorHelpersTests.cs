using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="LocalDeclaratorHelpers"/> —
/// collect / filter helpers previously duplicated on
/// InlineVariable / IntroduceParameter.
/// </summary>
public class LocalDeclaratorHelpersTests
{
    [Fact]
    public void CollectLocalDeclarators_IncludesOrdinaryLocals_InSpanOrder()
    {
        var root = Parse("""
            class C
            {
                void M()
                {
                    var later = 2;
                    var earlier = 1;
                }
            }
            """);

        var collected = LocalDeclaratorHelpers.CollectLocalDeclarators(root);

        Assert.Equal(new[] { "later", "earlier" }, collected.Select(d => d.Identifier.ValueText));
        Assert.True(collected[0].SpanStart < collected[1].SpanStart);
    }

    [Fact]
    public void CollectLocalDeclarators_ExcludesFieldsForLoopsAndUsingDeclarations()
    {
        var root = Parse("""
            class C
            {
                int field = 1;

                void M()
                {
                    var local = 2;
                    using var stream = new System.IO.MemoryStream();
                    for (int i = 0; i < 1; i++)
                    {
                    }
                }
            }
            """);

        var collected = LocalDeclaratorHelpers.CollectLocalDeclarators(root);

        Assert.Equal(new[] { "local" }, collected.Select(d => d.Identifier.ValueText));
    }

    [Fact]
    public void IsLocalDeclarator_True_ForOrdinaryLocal()
    {
        var root = Parse("""
            class C
            {
                void M()
                {
                    var value = 1;
                }
            }
            """);
        var declarator = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();

        Assert.True(LocalDeclaratorHelpers.IsLocalDeclarator(declarator));
    }

    [Fact]
    public void IsLocalDeclarator_False_ForUsingVarAndField()
    {
        var usingRoot = Parse("""
            class C
            {
                void M()
                {
                    using var stream = new System.IO.MemoryStream();
                }
            }
            """);
        var fieldRoot = Parse("""
            class C
            {
                int field = 1;
            }
            """);

        Assert.False(LocalDeclaratorHelpers.IsLocalDeclarator(
            usingRoot.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single()));
        Assert.False(LocalDeclaratorHelpers.IsLocalDeclarator(
            fieldRoot.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single()));
    }

    private static CompilationUnitSyntax Parse(string source) =>
        (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source).GetRoot();
}
