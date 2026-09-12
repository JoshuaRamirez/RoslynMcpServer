using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

public class SignatureOverrideHelpersTests
{
    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };
        return CSharpCompilation.Create(
            "SignatureOverrideHelpersTests",
            new[] { tree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    [Fact]
    public void HasSourceDeclaration_True_ForSourceMethod_False_ForMetadata()
    {
        var compilation = CreateCompilation("""
            class C
            {
                public void M() { }
            }
            """);
        var c = compilation.GetTypeByMetadataName("C")!;
        var m = c.GetMembers("M").OfType<IMethodSymbol>().Single();
        Assert.True(SignatureOverrideHelpers.HasSourceDeclaration(m));

        var objectToString = compilation.GetSpecialType(SpecialType.System_Object)
            .GetMembers("ToString")
            .OfType<IMethodSymbol>()
            .Single();
        Assert.False(SignatureOverrideHelpers.HasSourceDeclaration(objectToString));
    }

    [Fact]
    public void GetOverrideRoot_WalksOverrideChain()
    {
        var compilation = CreateCompilation("""
            class A
            {
                public virtual void M() { }
            }
            class B : A
            {
                public override void M() { }
            }
            class C : B
            {
                public override void M() { }
            }
            """);
        var a = compilation.GetTypeByMetadataName("A")!.GetMembers("M").OfType<IMethodSymbol>().Single();
        var b = compilation.GetTypeByMetadataName("B")!.GetMembers("M").OfType<IMethodSymbol>().Single();
        var c = compilation.GetTypeByMetadataName("C")!.GetMembers("M").OfType<IMethodSymbol>().Single();

        Assert.True(SymbolEqualityComparer.Default.Equals(a, SignatureOverrideHelpers.GetOverrideRoot(a)));
        Assert.True(SymbolEqualityComparer.Default.Equals(a, SignatureOverrideHelpers.GetOverrideRoot(b)));
        Assert.True(SymbolEqualityComparer.Default.Equals(a, SignatureOverrideHelpers.GetOverrideRoot(c)));
    }

    [Fact]
    public void ShareOverrideRoot_True_WhenSameRoot_False_Otherwise()
    {
        var compilation = CreateCompilation("""
            class A
            {
                public virtual void M() { }
                public virtual void N() { }
            }
            class B : A
            {
                public override void M() { }
                public override void N() { }
            }
            """);
        var aM = compilation.GetTypeByMetadataName("A")!.GetMembers("M").OfType<IMethodSymbol>().Single();
        var bM = compilation.GetTypeByMetadataName("B")!.GetMembers("M").OfType<IMethodSymbol>().Single();
        var bN = compilation.GetTypeByMetadataName("B")!.GetMembers("N").OfType<IMethodSymbol>().Single();

        Assert.True(SignatureOverrideHelpers.ShareOverrideRoot(aM, bM));
        Assert.False(SignatureOverrideHelpers.ShareOverrideRoot(bM, bN));
    }
}
