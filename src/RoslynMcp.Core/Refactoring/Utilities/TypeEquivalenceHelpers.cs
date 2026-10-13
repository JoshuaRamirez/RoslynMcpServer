using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared type-equivalence used by change_return_type / reorder_parameters
/// for signature collision and related checks. Same body as the two
/// identical copies (distinct from <see cref="SymbolEqualityComparer"/> alone:
/// matches type parameters by kind/ordinal and recurses into arrays / generics).
/// <see cref="ParameterListsMatch"/> / <see cref="ParameterListsMatchReordered"/>
/// carry the per-parameter <see cref="RefKind"/> + <see cref="TypesEquivalent"/>
/// loop shared by the two <c>ValidateNoOverloadCollision</c> checks on
/// change_return_type (identity order) and reorder_parameters (reordered).
/// </summary>
internal static class TypeEquivalenceHelpers
{
    /// <summary>
    /// True when <paramref name="left"/> and <paramref name="right"/> are
    /// equivalent for Signature-family checks. Same body as the two private
    /// copies on change_return_type / reorder_parameters.
    /// </summary>
    internal static bool TypesEquivalent(ITypeSymbol left, ITypeSymbol right)
    {
        if (SymbolEqualityComparer.Default.Equals(left, right))
            return true;

        if (left is ITypeParameterSymbol leftTypeParameter &&
            right is ITypeParameterSymbol rightTypeParameter)
        {
            return leftTypeParameter.TypeParameterKind == rightTypeParameter.TypeParameterKind &&
                   leftTypeParameter.Ordinal == rightTypeParameter.Ordinal &&
                   (leftTypeParameter.TypeParameterKind != TypeParameterKind.Type ||
                    SymbolEqualityComparer.Default.Equals(
                        leftTypeParameter.ContainingType,
                        rightTypeParameter.ContainingType));
        }

        if (left is IArrayTypeSymbol leftArray && right is IArrayTypeSymbol rightArray)
        {
            return leftArray.Rank == rightArray.Rank &&
                   TypesEquivalent(leftArray.ElementType, rightArray.ElementType);
        }

        if (left is INamedTypeSymbol leftNamed && right is INamedTypeSymbol rightNamed)
        {
            if (!SymbolEqualityComparer.Default.Equals(leftNamed.OriginalDefinition, rightNamed.OriginalDefinition))
                return false;
            if (leftNamed.TypeArguments.Length != rightNamed.TypeArguments.Length)
                return false;

            for (var i = 0; i < leftNamed.TypeArguments.Length; i++)
            {
                if (!TypesEquivalent(leftNamed.TypeArguments[i], rightNamed.TypeArguments[i]))
                    return false;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="left"/> and <paramref name="right"/> have the
    /// same length and each positional parameter pair shares its
    /// <see cref="RefKind"/> and has equivalent types via
    /// <see cref="TypesEquivalent"/>. Used by change_return_type's overload
    /// collision check (same body as its former private ParameterTypesMatch).
    /// </summary>
    internal static bool ParameterListsMatch(
        ImmutableArray<IParameterSymbol> left,
        ImmutableArray<IParameterSymbol> right)
    {
        if (left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (!ParametersMatch(left[i], right[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="source"/> reordered by
    /// <paramref name="newOrder"/> (position <c>i</c> takes
    /// <c>source[newOrder[i]]</c>) matches <paramref name="target"/>
    /// position-by-position on <see cref="RefKind"/> and
    /// <see cref="TypesEquivalent"/>. Used by reorder_parameters' overload
    /// collision check (same body as its former private ParameterTypesMatch).
    /// Callers validate <paramref name="newOrder"/> as a permutation of
    /// <paramref name="source"/>'s indices.
    /// </summary>
    internal static bool ParameterListsMatchReordered(
        ImmutableArray<IParameterSymbol> source,
        int[] newOrder,
        ImmutableArray<IParameterSymbol> target)
    {
        if (newOrder.Length != target.Length)
            return false;

        for (var i = 0; i < newOrder.Length; i++)
        {
            if (!ParametersMatch(source[newOrder[i]], target[i]))
                return false;
        }

        return true;
    }

    private static bool ParametersMatch(IParameterSymbol left, IParameterSymbol right) =>
        left.RefKind == right.RefKind && TypesEquivalent(left.Type, right.Type);
}
