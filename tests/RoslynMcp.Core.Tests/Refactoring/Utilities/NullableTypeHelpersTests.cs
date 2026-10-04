using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class NullableTypeHelpersTests
{
    [Fact]
    public void UnwrapNullable_Null_ReturnsNull()
    {
        Assert.Null(NullableTypeHelpers.UnwrapNullable(null));
    }

    [Fact]
    public void UnwrapNullable_NullableInt_ReturnsInt()
    {
        var type = GetFieldType(
            """
            public class C { public int? F; }
            """);

        var unwrapped = NullableTypeHelpers.UnwrapNullable(type);

        Assert.Equal(SpecialType.System_Int32, unwrapped.SpecialType);
    }

    [Fact]
    public void UnwrapNullable_NullableStruct_ReturnsStruct()
    {
        var type = GetFieldType(
            """
            public struct S { }
            public class C { public S? F; }
            """);

        var unwrapped = NullableTypeHelpers.UnwrapNullable(type);

        Assert.Equal("S", unwrapped.Name);
        Assert.Equal(TypeKind.Struct, unwrapped.TypeKind);
    }

    [Fact]
    public void UnwrapNullable_NonNullableValueType_ReturnsSameType()
    {
        var type = GetFieldType(
            """
            public class C { public double F; }
            """);

        Assert.Same(type, NullableTypeHelpers.UnwrapNullable(type));
    }

    [Fact]
    public void UnwrapNullable_NullableReferenceAnnotation_ReturnsSameType()
    {
        var type = GetFieldType(
            """
            #nullable enable
            public class C { public string? F; }
            """);

        Assert.Same(type, NullableTypeHelpers.UnwrapNullable(type));
    }

    [Fact]
    public void UnwrapNullable_OtherGenericStruct_ReturnsSameType()
    {
        var type = GetFieldType(
            """
            public struct Box<T> { }
            public class C { public Box<int> F; }
            """);

        Assert.Same(type, NullableTypeHelpers.UnwrapNullable(type));
    }

    private static ITypeSymbol GetFieldType(string source)
    {
        var compilation = CreateCompilation(source);
        var type = compilation.GetTypeByMetadataName("C")!;
        return type.GetMembers("F").OfType<IFieldSymbol>().Single().Type;
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            "NullableTypeHelpersTests",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
