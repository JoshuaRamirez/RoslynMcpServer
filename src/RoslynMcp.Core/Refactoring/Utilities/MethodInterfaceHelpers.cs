using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared helpers for detecting whether a method implements an interface member.
/// Used by make_static / make_non_static validation.
/// </summary>
internal static class MethodInterfaceHelpers
{
    /// <summary>
    /// True when <paramref name="method"/> explicitly or implicitly implements
    /// an interface member on its containing type. Same body as the two private
    /// copies on make_static / make_non_static.
    /// </summary>
    internal static bool ImplementsInterface(IMethodSymbol method)
    {
        if (method.ExplicitInterfaceImplementations.Length > 0)
            return true;

        if (method.ContainingType == null)
            return false;

        foreach (var iface in method.ContainingType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers(method.Name))
            {
                var implementation = method.ContainingType.FindImplementationForInterfaceMember(member);
                if (implementation == null)
                    continue;

                if (SymbolEqualityComparer.Default.Equals(implementation, method) ||
                    SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, method.OriginalDefinition))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
