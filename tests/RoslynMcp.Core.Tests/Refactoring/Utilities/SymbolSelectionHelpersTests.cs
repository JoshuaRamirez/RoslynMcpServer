using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class SymbolSelectionHelpersTests
{
    [Fact]
    public void ConfirmSymbolName_ReturnsSymbol_WhenExpectedNameNullOrWhitespace()
    {
        var compilation = Compile("""
            class C
            {
                public void M() { }
            }
            """);
        var method = compilation.GetTypeByMetadataName("C")!.GetMembers("M").OfType<IMethodSymbol>().Single();

        Assert.Same(method, SymbolSelectionHelpers.ConfirmSymbolName(method, null));
        Assert.Same(method, SymbolSelectionHelpers.ConfirmSymbolName(method, ""));
        Assert.Same(method, SymbolSelectionHelpers.ConfirmSymbolName(method, "   "));
    }

    [Fact]
    public void ConfirmSymbolName_ReturnsSymbol_WhenExpectedNameMatches()
    {
        var compilation = Compile("""
            class C
            {
                public void M() { }
            }
            """);
        var method = compilation.GetTypeByMetadataName("C")!.GetMembers("M").OfType<IMethodSymbol>().Single();

        Assert.Same(method, SymbolSelectionHelpers.ConfirmSymbolName(method, "M"));
    }

    [Fact]
    public void ConfirmSymbolName_Throws_WhenExpectedNameDiffers()
    {
        var compilation = Compile("""
            class C
            {
                public void M() { }
            }
            """);
        var method = compilation.GetTypeByMetadataName("C")!.GetMembers("M").OfType<IMethodSymbol>().Single();

        var ex = Assert.Throws<RefactoringException>(() =>
            SymbolSelectionHelpers.ConfirmSymbolName(method, "Other"));
        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Contains("Other", ex.Message);
    }

    [Fact]
    public void IsDefinitionLocation_True_WhenSourceTreeAndSpanMatch()
    {
        var compilation = Compile("""
            class C
            {
                public void M() { }
            }
            """);
        var method = compilation.GetTypeByMetadataName("C")!.GetMembers("M").OfType<IMethodSymbol>().Single();
        var location = method.Locations.Single(l => l.IsInSource);

        Assert.True(SymbolSelectionHelpers.IsDefinitionLocation(method, location));
    }

    [Fact]
    public void IsDefinitionLocation_False_WhenSourceSpanDiffers()
    {
        var compilation = Compile("""
            class C
            {
                public void M() { }
                public void N() { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var m = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var n = type.GetMembers("N").OfType<IMethodSymbol>().Single();
        var nLocation = n.Locations.Single(l => l.IsInSource);

        Assert.False(SymbolSelectionHelpers.IsDefinitionLocation(m, nLocation));
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
            "SymbolSelectionHelpersTests",
            new[] { tree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var diagnostics = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(diagnostics.Length == 0, string.Join("\n", diagnostics.Select(d => d.ToString())));
        return compilation;
    }
}
