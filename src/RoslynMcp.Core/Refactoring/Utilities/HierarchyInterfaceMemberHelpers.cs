using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared signature-only interface property/indexer syntax helpers used by
/// <c>pull_members_up</c> and <c>push_members_down</c> when moving members
/// onto an interface (extracted from the prior identical private
/// <c>ToInterfaceProperty</c> / <c>ToInterfaceIndexer</c> copies).
/// </summary>
internal static class HierarchyInterfaceMemberHelpers
{
    /// <summary>
    /// Rebuilds <paramref name="accessorList"/> as signature-only accessors
    /// (no modifiers, no bodies, trailing semicolon). When
    /// <paramref name="skipNonPublic"/> is true, accessors with explicit
    /// non-public accessibility are dropped (same public-accessor gate as
    /// extract_interface <c>CreateInterfaceIndexer</c>). A missing accessor
    /// list (expression-bodied member) yields a single <c>get;</c>. The
    /// result may be empty when every accessor is skipped.
    /// </summary>
    internal static List<AccessorDeclarationSyntax> ToSignatureOnlyAccessors(
        AccessorListSyntax? accessorList,
        bool skipNonPublic)
    {
        var accessors = new List<AccessorDeclarationSyntax>();
        if (accessorList != null)
        {
            foreach (var accessor in accessorList.Accessors)
            {
                // A private/protected/internal setter must not become a
                // public interface set;.
                if (skipNonPublic && AccessibilityModifiers.HasNonPublicAccessibility(accessor))
                    continue;

                accessors.Add(accessor
                    .WithModifiers(SyntaxFactory.TokenList())
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

        return accessors;
    }

    /// <summary>
    /// Converts <paramref name="property"/> into a signature-only interface
    /// property: modifiers, expression body and initializer semicolon are
    /// dropped and every accessor becomes <c>get;</c>/<c>set;</c>/<c>init;</c>.
    /// </summary>
    internal static PropertyDeclarationSyntax ToSignatureOnlyInterfaceProperty(PropertyDeclarationSyntax property)
    {
        var accessors = ToSignatureOnlyAccessors(property.AccessorList, skipNonPublic: false);

        return property
            .WithModifiers(SyntaxFactory.TokenList())
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Converts <paramref name="indexer"/> into a signature-only interface
    /// indexer keeping only public accessors. Throws
    /// <see cref="ErrorCodes.MemberNotInterfaceCompatible"/> with
    /// <paramref name="noPublicAccessorsMessage"/> when no accessor survives.
    /// </summary>
    internal static IndexerDeclarationSyntax ToSignatureOnlyInterfaceIndexer(
        IndexerDeclarationSyntax indexer,
        string noPublicAccessorsMessage)
    {
        var accessors = ToSignatureOnlyAccessors(indexer.AccessorList, skipNonPublic: true);

        if (accessors.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotInterfaceCompatible,
                noPublicAccessorsMessage);
        }

        return indexer
            .WithModifiers(SyntaxFactory.TokenList())
            .WithExplicitInterfaceSpecifier(null)
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }
}
