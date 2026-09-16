using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="PartialMethodHelpers"/> —
/// CanonicalPartialMethod prefers implementation, GetPartialMethodParts
/// distinct parts, EnumerateDeclaringSyntaxReferences across parts.
/// </summary>
public class PartialMethodHelpersTests
{
    [Fact]
    public void CanonicalPartialMethod_FromDefinition_PrefersImplementation()
    {
        var (definition, implementation) = GetPartialParts();

        var canonical = PartialMethodHelpers.CanonicalPartialMethod(definition);

        Assert.True(SymbolEqualityComparer.Default.Equals(implementation, canonical));
        Assert.NotNull(canonical.PartialDefinitionPart);
        Assert.Null(canonical.PartialImplementationPart);
    }

    [Fact]
    public void CanonicalPartialMethod_FromImplementation_ReturnsImplementation()
    {
        var (_, implementation) = GetPartialParts();

        var canonical = PartialMethodHelpers.CanonicalPartialMethod(implementation);

        Assert.True(SymbolEqualityComparer.Default.Equals(implementation, canonical));
    }

    [Fact]
    public void CanonicalPartialMethod_NonPartial_ReturnsSameMethod()
    {
        var compilation = CreateCompilation("""
            public partial class Host
            {
                public void Ordinary() { }
            }
            """);
        var method = compilation.GetTypeByMetadataName("Host")!
            .GetMembers("Ordinary").OfType<IMethodSymbol>().Single();

        Assert.True(SymbolEqualityComparer.Default.Equals(
            method,
            PartialMethodHelpers.CanonicalPartialMethod(method)));
    }

    [Fact]
    public void GetPartialMethodParts_FromDefinition_ReturnsDistinctParts()
    {
        var (definition, implementation) = GetPartialParts();

        var parts = PartialMethodHelpers.GetPartialMethodParts(definition).ToList();

        Assert.Equal(2, parts.Count);
        Assert.Contains(parts, p => SymbolEqualityComparer.Default.Equals(p, definition));
        Assert.Contains(parts, p => SymbolEqualityComparer.Default.Equals(p, implementation));
    }

    [Fact]
    public void GetPartialMethodParts_FromImplementation_ReturnsDistinctParts()
    {
        var (definition, implementation) = GetPartialParts();

        var parts = PartialMethodHelpers.GetPartialMethodParts(implementation).ToList();

        Assert.Equal(2, parts.Count);
        Assert.Contains(parts, p => SymbolEqualityComparer.Default.Equals(p, definition));
        Assert.Contains(parts, p => SymbolEqualityComparer.Default.Equals(p, implementation));
    }

    [Fact]
    public void GetPartialMethodParts_NonPartial_ReturnsOnlySelf()
    {
        var compilation = CreateCompilation("""
            public class Host
            {
                public void Ordinary() { }
            }
            """);
        var method = compilation.GetTypeByMetadataName("Host")!
            .GetMembers("Ordinary").OfType<IMethodSymbol>().Single();

        var parts = PartialMethodHelpers.GetPartialMethodParts(method).ToList();

        Assert.Single(parts);
        Assert.True(SymbolEqualityComparer.Default.Equals(method, parts[0]));
    }

    [Fact]
    public void EnumerateDeclaringSyntaxReferences_Partial_YieldsBothParts()
    {
        var (definition, _) = GetPartialParts();

        var references = PartialMethodHelpers.EnumerateDeclaringSyntaxReferences(definition).ToList();

        Assert.Equal(2, references.Count);
        var nodes = references.Select(r => r.GetSyntax()).ToList();
        Assert.Contains(nodes, n => n is MethodDeclarationSyntax m && m.Body == null);
        Assert.Contains(nodes, n => n is MethodDeclarationSyntax m && m.Body != null);
    }

    [Fact]
    public void EnumerateDeclaringSyntaxReferences_NonPartial_YieldsSingleReference()
    {
        var compilation = CreateCompilation("""
            public class Host
            {
                public void Ordinary() { }
            }
            """);
        var method = compilation.GetTypeByMetadataName("Host")!
            .GetMembers("Ordinary").OfType<IMethodSymbol>().Single();

        var references = PartialMethodHelpers.EnumerateDeclaringSyntaxReferences(method).ToList();

        Assert.Single(references);
        Assert.IsType<MethodDeclarationSyntax>(references[0].GetSyntax());
    }

    private static (IMethodSymbol Definition, IMethodSymbol Implementation) GetPartialParts()
    {
        var compilation = CreateCompilation("""
            public partial class Host
            {
                public partial void Work();
            }

            public partial class Host
            {
                public partial void Work() { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("Host")!;
        var members = type.GetMembers("Work").OfType<IMethodSymbol>().ToList();
        var seed = members[0];
        var definition = seed.PartialDefinitionPart
            ?? members.Select(m => m.PartialDefinitionPart).FirstOrDefault(d => d != null)
            ?? members.First(m => m.PartialImplementationPart != null);
        var implementation = definition.PartialImplementationPart
            ?? seed.PartialImplementationPart
            ?? members.First(m => m.PartialDefinitionPart != null);

        Assert.NotNull(definition.PartialImplementationPart);
        Assert.NotNull(implementation.PartialDefinitionPart);
        return (definition, implementation);
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            "PartialMethodHelpersTests",
            [tree],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
