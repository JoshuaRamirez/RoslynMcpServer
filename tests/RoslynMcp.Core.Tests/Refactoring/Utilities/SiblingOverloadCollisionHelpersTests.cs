using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Characterization tests for <see cref="SiblingOverloadCollisionHelpers"/> —
/// core / insert / full-replace / remove mappings folded from
/// ChangeSignature / AddParameter / RemoveParameter.
/// </summary>
public class SiblingOverloadCollisionHelpersTests
{
    [Fact]
    public void WouldCollideWithSibling_NoContainingType_ReturnsFalse()
    {
        // Roslyn 5 reduced extensions retain ContainingType; proxy the null
        // early-return path that the three former copies guarded on.
        var compilation = CreateCompilation("""
            public class C
            {
                public void M(int a) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var declared = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        var method = NullContainingType(declared);
        Assert.Null(method.ContainingType);

        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        Assert.False(SiblingOverloadCollisionHelpers.WouldCollideWithSibling(
            method,
            [(intType, RefKind.None)]));
        Assert.False(SiblingOverloadCollisionHelpers.WouldCollideWithSiblingAfterRemove(method, 0));
    }

    [Fact]
    public void WouldCollideWithSibling_LengthMismatch_ReturnsFalse()
    {
        var (method, sibling, _) = GetOverloads("""
            public class C
            {
                public void M(int a) { }
                public void M(int a, string b) { }
            }
            """, m => m.Parameters.Length == 1);

        // Prospective length does not match sibling → no collision.
        Assert.False(SiblingOverloadCollisionHelpers.WouldCollideWithSibling(
            method,
            [
                (method.Parameters[0].Type, RefKind.None),
                (sibling.Parameters[1].Type, RefKind.None),
                (sibling.Parameters[1].Type, RefKind.None),
            ]));
    }

    [Fact]
    public void WouldCollideWithSibling_ArityMismatch_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M<T>(int a) { }
                public void M(int a) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var generic = type.GetMembers("M").OfType<IMethodSymbol>().Single(m => m.TypeParameters.Length == 1);
        var nonGeneric = type.GetMembers("M").OfType<IMethodSymbol>().Single(m => m.TypeParameters.Length == 0);

        // Same prospective types as the non-generic sibling, but arity differs.
        Assert.False(SiblingOverloadCollisionHelpers.WouldCollideWithSibling(
            generic,
            [(nonGeneric.Parameters[0].Type, RefKind.None)]));
    }

    [Fact]
    public void WouldCollideWithSibling_TypeMismatch_ReturnsFalse()
    {
        var (method, _, compilation) = GetOverloads("""
            public class C
            {
                public void M(int a) { }
                public void M(string a) { }
            }
            """, m => m.Parameters[0].Type.SpecialType == SpecialType.System_Int32);

        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        // Prospective still int — no sibling with (int) besides self.
        Assert.False(SiblingOverloadCollisionHelpers.WouldCollideWithSibling(
            method,
            [(intType, RefKind.None)]));
    }

    [Fact]
    public void WouldCollideWithSibling_RefKindMismatch_ReturnsFalse()
    {
        var (method, sibling, _) = GetOverloads("""
            public class C
            {
                public void M(int a) { }
                public void M(ref int a) { }
            }
            """, m => m.Parameters[0].RefKind == RefKind.None);

        // Prospective RefKind.None vs sibling ref int → distinct overload.
        Assert.False(SiblingOverloadCollisionHelpers.WouldCollideWithSibling(
            method,
            [(sibling.Parameters[0].Type, RefKind.None)]));
    }

    [Fact]
    public void WouldCollideWithSibling_FullReplaceCollision_ReturnsTrue()
    {
        var (tree, model, method, methodDecl) = GetMethodContext("""
            public class C
            {
                public void M(int a) { }
                public void M(string a) { }
            }
            """, methodSelector: m => m.Parameters[0].Type.SpecialType == SpecialType.System_Int32);

        // Full-replace int → string collides with the string sibling.
        Assert.True(SiblingOverloadCollisionHelpers.WouldCollideWithSibling(
            method,
            ["string"],
            model,
            methodDecl.SpanStart));
    }

    [Fact]
    public void WouldCollideWithSibling_FullReplaceErrorType_ReturnsFalse()
    {
        var (tree, model, method, methodDecl) = GetMethodContext("""
            public class C
            {
                public void M(int a) { }
                public void M(string a) { }
            }
            """, methodSelector: m => m.Parameters[0].Type.SpecialType == SpecialType.System_Int32);

        Assert.False(SiblingOverloadCollisionHelpers.WouldCollideWithSibling(
            method,
            ["NotARealType"],
            model,
            methodDecl.SpanStart));
    }

    [Fact]
    public void WouldCollideWithSiblingAfterInsert_TrueCollision_ReturnsTrue()
    {
        var (_, model, method, methodDecl) = GetMethodContext("""
            public class C
            {
                public void M(int a) { }
                public void M(int a, string b) { }
            }
            """, methodSelector: m => m.Parameters.Length == 1);

        // Insert string at end → matches sibling (int, string).
        Assert.True(SiblingOverloadCollisionHelpers.WouldCollideWithSiblingAfterInsert(
            method,
            methodDecl,
            model,
            "string",
            insertIndex: 1));
    }

    [Fact]
    public void WouldCollideWithSiblingAfterInsert_InsertAtFront_TrueCollision()
    {
        var (_, model, method, methodDecl) = GetMethodContext("""
            public class C
            {
                public void M(int a) { }
                public void M(string a, int b) { }
            }
            """, methodSelector: m => m.Parameters.Length == 1);

        Assert.True(SiblingOverloadCollisionHelpers.WouldCollideWithSiblingAfterInsert(
            method,
            methodDecl,
            model,
            "string",
            insertIndex: 0));
    }

    [Fact]
    public void WouldCollideWithSiblingAfterInsert_TypeMismatch_ReturnsFalse()
    {
        var (_, model, method, methodDecl) = GetMethodContext("""
            public class C
            {
                public void M(int a) { }
                public void M(int a, string b) { }
            }
            """, methodSelector: m => m.Parameters.Length == 1);

        Assert.False(SiblingOverloadCollisionHelpers.WouldCollideWithSiblingAfterInsert(
            method,
            methodDecl,
            model,
            "object",
            insertIndex: 1));
    }

    [Fact]
    public void WouldCollideWithSiblingAfterInsert_SiblingRefKind_ReturnsFalse()
    {
        var (_, model, method, methodDecl) = GetMethodContext("""
            public class C
            {
                public void M(int a) { }
                public void M(int a, ref string b) { }
            }
            """, methodSelector: m => m.Parameters.Length == 1);

        // Insert path requires sibling RefKind.None at every slot.
        Assert.False(SiblingOverloadCollisionHelpers.WouldCollideWithSiblingAfterInsert(
            method,
            methodDecl,
            model,
            "string",
            insertIndex: 1));
    }

    [Fact]
    public void WouldCollideWithSiblingAfterRemove_TrueCollision_ReturnsTrue()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M(int a, string b) { }
                public void M(int a) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single(m => m.Parameters.Length == 2);

        Assert.True(SiblingOverloadCollisionHelpers.WouldCollideWithSiblingAfterRemove(method, removeIndex: 1));
    }

    [Fact]
    public void WouldCollideWithSiblingAfterRemove_PreservesOriginalRefKind_Collision()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M(ref int a, string b) { }
                public void M(ref int a) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single(m => m.Parameters.Length == 2);

        // Remove path compares sibling RefKind to the expected original RefKind.
        Assert.True(SiblingOverloadCollisionHelpers.WouldCollideWithSiblingAfterRemove(method, removeIndex: 1));
    }

    [Fact]
    public void WouldCollideWithSiblingAfterRemove_RefKindMismatch_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M(ref int a, string b) { }
                public void M(int a) { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single(m => m.Parameters.Length == 2);

        Assert.False(SiblingOverloadCollisionHelpers.WouldCollideWithSiblingAfterRemove(method, removeIndex: 1));
    }

    [Fact]
    public void WouldCollideWithSiblingAfterRemove_InvalidIndex_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M(int a) { }
                public void M() { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single(m => m.Parameters.Length == 1);

        Assert.False(SiblingOverloadCollisionHelpers.WouldCollideWithSiblingAfterRemove(method, removeIndex: -1));
        Assert.False(SiblingOverloadCollisionHelpers.WouldCollideWithSiblingAfterRemove(method, removeIndex: 1));
    }

    private static (IMethodSymbol Method, IMethodSymbol Sibling, CSharpCompilation Compilation) GetOverloads(
        string source,
        Func<IMethodSymbol, bool> methodSelector)
    {
        var compilation = CreateCompilation(source);
        var type = compilation.GetTypeByMetadataName("C")!;
        var methods = type.GetMembers("M").OfType<IMethodSymbol>().ToList();
        var method = methods.Single(methodSelector);
        var sibling = methods.Single(m => !SymbolEqualityComparer.Default.Equals(m, method));
        return (method, sibling, compilation);
    }

    /// <summary>
    /// Roslyn 5 keeps <see cref="IMethodSymbol.ContainingType"/> on reduced
    /// extensions; proxy null to characterize the shared early-return guard.
    /// </summary>
    private static IMethodSymbol NullContainingType(IMethodSymbol inner)
    {
        var proxy = DispatchProxy.Create<IMethodSymbol, NullContainingTypeMethodProxy>()!;
        ((NullContainingTypeMethodProxy)(object)proxy).Inner = inner;
        return proxy;
    }

    private class NullContainingTypeMethodProxy : DispatchProxy
    {
        public IMethodSymbol Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "get_ContainingType")
                return null;
            return targetMethod.Invoke(Inner, args);
        }
    }

    private static (SyntaxTree Tree, SemanticModel Model, IMethodSymbol Method, MethodDeclarationSyntax MethodDecl)
        GetMethodContext(string source, Func<IMethodSymbol, bool> methodSelector)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "SiblingOverloadCollisionHelpersTests",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single(methodSelector);
        var methodDecl = method.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<MethodDeclarationSyntax>()
            .Single();
        return (tree, model, method, methodDecl);
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            "SiblingOverloadCollisionHelpersTests",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
