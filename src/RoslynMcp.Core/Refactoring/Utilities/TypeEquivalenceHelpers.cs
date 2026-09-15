using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared type-equivalence used by change_return_type / reorder_parameters
/// for signature collision and related checks. Same body as the two
/// identical copies (distinct from <see cref="SymbolEqualityComparer"/> alone:
/// matches type parameters by kind/ordinal and recurses into arrays / generics).
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
}
