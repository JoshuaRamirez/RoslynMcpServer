using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Organize.Utilities;

/// <summary>
/// Provides standardized sorting for using directives following C# conventions.
/// </summary>
/// <remarks>
/// Sort order:
/// <list type="number">
///   <item>Regular using directives (System namespaces first, then alphabetical)</item>
///   <item>Static using directives (System namespaces first, then alphabetical)</item>
///   <item>Alias using directives (alphabetical by alias name)</item>
/// </list>
/// </remarks>
public static class UsingDirectiveSorter
{
    /// <summary>
    /// Sorts using directives following C# conventions.
    /// </summary>
    /// <param name="usings">The using directives to sort.</param>
    /// <returns>A list of sorted using directives.</returns>
    public static List<UsingDirectiveSyntax> Sort(IEnumerable<UsingDirectiveSyntax> usings)
    {
        var usingsList = usings.ToList();

        // Categorize usings
        var regularUsings = new List<UsingDirectiveSyntax>();
        var staticUsings = new List<UsingDirectiveSyntax>();
        var aliasUsings = new List<UsingDirectiveSyntax>();

        foreach (var u in usingsList)
        {
            if (u.Alias != null)
            {
                aliasUsings.Add(u);
            }
            else if (u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
            {
                staticUsings.Add(u);
            }
            else
            {
                regularUsings.Add(u);
            }
        }

        // Sort each category
        var sortedRegular = SortByNamespace(regularUsings);
        var sortedStatic = SortByNamespace(staticUsings);
        var sortedAlias = SortByAlias(aliasUsings);

        // Combine in order: regular, static, alias
        var result = new List<UsingDirectiveSyntax>();
        result.AddRange(sortedRegular);
        result.AddRange(sortedStatic);
        result.AddRange(sortedAlias);

        return result;
    }

    /// <summary>
    /// Sorts using directives by namespace with System namespaces first.
    /// </summary>
    private static List<UsingDirectiveSyntax> SortByNamespace(List<UsingDirectiveSyntax> usings)
    {
        return usings
            .OrderBy(u => GetSortPriority(u.Name?.ToString() ?? ""))
            .ThenBy(u => u.Name?.ToString() ?? "", StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Sorts alias using directives by their alias name.
    /// </summary>
    private static List<UsingDirectiveSyntax> SortByAlias(List<UsingDirectiveSyntax> usings)
    {
        return usings
            .OrderBy(u => u.Alias?.Name.ToString() ?? "", StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Gets the sort priority for a namespace name.
    /// System namespaces have priority 0, others have priority 1.
    /// </summary>
    private static int GetSortPriority(string namespaceName)
    {
        if (namespaceName.StartsWith("System", StringComparison.Ordinal))
        {
            // Distinguish "System" from namespaces that happen to start with "System"
            // e.g., "SystemX" should not be grouped with System
            if (namespaceName == "System" || namespaceName.StartsWith("System.", StringComparison.Ordinal))
            {
                return 0;
            }
        }

        return 1;
    }
}
