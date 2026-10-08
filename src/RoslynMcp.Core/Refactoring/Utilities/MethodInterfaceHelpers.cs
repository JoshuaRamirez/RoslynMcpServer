using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared helpers for detecting whether a member implements an interface member.
/// Used by make_static / make_non_static validation, use_base_type /
/// push_members_down interface-contract checks, and rename_symbol
/// interface-member discovery.
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

    /// <summary>
    /// Interface members on <paramref name="containing"/>'s
    /// <see cref="ITypeSymbol.AllInterfaces"/> (same name as
    /// <paramref name="member"/>) whose
    /// <see cref="ITypeSymbol.FindImplementationForInterfaceMember"/> on
    /// <paramref name="containing"/> is <paramref name="member"/>
    /// (<see cref="SymbolEqualityComparer.Default"/>). Lazy, in interface
    /// then member order. Same loop as the prior private copies on
    /// UseBaseTypeOperation / PushMembersDownOperation
    /// <c>ImplementsInterfaceMember</c> and RenameSymbolOperation
    /// <c>GetImplementedInterfaceMembers</c>.
    /// </summary>
    internal static IEnumerable<ISymbol> ImplicitlyImplementedInterfaceMembers(
        ISymbol member,
        INamedTypeSymbol containing)
    {
        foreach (var iface in containing.AllInterfaces)
        {
            foreach (var ifaceMember in iface.GetMembers(member.Name))
            {
                var implementation = containing.FindImplementationForInterfaceMember(ifaceMember);
                if (implementation != null && SymbolEqualityComparer.Default.Equals(implementation, member))
                    yield return ifaceMember;
            }
        }
    }

    /// <summary>
    /// True when <see cref="ImplicitlyImplementedInterfaceMembers"/> yields
    /// any member; stops at the first match.
    /// </summary>
    internal static bool ImplicitlyImplementsInterfaceMember(ISymbol member, INamedTypeSymbol containing) =>
        ImplicitlyImplementedInterfaceMembers(member, containing).Any();
}
