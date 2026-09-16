using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Maps <see cref="INamedTypeSymbol"/> to contract <see cref="Contracts.Enums.SymbolKind"/>
/// for MoveType operations. Intentionally distinct from
/// <see cref="RoslynMcp.Core.Resolution.SymbolKindMapper"/>: MoveType treats
/// <see cref="TypeKind.Class"/> as Class even when <c>IsRecord</c> is true
/// (record class), matching the historical MoveType switch.
/// </summary>
internal static class NamedTypeSymbolKindHelpers
{
    /// <summary>
    /// Same switch body as the two MoveType private MapSymbolKind copies.
    /// Do not redirect through <see cref="RoslynMcp.Core.Resolution.SymbolKindMapper.Map"/>.
    /// </summary>
    internal static Contracts.Enums.SymbolKind Map(INamedTypeSymbol symbol)
    {
        return symbol.TypeKind switch
        {
            TypeKind.Class => Contracts.Enums.SymbolKind.Class,
            TypeKind.Struct => Contracts.Enums.SymbolKind.Struct,
            TypeKind.Interface => Contracts.Enums.SymbolKind.Interface,
            TypeKind.Enum => Contracts.Enums.SymbolKind.Enum,
            TypeKind.Delegate => Contracts.Enums.SymbolKind.Delegate,
            _ when symbol.IsRecord => Contracts.Enums.SymbolKind.Record,
            _ => Contracts.Enums.SymbolKind.Class
        };
    }
}
