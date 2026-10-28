using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Query.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Unit tests for <see cref="SiteSymbolFilter"/>, the accessibilityFilter / nameFilter / kindFilter /
/// staticFilter combination that <c>find_references</c> and <c>get_diagnostics</c> apply to a declaring
/// site symbol.
/// </summary>
public class SiteSymbolFilterTests
{
    private const string Source = """
        namespace N
        {
            public class Widget
            {
                public static void Build() { }
                private int _count;
                internal string Label { get; set; } = "";
            }
        }
        """;

    private static readonly CSharpCompilation Compilation = CSharpCompilation.Create(
        "SiteSymbolFilterTests",
        new[] { CSharpSyntaxTree.ParseText(Source) },
        new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

    private static INamedTypeSymbol Widget => Compilation.GetTypeByMetadataName("N.Widget")!;
    private static ISymbol Member(string name) => Widget.GetMembers(name).Single();
    private static INamespaceSymbol Namespace => Widget.ContainingNamespace;

    [Fact]
    public void Create_NoFilters_IsInactiveAndMatchesEverything()
    {
        var filter = SiteSymbolFilter.Create(null, "  ", "", null);

        Assert.False(filter.IsActive);
        Assert.True(filter.Matches(null));
        Assert.True(filter.Matches(Namespace));
        Assert.True(filter.Matches(Widget));
    }

    [Theory]
    [InlineData("public", null, null, null)]
    [InlineData(null, "Widget", null, null)]
    [InlineData(null, null, "Class", null)]
    [InlineData(null, null, null, false)]
    public void Create_AnyFilter_IsActive(string? accessibility, string? name, string? kind, bool? isStatic)
    {
        Assert.True(SiteSymbolFilter.Create(accessibility, name, kind, isStatic).IsActive);
    }

    [Theory]
    [InlineData("public", null, null, null)]
    [InlineData(null, "N", null, null)]
    [InlineData(null, null, "Namespace", null)]
    [InlineData(null, null, null, false)]
    [InlineData(null, null, null, true)]
    public void Matches_ActiveFilter_RejectsNullAndNamespaceSites(
        string? accessibility, string? name, string? kind, bool? isStatic)
    {
        var filter = SiteSymbolFilter.Create(accessibility, name, kind, isStatic);

        Assert.False(filter.Matches(null));
        Assert.False(filter.Matches(Namespace));
    }

    [Theory]
    [InlineData("public", "Build", true)]
    [InlineData("private", "Build", false)]
    [InlineData("private", "_count", true)]
    [InlineData("internal", "Label", true)]
    [InlineData("public", "Label", false)]
    public void Matches_AccessibilityFilter_ComparesDeclaredAccessibilityExactly(
        string accessibility, string member, bool expected)
    {
        Assert.Equal(expected, SiteSymbolFilter.Create(accessibility, null, null, null).Matches(Member(member)));
    }

    [Theory]
    [InlineData("build", "Build", true)]
    [InlineData("  uil ", "Build", true)]
    [InlineData("Label", "_count", false)]
    public void Matches_NameFilter_IsTrimmedCaseInsensitiveSubstring(string name, string member, bool expected)
    {
        Assert.Equal(expected, SiteSymbolFilter.Create(null, name, null, null).Matches(Member(member)));
    }

    [Theory]
    [InlineData("Method", "Build", true)]
    [InlineData("Field", "_count", true)]
    [InlineData("Property", "Label", true)]
    [InlineData("Field", "Label", false)]
    public void Matches_KindFilter_UsesSymbolKindMapper(string kind, string member, bool expected)
    {
        Assert.Equal(expected, SiteSymbolFilter.Create(null, null, kind, null).Matches(Member(member)));
    }

    [Theory]
    [InlineData(true, "Build", true)]
    [InlineData(false, "Build", false)]
    [InlineData(false, "_count", true)]
    [InlineData(true, "_count", false)]
    public void Matches_StaticFilter_ComparesIsStatic(bool isStatic, string member, bool expected)
    {
        Assert.Equal(expected, SiteSymbolFilter.Create(null, null, null, isStatic).Matches(Member(member)));
    }

    [Fact]
    public void Matches_CombinedFilters_RequireEverySetFilter()
    {
        Assert.True(SiteSymbolFilter.Create("public", "Bui", "Method", true).Matches(Member("Build")));
        Assert.False(SiteSymbolFilter.Create("public", "Bui", "Method", false).Matches(Member("Build")));
        Assert.False(SiteSymbolFilter.Create("public", "Xyz", "Method", true).Matches(Member("Build")));
        Assert.False(SiteSymbolFilter.Create("private", "Bui", "Method", true).Matches(Member("Build")));
        Assert.False(SiteSymbolFilter.Create("public", "Bui", "Field", true).Matches(Member("Build")));
    }
}
