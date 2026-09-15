using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class MethodSymbolHelpersTests
{
    [Fact]
    public void IsInNameof_InsideNameofInvocation_ReturnsTrue()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
                    _ = nameof(C);
                }
            }
            """);
        var root = tree.GetRoot();
        var identifier = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(n => n.Identifier.Text == "C" &&
                         n.Ancestors().OfType<InvocationExpressionSyntax>().Any());

        Assert.True(MethodSymbolHelpers.IsInNameof(identifier));
    }

    [Fact]
    public void IsInNameof_OutsideNameof_ReturnsFalse()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M()
                {
                    var x = C.Equals(null, null);
                }
            }
            """);
        var root = tree.GetRoot();
        var identifier = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(n => n.Identifier.Text == "C");

        Assert.False(MethodSymbolHelpers.IsInNameof(identifier));
    }

    [Fact]
    public void HasVirtualModifier_VirtualMethod_ReturnsTrue()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public virtual void M() { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();

        Assert.True(MethodSymbolHelpers.HasVirtualModifier(method));
    }

    [Fact]
    public void HasVirtualModifier_NonVirtualMethod_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M() { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();

        Assert.False(MethodSymbolHelpers.HasVirtualModifier(method));
    }

    [Fact]
    public void NormalizeMethodSymbol_OrdinaryMethod_ReturnsOriginalDefinition()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M() { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();

        var normalized = MethodSymbolHelpers.NormalizeMethodSymbol(method);
        Assert.Same(method.OriginalDefinition, normalized);
    }

    [Fact]
    public void NormalizeMethodSymbol_PropertyAccessor_ThrowsInvalidSymbolKind()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public int P { get; set; }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var property = type.GetMembers("P").OfType<IPropertySymbol>().Single();
        var getter = property.GetMethod!;

        var ex = Assert.Throws<RefactoringException>(
            () => MethodSymbolHelpers.NormalizeMethodSymbol(getter));
        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
    }

    [Fact]
    public void NormalizeMethodSymbol_NonMethodSymbol_ThrowsInvalidSymbolKind()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public int F;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var field = type.GetMembers("F").OfType<IFieldSymbol>().Single();

        var ex = Assert.Throws<RefactoringException>(
            () => MethodSymbolHelpers.NormalizeMethodSymbol(field));
        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            "MethodSymbolHelpersTests",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
