using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared accessibility ranking used by convert_anonymous_to_class /
/// convert_tuple_to_struct when computing effective accessibility through
/// containing types. Same bodies as the two private copies. Distinct from
/// <see cref="EqualityMemberCollector"/> / generate_constructor (different
/// AccessibilityRank ordering).
/// </summary>
internal static class AccessibilityRankHelpers
{
    /// <summary>
    /// Returns the less-accessible of <paramref name="left"/> and
    /// <paramref name="right"/> using <see cref="AccessibilityRank"/>.
    /// </summary>
    internal static Accessibility MinAccessibility(Accessibility left, Accessibility right)
    {
        return AccessibilityRank(left) <= AccessibilityRank(right) ? left : right;
    }

    /// <summary>
    /// Ordinal rank from most restrictive (0) to least (5). Unknown /
    /// <see cref="Accessibility.NotApplicable"/> map to 5.
    /// </summary>
    internal static int AccessibilityRank(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Private => 0,
        Accessibility.ProtectedAndInternal => 1,
        Accessibility.Protected => 2,
        Accessibility.Internal => 3,
        Accessibility.ProtectedOrInternal => 4,
        Accessibility.Public => 5,
        _ => 5
    };
}
