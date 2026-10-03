using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class ExplicitImplementationHelpersTests
{
    [Fact]
    public void IsExplicitImplementation_ExplicitMethod_ReturnsTrue()
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
            .Single(m => m.MethodKind == MethodKind.ExplicitInterfaceImplementation);

        Assert.True(ExplicitImplementationHelpers.IsExplicitImplementation(method));
    }

    [Fact]
    public void IsExplicitImplementation_ImplicitMethod_ReturnsFalse()
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

        Assert.False(ExplicitImplementationHelpers.IsExplicitImplementation(method));
    }

    [Fact]
    public void IsExplicitImplementation_OrdinaryMethod_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M() { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();

        Assert.False(ExplicitImplementationHelpers.IsExplicitImplementation(method));
    }

    [Fact]
    public void IsExplicitImplementation_ExplicitProperty_ReturnsTrue()
    {
        var compilation = CreateCompilation("""
            public interface IFoo
            {
                int P { get; }
            }
            public class C : IFoo
            {
                int IFoo.P => 0;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var property = type.GetMembers().OfType<IPropertySymbol>()
            .Single(p => p.ExplicitInterfaceImplementations.Length > 0);

        Assert.True(ExplicitImplementationHelpers.IsExplicitImplementation(property));
    }

    [Fact]
    public void IsExplicitImplementation_OrdinaryProperty_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public int P { get; set; }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var property = type.GetMembers("P").OfType<IPropertySymbol>().Single();

        Assert.False(ExplicitImplementationHelpers.IsExplicitImplementation(property));
    }

    [Fact]
    public void IsExplicitImplementation_ExplicitEvent_ReturnsTrue()
    {
        var compilation = CreateCompilation("""
            using System;
            public interface IFoo
            {
                event EventHandler E;
            }
            public class C : IFoo
            {
                event EventHandler IFoo.E
                {
                    add { }
                    remove { }
                }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var evt = type.GetMembers().OfType<IEventSymbol>()
            .Single(e => e.ExplicitInterfaceImplementations.Length > 0);

        Assert.True(ExplicitImplementationHelpers.IsExplicitImplementation(evt));
    }

    [Fact]
    public void IsExplicitImplementation_OrdinaryEvent_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            using System;
            public class C
            {
                public event EventHandler E;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var evt = type.GetMembers("E").OfType<IEventSymbol>().Single();

        Assert.False(ExplicitImplementationHelpers.IsExplicitImplementation(evt));
    }

    [Fact]
    public void IsExplicitImplementation_Field_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public int F;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var field = type.GetMembers("F").OfType<IFieldSymbol>().Single();

        Assert.False(ExplicitImplementationHelpers.IsExplicitImplementation(field));
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            "ExplicitImplementationHelpersTests",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
