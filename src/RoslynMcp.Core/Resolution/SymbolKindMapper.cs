using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Resolution;

/// <summary>
/// Maps Roslyn ISymbol kinds to contract SymbolKind enum values.
/// </summary>
public static class SymbolKindMapper
{
    /// <summary>
    /// Maps a Roslyn ISymbol to the contract SymbolKind enum.
    /// </summary>
    public static Contracts.Enums.SymbolKind Map(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol namedType => namedType.TypeKind switch
            {
                TypeKind.Class => namedType.IsRecord
                    ? Contracts.Enums.SymbolKind.Record
                    : Contracts.Enums.SymbolKind.Class,
                TypeKind.Struct => Contracts.Enums.SymbolKind.Struct,
                TypeKind.Interface => Contracts.Enums.SymbolKind.Interface,
                TypeKind.Enum => Contracts.Enums.SymbolKind.Enum,
                TypeKind.Delegate => Contracts.Enums.SymbolKind.Delegate,
                _ when namedType.IsRecord => Contracts.Enums.SymbolKind.Record,
                _ => Contracts.Enums.SymbolKind.Class
            },
            IMethodSymbol => Contracts.Enums.SymbolKind.Method,
            IPropertySymbol => Contracts.Enums.SymbolKind.Property,
            IFieldSymbol field => field.IsConst
                ? Contracts.Enums.SymbolKind.Constant
                : Contracts.Enums.SymbolKind.Field,
            IEventSymbol => Contracts.Enums.SymbolKind.Event,
            ILocalSymbol => Contracts.Enums.SymbolKind.Local,
            IParameterSymbol => Contracts.Enums.SymbolKind.Parameter,
            INamespaceSymbol => Contracts.Enums.SymbolKind.Namespace,
            _ => Contracts.Enums.SymbolKind.Class
        };
    }
}
