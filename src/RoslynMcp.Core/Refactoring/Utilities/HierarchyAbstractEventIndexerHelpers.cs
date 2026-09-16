using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared event/indexer abstract-member syntax helpers used by
/// <c>HierarchyAbstractMemberRewriter</c> and <c>push_members_down</c>
/// when converting concrete members to abstract declarations (extracted
/// shared implementation). Callers supply abstract modifiers from their
/// own <c>ToAbstractModifiers</c> (those helpers intentionally differ).
/// </summary>
internal static class HierarchyAbstractEventIndexerHelpers
{
    /// <summary>
    /// Indexers that can become abstract: non-static, not explicit-interface,
    /// and without an explicit private-only accessor (CS0621).
    /// </summary>
    internal static bool CanMakeIndexerAbstract(IndexerDeclarationSyntax indexer) =>
        !indexer.Modifiers.Any(SyntaxKind.StaticKeyword) &&
        indexer.ExplicitInterfaceSpecifier == null &&
        (indexer.AccessorList == null
            || indexer.AccessorList.Accessors.All(accessor => !AccessibilityModifiers.IsPrivateOnlyAccessor(accessor)));

    /// <summary>
    /// Event declarations that can become abstract: non-static and not
    /// explicit-interface.
    /// </summary>
    internal static bool CanMakeEventAbstract(EventDeclarationSyntax eventDecl) =>
        !eventDecl.Modifiers.Any(SyntaxKind.StaticKeyword) &&
        eventDecl.ExplicitInterfaceSpecifier == null;

    /// <summary>
    /// Event fields that can become abstract: non-static.
    /// </summary>
    internal static bool CanMakeEventAbstract(EventFieldDeclarationSyntax eventField) =>
        !eventField.Modifiers.Any(SyntaxKind.StaticKeyword);

    /// <summary>
    /// Converts an event declaration into an abstract event with
    /// <paramref name="abstractModifiers"/> (no accessor list).
    /// </summary>
    internal static EventDeclarationSyntax ToAbstractEvent(
        EventDeclarationSyntax eventDecl,
        SyntaxTokenList abstractModifiers)
    {
        return eventDecl
            .WithModifiers(abstractModifiers)
            .WithAccessorList(null)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Converts an event field into an abstract event declaration with
    /// <paramref name="abstractModifiers"/> (first variable only).
    /// </summary>
    internal static EventDeclarationSyntax ToAbstractEvent(
        EventFieldDeclarationSyntax eventField,
        SyntaxTokenList abstractModifiers)
    {
        var variable = eventField.Declaration.Variables.First();
        return SyntaxFactory.EventDeclaration(eventField.Declaration.Type, variable.Identifier)
            .WithAttributeLists(eventField.AttributeLists)
            .WithModifiers(abstractModifiers)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Converts an indexer into an abstract indexer with
    /// <paramref name="abstractModifiers"/> and semicolon accessors.
    /// </summary>
    internal static IndexerDeclarationSyntax ToAbstractIndexer(
        IndexerDeclarationSyntax indexer,
        SyntaxTokenList abstractModifiers)
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
            .WithModifiers(abstractModifiers)
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }
}
