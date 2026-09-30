using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="HierarchyDeclaredMemberHelpers"/> —
/// declared-member enumeration previously duplicated on
/// PullMembersUp / PushMembersDown.
/// </summary>
public class HierarchyDeclaredMemberHelpersTests
{
    [Fact]
    public void EnumerateDeclaredMembers_YieldsMethodPropertyIndexerFieldEvent()
    {
        var (typeDecl, model) = CreateType("""
            public class C
            {
                public void M() { }
                public int P { get; set; }
                public int this[int i] => i;
                public int F;
                public event System.EventHandler E;
                public event System.EventHandler ExplicitE { add { } remove { } }
            }
            """);

        var results = HierarchyDeclaredMemberHelpers
            .EnumerateDeclaredMembers(typeDecl, model, CancellationToken.None)
            .ToList();

        Assert.Equal(
            new[] { "M", "P", "this[]", "F", "E", "ExplicitE" },
            results.Select(r => r.Name));
        Assert.All(results, r => Assert.NotNull(r.Symbol));
        Assert.IsType<MethodDeclarationSyntax>(results[0].Syntax);
        Assert.IsType<PropertyDeclarationSyntax>(results[1].Syntax);
        Assert.IsType<IndexerDeclarationSyntax>(results[2].Syntax);
        Assert.IsType<FieldDeclarationSyntax>(results[3].Syntax);
        Assert.IsType<EventFieldDeclarationSyntax>(results[4].Syntax);
        Assert.IsType<EventDeclarationSyntax>(results[5].Syntax);
    }

    [Fact]
    public void EnumerateDeclaredMembers_ExpandsMultiVariableFieldAndEventField()
    {
        var (typeDecl, model) = CreateType("""
            public class C
            {
                public int A, B;
                public event System.EventHandler E1, E2;
            }
            """);

        var results = HierarchyDeclaredMemberHelpers
            .EnumerateDeclaredMembers(typeDecl, model, CancellationToken.None)
            .ToList();

        Assert.Equal(new[] { "A", "B", "E1", "E2" }, results.Select(r => r.Name));
        Assert.Same(results[0].Syntax, results[1].Syntax);
        Assert.Same(results[2].Syntax, results[3].Syntax);
        Assert.All(results, r => Assert.NotNull(r.Symbol));
    }

    [Fact]
    public void EnumerateDeclaredMembers_SkipsNestedTypesAndConstructors()
    {
        var (typeDecl, model) = CreateType("""
            public class C
            {
                public C() { }
                public void M() { }
                public class Nested { }
            }
            """);

        var results = HierarchyDeclaredMemberHelpers
            .EnumerateDeclaredMembers(typeDecl, model, CancellationToken.None)
            .ToList();

        Assert.Equal(new[] { "M" }, results.Select(r => r.Name));
    }

    private static (TypeDeclarationSyntax TypeDecl, SemanticModel Model) CreateType(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "HierarchyDeclaredMemberHelpersTests",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var typeDecl = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().First();
        return (typeDecl, model);
    }
}
