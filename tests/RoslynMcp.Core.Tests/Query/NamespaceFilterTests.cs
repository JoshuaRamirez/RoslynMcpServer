using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Query.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Unit tests for <see cref="NamespaceFilter"/>, the shared <c>namespaceFilter</c> normalization and
/// matching of <c>search_symbols</c>, <c>find_implementations</c>, and <c>find_references</c>.
/// </summary>
public class NamespaceFilterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("global::")]
    [InlineData("  global::  ")]
    public void Normalize_OmittedBlankOrRoot_ReturnsNull(string? namespaceFilter)
    {
        Assert.Null(NamespaceFilter.Normalize(namespaceFilter));
    }

    [Theory]
    [InlineData("App.Services", "App.Services")]
    [InlineData("  App.Services  ", "App.Services")]
    [InlineData("global::App.Services", "App.Services")]
    [InlineData(" global:: App.Services ", "App.Services")]
    [InlineData("app.services", "app.services")]
    public void Normalize_TrimsAndStripsGlobalPrefix(string namespaceFilter, string expected)
    {
        Assert.Equal(expected, NamespaceFilter.Normalize(namespaceFilter));
    }

    [Theory]
    [InlineData("App.Services", "App.Services.Widget", true)]
    [InlineData("App.Services", "App.Services.Orders.Widget", true)]
    [InlineData("App", "App.Services.Orders.Widget", true)]
    [InlineData("App.Services", "App.ServicesExtra.Widget", false)]
    [InlineData("App.Serv", "App.Services.Widget", false)]
    [InlineData("app.services", "App.Services.Widget", false)]
    [InlineData("App.Services.Orders", "App.Services.Widget", false)]
    [InlineData("App.Services", "Widget", false)]
    public void IsInNamespace_MatchesEqualOrNestedNamespaceOrdinally(string filter, string typeName, bool expected)
    {
        var source = typeName.Contains('.')
            ? $"namespace {typeName[..typeName.LastIndexOf('.')]} {{ public class {typeName[(typeName.LastIndexOf('.') + 1)..]} {{ }} }}"
            : $"public class {typeName} {{ }}";
        var compilation = CSharpCompilation.Create("NsTest", [CSharpSyntaxTree.ParseText(source)]);
        var type = compilation.GetTypeByMetadataName(typeName);
        Assert.NotNull(type);

        Assert.Equal(expected, NamespaceFilter.IsInNamespace(type, filter));
    }

    [Theory]
    [InlineData("App.Services", true)]
    [InlineData("App", true)]
    [InlineData("App.Services.Orders", true)]
    [InlineData("App.Serv", false)]
    [InlineData("app.services.orders", false)]
    public void IsSiteInNamespace_NamespaceEnclosingSymbol_UsesThatNamespace(string filter, bool expected)
    {
        var compilation = CSharpCompilation.Create("NsTest", [CSharpSyntaxTree.ParseText(
            "namespace App.Services.Orders { public class Widget { } }")]);
        var ns = compilation.GetTypeByMetadataName("App.Services.Orders.Widget")!.ContainingNamespace;

        // The site's enclosing symbol is the namespace itself (e.g. a using directive inside it).
        Assert.Equal(expected, NamespaceFilter.IsSiteInNamespace(ns, filter));
    }

    [Theory]
    [InlineData("App.Services", true)]
    [InlineData("App.Services.Orders", false)]
    public void IsSiteInNamespace_MemberEnclosingSymbol_UsesContainingNamespace(string filter, bool expected)
    {
        var compilation = CSharpCompilation.Create("NsTest", [CSharpSyntaxTree.ParseText(
            "namespace App.Services { public class Widget { public void Run() { } } }")]);
        var method = compilation.GetTypeByMetadataName("App.Services.Widget")!.GetMembers("Run").Single();

        Assert.Equal(expected, NamespaceFilter.IsSiteInNamespace(method, filter));
    }

    [Fact]
    public void IsSiteInNamespace_GlobalNamespace_NeverMatches()
    {
        var compilation = CSharpCompilation.Create("NsTest", [CSharpSyntaxTree.ParseText("public class Widget { }")]);

        Assert.False(NamespaceFilter.IsSiteInNamespace(compilation.GlobalNamespace, "App"));
        Assert.False(NamespaceFilter.IsSiteInNamespace(compilation.GetTypeByMetadataName("Widget")!, "App"));
    }
}
