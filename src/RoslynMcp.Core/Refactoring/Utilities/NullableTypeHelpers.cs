using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared <see cref="System.Nullable{T}"/> type helpers.
/// </summary>
internal static class NullableTypeHelpers
{
    /// <summary>
    /// Returns the underlying type argument when <paramref name="type"/> is a
    /// constructed <see cref="System.Nullable{T}"/>; otherwise returns
    /// <paramref name="type"/> unchanged (including <c>null</c>). Same body as
    /// the InvertIf / GenerateEqualsHashCode private copies.
    /// </summary>
    [return: NotNullIfNotNull(nameof(type))]
    internal static ITypeSymbol? UnwrapNullable(ITypeSymbol? type) =>
        type is INamedTypeSymbol named &&
        named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
        named.TypeArguments.Length == 1
            ? named.TypeArguments[0]
            : type;
}
