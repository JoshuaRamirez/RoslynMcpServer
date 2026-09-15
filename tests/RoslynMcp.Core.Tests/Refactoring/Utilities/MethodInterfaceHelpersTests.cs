using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class MethodInterfaceHelpersTests
{
    [Fact]
    public void ImplementsInterface_ExplicitImplementation_ReturnsTrue()
    {
        var compilation = CreateCompilation("""
            public interface IFoo
            {
                void M();
            }
            public class C : IFoo
            {
                void IFoo.M() { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers().OfType<IMethodSymbol>()
            .Single(m => m.Name.Contains("M") && m.MethodKind == MethodKind.ExplicitInterfaceImplementation);

        Assert.True(MethodInterfaceHelpers.ImplementsInterface(method));
    }

    [Fact]
    public void ImplementsInterface_ImplicitImplementation_ReturnsTrue()
    {
        var compilation = CreateCompilation("""
            public interface IFoo
            {
                void M();
            }
            public class C : IFoo
            {
                public void M() { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();

        Assert.True(MethodInterfaceHelpers.ImplementsInterface(method));
    }

    [Fact]
    public void ImplementsInterface_OrdinaryMethodWithNoInterface_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M() { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();

        Assert.False(MethodInterfaceHelpers.ImplementsInterface(method));
    }

    [Fact]
    public void ImplementsInterface_SameNameAsInterfaceMemberButDoesNotImplement_ReturnsFalse()
    {
        // C implements IFoo via explicit M(); the overload M(int) shares the name
        // but is not an interface implementation.
        var compilation = CreateCompilation("""
            public interface IFoo
            {
                void M();
            }
            public class C : IFoo
            {
                void IFoo.M() { }
                public void M(int value) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>()
            .Single(m => m.Parameters.Length == 1);

        Assert.False(MethodInterfaceHelpers.ImplementsInterface(method));
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            "MethodInterfaceHelpersTests",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
