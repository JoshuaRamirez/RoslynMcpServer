using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class NamespaceEqualityHelpersTests
{
    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, "", true)]
    [InlineData("", null, true)]
    [InlineData("", "", true)]
    [InlineData("A", "A", true)]
    [InlineData("A.B", "A.B", true)]
    [InlineData("A", "B", false)]
    [InlineData("A", "a", false)]
    [InlineData("A", "", false)]
    [InlineData(null, "A", false)]
    public void NamespacesEqual_Strings_OrdinalEmptyNullRules(string? left, string? right, bool expected)
    {
        Assert.Equal(expected, NamespaceEqualityHelpers.NamespacesEqual(left, right));
    }

    [Fact]
    public void NamespacesEqual_Symbol_NullOrGlobal_EqualsEmptyName()
    {
        var compilation = Compile("""
            namespace Sample.Ns
            {
                public class C { }
            }
            """);
        var global = compilation.GlobalNamespace;
        Assert.True(NamespaceEqualityHelpers.NamespacesEqual((INamespaceSymbol?)null, null));
        Assert.True(NamespaceEqualityHelpers.NamespacesEqual((INamespaceSymbol?)null, ""));
        Assert.True(NamespaceEqualityHelpers.NamespacesEqual(global, null));
        Assert.True(NamespaceEqualityHelpers.NamespacesEqual(global, ""));
        Assert.False(NamespaceEqualityHelpers.NamespacesEqual(global, "Sample"));
    }

    [Fact]
    public void NamespacesEqual_Symbol_MatchingAndMismatchDisplay()
    {
        var compilation = Compile("""
            namespace Sample.Ns
            {
                public class C { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("Sample.Ns.C")!;
        var ns = type.ContainingNamespace;
        Assert.True(NamespaceEqualityHelpers.NamespacesEqual(ns, "Sample.Ns"));
        Assert.False(NamespaceEqualityHelpers.NamespacesEqual(ns, "Sample"));
        Assert.False(NamespaceEqualityHelpers.NamespacesEqual(ns, "sample.ns"));
        Assert.False(NamespaceEqualityHelpers.NamespacesEqual(ns, null));
        Assert.False(NamespaceEqualityHelpers.NamespacesEqual(ns, ""));
    }

    [Fact]
    public void ToNamespaceName_NullGlobalNamed()
    {
        var compilation = Compile("""
            namespace Sample.Ns
            {
                public class C { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("Sample.Ns.C")!;
        Assert.Null(NamespaceEqualityHelpers.ToNamespaceName(null));
        Assert.Null(NamespaceEqualityHelpers.ToNamespaceName(compilation.GlobalNamespace));
        Assert.Equal("Sample.Ns", NamespaceEqualityHelpers.ToNamespaceName(type.ContainingNamespace));
    }

    [Fact]
    public void TypeNameBindsToDifferentType_ErrorName_IsDifferent()
    {
        var (model, position, expected) = BuildModelAtMarker("""
            class C
            {
                void M()
                {
                    /*pos*/int x = 0;
                }
            }
            """, "/*pos*/");
        Assert.True(NamespaceEqualityHelpers.TypeNameBindsToDifferentType("DoesNotExist", expected, model, position));
    }

    [Fact]
    public void TypeNameBindsToDifferentType_MatchingInt_IsSame()
    {
        var (model, position, expected) = BuildModelAtMarker("""
            class C
            {
                void M()
                {
                    /*pos*/int x = 0;
                }
            }
            """, "/*pos*/");
        Assert.False(NamespaceEqualityHelpers.TypeNameBindsToDifferentType("int", expected, model, position));
    }

    [Fact]
    public void TypeNameBindsToDifferentType_WrongType_IsDifferent()
    {
        var (model, position, expected) = BuildModelAtMarker("""
            class C
            {
                void M()
                {
                    /*pos*/int x = 0;
                }
            }
            """, "/*pos*/");
        Assert.True(NamespaceEqualityHelpers.TypeNameBindsToDifferentType("string", expected, model, position));
    }

    private static (SemanticModel Model, int Position, ITypeSymbol Expected) BuildModelAtMarker(string source, string marker)
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
            "NamespaceEqualityHelpersTests_Bind",
            new[] { tree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var position = markerIndex;
        var expected = compilation.GetSpecialType(SpecialType.System_Int32);
        return (model, position, expected);
    }

    private static Compilation Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            "NamespaceEqualityHelpersTests",
            new[] { tree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var diagnostics = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(diagnostics.Length == 0, string.Join("\n", diagnostics.Select(d => d.ToString())));
        return compilation;
    }
}
