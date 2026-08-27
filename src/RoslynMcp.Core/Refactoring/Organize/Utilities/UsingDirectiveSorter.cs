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
///   <item>Global using directives ahead of non-global directives (CS8915), including across regular/static/alias groups</item>
///   <item>Regular using directives (System namespaces first when <c>systemFirst</c> is true, then alphabetical)</item>
///   <item>Static using directives (System namespaces first when <c>systemFirst</c> is true, then alphabetical)</item>
///   <item>Alias using directives (alphabetical by alias name)</item>
/// </list>
/// </remarks>
public static class UsingDirectiveSorter
{
    /// <summary>
    /// Sorts using directives following C# conventions.
    /// </summary>
    /// <param name="usings">The using directives to sort.</param>
    /// <param name="systemFirst">
    /// When true (default), System / System.* namespaces are placed first within regular and static groups.
    /// When false, namespaces sort alphabetically with no System priority. Alias usings stay alphabetical by alias.
    /// Global usings stay ahead of non-global usings in both modes.
    /// </param>
    /// <returns>A list of sorted using directives.</returns>
    public static List<UsingDirectiveSyntax> Sort(IEnumerable<UsingDirectiveSyntax> usings, bool systemFirst = true)
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
        var sortedRegular = SortByNamespace(regularUsings, systemFirst);
        var sortedStatic = SortByNamespace(staticUsings, systemFirst);
        var sortedAlias = SortByAlias(aliasUsings);

        // Combine in order: regular, static, alias — then lift globals ahead of non-globals
        // so mixed groups cannot produce CS8915 (global usings must precede non-global usings).
        var result = new List<UsingDirectiveSyntax>();
        result.AddRange(sortedRegular);
        result.AddRange(sortedStatic);
        result.AddRange(sortedAlias);

        return OrderGlobalsFirst(result);
    }

    /// <summary>
    /// Sorts using directives by namespace, optionally placing System namespaces first.
    /// Global usings stay ahead of non-global usings within the group.
    /// </summary>
    private static List<UsingDirectiveSyntax> SortByNamespace(List<UsingDirectiveSyntax> usings, bool systemFirst)
    {
        var ordered = usings.OrderBy(u => IsGlobalUsing(u) ? 0 : 1);

        if (systemFirst)
        {
            ordered = ordered.ThenBy(u => GetSortPriority(u.Name?.ToString() ?? ""));
        }

        return ordered
            .ThenBy(u => u.Name?.ToString() ?? "", StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Sorts alias using directives by their alias name.
    /// Global usings stay ahead of non-global usings within the group.
    /// </summary>
    private static List<UsingDirectiveSyntax> SortByAlias(List<UsingDirectiveSyntax> usings)
    {
        return usings
            .OrderBy(u => IsGlobalUsing(u) ? 0 : 1)
            .ThenBy(u => u.Alias?.Name.ToString() ?? "", StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Keeps every global using ahead of every non-global using while preserving
    /// relative order (regular, then static, then alias) within each partition.
    /// </summary>
    private static List<UsingDirectiveSyntax> OrderGlobalsFirst(List<UsingDirectiveSyntax> usings)
    {
        return usings
            .OrderBy(u => IsGlobalUsing(u) ? 0 : 1)
            .ToList();
    }

    private static bool IsGlobalUsing(UsingDirectiveSyntax usingDirective)
    {
        return usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword);
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
