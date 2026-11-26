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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sort_NamesDifferingOnlyInCase_PutLowercaseFirst(bool systemFirst)
    {
        // Roslyn's TokenComparer breaks a case-insensitive tie case-sensitively, lowercase first.
        var sorted = UsingDirectiveSorter.Sort(Parse("using ABC;", "using abc;"), systemFirst);

        Assert.Equal(new[] { "abc", "ABC" }, sorted.Select(u => u.Name!.ToString()));
    }

    [Fact]
    public void Sort_AliasesDifferingOnlyInCase_PutLowercaseFirst()
    {
        var sorted = UsingDirectiveSorter.Sort(Parse("using X = System.String;", "using x = System.Int32;"));

        Assert.Equal(new[] { "x", "X" }, sorted.Select(u => u.Alias!.Name.ToString()));
    }

    [Theory]
    [InlineData("Abc", "Ab_c")]
    [InlineData("Az", "A_B")]
    [InlineData("Ab", "A_b")]
    public void Sort_Underscore_SortsBeforeLetters(string letterName, string underscoreName)
    {
        // Roslyn compares like Visual Studio (invariant culture), where '_' precedes letters; an
        // OrdinalIgnoreCase compare upper-cases and put '_' (0x5F) after 'A'-'Z' (#3085 review).
        var sorted = UsingDirectiveSorter.Sort(Parse($"using {letterName};", $"using {underscoreName};"));

        Assert.Equal(new[] { underscoreName, letterName }, sorted.Select(u => u.Name!.ToString()));
    }

    [Fact]
    public void Sort_Underscore_InStaticAndAliasGroups_SortsBeforeLetters()
    {
        var sorted = UsingDirectiveSorter.Sort(Parse(
            "using static Ab.Abc;", "using static Ab.Ab_c;", "using Az = System.String;", "using A_B = System.Int32;"));

        Assert.Equal(
            new[] { "using static Ab.Ab_c;", "using static Ab.Abc;", "using A_B = System.Int32;", "using Az = System.String;" },
            sorted.Select(u => u.ToString()));
    }

    [Fact]
    public void Sort_DottedNames_CompareSegmentBySegment()
    {
        // Roslyn's NameSyntaxComparer compares each dotted segment, and a prefix sorts first:
        // A.Z before A_B (segment "A" is a prefix of "A_B"), even though '_' precedes '.' as text.
        var sorted = UsingDirectiveSorter.Sort(Parse("using A_B;", "using A.Z;", "using A;"));

        Assert.Equal(new[] { "A", "A.Z", "A_B" }, sorted.Select(u => u.Name!.ToString()));
    }

    private static List<UsingDirectiveSyntax> Parse(params string[] lines) =>
        CSharpSyntaxTree.ParseText(string.Join(Environment.NewLine, lines)).GetCompilationUnitRoot().Usings.ToList();
}
