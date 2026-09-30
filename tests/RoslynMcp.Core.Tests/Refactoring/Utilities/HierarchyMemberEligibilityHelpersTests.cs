using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="HierarchyMemberEligibilityHelpers"/> —
/// eligibility / project-id helpers previously duplicated on
/// PullMembersUp / PushMembersDown.
/// </summary>
public class HierarchyMemberEligibilityHelpersTests
{
    [Fact]
    public void IsSupportedMember_True_ForOrdinaryMethodPropertyFieldEvent()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M() { }
                public int P { get; set; }
                public int F;
                public event System.EventHandler E;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;

        Assert.True(HierarchyMemberEligibilityHelpers.IsSupportedMember(
            type.GetMembers("M").OfType<IMethodSymbol>().Single()));
        Assert.True(HierarchyMemberEligibilityHelpers.IsSupportedMember(
            type.GetMembers("P").OfType<IPropertySymbol>().Single()));
        Assert.True(HierarchyMemberEligibilityHelpers.IsSupportedMember(
            type.GetMembers("F").OfType<IFieldSymbol>().Single()));
        Assert.True(HierarchyMemberEligibilityHelpers.IsSupportedMember(
            type.GetMembers("E").OfType<IEventSymbol>().Single()));
    }

    [Fact]
    public void IsSupportedMember_False_ForConstructorAndNamedType()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public C() { }
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        var ctor = type.InstanceConstructors.First(c => !c.IsImplicitlyDeclared);

        Assert.False(HierarchyMemberEligibilityHelpers.IsSupportedMember(ctor));
        Assert.False(HierarchyMemberEligibilityHelpers.IsSupportedMember(type));
    }

    [Fact]
    public void IsInterfaceCompatible_True_ForPublicInstanceMethodPropertyEvent()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M() { }
                public int P { get; set; }
                public event System.EventHandler E;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;

        Assert.True(HierarchyMemberEligibilityHelpers.IsInterfaceCompatible(
            type.GetMembers("M").OfType<IMethodSymbol>().Single()));
        Assert.True(HierarchyMemberEligibilityHelpers.IsInterfaceCompatible(
            type.GetMembers("P").OfType<IPropertySymbol>().Single()));
        Assert.True(HierarchyMemberEligibilityHelpers.IsInterfaceCompatible(
            type.GetMembers("E").OfType<IEventSymbol>().Single()));
    }

    [Fact]
    public void IsInterfaceCompatible_False_ForStaticNonPublicField()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public static void StaticM() { }
                private void PrivateM() { }
                public int F;
            }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;

        Assert.False(HierarchyMemberEligibilityHelpers.IsInterfaceCompatible(
            type.GetMembers("StaticM").OfType<IMethodSymbol>().Single()));
        Assert.False(HierarchyMemberEligibilityHelpers.IsInterfaceCompatible(
            type.GetMembers("PrivateM").OfType<IMethodSymbol>().Single()));
        Assert.False(HierarchyMemberEligibilityHelpers.IsInterfaceCompatible(
            type.GetMembers("F").OfType<IFieldSymbol>().Single()));
    }

    [Fact]
    public async Task ResolveSymbolProjectId_ReturnsOwningProject_WhenDeclaringDocumentExists()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp)
            .WithMetadataReferences(new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            });
        var document = workspace.AddDocument(project.Id, "C.cs", SourceText.From("""
            public class C
            {
                public void M() { }
            }
            """));
        var compilation = (await document.Project.GetCompilationAsync())!;
        var type = compilation.GetTypeByMetadataName("C")!;
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();

        var projectId = HierarchyMemberEligibilityHelpers.ResolveSymbolProjectId(
            document.Project.Solution, method);

        Assert.Equal(document.Project.Id, projectId);
    }

    [Fact]
    public void ResolveSymbolProjectId_ReturnsNull_WhenNoDeclaringDocumentInSolution()
    {
        var compilation = CreateCompilation("""
            public class C
            {
                public void M() { }
            }
            """);
        var method = compilation.GetTypeByMetadataName("C")!
            .GetMembers("M").OfType<IMethodSymbol>().Single();
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Empty", LanguageNames.CSharp);

        var projectId = HierarchyMemberEligibilityHelpers.ResolveSymbolProjectId(
            project.Solution, method);

        Assert.Null(projectId);
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            "HierarchyMemberEligibilityHelpersTests",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
