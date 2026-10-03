using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class SymbolBindingHelpersTests
{
    [Fact]
    public void BindingsEqual_SameSymbolsSameOrder_ReturnsTrue()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public int A;
                public int B;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var a = type.GetMembers("A").OfType<IFieldSymbol>().Single();
        var b = type.GetMembers("B").OfType<IFieldSymbol>().Single();

        Assert.True(SymbolBindingHelpers.BindingsEqual([a, b], [a, b]));
    }

    [Fact]
    public void BindingsEqual_LengthMismatch_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public int A;
                public int B;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var a = type.GetMembers("A").OfType<IFieldSymbol>().Single();
        var b = type.GetMembers("B").OfType<IFieldSymbol>().Single();

        Assert.False(SymbolBindingHelpers.BindingsEqual([a, b], [a]));
    }

    [Fact]
    public void BindingsEqual_UnequalSymbols_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public int A;
                public int B;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var a = type.GetMembers("A").OfType<IFieldSymbol>().Single();
        var b = type.GetMembers("B").OfType<IFieldSymbol>().Single();

        Assert.False(SymbolBindingHelpers.BindingsEqual([a], [b]));
    }

    [Fact]
    public void BindingsEqual_BothEmpty_ReturnsTrue()
    {
        Assert.True(SymbolBindingHelpers.BindingsEqual(
            Array.Empty<ISymbol?>(),
            Array.Empty<ISymbol?>()));
    }

    [Fact]
    public void BindingsEqual_MatchingNullEntries_ReturnsTrue()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public int A;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var a = type.GetMembers("A").OfType<IFieldSymbol>().Single();

        Assert.True(SymbolBindingHelpers.BindingsEqual([a, null], [a, null]));
    }

    [Fact]
    public void BindingsEqual_NullVsSymbol_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public int A;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var a = type.GetMembers("A").OfType<IFieldSymbol>().Single();

        Assert.False(SymbolBindingHelpers.BindingsEqual([null], [a]));
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            "SymbolBindingHelpersTests",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
