using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Parses the optional <c>kindFilter</c> string shared by <c>search_symbols</c> and
/// <c>get_document_outline</c> into a <see cref="Contracts.Enums.SymbolKind"/>.
/// </summary>
internal static class SymbolKindFilterParser
{
    /// <summary>
    /// Parses <paramref name="kindFilter"/> as a single <see cref="Contracts.Enums.SymbolKind"/> member name,
    /// case-insensitively and ignoring surrounding whitespace.
    /// Returns <c>null</c> when the filter is omitted, empty, or whitespace (no filtering).
    /// Only exact defined member names are accepted: numeric values (<c>"6"</c>, <c>"-1"</c>), undefined
    /// values (<c>"999"</c>), and comma-separated combinations (<c>"Class, Method"</c>) are rejected, unlike
    /// <see cref="System.Enum.TryParse{TEnum}(string, bool, out TEnum)"/>.
    /// </summary>
    /// <exception cref="RefactoringException">
    /// <see cref="ErrorCodes.InvalidSymbolKind"/> when the value is not a recognized symbol kind name.
    /// </exception>
    public static Contracts.Enums.SymbolKind? Parse(string? kindFilter)
    {
        if (string.IsNullOrWhiteSpace(kindFilter)) return null;

        var name = kindFilter.Trim();
        foreach (var kind in System.Enum.GetValues<Contracts.Enums.SymbolKind>())
        {
            if (string.Equals(kind.ToString(), name, System.StringComparison.OrdinalIgnoreCase))
                return kind;
        }

        var validKinds = string.Join(", ", System.Enum.GetNames<Contracts.Enums.SymbolKind>());
        throw new RefactoringException(
            ErrorCodes.InvalidSymbolKind,
            $"Invalid kindFilter '{kindFilter}'. Valid values: {validKinds}");
    }
}
