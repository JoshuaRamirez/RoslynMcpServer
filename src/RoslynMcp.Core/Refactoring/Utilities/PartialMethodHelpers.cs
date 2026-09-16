using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared partial-method part walk used by make_static / make_non_static.
/// Same bodies as the two identical MakeStatic / MakeNonStatic copies.
/// Named PartialMethodHelpers (not MethodSymbolHelpers) because this cluster
/// is the partial definition/implementation walk used together by those
/// operations.
/// </summary>
internal static class PartialMethodHelpers
{
    /// <summary>
    /// Prefers the implementation part of a partial method so the walk
    /// treats definition + implementation as one method. Same body as the
    /// two MakeStatic / MakeNonStatic copies.
    /// </summary>
    internal static IMethodSymbol CanonicalPartialMethod(IMethodSymbol method)
    {
        var implementation = method.PartialImplementationPart
            ?? method.PartialDefinitionPart?.PartialImplementationPart;
        return implementation ?? method.PartialDefinitionPart ?? method;
    }

    /// <summary>
    /// Both partial definition and implementation (when present), plus
    /// <paramref name="method"/> itself. Same body as the two MakeStatic /
    /// MakeNonStatic copies.
    /// </summary>
    internal static IEnumerable<IMethodSymbol> GetPartialMethodParts(IMethodSymbol method)
    {
        var parts = new List<IMethodSymbol> { method };
        if (method.PartialDefinitionPart != null)
            parts.Add(method.PartialDefinitionPart);
        if (method.PartialImplementationPart != null)
            parts.Add(method.PartialImplementationPart);
        if (method.PartialDefinitionPart?.PartialImplementationPart != null)
            parts.Add(method.PartialDefinitionPart.PartialImplementationPart);
        if (method.PartialImplementationPart?.PartialDefinitionPart != null)
            parts.Add(method.PartialImplementationPart.PartialDefinitionPart);
        return parts.Distinct<IMethodSymbol>(SymbolEqualityComparer.Default);
    }

    /// <summary>
    /// Declaring syntax references across all partial parts of
    /// <paramref name="method"/>. Same body as the two MakeStatic /
    /// MakeNonStatic copies (promoted from private to internal).
    /// </summary>
    internal static IEnumerable<SyntaxReference> EnumerateDeclaringSyntaxReferences(IMethodSymbol method)
    {
        foreach (var part in GetPartialMethodParts(method))
        {
            foreach (var reference in part.DeclaringSyntaxReferences)
                yield return reference;
        }
    }
}
