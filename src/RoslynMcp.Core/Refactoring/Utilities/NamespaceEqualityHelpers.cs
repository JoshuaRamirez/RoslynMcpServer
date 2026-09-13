using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared namespace-name equality and type-name speculative binding used by
/// convert_anonymous_to_class / convert_tuple_to_struct. Same bodies as the
/// two private copies. Distinct from change_return_type
/// (<c>TypesEquivalent</c> instead of <see cref="SymbolEqualityComparer"/>).
/// </summary>
internal static class NamespaceEqualityHelpers
{
    /// <summary>
    /// Ordinal equality for namespace name strings; both null/empty count as equal.
    /// </summary>
    internal static bool NamespacesEqual(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right))
            return true;
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ordinal equality of a namespace symbol's display string against
    /// <paramref name="name"/>; null/global namespace equals an empty/null name.
    /// </summary>
    internal static bool NamespacesEqual(INamespaceSymbol? symbol, string? name)
    {
        if (symbol == null || symbol.IsGlobalNamespace)
            return string.IsNullOrEmpty(name);
        return string.Equals(symbol.ToDisplayString(), name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Display string for <paramref name="symbol"/>, or null when the symbol
    /// is null, global, or has an empty display string.
    /// </summary>
    internal static string? ToNamespaceName(INamespaceSymbol? symbol)
    {
        if (symbol == null || symbol.IsGlobalNamespace)
            return null;

        var name = symbol.ToDisplayString();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// True when <paramref name="display"/> does not speculative-bind at
    /// <paramref name="position"/> to <paramref name="expected"/> (null or
    /// error type counts as different).
    /// </summary>
    internal static bool TypeNameBindsToDifferentType(
        string display,
        ITypeSymbol expected,
        SemanticModel model,
        int position)
    {
        var parsed = SyntaxFactory.ParseTypeName(display);
        var spec = model.GetSpeculativeTypeInfo(position, parsed, SpeculativeBindingOption.BindAsTypeOrNamespace);
        if (spec.Type == null || spec.Type.TypeKind == TypeKind.Error)
            return true;

        return !SymbolEqualityComparer.Default.Equals(spec.Type, expected);
    }
}
