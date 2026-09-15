using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class TypeEquivalenceHelpersTests
{
    [Fact]
    public void TypesEquivalent_IdenticalNamedTypes_ReturnsTrue()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public int M() => 0;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);

        Assert.True(TypeEquivalenceHelpers.TypesEquivalent(method.ReturnType, intType));
    }

    [Fact]
    public void TypesEquivalent_DistinctNamedTypes_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public int M() => 0;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var stringType = compilation.GetSpecialType(SpecialType.System_String);

        Assert.False(TypeEquivalenceHelpers.TypesEquivalent(method.ReturnType, stringType));
    }

    [Fact]
    public void TypesEquivalent_MatchingMethodTypeParameters_ReturnsTrue()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M<T>(T a) { }
                public void N<T>(T a) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var m = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var n = type.GetMembers("N").OfType<IMethodSymbol>().Single();

        Assert.True(TypeEquivalenceHelpers.TypesEquivalent(m.Parameters[0].Type, n.Parameters[0].Type));
    }

    [Fact]
    public void TypesEquivalent_ArrayElementRecursion_ReturnsTrue()
    {
        // Distinct method type-params so SymbolEqualityComparer does not short-circuit;
        // equivalence requires the array branch + recursive element comparison.
        var compilation = CreateCompilation("""
            public class C
            {
                public void A<T>(T[] a) { }
                public void B<T>(T[] a) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var a = type.GetMembers("A").OfType<IMethodSymbol>().Single();
        var b = type.GetMembers("B").OfType<IMethodSymbol>().Single();

        Assert.False(SymbolEqualityComparer.Default.Equals(a.Parameters[0].Type, b.Parameters[0].Type));
        Assert.True(TypeEquivalenceHelpers.TypesEquivalent(a.Parameters[0].Type, b.Parameters[0].Type));
    }

    [Fact]
    public void TypesEquivalent_ConstructedGenericsMatchingArgs_ReturnsTrue()
    {
        // Distinct method type-params as type args so SymbolEqualityComparer does not
        // short-circuit; named-type OriginalDefinition match + recursive type-arg compare.
        var compilation = CreateCompilationWithSystemCollections("""
            using System.Collections.Generic;
            public class C
            {
                public void A<T>(List<T> a) { }
                public void B<T>(List<T> a) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var a = type.GetMembers("A").OfType<IMethodSymbol>().Single();
        var b = type.GetMembers("B").OfType<IMethodSymbol>().Single();

        Assert.False(SymbolEqualityComparer.Default.Equals(a.Parameters[0].Type, b.Parameters[0].Type));
        Assert.True(TypeEquivalenceHelpers.TypesEquivalent(a.Parameters[0].Type, b.Parameters[0].Type));
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            "TypeEquivalenceHelpersTests",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static CSharpCompilation CreateCompilationWithSystemCollections(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        };
        return CSharpCompilation.Create(
            "TypeEquivalenceHelpersTests",
            [tree],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
