using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="AllFilesMethodEligibilityHelpers"/> —
/// IsEligibleForAllFiles gates + ImplementsAnyInterfaceMember /
/// ModuleInitializer / UnmanagedCallersOnly attribute helpers.
/// </summary>
public class AllFilesMethodEligibilityHelpersTests
{
    [Fact]
    public void IsEligibleForAllFiles_OrdinaryMethod_ReturnsTrue()
    {
        var (method, decl) = GetMethodAndDecl("""
            public class C
            {
                public void M() { }
            }
            """, "C", "M");

        Assert.True(AllFilesMethodEligibilityHelpers.IsEligibleForAllFiles(method, decl));
    }

    [Fact]
    public void IsEligibleForAllFiles_ExtensionMethod_ReturnsFalse()
    {
        var (method, decl) = GetMethodAndDecl("""
            public static class Ext
            {
                public static void M(this string s) { }
            }
            """, "Ext", "M");

        Assert.False(AllFilesMethodEligibilityHelpers.IsEligibleForAllFiles(method, decl));
    }

    [Fact]
    public void IsEligibleForAllFiles_PartialMethod_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            public partial class Host
            {
                partial void M();
            }
            public partial class Host
            {
                partial void M() { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("Host")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().First();
        var decl = method.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<MethodDeclarationSyntax>()
            .First();

        Assert.False(AllFilesMethodEligibilityHelpers.IsEligibleForAllFiles(method, decl));
    }

    [Fact]
    public void IsEligibleForAllFiles_ExternMethod_ReturnsFalse()
    {
        var (method, decl) = GetMethodAndDecl("""
            using System.Runtime.InteropServices;
            public class C
            {
                [DllImport("kernel32")]
                public static extern int M();
            }
            """, "C", "M");

        Assert.False(AllFilesMethodEligibilityHelpers.IsEligibleForAllFiles(method, decl));
    }

    [Fact]
    public void IsEligibleForAllFiles_Override_ReturnsFalse()
    {
        var (method, decl) = GetMethodAndDecl("""
            public class Base
            {
                public virtual void M() { }
            }
            public class Derived : Base
            {
                public override void M() { }
            }
            """, "Derived", "M");

        Assert.False(AllFilesMethodEligibilityHelpers.IsEligibleForAllFiles(method, decl));
    }

    [Fact]
    public void IsEligibleForAllFiles_InterfaceMember_ReturnsFalse()
    {
        var (method, decl) = GetMethodAndDecl("""
            public interface IFoo
            {
                void M();
            }
            """, "IFoo", "M");

        Assert.False(AllFilesMethodEligibilityHelpers.IsEligibleForAllFiles(method, decl));
    }

    [Fact]
    public void IsEligibleForAllFiles_ExplicitInterfaceImplementation_ReturnsFalse()
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
        var decl = (MethodDeclarationSyntax)method.DeclaringSyntaxReferences[0].GetSyntax();

        Assert.False(AllFilesMethodEligibilityHelpers.IsEligibleForAllFiles(method, decl));
    }

    [Fact]
    public void IsEligibleForAllFiles_ImplicitInterfaceImplementation_ReturnsFalse()
    {
        var (method, decl) = GetMethodAndDecl("""
            public interface IFoo
            {
                void M();
            }
            public class C : IFoo
            {
                public void M() { }
            }
            """, "C", "M");

        Assert.False(AllFilesMethodEligibilityHelpers.IsEligibleForAllFiles(method, decl));
    }

    [Fact]
    public void IsEligibleForAllFiles_ModuleInitializer_Attribute_ReturnsFalse()
    {
        var (method, decl) = GetMethodAndDecl("""
            using System.Runtime.CompilerServices;
            public static class C
            {
                [ModuleInitializer]
                public static void M() { }
            }
            """, "C", "M");

        Assert.False(AllFilesMethodEligibilityHelpers.IsEligibleForAllFiles(method, decl));
    }

    [Fact]
    public void IsEligibleForAllFiles_UnmanagedCallersOnly_Attribute_ReturnsFalse()
    {
        var (method, decl) = GetMethodAndDecl("""
            using System.Runtime.InteropServices;
            public static class C
            {
                [UnmanagedCallersOnly]
                public static void M() { }
            }
            """, "C", "M");

        Assert.False(AllFilesMethodEligibilityHelpers.IsEligibleForAllFiles(method, decl));
    }

    [Fact]
    public void ImplementsAnyInterfaceMember_Implicit_ReturnsTrue()
    {
        var (method, _) = GetMethodAndDecl("""
            public interface IFoo
            {
                void M();
            }
            public class C : IFoo
            {
                public void M() { }
            }
            """, "C", "M");

        Assert.True(AllFilesMethodEligibilityHelpers.ImplementsAnyInterfaceMember(method));
    }

    [Fact]
    public void ImplementsAnyInterfaceMember_Ordinary_ReturnsFalse()
    {
        var (method, _) = GetMethodAndDecl("""
            public class C
            {
                public void M() { }
            }
            """, "C", "M");

        Assert.False(AllFilesMethodEligibilityHelpers.ImplementsAnyInterfaceMember(method));
    }

    [Fact]
    public void HasModuleInitializerAttribute_SyntacticOnly_ReturnsTrue()
    {
        // Unresolved attribute name still matches the syntactic Contains check.
        var tree = CSharpSyntaxTree.ParseText("""
            public static class C
            {
                [ModuleInitializer]
                public static void M() { }
            }
            """);
        var compilation = CSharpCompilation.Create(
            "AllFilesMethodEligibilityHelpersTests_SynModInit",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var decl = (MethodDeclarationSyntax)method.DeclaringSyntaxReferences[0].GetSyntax();

        Assert.True(AllFilesMethodEligibilityHelpers.HasModuleInitializerAttribute(method, decl));
    }

    [Fact]
    public void HasUnmanagedCallersOnlyAttribute_SyntacticOnly_ReturnsTrue()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            public static class C
            {
                [UnmanagedCallersOnly]
                public static void M() { }
            }
            """);
        var compilation = CSharpCompilation.Create(
            "AllFilesMethodEligibilityHelpersTests_SynUco",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var decl = (MethodDeclarationSyntax)method.DeclaringSyntaxReferences[0].GetSyntax();

        Assert.True(AllFilesMethodEligibilityHelpers.HasUnmanagedCallersOnlyAttribute(method, decl));
    }

    private static (IMethodSymbol Method, MethodDeclarationSyntax Decl) GetMethodAndDecl(
        string source,
        string typeName,
        string methodName)
    {
        var compilation = CreateCompilation(source);
        var type = compilation.GetTypeByMetadataName(typeName)!;
        var method = type.GetMembers(methodName).OfType<IMethodSymbol>().Single();
        var decl = (MethodDeclarationSyntax)method.DeclaringSyntaxReferences[0].GetSyntax();
        return (method, decl);
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };

        // ModuleInitializer / UnmanagedCallersOnly / DllImport live in
        // System.Runtime.CompilerServices / InteropServices; resolve via
        // the runtime assemblies that ship with the test host.
        TryAddRef(refs, typeof(System.Runtime.CompilerServices.ModuleInitializerAttribute).Assembly.Location);
        TryAddRef(refs, typeof(System.Runtime.InteropServices.DllImportAttribute).Assembly.Location);
        TryAddRef(refs, typeof(System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute).Assembly.Location);

        return CSharpCompilation.Create(
            "AllFilesMethodEligibilityHelpersTests",
            [tree],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static void TryAddRef(List<MetadataReference> refs, string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;
        if (refs.Any(r => r.Display == path))
            return;
        refs.Add(MetadataReference.CreateFromFile(path));
    }
}
