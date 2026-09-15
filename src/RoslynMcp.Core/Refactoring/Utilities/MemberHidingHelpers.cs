using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared member-hiding checks used by EqualityMemberCollector /
/// GenerateConstructor. Same body as the two private copies.
/// </summary>
internal static class MemberHidingHelpers
{
    /// <summary>
    /// True when a closer type hides or overrides <paramref name="member"/> so
    /// <c>this.Name</c> would bind to that closer member (or fail to compile)
    /// instead of the inherited one. Any non-implicit closer member with the
    /// same name counts as a hider, including methods and nested types.
    /// Implicit members (for example auto-property backing fields) are ignored.
    /// </summary>
    internal static bool IsHiddenFrom(ISymbol member, INamedTypeSymbol fromType)
    {
        var declaring = member.ContainingType;
        for (var current = fromType;
             current != null && !SymbolEqualityComparer.Default.Equals(current, declaring);
             current = current.BaseType)
        {
            foreach (var candidate in current.GetMembers(member.Name))
            {
                if (candidate.IsImplicitlyDeclared)
                    continue;
                return true;
            }
        }

        return false;
    }
}
