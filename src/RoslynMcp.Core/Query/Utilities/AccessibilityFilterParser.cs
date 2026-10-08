using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Parses the optional <c>accessibilityFilter</c> string of <c>search_symbols</c> into a Roslyn
/// <see cref="Accessibility"/>.
/// </summary>
internal static class AccessibilityFilterParser
{
    /// <summary>
    /// The accepted C# accessibility spellings, in the order reported in error messages.
    /// </summary>
    private static readonly (string Spelling, Accessibility Accessibility)[] ValidValues =
    [
        ("public", Accessibility.Public),
        ("internal", Accessibility.Internal),
        ("protected", Accessibility.Protected),
        ("private", Accessibility.Private),
        ("protected internal", Accessibility.ProtectedOrInternal),
        ("private protected", Accessibility.ProtectedAndInternal)
    ];

    /// <summary>
    /// The reversed modifier orders C# also accepts for the two compound accessibilities.
    /// </summary>
    private static readonly (string Spelling, Accessibility Accessibility)[] ReversedOrderAliases =
    [
        ("internal protected", Accessibility.ProtectedOrInternal),
        ("protected private", Accessibility.ProtectedAndInternal)
    ];

    /// <summary>
    /// Parses <paramref name="accessibilityFilter"/> as a single C# accessibility spelling
    /// (<c>public</c>, <c>internal</c>, <c>protected</c>, <c>private</c>, <c>protected internal</c>,
    /// <c>private protected</c>), case-insensitively, ignoring surrounding whitespace and treating any
    /// run of inner whitespace as a single space. The compound accessibilities are also accepted in the
    /// reversed modifier order C# allows (<c>internal protected</c>, <c>protected private</c>).
    /// Returns <c>null</c> when the filter is omitted, empty, or whitespace (no filtering).
    /// Roslyn enum names (<c>ProtectedOrInternal</c>), VB spellings (<c>friend</c>), numeric values, and
    /// combinations (<c>public, internal</c>) are rejected.
    /// </summary>
    /// <exception cref="RefactoringException">
    /// <see cref="ErrorCodes.InvalidVisibility"/> when the value is not a recognized accessibility.
    /// </exception>
    public static Accessibility? Parse(string? accessibilityFilter)
    {
        if (string.IsNullOrWhiteSpace(accessibilityFilter)) return null;

        var normalized = string.Join(
            ' ',
            accessibilityFilter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        foreach (var (spelling, accessibility) in ValidValues.Concat(ReversedOrderAliases))
        {
            if (string.Equals(spelling, normalized, StringComparison.OrdinalIgnoreCase))
                return accessibility;
        }

        var valid = string.Join(", ", ValidValues.Select(v => v.Spelling));
        throw new RefactoringException(
            ErrorCodes.InvalidVisibility,
            $"Invalid accessibilityFilter '{accessibilityFilter}'. Valid values: {valid}");
    }
}
