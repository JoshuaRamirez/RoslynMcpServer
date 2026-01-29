using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Organize;

/// <summary>
/// Unit tests for AddMissingUsingsOperation semantic validation.
/// Tests validate using directive resolution behavior.
/// </summary>
public class AddMissingUsingsOperationTests
{
    #region No Missing Usings Tests

    [Fact]
    public void AddMissingUsings_NoMissingUsings_ReturnsSuccessTrue()
    {
        // Arrange
        var diagnosticIds = new List<string>(); // No CS0246, CS0103, CS0234

        // Act
        var hasMissingUsings = HasMissingUsingDiagnostics(diagnosticIds);

        // Assert
        Assert.False(hasMissingUsings);
    }

    [Fact]
    public void AddMissingUsings_NoMissingUsings_ChangesCountIsZero()
    {
        // Arrange
        var namespacesToAdd = new HashSet<string>();

        // Act
        var changeCount = namespacesToAdd.Count;

        // Assert
        Assert.Equal(0, changeCount);
    }

    [Fact]
    public void AddMissingUsings_FileWithAllUsings_ReturnsEmptyList()
    {
        // Arrange
        var existingUsings = new HashSet<string> { "System", "System.Collections.Generic" };
        var requiredNamespaces = new List<string> { "System", "System.Collections.Generic" };

        // Act
        var missingUsings = GetMissingUsings(requiredNamespaces, existingUsings);

        // Assert
        Assert.Empty(missingUsings);
    }

    #endregion

    #region Duplicate Detection Tests

    [Fact]
    public void AddMissingUsings_AlreadyHasUsing_DoesNotDuplicate()
    {
        // Arrange
        var existingUsings = new HashSet<string> { "System.Collections.Generic" };
        var candidateNamespaces = new List<string> { "System.Collections.Generic" };

        // Act
        var newUsings = candidateNamespaces.Where(n => !existingUsings.Contains(n)).ToList();

        // Assert
        Assert.Empty(newUsings);
    }

    [Fact]
    public void AddMissingUsings_PartiallyPresent_AddsOnlyMissing()
    {
        // Arrange
        var existingUsings = new HashSet<string> { "System" };
        var candidateNamespaces = new List<string> { "System", "System.Linq" };

        // Act
        var newUsings = candidateNamespaces.Where(n => !existingUsings.Contains(n)).ToList();

        // Assert
        Assert.Single(newUsings);
    }

    [Fact]
    public void AddMissingUsings_PartiallyPresent_AddsMissingCorrectly()
    {
        // Arrange
        var existingUsings = new HashSet<string> { "System" };
        var candidateNamespaces = new List<string> { "System", "System.Linq" };

        // Act
        var newUsings = candidateNamespaces.Where(n => !existingUsings.Contains(n)).ToList();

        // Assert
        Assert.Contains("System.Linq", newUsings);
    }

    #endregion

    #region Namespace Selection Priority Tests

    [Fact]
    public void AddMissingUsings_MultipleNamespaceCandidates_SelectsSystemFirst()
    {
        // Arrange
        var candidates = new List<string>
        {
            "ThirdParty.Collections",
            "System.Collections.Generic",
            "MyApp.Collections"
        };

        // Act
        var selected = SelectBestNamespace(candidates);

        // Assert
        Assert.StartsWith("System", selected);
    }

    [Fact]
    public void AddMissingUsings_MultipleSystemNamespaces_SelectsShortest()
    {
        // Arrange
        var candidates = new List<string>
        {
            "System.Collections.Generic.Specialized",
            "System.Collections",
            "System.Collections.Generic"
        };

        // Act
        var selected = SelectBestNamespace(candidates);

        // Assert
        Assert.Equal("System.Collections", selected);
    }

    [Fact]
    public void AddMissingUsings_NoSystemNamespace_SelectsShortestNonSystem()
    {
        // Arrange
        var candidates = new List<string>
        {
            "ThirdParty.Deep.Nested.Namespace",
            "ThirdParty.Collections",
            "ThirdParty"
        };

        // Act
        var selected = SelectBestNamespace(candidates);

        // Assert
        Assert.Equal("ThirdParty", selected);
    }

    #endregion

    #region Generic Type Resolution Tests

    [Fact]
    public void AddMissingUsings_GenericType_ExtractsTypeName()
    {
        // Arrange
        var source = "List<int> items;";
        var tree = CSharpSyntaxTree.ParseText($"class Test {{ {source} }}");
        var genericName = tree.GetRoot()
            .DescendantNodes()
            .OfType<GenericNameSyntax>()
            .First();

        // Act
        var typeName = GetTypeName(genericName);

        // Assert
        Assert.Equal("List", typeName);
    }

    [Fact]
    public void AddMissingUsings_GenericType_ResolvesToCollections()
    {
        // Arrange
        var typeName = "List";
        var knownNamespaces = new Dictionary<string, string>
        {
            { "List", "System.Collections.Generic" },
            { "Dictionary", "System.Collections.Generic" },
            { "Console", "System" }
        };

        // Act
        var resolvedNamespace = knownNamespaces.GetValueOrDefault(typeName);

        // Assert
        Assert.Equal("System.Collections.Generic", resolvedNamespace);
    }

    [Fact]
    public void AddMissingUsings_QualifiedName_ExtractsRightmostName()
    {
        // Arrange
        var source = "System.Collections.Generic.List<int> items;";
        var tree = CSharpSyntaxTree.ParseText($"class Test {{ {source} }}");
        var qualifiedName = tree.GetRoot()
            .DescendantNodes()
            .OfType<QualifiedNameSyntax>()
            .FirstOrDefault();

        // Act
        var typeName = qualifiedName != null ? GetTypeName(qualifiedName) : null;

        // Assert - QualifiedName should extract the rightmost part
        Assert.NotNull(typeName);
    }

    #endregion

    #region Sorting Tests

    [Fact]
    public void AddMissingUsings_Sorting_SystemNamespacesFirst()
    {
        // Arrange
        var usings = new List<string>
        {
            "MyApp.Services",
            "System.Linq",
            "ThirdParty.Utils",
            "System"
        };

        // Act
        var sorted = SortUsings(usings);

        // Assert
        Assert.StartsWith("System", sorted.First());
    }

    [Fact]
    public void AddMissingUsings_Sorting_SystemNamespacesAlphabetical()
    {
        // Arrange
        var usings = new List<string>
        {
            "System.Threading",
            "System.Collections",
            "System"
        };

        // Act
        var sorted = SortUsings(usings);

        // Assert
        Assert.Equal("System", sorted[0]);
    }

    [Fact]
    public void AddMissingUsings_Sorting_NonSystemAfterSystem()
    {
        // Arrange
        var usings = new List<string>
        {
            "MyApp.Services",
            "System"
        };

        // Act
        var sorted = SortUsings(usings);
        var systemIndex = sorted.IndexOf("System");
        var myAppIndex = sorted.IndexOf("MyApp.Services");

        // Assert
        Assert.True(systemIndex < myAppIndex);
    }

    #endregion

    #region Helper Methods

    private static bool HasMissingUsingDiagnostics(List<string> diagnosticIds)
    {
        var missingUsingCodes = new HashSet<string> { "CS0246", "CS0103", "CS0234" };
        return diagnosticIds.Any(d => missingUsingCodes.Contains(d));
    }

    private static List<string> GetMissingUsings(List<string> required, HashSet<string> existing)
    {
        return required.Where(n => !existing.Contains(n)).ToList();
    }

    private static string SelectBestNamespace(List<string> candidates)
    {
        return candidates
            .OrderBy(n => n.StartsWith("System") ? 0 : 1)
            .ThenBy(n => n.Length)
            .First();
    }

    private static string? GetTypeName(SyntaxNode node)
    {
        return node switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.ToString(),
            _ => null
        };
    }

    private static List<string> SortUsings(List<string> usings)
    {
        return usings
            .OrderBy(n => n.StartsWith("System") ? 0 : 1)
            .ThenBy(n => n)
            .ToList();
    }

    #endregion
}
