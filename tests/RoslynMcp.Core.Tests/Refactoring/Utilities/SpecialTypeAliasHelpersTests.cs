using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="SpecialTypeAliasHelpers"/> —
/// TryGetSpecialTypeAlias hit/miss arms and MatchesRequestedType display /
/// FQN / name / keyword-alias arms.
/// </summary>
public class SpecialTypeAliasHelpersTests
{
    [Theory]
    [InlineData(SpecialType.System_Boolean, "bool")]
    [InlineData(SpecialType.System_Int32, "int")]
    [InlineData(SpecialType.System_String, "string")]
    [InlineData(SpecialType.System_Object, "object")]
    [InlineData(SpecialType.System_Void, "void")]
    [InlineData(SpecialType.System_IntPtr, "nint")]
    [InlineData(SpecialType.System_UIntPtr, "nuint")]
    [InlineData(SpecialType.System_Double, "double")]
    public void TryGetSpecialTypeAlias_KnownSpecialType_ReturnsKeyword(SpecialType specialType, string expected)
    {
        Assert.True(SpecialTypeAliasHelpers.TryGetSpecialTypeAlias(specialType, out var alias));
        Assert.Equal(expected, alias);
    }

    [Fact]
    public void TryGetSpecialTypeAlias_None_ReturnsFalse()
    {
        Assert.False(SpecialTypeAliasHelpers.TryGetSpecialTypeAlias(SpecialType.None, out var alias));
        Assert.Equal("", alias);
    }

    [Fact]
    public void MatchesRequestedType_KeywordAlias_ReturnsTrue()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public int Field;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var field = type.GetMembers("Field").OfType<IFieldSymbol>().Single();

        Assert.True(SpecialTypeAliasHelpers.MatchesRequestedType("int", field.Type));
        Assert.True(SpecialTypeAliasHelpers.MatchesRequestedType("Int32", field.Type));
        Assert.False(SpecialTypeAliasHelpers.MatchesRequestedType("string", field.Type));
    }

    [Fact]
    public void MatchesRequestedType_DisplayString_ReturnsTrue()
    {
        var compilation = CreateCompilation("""
            namespace Sample
            {
                public class Worker { }
                public class Host
                {
                    public Worker Field = null!;
                }
            }
            """);
        var host = compilation.GetTypeByMetadataName("Sample.Host")!;
        var field = host.GetMembers("Field").OfType<IFieldSymbol>().Single();

        Assert.True(SpecialTypeAliasHelpers.MatchesRequestedType("Sample.Worker", field.Type));
        Assert.True(SpecialTypeAliasHelpers.MatchesRequestedType("Worker", field.Type));
        Assert.False(SpecialTypeAliasHelpers.MatchesRequestedType("Host", field.Type));
    }

    [Fact]
    public void MatchesRequestedType_TrimsRequestedType()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public string Field = "";
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var field = type.GetMembers("Field").OfType<IFieldSymbol>().Single();

        Assert.True(SpecialTypeAliasHelpers.MatchesRequestedType("  string  ", field.Type));
    }

    [Theory]
    [InlineData("nint", "System.IntPtr")]
    [InlineData("nuint", "System.UIntPtr")]
    public void PropertyTypeMatchesField_NativeIntAlias_DoesNotMatchPointerField(string requested, string fieldType)
    {
        // Self-contained corlib without RuntimeFeature.NumericIntPtr: the
        // field's display string is System.IntPtr / System.UIntPtr (not
        // nint / nuint), so only the alias map could produce a nint/nuint
        // match. Old generate_property map had no IntPtr/UIntPtr entries.
        var compilation = CreateCorlibCompilation($$"""
            namespace System
            {
                public class Object { }
                public abstract class ValueType { }
                public struct Void { }
                public struct Boolean { }
                public struct Int32 { }
                public struct IntPtr { }
                public struct UIntPtr { }
                public class String { }
                public class Attribute { }
            }
            public class C
            {
                public {{fieldType}} Field;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var field = type.GetMembers("Field").OfType<IFieldSymbol>().Single();

        // generate_property keeps its original alias map: no nint/nuint widening.
        Assert.False(RoslynMcp.Core.Refactoring.Generate.GeneratePropertyOperation.PropertyTypeMatchesField(requested, field.Type));
        Assert.True(RoslynMcp.Core.Refactoring.Generate.GeneratePropertyOperation.PropertyTypeMatchesField(fieldType, field.Type));
        // generate_method_stub path keeps the full map.
        Assert.True(SpecialTypeAliasHelpers.MatchesRequestedType(requested, field.Type));
    }

    private static Compilation CreateCorlibCompilation(string source) =>
        CSharpCompilation.Create(
            "MiniCorlib",
            new[] { CSharpSyntaxTree.ParseText(source) },
            Array.Empty<MetadataReference>(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static Compilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };
        return CSharpCompilation.Create(
            "SpecialTypeAliasHelpersTests",
            new[] { tree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
