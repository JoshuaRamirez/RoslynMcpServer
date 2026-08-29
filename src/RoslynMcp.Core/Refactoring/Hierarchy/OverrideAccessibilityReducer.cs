using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;

namespace RoslynMcp.Core.Refactoring.Hierarchy;

/// <summary>
/// Shared CS0507 / CS0273 reduction for hierarchy override emission.
/// Used by <see cref="PushMembersDownOperation"/> and
/// <see cref="PullMembersUpOperation"/>.
/// </summary>
internal static class OverrideAccessibilityReducer
{
    /// <summary>
    /// Same-assembly: keep <c>protected internal</c>. Cross-assembly
    /// <c>protected internal</c> becomes <c>protected</c> (CS0507). Other
    /// accessibilities are unchanged.
    /// </summary>
    internal static SyntaxTokenList ReduceCrossAssemblyOverrideAccessibility(
        SyntaxTokenList modifiers,
        ISymbol member,
        INamedTypeSymbol target)
    {
        if (SyntaxGenerationHelper.OverrideAccessibility(member, target) != Accessibility.Protected)
            return modifiers;
        if (!modifiers.Any(SyntaxKind.InternalKeyword))
            return modifiers;

        var tokens = modifiers.Where(token => !token.IsKind(SyntaxKind.InternalKeyword)).ToList();
        if (!HasAccessibility(tokens))
            tokens.Insert(0, SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
        return SyntaxFactory.TokenList(tokens);
    }

    /// <summary>
    /// Reduces member and accessor <c>protected internal</c> to
    /// <c>protected</c> when the override is emitted in another assembly.
    /// Methods, properties, events, and indexers share this path.
    /// </summary>
    internal static T ReduceOverrideAccessibility<T>(
        T member,
        ISymbol symbol,
        INamedTypeSymbol target)
        where T : MemberDeclarationSyntax
    {
        var updated = (T)member.WithModifiers(
            ReduceCrossAssemblyOverrideAccessibility(member.Modifiers, symbol, target));

        if (updated is BasePropertyDeclarationSyntax propertyDecl &&
            symbol is IPropertySymbol property)
        {
            return (T)(MemberDeclarationSyntax)ReduceAccessorOverrideAccessibility(
                propertyDecl, property, target);
        }

        return updated;
    }

    /// <summary>
    /// Reduces indexer and accessor <c>protected internal</c> to
    /// <c>protected</c> when the override is emitted in another assembly.
    /// </summary>
    internal static IndexerDeclarationSyntax ReduceIndexerOverrideAccessibility(
        IndexerDeclarationSyntax indexer,
        IPropertySymbol symbol,
        INamedTypeSymbol target)
        => ReduceOverrideAccessibility(indexer, symbol, target);

    /// <summary>
    /// Reduces accessor <c>protected internal</c> and drops an accessor
    /// modifier that matches the (possibly CS0507-reduced) property
    /// accessibility (CS0273).
    /// </summary>
    private static BasePropertyDeclarationSyntax ReduceAccessorOverrideAccessibility(
        BasePropertyDeclarationSyntax member,
        IPropertySymbol symbol,
        INamedTypeSymbol target)
    {
        if (member.AccessorList == null)
            return member;

        var propertyAccessibility = SyntaxGenerationHelper.OverrideAccessibility(symbol, target);
        var accessors = new List<AccessorDeclarationSyntax>();
        foreach (var accessor in member.AccessorList.Accessors)
        {
            var accessorSymbol = accessor.Kind() switch
            {
                SyntaxKind.GetAccessorDeclaration => (ISymbol?)symbol.GetMethod,
                SyntaxKind.SetAccessorDeclaration or SyntaxKind.InitAccessorDeclaration => symbol.SetMethod,
                _ => null
            };

            if (accessorSymbol == null)
            {
                accessors.Add(accessor);
                continue;
            }

            var accessorAccessibility = SyntaxGenerationHelper.OverrideAccessibility(accessorSymbol, target);
            if (accessorAccessibility == Accessibility.NotApplicable
                || accessorAccessibility == propertyAccessibility)
            {
                accessors.Add(accessor.WithModifiers(default));
                continue;
            }

            accessors.Add(accessor.WithModifiers(
                ReduceCrossAssemblyOverrideAccessibility(accessor.Modifiers, accessorSymbol, target)));
        }

        return member.WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)));
    }

    private static bool HasAccessibility(IEnumerable<SyntaxToken> modifiers) =>
        modifiers.Any(token =>
            token.IsKind(SyntaxKind.PublicKeyword) ||
            token.IsKind(SyntaxKind.ProtectedKeyword) ||
            token.IsKind(SyntaxKind.InternalKeyword) ||
            token.IsKind(SyntaxKind.PrivateKeyword));
}
