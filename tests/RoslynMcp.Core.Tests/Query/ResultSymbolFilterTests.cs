using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Query.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Unit tests for <see cref="ResultSymbolFilter"/>, the namespaceFilter / accessibilityFilter / kindFilter /
/// nameFilter / staticFilter / abstractFilter combination that <c>find_implementations</c>,
/// <c>get_type_hierarchy</c> and <c>get_symbol_info</c> (members, no abstractFilter) apply to each returned symbol.
/// </summary>
public class ResultSymbolFilterTests
{
    private const string Source = """
        namespace Outer
        {
            public abstract class Shape
            {
                public abstract double Area();
                public static int Count;
            }

            internal sealed class Circle : Shape
            {
                public override double Area() => 0;
            }

            namespace Inner
            {
                public record Square;
                public interface IDrawable { void Draw(); }
            }
        }

        public class GlobalWidget { }
        """;

    private static readonly CSharpCompilation Compilation = CSharpCompilation.Create(
        "ResultSymbolFilterTests",
        new[] { CSharpSyntaxTree.ParseText(Source) },
        new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

    private static INamedTypeSymbol Type(string metadataName) => Compilation.GetTypeByMetadataName(metadataName)!;
    private static ISymbol Member(string type, string name) => Type(type).GetMembers(name).Single();

    private static ResultSymbolFilter Filter(
        string? ns = null,
        string? accessibility = null,
        string? kind = null,
        string? name = null,
        bool? isStatic = null,
        bool? isAbstract = null) =>
        ResultSymbolFilter.Create(ns, accessibility, kind, name, isStatic, isAbstract);

    [Theory]
    [InlineData("Outer.Shape")]
    [InlineData("Outer.Circle")]
    [InlineData("Outer.Inner.Square")]
    [InlineData("GlobalWidget")]
    public void Create_NoFilters_MatchesEverything(string metadataName)
    {
        Assert.True(Filter().Matches(Type(metadataName)));
        Assert.True(Filter(" ", "", "  ", "").Matches(Type(metadataName)));
        Assert.True(Filter(ns: "global::").Matches(Type(metadataName)));
    }

    [Theory]
    [InlineData("Outer", "Outer.Shape", true)]
    [InlineData("Outer", "Outer.Inner.Square", true)]
    [InlineData("global::Outer.Inner", "Outer.Inner.Square", true)]
    [InlineData("Outer.Inner", "Outer.Circle", false)]
    [InlineData("Out", "Outer.Shape", false)]
    [InlineData("Outer", "GlobalWidget", false)]
    public void Matches_NamespaceFilter_MatchesContainingOrNestedNamespace(
        string ns, string metadataName, bool expected)
    {
        Assert.Equal(expected, Filter(ns: ns).Matches(Type(metadataName)));
    }

    [Fact]
    public void Matches_NamespaceFilter_UsesMemberContainingTypeNamespace()
    {
        Assert.True(Filter(ns: "Outer").Matches(Member("Outer.Circle", "Area")));
        Assert.False(Filter(ns: "Outer.Inner").Matches(Member("Outer.Circle", "Area")));
    }

    [Theory]
    [InlineData("public", "Outer.Shape", true)]
    [InlineData("internal", "Outer.Circle", true)]
    [InlineData("public", "Outer.Circle", false)]
    [InlineData("private", "Outer.Shape", false)]
    public void Matches_AccessibilityFilter_ComparesDeclaredAccessibilityExactly(
        string accessibility, string metadataName, bool expected)
    {
        Assert.Equal(expected, Filter(accessibility: accessibility).Matches(Type(metadataName)));
    }

    [Theory]
    [InlineData("Class", "Outer.Shape", true)]
    [InlineData("Record", "Outer.Inner.Square", true)]
    [InlineData("Class", "Outer.Inner.Square", false)]
    [InlineData("Interface", "Outer.Inner.IDrawable", true)]
    [InlineData("Interface", "Outer.Circle", false)]
    public void Matches_KindFilter_UsesSymbolKindMapper(string kind, string metadataName, bool expected)
    {
        Assert.Equal(expected, Filter(kind: kind).Matches(Type(metadataName)));
    }

    [Theory]
    [InlineData("circ", "Outer.Circle", true)]
    [InlineData("  HAP ", "Outer.Shape", true)]
    [InlineData("Square", "Outer.Circle", false)]
    public void Matches_NameFilter_IsTrimmedCaseInsensitiveSubstring(string name, string metadataName, bool expected)
    {
        Assert.Equal(expected, Filter(name: name).Matches(Type(metadataName)));
    }

    [Fact]
    public void Matches_StaticFilter_ComparesIsStatic()
    {
        Assert.True(Filter(isStatic: true).Matches(Member("Outer.Shape", "Count")));
        Assert.False(Filter(isStatic: false).Matches(Member("Outer.Shape", "Count")));
        Assert.True(Filter(isStatic: false).Matches(Member("Outer.Shape", "Area")));
        Assert.False(Filter(isStatic: true).Matches(Type("Outer.Circle")));
    }

    [Theory]
    [InlineData(true, "Outer.Shape", true)]
    [InlineData(false, "Outer.Shape", false)]
    [InlineData(false, "Outer.Circle", true)]
    [InlineData(true, "Outer.Circle", false)]
    [InlineData(true, "Outer.Inner.IDrawable", true)]
    public void Matches_AbstractFilter_ComparesIsAbstract(bool isAbstract, string metadataName, bool expected)
    {
        Assert.Equal(expected, Filter(isAbstract: isAbstract).Matches(Type(metadataName)));
    }

    [Fact]
    public void Matches_AbstractFilter_JudgesMembers()
    {
        Assert.True(Filter(isAbstract: true).Matches(Member("Outer.Shape", "Area")));
        Assert.False(Filter(isAbstract: true).Matches(Member("Outer.Circle", "Area")));
        Assert.True(Filter(isAbstract: false).Matches(Member("Outer.Circle", "Area")));
    }

    [Fact]
    public void Matches_CombinedFilters_RequireEverySetFilter()
    {
        var circle = Type("Outer.Circle");

        Assert.True(Filter("Outer", "internal", "Class", "irc", false, false).Matches(circle));
        Assert.False(Filter("Outer.Inner", "internal", "Class", "irc", false, false).Matches(circle));
        Assert.False(Filter("Outer", "public", "Class", "irc", false, false).Matches(circle));
        Assert.False(Filter("Outer", "internal", "Record", "irc", false, false).Matches(circle));
        Assert.False(Filter("Outer", "internal", "Class", "xyz", false, false).Matches(circle));
        Assert.False(Filter("Outer", "internal", "Class", "irc", true, false).Matches(circle));
        Assert.False(Filter("Outer", "internal", "Class", "irc", false, true).Matches(circle));
    }

    [Fact]
    public void Matches_MemberFilters_WithoutAbstractFilter_SelectTypeMembers()
    {
        // get_symbol_info shape: kind / accessibility / name / namespace / static over a type's members,
        // abstractFilter always null.
        var members = Type("Outer.Shape").GetMembers()
            .Where(m => !m.IsImplicitlyDeclared && m.CanBeReferencedByName)
            .ToList();

        static string[] Names(IEnumerable<ISymbol> symbols) => symbols.Select(m => m.Name).ToArray();

        Assert.Equal(["Area", "Count"], Names(members.Where(Filter().Matches)));
        Assert.Equal(["Count"], Names(members.Where(Filter("Outer", "public", "Field", "cou", true).Matches)));
        Assert.Equal(["Area"], Names(members.Where(Filter("Outer", "public", "Method", "AREA", false).Matches)));
        Assert.DoesNotContain(members, Filter("Outer.Inner", "public", "Field", "cou", true).Matches);
        Assert.DoesNotContain(members, Filter("Outer", "private", "Field", "cou", true).Matches);
        Assert.DoesNotContain(members, Filter("Outer", "public", "Method", "cou", true).Matches);
        Assert.DoesNotContain(members, Filter("Outer", "public", "Field", "area", true).Matches);
        Assert.DoesNotContain(members, Filter("Outer", "public", "Field", "cou", false).Matches);
    }
}
