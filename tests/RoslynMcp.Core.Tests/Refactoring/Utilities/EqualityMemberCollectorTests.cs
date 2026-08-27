using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// API-shape and collection tests for <see cref="EqualityMemberCollector"/>.
/// The two- and three-parameter overloads must remain for NuGet binary compatibility.
/// </summary>
public class EqualityMemberCollectorTests
{
    private const string HierarchySource = """
        public class Grand
        {
            public string GrandField;
            public string GrandProp { get; set; }
            private string GrandPrivate;
            protected string GrandProtected;
            internal string GrandInternal;
            public static string GrandStatic;
            public const string GrandConst = "c";
            public string this[int i] => GrandField;
        }

        public class Parent : Grand
        {
            public string ParentField;
            public string ParentProp { get; set; }
            private string ParentPrivate;
        }

        public class Child : Parent
        {
            public string ChildField;
            public string ChildProp { get; set; }
            private string ChildPrivate;
        }

        public class ObjectOnly
        {
            public string Name;
        }

        public class NamedBase
        {
            public string Name;
            public virtual string Title { get; set; }
        }

        public class NamedOverride : NamedBase
        {
            public override string Title { get; set; }
        }

        public class NamedHider : NamedBase
        {
            public new string Name;
        }

        public class IntermediatePrivateHider : NamedBase
        {
            private new string Name;
        }

        public class DerivedPastPrivateHider : IntermediatePrivateHider
        {
            public string Extra;
        }

        public class IntermediateStaticHider : NamedBase
        {
            public new static string Name;
        }

        public class DerivedPastStaticHider : IntermediateStaticHider
        {
            public string Extra;
        }
        """;

    [Fact]
    public void CollectMembers_TwoAndThreeParameterOverloads_ArePreserved()
    {
        var twoArg = typeof(EqualityMemberCollector).GetMethod(
            nameof(EqualityMemberCollector.CollectMembers),
            new[] { typeof(INamedTypeSymbol), typeof(IReadOnlyList<string>) });
        var threeArg = typeof(EqualityMemberCollector).GetMethod(
            nameof(EqualityMemberCollector.CollectMembers),
            new[] { typeof(INamedTypeSymbol), typeof(IReadOnlyList<string>), typeof(bool) });
        var fourArg = typeof(EqualityMemberCollector).GetMethod(
            nameof(EqualityMemberCollector.CollectMembers),
            new[] { typeof(INamedTypeSymbol), typeof(IReadOnlyList<string>), typeof(bool), typeof(bool) });

        Assert.NotNull(twoArg);
        Assert.NotNull(threeArg);
        Assert.NotNull(fourArg);
        Assert.NotSame(twoArg, threeArg);
        Assert.NotSame(threeArg, fourArg);
        Assert.Equal(typeof(List<ISymbol>), twoArg.ReturnType);
        Assert.Equal(typeof(List<ISymbol>), threeArg.ReturnType);
        Assert.Equal(typeof(List<ISymbol>), fourArg.ReturnType);
    }

    [Fact]
    public void CollectMembers_TwoParameter_IsThisTypeOnlyAndIncludesProperties()
    {
        var child = GetType("Child");

        var members = EqualityMemberCollector.CollectMembers(child);

        Assert.Equal(new[] { "ChildField", "ChildPrivate", "ChildProp" }, Names(members));
    }

    [Fact]
    public void CollectMembers_ThreeParameter_ForwardsIncludeInheritedMembersFalse()
    {
        var child = GetType("Child");

        var members = EqualityMemberCollector.CollectMembers(child, requestedFields: null, includeProperties: true);

        Assert.Equal(new[] { "ChildField", "ChildPrivate", "ChildProp" }, Names(members));
        Assert.DoesNotContain("ParentField", Names(members));
        Assert.DoesNotContain("GrandField", Names(members));
    }

    [Fact]
    public void CollectMembers_IncludeInheritedMembersFalse_DoesNotCollectBaseMembers()
    {
        var child = GetType("Child");

        var members = EqualityMemberCollector.CollectMembers(child, null, includeProperties: true, includeInheritedMembers: false);

        Assert.Equal(new[] { "ChildField", "ChildPrivate", "ChildProp" }, Names(members));
    }

    [Fact]
    public void CollectMembers_IncludeInheritedMembersTrue_AppendsAccessibleBaseMembersImmediateFirst()
    {
        var child = GetType("Child");

        var members = EqualityMemberCollector.CollectMembers(child, null, includeProperties: true, includeInheritedMembers: true);

        Assert.Equal(
            new[]
            {
                "ChildField", "ChildPrivate", "ChildProp",
                "ParentField", "ParentProp",
                "GrandField", "GrandProtected", "GrandInternal", "GrandProp"
            },
            Names(members));
        Assert.DoesNotContain("ParentPrivate", Names(members));
        Assert.DoesNotContain("GrandPrivate", Names(members));
        Assert.DoesNotContain("GrandStatic", Names(members));
        Assert.DoesNotContain("GrandConst", Names(members));
    }

    [Fact]
    public void CollectMembers_IncludeInheritedMembersTrue_SkipsPrivateBaseField()
    {
        var child = GetType("Child");

        var members = EqualityMemberCollector.CollectMembers(child, null, includeProperties: false, includeInheritedMembers: true);

        Assert.Contains("GrandProtected", Names(members));
        Assert.DoesNotContain("GrandPrivate", Names(members));
        Assert.DoesNotContain("ParentPrivate", Names(members));
        Assert.Contains("ChildPrivate", Names(members));
    }

    [Fact]
    public void CollectMembers_IncludeInheritedMembersTrue_IncludePropertiesFalse_SkipsBaseProperties()
    {
        var child = GetType("Child");

        var members = EqualityMemberCollector.CollectMembers(child, null, includeProperties: false, includeInheritedMembers: true);

        Assert.Equal(
            new[] { "ChildField", "ChildPrivate", "ParentField", "GrandField", "GrandProtected", "GrandInternal" },
            Names(members));
        Assert.DoesNotContain("ChildProp", Names(members));
        Assert.DoesNotContain("ParentProp", Names(members));
        Assert.DoesNotContain("GrandProp", Names(members));
    }

    [Fact]
    public void CollectMembers_IncludeInheritedMembersTrue_FieldsNamesInheritedMember_IncludesIt()
    {
        var child = GetType("Child");

        var members = EqualityMemberCollector.CollectMembers(
            child, new[] { "GrandField", "ParentProp" }, includeProperties: false, includeInheritedMembers: true);

        Assert.Equal(new[] { "ParentProp", "GrandField" }, Names(members));
    }

    [Fact]
    public void CollectMembers_IncludeInheritedMembersFalse_FieldsNamesInheritedMember_NotFound()
    {
        var child = GetType("Child");

        var members = EqualityMemberCollector.CollectMembers(
            child, new[] { "GrandField" }, includeProperties: true, includeInheritedMembers: false);

        Assert.Empty(members);
    }

    [Fact]
    public void CollectMembers_IncludeInheritedMembersTrue_ObjectOnlyBase_NoExtraMembers()
    {
        var type = GetType("ObjectOnly");

        var members = EqualityMemberCollector.CollectMembers(type, null, includeProperties: true, includeInheritedMembers: true);

        Assert.Equal(new[] { "Name" }, Names(members));
    }

    [Fact]
    public void CollectMembers_IncludeInheritedMembersTrue_Override_SkipsBaseProperty()
    {
        var type = GetType("NamedOverride");

        var members = EqualityMemberCollector.CollectMembers(type, null, includeProperties: true, includeInheritedMembers: true);

        Assert.Equal(new[] { "Title", "Name" }, Names(members));
        Assert.Equal(1, members.Count(m => m.Name == "Title"));
        Assert.Equal("NamedOverride", members.Single(m => m.Name == "Title").ContainingType.Name);
    }

    [Fact]
    public void CollectMembers_IncludeInheritedMembersTrue_NewField_SkipsHiddenBaseField()
    {
        var type = GetType("NamedHider");

        var members = EqualityMemberCollector.CollectMembers(type, null, includeProperties: true, includeInheritedMembers: true);

        Assert.Equal(new[] { "Name", "Title" }, Names(members));
        Assert.Equal(1, members.Count(m => m.Name == "Name"));
        Assert.Equal("NamedHider", members.Single(m => m.Name == "Name").ContainingType.Name);
    }

    [Fact]
    public void CollectMembers_IncludeInheritedMembersTrue_PrivateIntermediateHider_SkipsBaseField()
    {
        var type = GetType("DerivedPastPrivateHider");

        var members = EqualityMemberCollector.CollectMembers(type, null, includeProperties: false, includeInheritedMembers: true);

        Assert.Equal(new[] { "Extra" }, Names(members));
        Assert.DoesNotContain("Name", Names(members));
    }

    [Fact]
    public void CollectMembers_IncludeInheritedMembersTrue_StaticIntermediateHider_SkipsBaseField()
    {
        var type = GetType("DerivedPastStaticHider");

        var members = EqualityMemberCollector.CollectMembers(type, null, includeProperties: false, includeInheritedMembers: true);

        Assert.Equal(new[] { "Extra" }, Names(members));
        Assert.DoesNotContain("Name", Names(members));
    }

    [Fact]
    public void CollectMembers_TwoParameter_RequestedInheritedName_NotFound()
    {
        var child = GetType("Child");

        var members = EqualityMemberCollector.CollectMembers(child, new[] { "ParentField" });

        Assert.Empty(members);
    }

    private static INamedTypeSymbol GetType(string name)
    {
        var tree = CSharpSyntaxTree.ParseText(HierarchySource);
        var compilation = CSharpCompilation.Create("TestAssembly")
            .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddSyntaxTrees(tree);
        var model = compilation.GetSemanticModel(tree);
        var decl = tree.GetCompilationUnitRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .First(c => c.Identifier.Text == name);
        return model.GetDeclaredSymbol(decl)
            ?? throw new InvalidOperationException($"Could not resolve type '{name}'.");
    }

    private static string[] Names(IEnumerable<ISymbol> members) =>
        members.Select(m => m.Name).ToArray();
}
