using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Organize.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Organize.Utilities;

/// <summary>
/// Regression tests for #3085: names were compared ordinally, so every uppercase letter sorted
/// before every lowercase one (AutoMapper before Autofac, Zeta before iText).
/// </summary>
public class UsingDirectiveSorterCaseTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sort_MixedCaseNamespaces_AreAlphabeticalIgnoringCase(bool systemFirst)
    {
        var sorted = UsingDirectiveSorter.Sort(Parse("using Zeta;", "using AutoMapper;", "using iText;", "using Autofac;"), systemFirst);

        Assert.Equal(new[] { "Autofac", "AutoMapper", "iText", "Zeta" }, sorted.Select(u => u.Name!.ToString()));
    }

    [Fact]
    public void Sort_AlreadySortedIgnoringCase_KeepsOrder()
    {
        var usings = Parse("using System;", "using Autofac;", "using AutoMapper;", "using iText;", "using Zeta;");

        var sorted = UsingDirectiveSorter.Sort(usings);

        Assert.Equal(usings.Select(u => u.ToString()), sorted.Select(u => u.ToString()));
    }

    [Fact]
    public void Sort_StaticUsings_AreAlphabeticalIgnoringCase()
    {
        var sorted = UsingDirectiveSorter.Sort(Parse("using static Zeta.Z;", "using static iText.T;"));

        Assert.Equal(new[] { "iText.T", "Zeta.Z" }, sorted.Select(u => u.Name!.ToString()));
    }

    [Fact]
    public void Sort_Aliases_AreAlphabeticalIgnoringCase()
    {
        var sorted = UsingDirectiveSorter.Sort(Parse("using Zed = System.String;", "using alpha = System.Int32;"));

        Assert.Equal(new[] { "alpha", "Zed" }, sorted.Select(u => u.Alias!.Name.ToString()));
    }

    [Fact]
    public void Sort_NamesDifferingOnlyInCase_UseOrdinalTieBreak()
    {
        var sorted = UsingDirectiveSorter.Sort(Parse("using abc;", "using ABC;"));

        Assert.Equal(new[] { "ABC", "abc" }, sorted.Select(u => u.Name!.ToString()));
    }

    private static List<UsingDirectiveSyntax> Parse(params string[] lines) =>
        CSharpSyntaxTree.ParseText(string.Join(Environment.NewLine, lines)).GetCompilationUnitRoot().Usings.ToList();
}
