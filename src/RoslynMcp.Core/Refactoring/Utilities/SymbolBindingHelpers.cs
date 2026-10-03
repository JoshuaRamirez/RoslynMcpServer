using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared symbol-sequence equality used by extract_variable / introduce_field
/// when comparing binding lists from <c>CollectBindings</c>. Same body as the
/// prior private copies in those operations.
/// </summary>
internal static class SymbolBindingHelpers
{
    /// <summary>
    /// True when both sequences have the same length and each pair is equal
    /// under <see cref="SymbolEqualityComparer.Default"/>.
    /// </summary>
    internal static bool BindingsEqual(IReadOnlyList<ISymbol?> left, IReadOnlyList<ISymbol?> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(left[i], right[i]))
                return false;
        }

        return true;
    }
}
