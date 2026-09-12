using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class MemberDisplayHelpersTests
{
    [Fact]
    public void FormatIndexerParameterDisplay_FormatsRefKinds()
    {
        var compilation = Compile("""
            class C
            {
                public void M(int x, ref int r, out int o, in int i, ref readonly int rr) { o = 0; }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var texts = method.Parameters.Select(MemberDisplayHelpers.FormatIndexerParameterDisplay).ToArray();

        Assert.Equal("int x", texts[0]);
        Assert.Equal("ref int r", texts[1]);
        Assert.Equal("out int o", texts[2]);
        Assert.Equal("in int i", texts[3]);
        Assert.Equal("ref readonly int rr", texts[4]);
    }

    [Fact]
    public void DescribeMemberKind_MapsKnownKinds()
    {
        var compilation = Compile("""
            class C
            {
                public void M() { }
                public int P { get; set; }
                public int this[int i] => i;
                public event System.Action E;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var property = type.GetMembers("P").OfType<IPropertySymbol>().Single();
        var indexer = type.GetMembers().OfType<IPropertySymbol>().Single(p => p.IsIndexer);
        var evt = type.GetMembers("E").OfType<IEventSymbol>().Single();

        Assert.Equal("method", MemberDisplayHelpers.DescribeMemberKind(method));
        Assert.Equal("property", MemberDisplayHelpers.DescribeMemberKind(property));
        Assert.Equal("indexer", MemberDisplayHelpers.DescribeMemberKind(indexer));
        Assert.Equal("event", MemberDisplayHelpers.DescribeMemberKind(evt));
        Assert.Equal("member", MemberDisplayHelpers.DescribeMemberKind(type));
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
            "MemberDisplayHelpersTests",
            new[] { tree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var diagnostics = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(diagnostics.Length == 0, string.Join("\n", diagnostics.Select(d => d.ToString())));
        return compilation;
    }
}
