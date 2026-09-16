using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class ParameterTypeMatchHelpersTests
{
    [Fact]
    public void ParameterTypesMatch_Equal_symbols_returns_true()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M(int a) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);

        Assert.True(ParameterTypeMatchHelpers.ParameterTypesMatch(method.Parameters[0].Type, intType));
    }

    [Fact]
    public void ParameterTypesMatch_method_type_param_ordinal_match_returns_true()
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

        Assert.False(SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, n.Parameters[0].Type));
        Assert.True(ParameterTypeMatchHelpers.ParameterTypesMatch(m.Parameters[0].Type, n.Parameters[0].Type));
    }

    [Fact]
    public void ParameterTypesMatch_method_type_param_ordinal_mismatch_returns_false()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M<T,U>(T a) { }
                public void N<T,U>(U a) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var m = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var n = type.GetMembers("N").OfType<IMethodSymbol>().Single();

        Assert.False(ParameterTypeMatchHelpers.ParameterTypesMatch(m.Parameters[0].Type, n.Parameters[0].Type));
    }

    [Fact]
    public void ParameterTypesMatch_array_rank_and_element_recursion_returns_true()
    {
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
        Assert.True(ParameterTypeMatchHelpers.ParameterTypesMatch(a.Parameters[0].Type, b.Parameters[0].Type));
    }

    [Fact]
    public void ParameterTypesMatch_array_rank_mismatch_returns_false()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void A<T>(T[] a) { }
                public void B<T>(T[,] a) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var a = type.GetMembers("A").OfType<IMethodSymbol>().Single();
        var b = type.GetMembers("B").OfType<IMethodSymbol>().Single();

        Assert.False(ParameterTypeMatchHelpers.ParameterTypesMatch(a.Parameters[0].Type, b.Parameters[0].Type));
    }

    [Fact]
    public void ParameterTypesMatch_pointer_pointed_at_recursion_returns_true()
    {
        var compilation = CreateUnsafeCompilation("""
            public unsafe class C
            {
                public void A<T>(T* a) where T : unmanaged { }
                public void B<T>(T* a) where T : unmanaged { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var a = type.GetMembers("A").OfType<IMethodSymbol>().Single();
        var b = type.GetMembers("B").OfType<IMethodSymbol>().Single();

        Assert.False(SymbolEqualityComparer.Default.Equals(a.Parameters[0].Type, b.Parameters[0].Type));
        Assert.True(ParameterTypeMatchHelpers.ParameterTypesMatch(a.Parameters[0].Type, b.Parameters[0].Type));
    }

    [Fact]
    public void ParameterTypesMatch_named_generic_type_arg_recursion_returns_true()
    {
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
        Assert.True(ParameterTypeMatchHelpers.ParameterTypesMatch(a.Parameters[0].Type, b.Parameters[0].Type));
        Assert.True(ParameterTypeMatchHelpers.NamedTypesMatch(
            (INamedTypeSymbol)a.Parameters[0].Type,
            (INamedTypeSymbol)b.Parameters[0].Type));
    }

    [Fact]
    public void ParameterTypesMatch_tuple_element_recursion_returns_true()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void A<T>((T, int) a) { }
                public void B<T>((T, int) a) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var a = type.GetMembers("A").OfType<IMethodSymbol>().Single();
        var b = type.GetMembers("B").OfType<IMethodSymbol>().Single();

        Assert.False(SymbolEqualityComparer.Default.Equals(a.Parameters[0].Type, b.Parameters[0].Type));
        Assert.True(ParameterTypeMatchHelpers.ParameterTypesMatch(a.Parameters[0].Type, b.Parameters[0].Type));
    }

    [Fact]
    public void ParameterTypesMatch_distinct_named_types_returns_false()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M(int a) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var stringType = compilation.GetSpecialType(SpecialType.System_String);

        Assert.False(ParameterTypeMatchHelpers.ParameterTypesMatch(method.Parameters[0].Type, stringType));
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            "ParameterTypeMatchHelpersTests",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static CSharpCompilation CreateUnsafeCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            "ParameterTypeMatchHelpersTests",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithAllowUnsafe(true));
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
            "ParameterTypeMatchHelpersTests",
            [tree],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
