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

    [Fact]
    public void ImplicitlyImplementedInterfaceMembers_ImplicitMethod_YieldsEachInterfaceMemberInOrder()
    {
        var compilation = CreateCompilation("""
            public interface IFoo
            {
                void M();
            }
            public interface IBar
            {
                void M();
            }
            public class C : IFoo, IBar
            {
                public void M() { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();

        var implemented = MethodInterfaceHelpers.ImplicitlyImplementedInterfaceMembers(method, type).ToList();

        Assert.Equal(
            type.AllInterfaces.Select(i => i.Name),
            implemented.Select(m => m.ContainingType.Name));
        Assert.All(implemented, m => Assert.Equal("M", m.Name));
        Assert.True(MethodInterfaceHelpers.ImplicitlyImplementsInterfaceMember(method, type));
    }

    [Fact]
    public void ImplicitlyImplementedInterfaceMembers_ImplicitProperty_YieldsInterfaceProperty()
    {
        var compilation = CreateCompilation("""
            public interface IFoo
            {
                int P { get; }
            }
            public class C : IFoo
            {
                public int P => 1;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var property = type.GetMembers("P").OfType<IPropertySymbol>().Single();

        var implemented = Assert.Single(MethodInterfaceHelpers.ImplicitlyImplementedInterfaceMembers(property, type));

        Assert.Equal("IFoo", implemented.ContainingType.Name);
        Assert.True(MethodInterfaceHelpers.ImplicitlyImplementsInterfaceMember(property, type));
    }

    [Fact]
    public void ImplicitlyImplementedInterfaceMembers_SameNameOverloadThatDoesNotImplement_YieldsNothing()
    {
        var compilation = CreateCompilation("""
            public interface IFoo
            {
                void M();
            }
            public class C : IFoo
            {
                public void M() { }
                public void M(int value) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var overload = type.GetMembers("M").OfType<IMethodSymbol>().Single(m => m.Parameters.Length == 1);

        Assert.Empty(MethodInterfaceHelpers.ImplicitlyImplementedInterfaceMembers(overload, type));
        Assert.False(MethodInterfaceHelpers.ImplicitlyImplementsInterfaceMember(overload, type));
    }

    [Fact]
    public void ImplicitlyImplementedInterfaceMembers_ExplicitImplementation_YieldsNothing()
    {
        // The explicit implementation's Name is "IFoo.M", so the name filter
        // never sees IFoo.M; callers handle explicit implementations first.
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
            .Single(m => m.MethodKind == MethodKind.ExplicitInterfaceImplementation);

        Assert.Empty(MethodInterfaceHelpers.ImplicitlyImplementedInterfaceMembers(method, type));
        Assert.False(MethodInterfaceHelpers.ImplicitlyImplementsInterfaceMember(method, type));
    }

    [Fact]
    public void ImplicitlyImplementedInterfaceMembers_BaseMemberImplementsDerivedInterface_DependsOnContainingType()
    {
        // D re-lists IFoo and B.M satisfies it: the scan is relative to the
        // supplied containing type, not the member's declaring type.
        var compilation = CreateCompilation("""
            public interface IFoo
            {
                void M();
            }
            public class B
            {
                public void M() { }
            }
            public class D : B, IFoo
            {
            }
            """);
        var baseType = compilation.GetTypeByMetadataName("B")!;
        var derived = compilation.GetTypeByMetadataName("D")!;
        var method = baseType.GetMembers("M").OfType<IMethodSymbol>().Single();

        Assert.True(MethodInterfaceHelpers.ImplicitlyImplementsInterfaceMember(method, derived));
        Assert.False(MethodInterfaceHelpers.ImplicitlyImplementsInterfaceMember(method, baseType));
    }

    [Fact]
    public void ImplicitlyImplementedInterfaceMembers_NoInterfaces_YieldsNothing()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M() { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();

        Assert.Empty(MethodInterfaceHelpers.ImplicitlyImplementedInterfaceMembers(method, type));
        Assert.False(MethodInterfaceHelpers.ImplicitlyImplementsInterfaceMember(method, type));
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
