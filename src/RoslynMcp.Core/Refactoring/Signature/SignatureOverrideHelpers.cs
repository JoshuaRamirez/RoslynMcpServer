using System.Linq;
using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Signature;

/// <summary>
/// Shared Signature-family helpers for source-declared methods and
/// override-root identity (add/remove/reorder parameter + change return type).
/// </summary>
internal static class SignatureOverrideHelpers
{
    /// <summary>
    /// True when <paramref name="method"/> has at least one declaring syntax
    /// reference and at least one in-source location.
    /// </summary>
    internal static bool HasSourceDeclaration(IMethodSymbol method) =>
        method.DeclaringSyntaxReferences.Length > 0 &&
        method.Locations.Any(l => l.IsInSource);

    /// <summary>
    /// True when <paramref name="left"/> and <paramref name="right"/> share the
    /// same override root (walk <see cref="IMethodSymbol.OverriddenMethod"/>).
    /// </summary>
    internal static bool ShareOverrideRoot(IMethodSymbol left, IMethodSymbol right) =>
        SymbolEqualityComparer.Default.Equals(GetOverrideRoot(left), GetOverrideRoot(right));

    /// <summary>
    /// Walks <see cref="IMethodSymbol.OverriddenMethod"/> until null and returns
    /// that root method symbol.
    /// </summary>
    internal static IMethodSymbol GetOverrideRoot(IMethodSymbol method)
    {
        var current = method;
        while (current.OverriddenMethod != null)
            current = current.OverriddenMethod;
        return current;
    }
}
