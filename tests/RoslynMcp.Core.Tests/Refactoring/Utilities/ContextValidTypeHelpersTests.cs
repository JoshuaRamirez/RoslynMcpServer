using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="ContextValidTypeHelpers"/> —
/// ToContextValidTypeName display, MemberTypeBindsAtInsertion true/false,
/// ContainsTypeParameter walks, IsLessAccessibleThanPublic public vs internal,
/// and GetEffectiveAccessibility container walk.
/// </summary>
public class ContextValidTypeHelpersTests
{
    [Fact]
    public void ToContextValidTypeName_SimpleType_ReturnsDisplay()
    {
        var (model, position, type) = BuildAtMarker("""
            class Worker
            {
                void M()
                {
                    /*pos*/int x = 0;
                }
            }
            """, "/*pos*/", typeFromLocal: "x");

        var display = ContextValidTypeHelpers.ToContextValidTypeName(type, model, position);
        Assert.Equal("int", display);
    }

    [Fact]
    public void ToContextValidTypeName_Void_ReturnsVoidKeyword()
    {
        var compilation = CreateCompilation("""
            class Worker
            {
                void M() { }
            }
            """);
        var voidType = compilation.GetSpecialType(SpecialType.System_Void);
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var display = ContextValidTypeHelpers.ToContextValidTypeName(voidType, model, method.ReturnType.SpanStart);
        Assert.Equal("void", display);
    }

    [Fact]
    public void MemberTypeBindsAtInsertion_PublicNamedType_ReturnsTrue()
    {
        var (model, position, type) = BuildAtMarker("""
            public class Widget { }

            class Worker
            {
                void M()
                {
                    /*pos*/Widget w = null!;
                }
            }
            """, "/*pos*/", typeFromLocal: "w");

        Assert.True(ContextValidTypeHelpers.MemberTypeBindsAtInsertion(type, model, position));
    }

    [Fact]
    public void MemberTypeBindsAtInsertion_TypeParameter_ReturnsFalse()
    {
        var (model, position, type) = BuildAtMarker("""
            class Worker
            {
                void M<T>()
                {
                    /*pos*/T value = default!;
                }
            }
            """, "/*pos*/", typeFromLocal: "value");

        Assert.True(ContextValidTypeHelpers.ContainsTypeParameter(type));
        Assert.False(ContextValidTypeHelpers.MemberTypeBindsAtInsertion(type, model, position));
    }

    [Fact]
    public void ContainsTypeParameter_TypeParam_ReturnsTrue()
    {
        var (_, _, type) = BuildAtMarker("""
            class Worker
            {
                void M<T>()
                {
                    /*pos*/T value = default!;
                }
            }
            """, "/*pos*/", typeFromLocal: "value");

        Assert.True(ContextValidTypeHelpers.ContainsTypeParameter(type));
    }

    [Fact]
    public void ContainsTypeParameter_ArrayOfTypeParam_ReturnsTrue()
    {
        var (_, _, type) = BuildAtMarker("""
            class Worker
            {
                void M<T>()
                {
                    /*pos*/T[] values = null!;
                }
            }
            """, "/*pos*/", typeFromLocal: "values");

        Assert.True(ContextValidTypeHelpers.ContainsTypeParameter(type));
    }

    [Fact]
    public void ContainsTypeParameter_NamedWithoutTypeParam_ReturnsFalse()
    {
        var (_, _, type) = BuildAtMarker("""
            class Worker
            {
                void M()
                {
                    /*pos*/string s = "";
                }
            }
            """, "/*pos*/", typeFromLocal: "s");

        Assert.False(ContextValidTypeHelpers.ContainsTypeParameter(type));
    }

    [Fact]
    public void ContainsTypeParameter_NestedGenericNamedWithTypeParam_ReturnsTrue()
    {
        var (_, _, type) = BuildAtMarker("""
            class Box<T> { }

            class Worker
            {
                void M<T>()
                {
                    /*pos*/Box<System.Collections.Generic.List<T>> box = null!;
                }
            }
            """, "/*pos*/", typeFromLocal: "box");

        Assert.True(ContextValidTypeHelpers.ContainsTypeParameter(type));
    }

    [Fact]
    public void IsLessAccessibleThanPublic_PublicType_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            public class PublicType { }
            """);
        var type = compilation.GetTypeByMetadataName("PublicType")!;
        Assert.False(ContextValidTypeHelpers.IsLessAccessibleThanPublic(type));
    }

    [Fact]
    public void IsLessAccessibleThanPublic_InternalType_ReturnsTrue()
    {
        var compilation = CreateCompilation("""
            internal class InternalType { }
            """);
        var type = compilation.GetTypeByMetadataName("InternalType")!;
        Assert.True(ContextValidTypeHelpers.IsLessAccessibleThanPublic(type));
    }

    [Fact]
    public void GetEffectiveAccessibility_NestedPublicInInternal_IsInternal()
    {
        var compilation = CreateCompilation("""
            internal class Outer
            {
                public class Inner { }
            }
            """);
        var inner = compilation.GetTypeByMetadataName("Outer+Inner")!;
        Assert.Equal(Accessibility.Internal, ContextValidTypeHelpers.GetEffectiveAccessibility(inner));
    }

    [Fact]
    public void GetEffectiveAccessibility_TopLevelPublic_IsPublic()
    {
        var compilation = CreateCompilation("""
            public class Top { }
            """);
        var top = compilation.GetTypeByMetadataName("Top")!;
        Assert.Equal(Accessibility.Public, ContextValidTypeHelpers.GetEffectiveAccessibility(top));
    }

    [Fact]
    public void GetEffectiveAccessibility_InternalInProtected_IsPrivateProtected()
    {
        var compilation = CreateCompilation("""
            public class Outer
            {
                protected class Middle
                {
                    internal class Inner { }
                }
            }
            """);
        var inner = compilation.GetTypeByMetadataName("Outer+Middle+Inner")!;
        Assert.Equal(Accessibility.ProtectedAndInternal, ContextValidTypeHelpers.GetEffectiveAccessibility(inner));
    }

    private static (SemanticModel Model, int Position, ITypeSymbol Type) BuildAtMarker(
        string source,
        string marker,
        string typeFromLocal)
    {
        var markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0);
        var cleaned = source.Remove(markerIndex, marker.Length);
        var tree = CSharpSyntaxTree.ParseText(cleaned);
        var compilation = CreateCompilation(cleaned, tree);
        var model = compilation.GetSemanticModel(tree);
        var local = tree.GetRoot()
            .DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.Text == typeFromLocal);
        var localSymbol = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(local));
        return (model, markerIndex, localSymbol.Type);
    }

    private static CSharpCompilation CreateCompilation(string source, SyntaxTree? tree = null)
    {
        tree ??= CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            "ContextValidTypeHelpersTests",
            [tree],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
