using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared explicit-interface-implementation detection used by
/// implement_interface / implement_abstract when classifying existing
/// members, and by generate_overrides when skipping explicit
/// implementations in its hidden-by-non-override check. Same body as the
/// prior private Generate copies.
/// </summary>
internal static class ExplicitImplementationHelpers
{
    /// <summary>
    /// True when <paramref name="member"/> is an explicit interface
    /// implementation (method/property/event). Methods also match when
    /// <see cref="MethodKind.ExplicitInterfaceImplementation"/>. Same body
    /// as the prior private copies on ImplementInterfaceOperation /
    /// ImplementAbstractOperation / GenerateOverridesOperation.
    /// </summary>
    internal static bool IsExplicitImplementation(ISymbol member) =>
        member switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations.Length > 0
                || method.MethodKind == MethodKind.ExplicitInterfaceImplementation,
            IPropertySymbol property => property.ExplicitInterfaceImplementations.Length > 0,
            IEventSymbol evt => evt.ExplicitInterfaceImplementations.Length > 0,
            _ => false
        };
}
