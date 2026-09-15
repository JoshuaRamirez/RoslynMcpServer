using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class NamedTypeHelpersTests
{
    [Fact]
    public void IsObjectOrValueTypeBase_ClassWithObjectBase_ReturnsTrue()
    {
        var type = GetType("public class C { }", "C");

        Assert.True(NamedTypeHelpers.IsObjectOrValueTypeBase(type));
    }

    [Fact]
    public void IsObjectOrValueTypeBase_StructWithValueTypeBase_ReturnsTrue()
    {
        var type = GetType("public struct S { }", "S");

        Assert.True(NamedTypeHelpers.IsObjectOrValueTypeBase(type));
    }

    [Fact]
    public void IsObjectOrValueTypeBase_CustomClassBase_ReturnsFalse()
    {
        var type = GetType(
            """
            public class Base { }
            public class Derived : Base { }
            """,
            "Derived");

        Assert.False(NamedTypeHelpers.IsObjectOrValueTypeBase(type));
    }

    private static INamedTypeSymbol GetType(string source, string metadataName)
    {
        var compilation = CreateCompilation(source);
        return compilation.GetTypeByMetadataName(metadataName)!;
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            "NamedTypeHelpersTests",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
