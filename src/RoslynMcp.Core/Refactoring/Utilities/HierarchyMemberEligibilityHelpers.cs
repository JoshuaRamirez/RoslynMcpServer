using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared member-eligibility helpers used by
/// <c>PullMembersUpOperation</c> and <c>PushMembersDownOperation</c>.
/// Same bodies as the prior private <c>IsSupportedMember</c> /
/// <c>IsInterfaceCompatible</c> / <c>ResolveSymbolProjectId</c> copies.
/// </summary>
internal static class HierarchyMemberEligibilityHelpers
{
    /// <summary>
    /// True for ordinary methods, properties, fields, and events (the
    /// member kinds PullMembersUp / PushMembersDown will move). Same body
    /// as the prior private copies.
    /// </summary>
    internal static bool IsSupportedMember(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.MethodKind == MethodKind.Ordinary,
        IPropertySymbol => true,
        IFieldSymbol => true,
        IEventSymbol => true,
        _ => false
    };

    /// <summary>
    /// True when <paramref name="member"/> may live on an interface:
    /// public, non-static, ordinary method / property / event (fields
    /// excluded). Same body as the prior private copies.
    /// </summary>
    internal static bool IsInterfaceCompatible(ISymbol member)
    {
        if (member.IsStatic)
            return false;

        if (member.DeclaredAccessibility != Accessibility.Public)
            return false;

        return member switch
        {
            IMethodSymbol method => method.MethodKind == MethodKind.Ordinary,
            IPropertySymbol => true,
            IEventSymbol => true,
            _ => false
        };
    }

    /// <summary>
    /// Resolves the <see cref="ProjectId"/> that owns
    /// <paramref name="symbol"/>'s declaring syntax, when available.
    /// Same body as the prior private copies.
    /// </summary>
    internal static ProjectId? ResolveSymbolProjectId(Solution solution, ISymbol symbol)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var document = solution.GetDocument(reference.SyntaxTree);
            if (document != null)
                return document.Project.Id;
        }

        return null;
    }
}
