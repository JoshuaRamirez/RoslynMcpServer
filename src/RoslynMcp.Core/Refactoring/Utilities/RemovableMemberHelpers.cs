using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared cast/ancestor walk that maps a declaring-syntax reference node to
/// a removable method/property/indexer/event member for implement_interface
/// and implement_abstract replace-existing paths.
/// </summary>
internal static class RemovableMemberHelpers
{
    /// <summary>
    /// Returns <paramref name="syntax"/> when it is already a removable
    /// member declaration; otherwise the nearest ancestor member of those
    /// kinds; otherwise null. Same filter as the ImplementInterface /
    /// ImplementAbstract copies.
    /// </summary>
    internal static MemberDeclarationSyntax? AsRemovableMember(SyntaxNode syntax)
    {
        if (syntax is MethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax
            or EventDeclarationSyntax or EventFieldDeclarationSyntax)
        {
            return (MemberDeclarationSyntax)syntax;
        }

        var ancestor = syntax.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        return ancestor is MethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax
            or EventDeclarationSyntax or EventFieldDeclarationSyntax
            ? ancestor
            : null;
    }
}
