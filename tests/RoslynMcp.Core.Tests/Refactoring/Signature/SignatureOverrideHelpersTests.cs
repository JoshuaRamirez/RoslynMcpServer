using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
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

    [Fact]
    public async Task GetRelatedMethodsAsync_SeedOnly_WhenFlagsFalse()
    {
        var (solution, method) = CreateSolutionWithMethod("""
            public class C
            {
                public virtual void M(int x) { }
            }
            public class D : C
            {
                public override void M(int x) { }
            }
            """, "C", "M");

        var related = await SignatureOverrideHelpers.GetRelatedMethodsAsync(
            method, updateOverrides: false, updateImplementations: false, solution, default);

        Assert.Single(related);
        Assert.True(SymbolEqualityComparer.Default.Equals(method, related[0]));
    }

    [Fact]
    public async Task GetRelatedMethodsAsync_CollectsOverrides_WhenUpdateOverrides()
    {
        var (solution, method) = CreateSolutionWithMethod("""
            public class C
            {
                public virtual void M(int x) { }
            }
            public class D : C
            {
                public override void M(int x) { }
            }
            """, "C", "M");

        var related = await SignatureOverrideHelpers.GetRelatedMethodsAsync(
            method, updateOverrides: true, updateImplementations: false, solution, default);

        var names = related.Select(m => $"{m.ContainingType.Name}.{m.Name}").OrderBy(n => n).ToList();
        Assert.Contains("C.M", names);
        Assert.Contains("D.M", names);
        Assert.Equal(2, related.Count);
    }

    [Fact]
    public async Task GetRelatedMethodsAsync_CollectsInterfaceImplementations_WhenUpdateImplementations()
    {
        var (solution, method) = CreateSolutionWithMethod("""
            public interface I
            {
                void M(int x);
            }
            public class C : I
            {
                public void M(int x) { }
            }
            """, "I", "M");

        var related = await SignatureOverrideHelpers.GetRelatedMethodsAsync(
            method, updateOverrides: false, updateImplementations: true, solution, default);

        var names = related.Select(m => $"{m.ContainingType.Name}.{m.Name}").OrderBy(n => n).ToList();
        Assert.Contains("I.M", names);
        Assert.Contains("C.M", names);
        Assert.Equal(2, related.Count);
    }

    private static (Solution Solution, IMethodSymbol Method) CreateSolutionWithMethod(
        string source, string typeMetadataName, string methodName)
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "P",
            "P",
            LanguageNames.CSharp,
            metadataReferences: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            });
        var project = workspace.AddProject(projectInfo);
        var document = workspace.AddDocument(project.Id, "Code.cs", SourceText.From(source));
        var solution = document.Project.Solution;
        var compilation = document.Project.GetCompilationAsync().Result
            ?? throw new InvalidOperationException("No compilation");
        var type = compilation.GetTypeByMetadataName(typeMetadataName)
            ?? throw new InvalidOperationException($"Type {typeMetadataName} missing");
        var method = type.GetMembers(methodName).OfType<IMethodSymbol>().Single();
        return (solution, method);
    }

}
