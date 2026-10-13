using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Core.Query.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Unit tests for <see cref="NamespaceFilter"/>, the shared <c>namespaceFilter</c> normalization and
/// matching of <c>search_symbols</c> and <c>find_implementations</c>.
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
}
