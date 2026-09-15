using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared accessibility ranking used by EqualityMemberCollector /
/// generate_constructor when deciding member accessibility (Internal before
/// Protected). Same bodies as the two private copies. Distinct from
/// <see cref="AccessibilityRankHelpers"/> (Protected before Internal;
/// unknown defaults to 5) used by convert_anonymous_to_class /
/// convert_tuple_to_struct.
/// </summary>
internal static class MoreRestrictiveAccessibilityHelpers
{
    /// <summary>
    /// Returns the more-restrictive of <paramref name="left"/> and
    /// <paramref name="right"/> using <see cref="AccessibilityRank"/>.
    /// </summary>
    internal static Accessibility MoreRestrictive(Accessibility left, Accessibility right)
    {
        return AccessibilityRank(left) <= AccessibilityRank(right) ? left : right;
    }

    /// <summary>
    /// Ordinal rank from most restrictive (0) to least (5). Unknown /
    /// <see cref="Accessibility.NotApplicable"/> map to 0.
    /// Internal ranks before Protected (unlike <see cref="AccessibilityRankHelpers"/>).
    /// </summary>
    internal static int AccessibilityRank(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Private => 0,
        Accessibility.ProtectedAndInternal => 1,
        Accessibility.Internal => 2,
        Accessibility.Protected => 3,
        Accessibility.ProtectedOrInternal => 4,
        Accessibility.Public => 5,
        _ => 0
    };

    /// <summary>
    /// True when <paramref name="member"/> and <paramref name="fromType"/> are
    /// declared in the same assembly.
    /// </summary>
    internal static bool SameAssembly(ISymbol member, INamedTypeSymbol fromType) =>
        SymbolEqualityComparer.Default.Equals(member.ContainingAssembly, fromType.ContainingAssembly);
}
