using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared parameter-type matching used by implement_interface /
/// implement_abstract when comparing method and property signatures.
/// Method type parameters are distinct symbols on the interface/abstract vs
/// implementation (<c>IFoo.M&lt;T&gt;(T)</c> vs <c>C.M&lt;T&gt;(T)</c>),
/// so <see cref="SymbolEqualityComparer.Default"/> misses an exact match.
/// Compare those by ordinal; recurse through constructed named types,
/// arrays, pointers, and tuples so <c>List&lt;T&gt;</c> / <c>T[]</c> still
/// match. Concrete / named types that
/// <see cref="SymbolEqualityComparer.Default"/> already equates stay as today.
/// Same bodies as the two private Generate copies (distinct from
/// <see cref="TypeEquivalenceHelpers.TypesEquivalent"/> and from
/// generate_overrides' simpler ParameterTypesMatch).
/// </summary>
internal static class ParameterTypeMatchHelpers
{
    /// <summary>
    /// True when <paramref name="left"/> and <paramref name="right"/> match
    /// for Generate implement_interface / implement_abstract signature checks.
    /// Same body as the two private copies on those operations.
    /// </summary>
    internal static bool ParameterTypesMatch(ITypeSymbol left, ITypeSymbol right)
    {
        if (SymbolEqualityComparer.Default.Equals(left, right))
            return true;

        if (left is ITypeParameterSymbol leftTp
            && leftTp.TypeParameterKind == TypeParameterKind.Method
            && right is ITypeParameterSymbol rightTp
            && rightTp.TypeParameterKind == TypeParameterKind.Method)
        {
            return leftTp.Ordinal == rightTp.Ordinal;
        }

        if (left is INamedTypeSymbol leftNamed && right is INamedTypeSymbol rightNamed)
            return NamedTypesMatch(leftNamed, rightNamed);

        if (left is IArrayTypeSymbol leftArray && right is IArrayTypeSymbol rightArray)
        {
            return leftArray.Rank == rightArray.Rank
                && ParameterTypesMatch(leftArray.ElementType, rightArray.ElementType);
        }

        if (left is IPointerTypeSymbol leftPtr && right is IPointerTypeSymbol rightPtr)
            return ParameterTypesMatch(leftPtr.PointedAtType, rightPtr.PointedAtType);

        return false;
    }

    /// <summary>
    /// True when <paramref name="left"/> and <paramref name="right"/> share an
    /// original definition and matching type arguments / tuple elements
    /// (recursive via <see cref="ParameterTypesMatch"/>). Same body as the
    /// two private copies on implement_interface / implement_abstract.
    /// </summary>
    internal static bool NamedTypesMatch(INamedTypeSymbol left, INamedTypeSymbol right)
    {
        if (!SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition))
            return false;

        if (left.IsTupleType || right.IsTupleType)
        {
            if (left.TupleElements.Length != right.TupleElements.Length)
                return false;

            for (var i = 0; i < left.TupleElements.Length; i++)
            {
                if (!ParameterTypesMatch(left.TupleElements[i].Type, right.TupleElements[i].Type))
                    return false;
            }

            return true;
        }

        if (left.TypeArguments.Length != right.TypeArguments.Length)
            return false;

        for (var i = 0; i < left.TypeArguments.Length; i++)
        {
            if (!ParameterTypesMatch(left.TypeArguments[i], right.TypeArguments[i]))
                return false;
        }

        return true;
    }
}
