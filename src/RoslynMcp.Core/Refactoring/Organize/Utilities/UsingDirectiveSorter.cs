using System.Globalization;
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
///   <item>Alias using directives (alphabetical by alias name, then by target name)</item>
/// </list>
/// Names are compared the way Roslyn's organize-imports does (Visual Studio's Remove and Sort Usings and
/// <c>dotnet format</c>; Roslyn's internal <c>UsingsAndExternAliasesDirectiveComparer</c>,
/// <c>NameSyntaxComparer</c> and <c>TokenComparer</c>, which are not public API): dotted names are compared
/// segment by segment on each identifier's <c>ValueText</c> (so <c>@class</c> is <c>class</c>) with an
/// invariant-culture, case-insensitive comparison (<c>_</c> before letters, <c>Autofac</c> before
/// <c>AutoMapper</c>), then case-sensitively so lowercase comes first on a case-only tie (<c>abc</c> before
/// <c>ABC</c>); a shorter name that is a prefix of a longer one comes first; and with <c>systemFirst</c> a
/// leading <c>System</c> segment comes first (#3085).
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
        var sortedAlias = SortByAlias(aliasUsings, systemFirst);

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
        var comparer = new RoslynNameComparer(systemFirst);
        return usings
            .OrderBy(u => IsGlobalUsing(u) ? 0 : 1)
            .ThenBy(u => u.Name, comparer)
            .ToList();
    }

    /// <summary>
    /// Sorts alias using directives by their alias name, then by target name when two aliases are equal.
    /// Global usings stay ahead of non-global usings within the group.
    /// </summary>
    private static List<UsingDirectiveSyntax> SortByAlias(List<UsingDirectiveSyntax> usings, bool systemFirst)
    {
        var comparer = new RoslynNameComparer(systemFirst);
        return usings
            .OrderBy(u => IsGlobalUsing(u) ? 0 : 1)
            .ThenBy(u => u, Comparer<UsingDirectiveSyntax>.Create((x, y) =>
            {
                var compare = comparer.CompareTokens(x.Alias!.Name.Identifier, y.Alias!.Name.Identifier);
                // Same alias: Roslyn compares the target names only when both are names (not `using X = int;`).
                return compare == 0 && x.Name != null && y.Name != null ? comparer.Compare(x.Name, y.Name) : compare;
            }))
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
    /// Mirrors Roslyn's <c>NameSyntaxComparer</c> over its <c>TokenComparer</c> (normal or System-first).
    /// </summary>
    private sealed class RoslynNameComparer(bool systemFirst) : IComparer<NameSyntax?>
    {
        private static readonly CompareInfo InvariantCompare = CultureInfo.InvariantCulture.CompareInfo;

        public int Compare(NameSyntax? x, NameSyntax? y)
        {
            if (x == y)
            {
                return 0;
            }

            if (x is null || y is null)
            {
                return x is null ? -1 : 1;
            }

            if (x.IsMissing || y.IsMissing)
            {
                return x.IsMissing == y.IsMissing ? 0 : x.IsMissing ? -1 : 1;
            }

            if (x is SimpleNameSyntax simpleX && y is SimpleNameSyntax simpleY)
            {
                return CompareSimpleNames(simpleX, simpleY);
            }

            // One or both are dotted / alias-qualified: compare part by part, shorter prefix first.
            var xParts = DecomposeNameParts(x);
            var yParts = DecomposeNameParts(y);
            for (var i = 0; i < xParts.Count && i < yParts.Count; i++)
            {
                var compare = Compare(xParts[i], yParts[i]);
                if (compare != 0)
                {
                    return compare;
                }
            }

            return xParts.Count - yParts.Count;
        }

        /// <summary>
        /// Roslyn's <c>TokenComparer</c>: with System-first, a <c>System</c> token directly after <c>using</c> /
        /// <c>static</c> wins; then invariant-culture ignoring case, then case-sensitive (lowercase first).
        /// </summary>
        public int CompareTokens(SyntaxToken x, SyntaxToken y)
        {
            if (systemFirst && FollowsUsingOrStatic(x) && FollowsUsingOrStatic(y))
            {
                var xIsSystem = x.ValueText == "System";
                var yIsSystem = y.ValueText == "System";
                if (xIsSystem != yIsSystem)
                {
                    return xIsSystem ? -1 : 1;
                }
            }

            if (x == y)
            {
                return 0;
            }

            var compare = InvariantCompare.Compare(x.ValueText, y.ValueText,
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth);
            return compare != 0
                ? compare
                : InvariantCompare.Compare(x.ValueText, y.ValueText, CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth);
        }

        private int CompareSimpleNames(SimpleNameSyntax x, SimpleNameSyntax y)
        {
            var compare = CompareTokens(x.Identifier, y.Identifier);
            if (compare != 0)
            {
                return compare;
            }

            // Two generics: fewer type arguments first, then each argument in turn. Identifier vs generic
            // with the same identifier returns -1 in both directions, exactly as Roslyn's NameSyntaxComparer
            // does (its comments say "Goo goes before Goo<T>"), so the same input sorts the same way.
            return (x, y) switch
            {
                (GenericNameSyntax genericX, GenericNameSyntax genericY) => CompareGenericArguments(genericX, genericY),
                (GenericNameSyntax, _) or (_, GenericNameSyntax) => -1,
                _ => 0
            };
        }

        private int CompareGenericArguments(GenericNameSyntax x, GenericNameSyntax y)
        {
            var compare = x.Arity - y.Arity;
            for (var i = 0; compare == 0 && i < x.Arity; i++)
            {
                compare = CompareTypes(x.TypeArgumentList.Arguments[i], y.TypeArgumentList.Arguments[i]);
            }

            return compare;
        }

        // Roslyn's TypeSyntaxComparer: unwrap array / pointer / nullable, compare names structurally,
        // otherwise (predefined types such as int) compare the first tokens.
        private int CompareTypes(TypeSyntax x, TypeSyntax y)
        {
            x = UnwrapType(x);
            y = UnwrapType(y);
            return x is NameSyntax xName && y is NameSyntax yName
                ? Compare(xName, yName)
                : CompareTokens(x.GetFirstToken(includeSkipped: true), y.GetFirstToken());
        }

        private static TypeSyntax UnwrapType(TypeSyntax type)
        {
            while (true)
            {
                switch (type)
                {
                    case ArrayTypeSyntax array:
                        type = array.ElementType;
                        break;
                    case PointerTypeSyntax pointer:
                        type = pointer.ElementType;
                        break;
                    case NullableTypeSyntax nullable:
                        type = nullable.ElementType;
                        break;
                    default:
                        return type;
                }
            }
        }

        private static bool FollowsUsingOrStatic(SyntaxToken token) =>
            token.GetPreviousToken(includeSkipped: true).Kind() is SyntaxKind.UsingKeyword or SyntaxKind.StaticKeyword;

        private static List<SimpleNameSyntax> DecomposeNameParts(NameSyntax name)
        {
            var result = new List<SimpleNameSyntax>();
            Decompose(name);
            return result;

            void Decompose(NameSyntax part)
            {
                switch (part)
                {
                    case QualifiedNameSyntax qualified:
                        Decompose(qualified.Left);
                        Decompose(qualified.Right);
                        break;
                    case AliasQualifiedNameSyntax aliased:
                        result.Add(aliased.Alias);
                        Decompose(aliased.Name);
                        break;
                    case SimpleNameSyntax simple:
                        result.Add(simple);
                        break;
                }
            }
        }
    }
}
