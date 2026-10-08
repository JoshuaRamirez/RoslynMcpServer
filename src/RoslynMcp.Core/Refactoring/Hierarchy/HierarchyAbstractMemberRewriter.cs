using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring.Utilities;

namespace RoslynMcp.Core.Refactoring.Hierarchy;

/// <summary>
/// Shared abstract-member rewrite used by
/// <see cref="PullMembersUpOperation"/>,
/// <see cref="RoslynMcp.Core.Refactoring.Extract.ExtractBaseClassOperation"/>,
/// and <see cref="PushMembersDownOperation"/> (<c>leaveAbstract</c>).
/// Push keeps <c>override</c> via <c>keepOverrideWhenPresent</c>; pull/extract default false.
/// </summary>
internal static class HierarchyAbstractMemberRewriter
{
    /// <summary>
    /// Methods, properties, events, and indexers that can become a legal
    /// abstract + override pair. Static and explicit-interface members
    /// cannot. An indexer or property with an explicit private accessor
    /// cannot (CS0621 / CS0546). Fields cannot be abstract.
    /// </summary>
    internal static bool CanBeAbstract(ISymbol member) => member switch
    {
        IMethodSymbol method =>
            method.MethodKind == MethodKind.Ordinary
            && !method.IsStatic
            && method.ExplicitInterfaceImplementations.Length == 0,
        IPropertySymbol property =>
            !property.IsStatic
            && property.ExplicitInterfaceImplementations.Length == 0
            && CanAbstractPropertyAccessors(property),
        IEventSymbol evt =>
            !evt.IsStatic
            && evt.ExplicitInterfaceImplementations.Length == 0,
        _ => false
    };

    /// <summary>
    /// True when a property/indexer's accessors allow an abstract + override
    /// pair. A wholly private property/indexer is lifted to protected;
    /// implicit accessors follow. An explicit private accessor on a more
    /// visible member cannot become abstract (CS0442 / CS0621) and cannot
    /// stay on the override if the base drops it (CS0546). Shared by
    /// <see cref="CanBeAbstract"/> and <see cref="PushMembersDownOperation"/>
    /// (<c>leaveAbstract</c>).
    /// </summary>
    internal static bool CanAbstractPropertyAccessors(IPropertySymbol property)
    {
        if (property.DeclaredAccessibility == Accessibility.Private)
            return true;

        return property.GetMethod?.DeclaredAccessibility != Accessibility.Private
            && property.SetMethod?.DeclaredAccessibility != Accessibility.Private;
    }

    /// <summary>
    /// Converts a concrete member into an abstract declaration on a base.
    /// When <paramref name="keepOverrideWhenPresent"/> is true (push
    /// <c>leaveAbstract</c>), an existing <c>override</c> is retained;
    /// pull/extract leave the default false.
    /// </summary>
    internal static MemberDeclarationSyntax ConvertToAbstract(
        MemberDeclarationSyntax member,
        string notMoveableMessage,
        bool keepOverrideWhenPresent = false)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method
                .WithModifiers(ToAbstractModifiers(method.Modifiers, keepOverrideWhenPresent))
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace(),
            PropertyDeclarationSyntax property =>
                HierarchyAbstractEventIndexerHelpers.ToAbstractProperty(property, ToAbstractModifiers(property.Modifiers, keepOverrideWhenPresent)),
            IndexerDeclarationSyntax indexer when HierarchyAbstractEventIndexerHelpers.CanMakeIndexerAbstract(indexer) =>
                HierarchyAbstractEventIndexerHelpers.ToAbstractIndexer(indexer, ToAbstractModifiers(indexer.Modifiers, keepOverrideWhenPresent)),
            EventDeclarationSyntax eventDecl when HierarchyAbstractEventIndexerHelpers.CanMakeEventAbstract(eventDecl) =>
                HierarchyAbstractEventIndexerHelpers.ToAbstractEvent(eventDecl, ToAbstractModifiers(eventDecl.Modifiers, keepOverrideWhenPresent)),
            EventFieldDeclarationSyntax eventField when HierarchyAbstractEventIndexerHelpers.CanMakeEventAbstract(eventField) =>
                HierarchyAbstractEventIndexerHelpers.ToAbstractEvent(eventField, ToAbstractModifiers(eventField.Modifiers, keepOverrideWhenPresent)),
            _ => throw new RefactoringException(
                ErrorCodes.MemberNotMoveable,
                notMoveableMessage)
        };
    }

    /// <summary>
    /// Keeps an override on the derived type after the member is made
    /// abstract on the base. <paramref name="target"/> is the destination
    /// base used for CS0507 reduction.
    /// </summary>
    internal static MemberDeclarationSyntax AddOverrideModifier(
        MemberDeclarationSyntax member,
        ISymbol symbol,
        INamedTypeSymbol target)
    {
        return member switch
        {
            MethodDeclarationSyntax method =>
                OverrideAccessibilityReducer.ReduceOverrideAccessibility(
                    method.WithModifiers(ToOverrideModifiers(method.Modifiers)),
                    symbol,
                    target),
            PropertyDeclarationSyntax property =>
                OverrideAccessibilityReducer.ReduceOverrideAccessibility(
                    property.WithModifiers(ToOverrideModifiers(property.Modifiers)),
                    symbol,
                    target),
            IndexerDeclarationSyntax indexer =>
                OverrideAccessibilityReducer.ReduceOverrideAccessibility(
                    indexer.WithModifiers(ToOverrideModifiers(indexer.Modifiers)),
                    symbol,
                    target),
            EventDeclarationSyntax eventDecl =>
                OverrideAccessibilityReducer.ReduceOverrideAccessibility(
                    eventDecl.WithModifiers(ToOverrideModifiers(eventDecl.Modifiers)),
                    symbol,
                    target),
            EventFieldDeclarationSyntax eventField =>
                OverrideAccessibilityReducer.ReduceOverrideAccessibility(
                    eventField.WithModifiers(ToOverrideModifiers(eventField.Modifiers)),
                    symbol,
                    target),
            _ => member
        };
    }

    /// <summary>
    /// Keeps only the requested declarator when an event field declares
    /// multiple variables.
    /// </summary>
    internal static MemberDeclarationSyntax IsolateMemberSyntax(
        MemberDeclarationSyntax syntax,
        string name)
    {
        return syntax switch
        {
            EventFieldDeclarationSyntax eventField when eventField.Declaration.Variables.Count > 1 =>
                eventField.WithDeclaration(eventField.Declaration.WithVariables(
                    SyntaxFactory.SingletonSeparatedList(
                        eventField.Declaration.Variables.First(v => v.Identifier.Text == name)))),
            _ => syntax
        };
    }

    private static SyntaxTokenList ToAbstractModifiers(
        SyntaxTokenList modifiers,
        bool keepOverrideWhenPresent) =>
        HierarchyModifierHelpers.ToAbstractModifiers(modifiers, keepOverrideWhenPresent);

    private static SyntaxTokenList ToOverrideModifiers(SyntaxTokenList modifiers) =>
        HierarchyModifierHelpers.ToOverrideModifiers(modifiers, stripSealed: false);
}
