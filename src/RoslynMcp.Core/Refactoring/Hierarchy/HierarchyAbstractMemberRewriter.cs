using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Hierarchy;

/// <summary>
/// Shared <c>makeAbstract</c> member rewrite used by
/// <see cref="PullMembersUpOperation"/> and
/// <see cref="RoslynMcp.Core.Refactoring.Extract.ExtractBaseClassOperation"/>
/// (same shape as <see cref="PushMembersDownOperation"/> <c>leaveAbstract</c>).
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
    /// A wholly private property/indexer is lifted to protected; implicit
    /// accessors follow. An explicit private accessor on a more visible
    /// member cannot become abstract (CS0621) and cannot stay on the
    /// override if the base drops it (CS0546).
    /// </summary>
    private static bool CanAbstractPropertyAccessors(IPropertySymbol property)
    {
        if (property.DeclaredAccessibility == Accessibility.Private)
            return true;

        return property.GetMethod?.DeclaredAccessibility != Accessibility.Private
            && property.SetMethod?.DeclaredAccessibility != Accessibility.Private;
    }

    /// <summary>
    /// Converts a concrete member into an abstract declaration on a base.
    /// </summary>
    internal static MemberDeclarationSyntax ConvertToAbstract(
        MemberDeclarationSyntax member,
        string notMoveableMessage)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method
                .WithModifiers(ToAbstractModifiers(method.Modifiers))
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace(),
            PropertyDeclarationSyntax property => ToAbstractProperty(property),
            IndexerDeclarationSyntax indexer when CanMakeIndexerAbstract(indexer) => ToAbstractIndexer(indexer),
            EventDeclarationSyntax eventDecl when CanMakeEventAbstract(eventDecl) => ToAbstractEvent(eventDecl),
            EventFieldDeclarationSyntax eventField when CanMakeEventAbstract(eventField) => ToAbstractEvent(eventField),
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

    private static bool CanMakeIndexerAbstract(IndexerDeclarationSyntax indexer) =>
        !indexer.Modifiers.Any(SyntaxKind.StaticKeyword) &&
        indexer.ExplicitInterfaceSpecifier == null &&
        (indexer.AccessorList == null
            || indexer.AccessorList.Accessors.All(accessor => !IsPrivateOnlyAccessor(accessor)));

    private static bool CanMakeEventAbstract(EventDeclarationSyntax eventDecl) =>
        !eventDecl.Modifiers.Any(SyntaxKind.StaticKeyword) &&
        eventDecl.ExplicitInterfaceSpecifier == null;

    private static bool CanMakeEventAbstract(EventFieldDeclarationSyntax eventField) =>
        !eventField.Modifiers.Any(SyntaxKind.StaticKeyword);

    private static bool IsPrivateOnlyAccessor(AccessorDeclarationSyntax accessor) =>
        accessor.Modifiers.Any(SyntaxKind.PrivateKeyword)
        && !accessor.Modifiers.Any(SyntaxKind.ProtectedKeyword)
        && !accessor.Modifiers.Any(SyntaxKind.InternalKeyword);

    private static EventDeclarationSyntax ToAbstractEvent(EventDeclarationSyntax eventDecl)
    {
        return eventDecl
            .WithModifiers(ToAbstractModifiers(eventDecl.Modifiers))
            .WithAccessorList(null)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .NormalizeWhitespace();
    }

    private static EventDeclarationSyntax ToAbstractEvent(EventFieldDeclarationSyntax eventField)
    {
        var variable = eventField.Declaration.Variables.First();
        return SyntaxFactory.EventDeclaration(eventField.Declaration.Type, variable.Identifier)
            .WithAttributeLists(eventField.AttributeLists)
            .WithModifiers(ToAbstractModifiers(eventField.Modifiers))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .NormalizeWhitespace();
    }

    private static PropertyDeclarationSyntax ToAbstractProperty(PropertyDeclarationSyntax property)
    {
        var accessors = new List<AccessorDeclarationSyntax>();
        if (property.AccessorList != null)
        {
            foreach (var accessor in property.AccessorList.Accessors)
            {
                accessors.Add(accessor
                    .WithBody(null)
                    .WithExpressionBody(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }
        }
        else
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        return property
            .WithModifiers(ToAbstractModifiers(property.Modifiers))
            .WithExpressionBody(null)
            .WithInitializer(null)
            .WithSemicolonToken(default)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }

    private static IndexerDeclarationSyntax ToAbstractIndexer(IndexerDeclarationSyntax indexer)
    {
        var accessors = new List<AccessorDeclarationSyntax>();
        if (indexer.AccessorList != null)
        {
            foreach (var accessor in indexer.AccessorList.Accessors)
            {
                accessors.Add(accessor
                    .WithBody(null)
                    .WithExpressionBody(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }
        }
        else
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        return indexer
            .WithModifiers(ToAbstractModifiers(indexer.Modifiers))
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }

    private static SyntaxTokenList ToAbstractModifiers(SyntaxTokenList modifiers)
    {
        var tokens = StripModifiers(
                modifiers,
                SyntaxKind.PrivateKeyword,
                SyntaxKind.VirtualKeyword,
                SyntaxKind.OverrideKeyword,
                SyntaxKind.SealedKeyword,
                SyntaxKind.AbstractKeyword,
                SyntaxKind.NewKeyword,
                SyntaxKind.AsyncKeyword)
            .ToList();

        if (!HasAccessibility(tokens))
            tokens.Insert(0, SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));

        tokens.Add(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));
        return SyntaxFactory.TokenList(tokens);
    }

    private static SyntaxTokenList ToOverrideModifiers(SyntaxTokenList modifiers)
    {
        var tokens = StripModifiers(
                modifiers,
                SyntaxKind.PrivateKeyword,
                SyntaxKind.VirtualKeyword,
                SyntaxKind.AbstractKeyword,
                SyntaxKind.OverrideKeyword,
                SyntaxKind.NewKeyword)
            .ToList();

        if (modifiers.Any(SyntaxKind.PrivateKeyword) || !HasAccessibility(tokens))
            tokens.Insert(0, SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));

        tokens.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword)
            .WithTrailingTrivia(SyntaxFactory.ElasticSpace));
        return SyntaxFactory.TokenList(tokens);
    }

    private static IEnumerable<SyntaxToken> StripModifiers(SyntaxTokenList modifiers, params SyntaxKind[] kinds)
    {
        var kindSet = kinds.ToHashSet();
        return modifiers.Where(token => !kindSet.Contains(token.Kind()));
    }

    private static bool HasAccessibility(IEnumerable<SyntaxToken> modifiers) =>
        modifiers.Any(token =>
            token.IsKind(SyntaxKind.PublicKeyword) ||
            token.IsKind(SyntaxKind.ProtectedKeyword) ||
            token.IsKind(SyntaxKind.InternalKeyword) ||
            token.IsKind(SyntaxKind.PrivateKeyword));
}
