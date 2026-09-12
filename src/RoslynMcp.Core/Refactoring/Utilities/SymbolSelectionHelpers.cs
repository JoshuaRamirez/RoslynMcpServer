using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared selected-symbol confirmation and definition-location checks used by
/// MakeStatic / MakeNonStatic / SafeDelete. Same bodies as the three private copies.
/// </summary>
internal static class SymbolSelectionHelpers
{
    /// <summary>
    /// Returns <paramref name="symbol"/> when <paramref name="expectedName"/> is
    /// null/whitespace or equals <see cref="ISymbol.Name"/>; otherwise throws
    /// <see cref="RefactoringException"/> with <see cref="ErrorCodes.SymbolNotFound"/>.
    /// </summary>
    internal static ISymbol ConfirmSymbolName(ISymbol symbol, string? expectedName)
    {
        if (!string.IsNullOrWhiteSpace(expectedName) && symbol.Name != expectedName)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"No symbol named '{expectedName}' found at the specified selection.");
        }

        return symbol;
    }

    /// <summary>
    /// True when any source location of <paramref name="symbol"/> shares the same
    /// <see cref="Location.SourceTree"/> and <see cref="Location.SourceSpan"/> as
    /// <paramref name="location"/>.
    /// </summary>
    internal static bool IsDefinitionLocation(ISymbol symbol, Location location)
    {
        return symbol.Locations.Any(definition =>
            definition.IsInSource &&
            definition.SourceTree == location.SourceTree &&
            definition.SourceSpan == location.SourceSpan);
    }
}
