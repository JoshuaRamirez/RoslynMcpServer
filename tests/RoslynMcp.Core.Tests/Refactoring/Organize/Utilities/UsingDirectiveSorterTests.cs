using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Organize.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Organize.Utilities;

/// <summary>
/// Unit tests for UsingDirectiveSorter.
/// Validates sorting behavior for regular, static, and alias using directives.
/// </summary>
public class UsingDirectiveSorterTests
{
    #region Regular Using Tests

    [Fact]
    public void Sort_RegularUsings_SortedAlphabetically()
    {
        // Arrange
        var usings = ParseUsings(
            "using Zebra;",
            "using Apple;",
            "using Microsoft.Extensions;");

        // Act
        var sorted = UsingDirectiveSorter.Sort(usings);

        // Assert
        Assert.Equal(3, sorted.Count);
        Assert.Equal("Apple", GetNamespaceName(sorted[0]));
        Assert.Equal("Microsoft.Extensions", GetNamespaceName(sorted[1]));
        Assert.Equal("Zebra", GetNamespaceName(sorted[2]));
    }

    [Fact]
    public void Sort_SystemNamespaces_PlacedFirst()
    {
        // Arrange
        var usings = ParseUsings(
            "using MyApp.Services;",
            "using System.Collections.Generic;",
            "using ThirdParty;",
            "using System;");

        // Act
        var sorted = UsingDirectiveSorter.Sort(usings);

        // Assert
        Assert.Equal(4, sorted.Count);
        Assert.Equal("System", GetNamespaceName(sorted[0]));
        Assert.Equal("System.Collections.Generic", GetNamespaceName(sorted[1]));
        Assert.Equal("MyApp.Services", GetNamespaceName(sorted[2]));
        Assert.Equal("ThirdParty", GetNamespaceName(sorted[3]));
    }

    [Fact]
    public void Sort_SystemPrefixedNonSystem_NotGroupedWithSystem()
    {
        // Arrange - "SystemX" should NOT be grouped with "System"
        var usings = ParseUsings(
            "using SystemX.Something;",
            "using System;",
            "using MyApp;");

        // Act
        var sorted = UsingDirectiveSorter.Sort(usings);

        // Assert
        Assert.Equal("System", GetNamespaceName(sorted[0]));
        Assert.Equal("MyApp", GetNamespaceName(sorted[1]));
        Assert.Equal("SystemX.Something", GetNamespaceName(sorted[2]));
    }

    #endregion

    #region Static Using Tests

    [Fact]
    public void Sort_StaticUsings_PlacedAfterRegular()
    {
        // Arrange
        var usings = ParseUsings(
            "using static System.Math;",
            "using System;",
            "using MyApp;");

        // Act
        var sorted = UsingDirectiveSorter.Sort(usings);

        // Assert
        Assert.Equal(3, sorted.Count);
        // Regular usings first
        Assert.Equal("System", GetNamespaceName(sorted[0]));
        Assert.False(IsStaticUsing(sorted[0]));
        Assert.Equal("MyApp", GetNamespaceName(sorted[1]));
        Assert.False(IsStaticUsing(sorted[1]));
        // Static usings after
        Assert.Equal("System.Math", GetNamespaceName(sorted[2]));
        Assert.True(IsStaticUsing(sorted[2]));
    }

    [Fact]
    public void Sort_StaticKeywordDetection_IsCorrect()
    {
        // Arrange - This test verifies the BUG-NEW-001 fix
        // Regular usings should NOT be misclassified as static
        var usings = ParseUsings(
            "using System;",
            "using System.Collections.Generic;",
            "using static System.Console;");

        // Act
        var sorted = UsingDirectiveSorter.Sort(usings);

        // Assert
        // First two should be regular (not static)
        Assert.False(IsStaticUsing(sorted[0]), "System should be regular, not static");
        Assert.False(IsStaticUsing(sorted[1]), "System.Collections.Generic should be regular, not static");
        // Last one should be static
        Assert.True(IsStaticUsing(sorted[2]), "System.Console should be static");
        // Verify ordering: regular before static
        Assert.Equal("System", GetNamespaceName(sorted[0]));
        Assert.Equal("System.Collections.Generic", GetNamespaceName(sorted[1]));
        Assert.Equal("System.Console", GetNamespaceName(sorted[2]));
    }

    [Fact]
    public void Sort_SystemStaticUsings_SystemFirstThenAlphabetical()
    {
        // Arrange
        var usings = ParseUsings(
            "using static MyApp.Helpers;",
            "using static System.Math;",
            "using static ThirdParty.Utils;",
            "using static System.Console;");

        // Act
        var sorted = UsingDirectiveSorter.Sort(usings);

        // Assert - All are static, so sorted by System first then alphabetical
        Assert.Equal("System.Console", GetNamespaceName(sorted[0]));
        Assert.Equal("System.Math", GetNamespaceName(sorted[1]));
        Assert.Equal("MyApp.Helpers", GetNamespaceName(sorted[2]));
        Assert.Equal("ThirdParty.Utils", GetNamespaceName(sorted[3]));
    }

    #endregion

    #region Alias Using Tests

    [Fact]
    public void Sort_AliasUsings_PlacedAfterStatic()
    {
        // Arrange
        var usings = ParseUsings(
            "using Alias = MyApp.VeryLongNamespace;",
            "using static System.Math;",
            "using System;");

        // Act
        var sorted = UsingDirectiveSorter.Sort(usings);

        // Assert
        Assert.Equal(3, sorted.Count);
        // Regular first
        Assert.Equal("System", GetNamespaceName(sorted[0]));
        Assert.False(IsStaticUsing(sorted[0]));
        Assert.False(IsAliasUsing(sorted[0]));
        // Static second
        Assert.Equal("System.Math", GetNamespaceName(sorted[1]));
        Assert.True(IsStaticUsing(sorted[1]));
        Assert.False(IsAliasUsing(sorted[1]));
        // Alias last
        Assert.True(IsAliasUsing(sorted[2]));
    }

    [Fact]
    public void Sort_AliasUsings_SortedByAliasName()
    {
        // Arrange
        var usings = ParseUsings(
            "using Zebra = MyApp.Z;",
            "using Alpha = MyApp.A;",
            "using Middle = MyApp.M;");

        // Act
        var sorted = UsingDirectiveSorter.Sort(usings);

        // Assert
        Assert.Equal("Alpha", GetAliasName(sorted[0]));
        Assert.Equal("Middle", GetAliasName(sorted[1]));
        Assert.Equal("Zebra", GetAliasName(sorted[2]));
    }

    #endregion

    #region Mixed and Edge Case Tests

    [Fact]
    public void Sort_MixedAll_CorrectGroupingAndOrder()
    {
        // Arrange - Mix of regular, static, and alias usings
        var usings = ParseUsings(
            "using Z = MyApp.Zeta;",
            "using static ThirdParty.Extensions;",
            "using ThirdParty;",
            "using static System.Math;",
            "using System.Linq;",
            "using A = MyApp.Alpha;",
            "using System;",
            "using MyApp.Services;");

        // Act
        var sorted = UsingDirectiveSorter.Sort(usings);

        // Assert
        Assert.Equal(8, sorted.Count);

        // Group 1: Regular usings (System first, then alphabetical)
        Assert.Equal("System", GetNamespaceName(sorted[0]));
        Assert.False(IsStaticUsing(sorted[0]));
        Assert.False(IsAliasUsing(sorted[0]));

        Assert.Equal("System.Linq", GetNamespaceName(sorted[1]));
        Assert.False(IsStaticUsing(sorted[1]));
        Assert.False(IsAliasUsing(sorted[1]));

        Assert.Equal("MyApp.Services", GetNamespaceName(sorted[2]));
        Assert.False(IsStaticUsing(sorted[2]));
        Assert.False(IsAliasUsing(sorted[2]));

        Assert.Equal("ThirdParty", GetNamespaceName(sorted[3]));
        Assert.False(IsStaticUsing(sorted[3]));
        Assert.False(IsAliasUsing(sorted[3]));

        // Group 2: Static usings (System first, then alphabetical)
        Assert.Equal("System.Math", GetNamespaceName(sorted[4]));
        Assert.True(IsStaticUsing(sorted[4]));

        Assert.Equal("ThirdParty.Extensions", GetNamespaceName(sorted[5]));
        Assert.True(IsStaticUsing(sorted[5]));

        // Group 3: Alias usings (alphabetical by alias name)
        Assert.Equal("A", GetAliasName(sorted[6]));
        Assert.True(IsAliasUsing(sorted[6]));

        Assert.Equal("Z", GetAliasName(sorted[7]));
        Assert.True(IsAliasUsing(sorted[7]));
    }

    [Fact]
    public void Sort_EmptyList_ReturnsEmpty()
    {
        // Arrange
        var usings = new List<UsingDirectiveSyntax>();

        // Act
        var sorted = UsingDirectiveSorter.Sort(usings);

        // Assert
        Assert.Empty(sorted);
    }

    [Fact]
    public void Sort_SingleUsing_ReturnsSame()
    {
        // Arrange
        var usings = ParseUsings("using System;");

        // Act
        var sorted = UsingDirectiveSorter.Sort(usings);

        // Assert
        Assert.Single(sorted);
        Assert.Equal("System", GetNamespaceName(sorted[0]));
    }

    [Fact]
    public void Sort_PreservesTrivia_InSortedResult()
    {
        // Arrange - Using with leading trivia (comment)
        var source = @"
// This is a comment
using System.Collections.Generic;
using System;";
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetCompilationUnitRoot();
        var usings = root.Usings.ToList();

        // Act
        var sorted = UsingDirectiveSorter.Sort(usings);

        // Assert
        Assert.Equal(2, sorted.Count);
        // Order should be System first
        Assert.Equal("System", GetNamespaceName(sorted[0]));
        Assert.Equal("System.Collections.Generic", GetNamespaceName(sorted[1]));
        // The trivia should be preserved on the original node
        var genericUsing = sorted.First(u => GetNamespaceName(u) == "System.Collections.Generic");
        Assert.True(genericUsing.HasLeadingTrivia);
    }

    #endregion

    #region Helper Methods

    private static List<UsingDirectiveSyntax> ParseUsings(params string[] usingStatements)
    {
        var source = string.Join(Environment.NewLine, usingStatements);
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetCompilationUnitRoot();
        return root.Usings.ToList();
    }

    private static string GetNamespaceName(UsingDirectiveSyntax usingDirective)
    {
        return usingDirective.Name?.ToString() ?? string.Empty;
    }

    private static string? GetAliasName(UsingDirectiveSyntax usingDirective)
    {
        return usingDirective.Alias?.Name.ToString();
    }

    private static bool IsStaticUsing(UsingDirectiveSyntax usingDirective)
    {
        return usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword);
    }

    private static bool IsAliasUsing(UsingDirectiveSyntax usingDirective)
    {
        return usingDirective.Alias != null;
    }

    #endregion
}
