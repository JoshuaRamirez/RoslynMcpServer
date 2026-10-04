using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class MoreRestrictiveAccessibilityHelpersTests
{
    [Theory]
    [InlineData(Accessibility.Private, Accessibility.Public, Accessibility.Private)]
    [InlineData(Accessibility.Public, Accessibility.Private, Accessibility.Private)]
    [InlineData(Accessibility.Protected, Accessibility.Internal, Accessibility.Internal)]
    [InlineData(Accessibility.Internal, Accessibility.Protected, Accessibility.Internal)]
    [InlineData(Accessibility.Private, Accessibility.Private, Accessibility.Private)]
    [InlineData(Accessibility.Public, Accessibility.Public, Accessibility.Public)]
    [InlineData(Accessibility.ProtectedAndInternal, Accessibility.Internal, Accessibility.ProtectedAndInternal)]
    [InlineData(Accessibility.ProtectedOrInternal, Accessibility.Public, Accessibility.ProtectedOrInternal)]
    [InlineData(Accessibility.NotApplicable, Accessibility.Public, Accessibility.NotApplicable)]
    [InlineData(Accessibility.Public, Accessibility.NotApplicable, Accessibility.NotApplicable)]
    [InlineData(Accessibility.NotApplicable, Accessibility.Private, Accessibility.NotApplicable)]
    [InlineData(Accessibility.Private, Accessibility.NotApplicable, Accessibility.Private)]
    public void MoreRestrictive_ReturnsMoreRestrictive(
        Accessibility left,
        Accessibility right,
        Accessibility expected)
    {
        Assert.Equal(expected, MoreRestrictiveAccessibilityHelpers.MoreRestrictive(left, right));
    }

    [Theory]
    [InlineData(Accessibility.Private, 0)]
    [InlineData(Accessibility.ProtectedAndInternal, 1)]
    [InlineData(Accessibility.Internal, 2)]
    [InlineData(Accessibility.Protected, 3)]
    [InlineData(Accessibility.ProtectedOrInternal, 4)]
    [InlineData(Accessibility.Public, 5)]
    [InlineData(Accessibility.NotApplicable, 0)]
    public void AccessibilityRank_KnownValues(Accessibility accessibility, int expected)
    {
        Assert.Equal(expected, MoreRestrictiveAccessibilityHelpers.AccessibilityRank(accessibility));
    }

    [Fact]
    public void SameAssembly_SameCompilation_ReturnsTrue()
    {
        var compilation = CreateCompilation("AsmA", "public class C { public int F; }");
        var type = compilation.GetTypeByMetadataName("C")!;
        var field = type.GetMembers("F").Single();

        Assert.True(MoreRestrictiveAccessibilityHelpers.SameAssembly(field, type));
    }

    [Fact]
    public void SameAssembly_DifferentCompilations_ReturnsFalse()
    {
        var compilationA = CreateCompilation("AsmA", "public class A { public int F; }");
        var compilationB = CreateCompilation("AsmB", "public class B { }");
        var field = compilationA.GetTypeByMetadataName("A")!.GetMembers("F").Single();
        var typeB = compilationB.GetTypeByMetadataName("B")!;

        Assert.False(MoreRestrictiveAccessibilityHelpers.SameAssembly(field, typeB));
    }

    [Theory]
    [InlineData(Accessibility.Public, true, true)]
    [InlineData(Accessibility.Protected, true, true)]
    [InlineData(Accessibility.ProtectedOrInternal, true, true)]
    [InlineData(Accessibility.Internal, true, true)]
    [InlineData(Accessibility.ProtectedAndInternal, true, true)]
    [InlineData(Accessibility.Private, true, false)]
    [InlineData(Accessibility.NotApplicable, true, false)]
    [InlineData(Accessibility.Public, false, true)]
    [InlineData(Accessibility.Protected, false, true)]
    [InlineData(Accessibility.ProtectedOrInternal, false, true)]
    [InlineData(Accessibility.Internal, false, false)]
    [InlineData(Accessibility.ProtectedAndInternal, false, false)]
    [InlineData(Accessibility.Private, false, false)]
    [InlineData(Accessibility.NotApplicable, false, false)]
    public void IsAccessibilityVisibleFrom_HonorsAccessibilityAndAssembly(
        Accessibility accessibility,
        bool sameAssembly,
        bool expected)
    {
        var compilationA = CreateCompilation("AsmA", "public class A { public int F; }");
        var field = compilationA.GetTypeByMetadataName("A")!.GetMembers("F").Single();
        var fromType = sameAssembly
            ? compilationA.GetTypeByMetadataName("A")!
            : CreateCompilation("AsmB", "public class B { }").GetTypeByMetadataName("B")!;

        Assert.Equal(
            expected,
            MoreRestrictiveAccessibilityHelpers.IsAccessibilityVisibleFrom(accessibility, field, fromType));
    }

    private static CSharpCompilation CreateCompilation(string assemblyName, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            assemblyName,
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
