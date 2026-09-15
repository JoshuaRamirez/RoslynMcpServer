using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared named-type helpers used by Generate-family operations.
/// </summary>
internal static class NamedTypeHelpers
{
    /// <summary>
    /// Returns true when <paramref name="typeSymbol"/> has no base type, or
    /// when its base is <see cref="SpecialType.System_Object"/> or
    /// <see cref="SpecialType.System_ValueType"/>. Same body as the two
    /// GenerateEqualsHashCode / GenerateToString private copies.
    /// </summary>
    internal static bool IsObjectOrValueTypeBase(INamedTypeSymbol typeSymbol)
    {
        var baseType = typeSymbol.BaseType;
        return baseType == null
            || baseType.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType;
    }
}
