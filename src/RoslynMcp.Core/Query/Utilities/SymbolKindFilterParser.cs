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
    /// Parses <paramref name="kindFilter"/> case-insensitively.
    /// Returns <c>null</c> when the filter is omitted, empty, or whitespace (no filtering).
    /// </summary>
    /// <exception cref="RefactoringException">
    /// <see cref="ErrorCodes.InvalidSymbolKind"/> when the value is not a recognized symbol kind.
    /// </exception>
    public static Contracts.Enums.SymbolKind? Parse(string? kindFilter)
    {
        if (string.IsNullOrWhiteSpace(kindFilter)) return null;

        if (!System.Enum.TryParse<Contracts.Enums.SymbolKind>(kindFilter, ignoreCase: true, out var kind))
        {
            var validKinds = string.Join(", ", System.Enum.GetNames<Contracts.Enums.SymbolKind>());
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Invalid kindFilter '{kindFilter}'. Valid values: {validKinds}");
        }

        return kind;
    }
}
