using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="NamespaceNameHelpers"/> —
/// GetFullNamespaceName nested join / null, GetContainingNamespaceName
/// syntax enclosing ns, and SemanticModel symbol path vs syntax fallback.
/// </summary>
public class NamespaceNameHelpersTests
{
    [Fact]
    public void GetFullNamespaceName_NestedDeclarations_JoinsEnclosingNames()
    {
        var root = Parse("""
            namespace Outer
            {
                namespace Inner
                {
                    class Worker { }
                }
            }
            """);
        var inner = root.DescendantNodes()
            .OfType<NamespaceDeclarationSyntax>()
            .Last();

        Assert.Equal("Inner", inner.Name.ToString());
        Assert.Equal("Outer.Inner", NamespaceNameHelpers.GetFullNamespaceName(inner));
    }

    [Fact]
    public void GetFullNamespaceName_Null_ReturnsNull()
    {
        Assert.Null(NamespaceNameHelpers.GetFullNamespaceName(null));
    }

    [Fact]
    public void GetFullNamespaceName_FileScoped_ReturnsName()
    {
        var root = Parse("""
            namespace N;

            class C { }
            """);
        var ns = root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().Single();
        Assert.Equal("N", NamespaceNameHelpers.GetFullNamespaceName(ns));
    }

    [Fact]
    public void GetContainingNamespaceName_SyntaxNode_ReturnsEnclosingFullName()
    {
        var root = Parse("""
            namespace Outer
            {
                namespace Inner
                {
                    class Worker
                    {
                        void M() { var x = 1; }
                    }
                }
            }
            """);
        var local = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();
        Assert.Equal("Outer.Inner", NamespaceNameHelpers.GetContainingNamespaceName(local));
    }

    [Fact]
    public void GetContainingNamespaceName_SyntaxNode_NoNamespace_ReturnsNull()
    {
        var root = Parse("""
            class Worker
            {
                void M() { var x = 1; }
            }
            """);
        var local = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();
        Assert.Null(NamespaceNameHelpers.GetContainingNamespaceName(local));
    }

    [Fact]
    public void GetContainingNamespaceName_SemanticModel_UsesSymbolNamespace()
    {
        var (model, node) = BuildModelAtMarker("""
            namespace Sample.Ns
            {
                class Worker
                {
                    void M()
                    {
                        /*pos*/var x = 1;
                    }
                }
            }
            """, "/*pos*/");

        Assert.Equal("Sample.Ns", NamespaceNameHelpers.GetContainingNamespaceName(model, node));
    }

    [Fact]
    public void GetContainingNamespaceName_SemanticModel_FallsBackToSyntaxWhenSymbolEmpty()
    {
        // Global namespace: ToNamespaceName(global) is null/empty, so syntax path applies.
        var (model, node) = BuildModelAtMarker("""
            class Worker
            {
                void M()
                {
                    /*pos*/var x = 1;
                }
            }
            """, "/*pos*/");

        Assert.Null(NamespaceNameHelpers.GetContainingNamespaceName(model, node));
    }

    [Fact]
    public void GetContainingNamespaceName_SemanticModelFallback_PreservesEnclosingSyntaxNamespace()
    {
        var (globalModel, _) = BuildModelAtMarker(new string(' ', 256) + "/*pos*/", "/*pos*/");

        var syntaxRoot = Parse("""
            namespace Outer.Inner
            {
                class Worker
                {
                    void M()
                    {
                        var x = 1;
                    }
                }
            }
            """);
        var syntaxNode = syntaxRoot.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();

        Assert.Equal("Outer.Inner", NamespaceNameHelpers.GetContainingNamespaceName(globalModel, syntaxNode));
    }

    private static SyntaxNode Parse(string source) =>
        CSharpSyntaxTree.ParseText(source).GetRoot();

    private static (SemanticModel Model, SyntaxNode Node) BuildModelAtMarker(string source, string marker)
    {
        var markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0);
        var cleaned = source.Remove(markerIndex, marker.Length);
        var tree = CSharpSyntaxTree.ParseText(cleaned);
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            "NamespaceNameHelpersTests",
            new[] { tree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var token = tree.GetRoot().FindToken(markerIndex);
        var node = token.Parent ?? tree.GetRoot();
        return (model, node);
    }
}
