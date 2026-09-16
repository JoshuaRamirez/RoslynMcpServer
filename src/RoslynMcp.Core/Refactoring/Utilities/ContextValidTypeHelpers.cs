using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared context-valid type display, bind-at-insertion, type-parameter walk,
/// and less-accessible-than-public helpers used by convert_anonymous_to_class /
/// convert_tuple_to_struct. Same bodies as the two identical Convert copies.
/// </summary>
internal static class ContextValidTypeHelpers
{
    /// <summary>
    /// Minimal display string for <paramref name="type"/> at
    /// <paramref name="position"/>, falling back to fully-qualified when the
    /// minimal name is empty or binds to a different type. Same body as the
    /// two Convert copies.
    /// </summary>
    internal static string ToContextValidTypeName(ITypeSymbol type, SemanticModel model, int position)
    {
        var display = type.ToMinimalDisplayString(model, position);
        if (string.IsNullOrWhiteSpace(display) || NamespaceEqualityHelpers.TypeNameBindsToDifferentType(display, type, model, position))
        {
            display = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
                .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
        }

        return display;
    }

    /// <summary>
    /// True when <paramref name="type"/> has no type parameters and its
    /// context-valid display binds to the same type at
    /// <paramref name="position"/>. Same body as the two Convert copies.
    /// </summary>
    internal static bool MemberTypeBindsAtInsertion(ITypeSymbol type, SemanticModel model, int position)
    {
        if (ContainsTypeParameter(type))
            return false;

        var display = ToContextValidTypeName(type, model, position);
        return !NamespaceEqualityHelpers.TypeNameBindsToDifferentType(display, type, model, position);
    }

    /// <summary>
    /// True when <paramref name="type"/> is or contains a type parameter
    /// (arrays, pointers, and named type arguments). Same body as the two
    /// Convert copies.
    /// </summary>
    internal static bool ContainsTypeParameter(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol)
            return true;

        if (type is IArrayTypeSymbol array)
            return ContainsTypeParameter(array.ElementType);

        if (type is IPointerTypeSymbol pointer)
            return ContainsTypeParameter(pointer.PointedAtType);

        if (type is INamedTypeSymbol named)
        {
            foreach (var argument in named.TypeArguments)
            {
                if (ContainsTypeParameter(argument))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="type"/> (or a type argument / element) is
    /// effectively less accessible than public. Same body as the two Convert
    /// copies.
    /// </summary>
    internal static bool IsLessAccessibleThanPublic(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
            return IsLessAccessibleThanPublic(array.ElementType);

        if (type is IPointerTypeSymbol pointer)
            return IsLessAccessibleThanPublic(pointer.PointedAtType);

        if (type is INamedTypeSymbol named)
        {
            if (GetEffectiveAccessibility(named) is not (Accessibility.Public or Accessibility.NotApplicable))
                return true;

            foreach (var argument in named.TypeArguments)
            {
                if (IsLessAccessibleThanPublic(argument))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Declared accessibility of <paramref name="symbol"/> min'd with each
    /// containing type. Same body as the two Convert copies.
    /// </summary>
    internal static Accessibility GetEffectiveAccessibility(ISymbol symbol)
    {
        var current = symbol.DeclaredAccessibility;
        for (var container = symbol.ContainingType; container != null; container = container.ContainingType)
            current = AccessibilityRankHelpers.MinAccessibility(current, container.DeclaredAccessibility);

        return current;
    }
}
