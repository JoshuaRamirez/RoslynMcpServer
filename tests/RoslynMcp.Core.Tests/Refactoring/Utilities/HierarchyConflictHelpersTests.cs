using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class HierarchyConflictHelpersTests
{
    [Fact]
    public void SignaturesMatch_True_WhenParamCountTypeParamsAndRefKindsAlign()
    {
        var compilation = Compile("""
            class C
            {
                public void M(int x, ref int y) { }
                public void N(int a, ref int b) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var m = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var n = type.GetMembers("N").OfType<IMethodSymbol>().Single();

        Assert.True(HierarchyConflictHelpers.SignaturesMatch(m, n));
    }

    [Fact]
    public void SignaturesMatch_False_OnParamCountMismatch()
    {
        var compilation = Compile("""
            class C
            {
                public void M(int x) { }
                public void N(int x, int y) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var m = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var n = type.GetMembers("N").OfType<IMethodSymbol>().Single();

        Assert.False(HierarchyConflictHelpers.SignaturesMatch(m, n));
    }

    [Fact]
    public void SignaturesMatch_False_OnTypeParamCountMismatch()
    {
        var compilation = Compile("""
            class C
            {
                public void M<T>(T x) { }
                public void N(int x) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var m = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var n = type.GetMembers("N").OfType<IMethodSymbol>().Single();

        Assert.False(HierarchyConflictHelpers.SignaturesMatch(m, n));
    }

    [Fact]
    public void SignaturesMatch_True_WhenRefInOutDiffer()
    {
        // CS0663: ref/in/out alone do not distinguish overloads — collide.
        var compilation = Compile("""
            class C
            {
                public void M(ref int x) { }
                public void N(out int x) { x = 0; }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var m = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var n = type.GetMembers("N").OfType<IMethodSymbol>().Single();

        Assert.True(HierarchyConflictHelpers.SignaturesMatch(m, n));
    }

    [Fact]
    public void SignaturesMatch_False_OnByValueVsByRefMismatch()
    {
        var compilation = Compile("""
            class C
            {
                public void M(int x) { }
                public void N(ref int x) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var m = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var n = type.GetMembers("N").OfType<IMethodSymbol>().Single();

        Assert.False(HierarchyConflictHelpers.SignaturesMatch(m, n));
    }

    [Fact]
    public void SignaturesMatch_False_OnParameterTypeMismatch()
    {
        var compilation = Compile("""
            class C
            {
                public void M(int x) { }
                public void N(string x) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var m = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var n = type.GetMembers("N").OfType<IMethodSymbol>().Single();

        Assert.False(HierarchyConflictHelpers.SignaturesMatch(m, n));
    }

    [Fact]
    public void IndexerSignaturesMatch_True_WhenParamsAndRefKindsAlign()
    {
        var compilation = Compile("""
            class A
            {
                public int this[int i, in int j] => i;
            }
            class B
            {
                public int this[int x, in int y] => x;
            }
            """);
        var a = compilation.GetTypeByMetadataName("A")!;
        var b = compilation.GetTypeByMetadataName("B")!;
        var left = a.GetMembers().OfType<IPropertySymbol>().Single(p => p.IsIndexer);
        var right = b.GetMembers().OfType<IPropertySymbol>().Single(p => p.IsIndexer);

        Assert.True(HierarchyConflictHelpers.IndexerSignaturesMatch(left, right));
    }

    [Fact]
    public void IndexerSignaturesMatch_False_OnParamCountOrRefKindMismatch()
    {
        var compilation = Compile("""
            class A
            {
                public int this[int i] => i;
            }
            class B
            {
                public int this[int i, int j] => i;
            }
            class C
            {
                public int this[in int i] => i;
            }
            """);
        var a = compilation.GetTypeByMetadataName("A")!;
        var b = compilation.GetTypeByMetadataName("B")!;
        var c = compilation.GetTypeByMetadataName("C")!;
        var ia = a.GetMembers().OfType<IPropertySymbol>().Single(p => p.IsIndexer);
        var ib = b.GetMembers().OfType<IPropertySymbol>().Single(p => p.IsIndexer);
        var ic = c.GetMembers().OfType<IPropertySymbol>().Single(p => p.IsIndexer);

        Assert.False(HierarchyConflictHelpers.IndexerSignaturesMatch(ia, ib));
        Assert.False(HierarchyConflictHelpers.IndexerSignaturesMatch(ia, ic));
    }

    [Fact]
    public void HasConflict_True_WhenSameMethodSignatureExists()
    {
        var compilation = Compile("""
            class Base
            {
                public void M(int x) { }
            }
            class Derived
            {
                public void M(int y) { }
            }
            """);
        var target = compilation.GetTypeByMetadataName("Base")!;
        var member = compilation.GetTypeByMetadataName("Derived")!.GetMembers("M").OfType<IMethodSymbol>().Single();

        Assert.True(HierarchyConflictHelpers.HasConflict(target, member));
    }

    [Fact]
    public void HasConflict_False_WhenMethodOverloadDiffers()
    {
        var compilation = Compile("""
            class Base
            {
                public void M(int x) { }
            }
            class Derived
            {
                public void M(string y) { }
            }
            """);
        var target = compilation.GetTypeByMetadataName("Base")!;
        var member = compilation.GetTypeByMetadataName("Derived")!.GetMembers("M").OfType<IMethodSymbol>().Single();

        Assert.False(HierarchyConflictHelpers.HasConflict(target, member));
    }

    [Fact]
    public void HasConflict_True_WhenExistingDiffersOnlyByRefInOut()
    {
        // Target already has M(out int); pushing M(ref int) would CS0663.
        var compilation = Compile("""
            class Target
            {
                public void M(out int x) { x = 0; }
            }
            class Source
            {
                public void M(ref int x) { }
            }
            """);
        var target = compilation.GetTypeByMetadataName("Target")!;
        var member = compilation.GetTypeByMetadataName("Source")!.GetMembers("M").OfType<IMethodSymbol>().Single();

        Assert.True(HierarchyConflictHelpers.HasConflict(target, member));
    }

    [Fact]
    public void SignaturesMatch_True_WhenMethodTypeParametersMatchByOrdinal()
    {
        // M<T>(T) vs M<U>(U): type-parameter symbols differ by identity but
        // C# declaration signatures collide (CS0111).
        var compilation = Compile("""
            class C
            {
                public void M<T>(T x) { }
                public void N<U>(U y) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var m = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var n = type.GetMembers("N").OfType<IMethodSymbol>().Single();

        Assert.True(HierarchyConflictHelpers.SignaturesMatch(m, n));
    }

    [Fact]
    public void HasConflict_True_WhenExistingGenericMethodMatchesByOrdinal()
    {
        var compilation = Compile("""
            class Target
            {
                public void M<U>(U value) { }
            }
            class Source
            {
                public void M<T>(T value) { }
            }
            """);
        var target = compilation.GetTypeByMetadataName("Target")!;
        var member = compilation.GetTypeByMetadataName("Source")!.GetMembers("M").OfType<IMethodSymbol>().Single();

        Assert.True(HierarchyConflictHelpers.HasConflict(target, member));
    }

    [Fact]
    public void HasConflict_True_WhenSameIndexerSignatureExists()
    {
        var compilation = Compile("""
            class Base
            {
                public int this[int i] => i;
            }
            class Derived
            {
                public int this[int j] => j;
            }
            """);
        var target = compilation.GetTypeByMetadataName("Base")!;
        var member = compilation.GetTypeByMetadataName("Derived")!
            .GetMembers().OfType<IPropertySymbol>().Single(p => p.IsIndexer);

        Assert.True(HierarchyConflictHelpers.HasConflict(target, member));
    }

    [Fact]
    public void HasConflict_True_OnPropertyNameClash()
    {
        var compilation = Compile("""
            class Base
            {
                public int P { get; set; }
            }
            class Derived
            {
                public string P { get; set; }
            }
            """);
        var target = compilation.GetTypeByMetadataName("Base")!;
        var member = compilation.GetTypeByMetadataName("Derived")!.GetMembers("P").OfType<IPropertySymbol>().Single();

        Assert.True(HierarchyConflictHelpers.HasConflict(target, member));
    }

    [Fact]
    public void HasConflict_False_WhenNoMatchingMember()
    {
        var compilation = Compile("""
            class Base
            {
                public void Other() { }
            }
            class Derived
            {
                public void M() { }
            }
            """);
        var target = compilation.GetTypeByMetadataName("Base")!;
        var member = compilation.GetTypeByMetadataName("Derived")!.GetMembers("M").OfType<IMethodSymbol>().Single();

        Assert.False(HierarchyConflictHelpers.HasConflict(target, member));
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
            "HierarchyConflictHelpersTests",
            new[] { tree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var diagnostics = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(diagnostics.Length == 0, string.Join("\n", diagnostics.Select(d => d.ToString())));
        return compilation;
    }
}
