using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared hierarchy member-conflict checks used by PullMembersUp /
/// PushMembersDown. Same bodies as the two private copies.
/// </summary>
internal static class HierarchyConflictHelpers
{
    /// <summary>
    /// True when <paramref name="target"/> already declares a non-implicit
    /// member that conflicts with <paramref name="member"/> (same name;
    /// methods/indexers only when signatures match; other kinds on name alone).
    /// </summary>
    internal static bool HasConflict(INamedTypeSymbol target, ISymbol member)
    {
        foreach (var existing in target.GetMembers(member.Name))
        {
            if (existing.IsImplicitlyDeclared)
                continue;

            if (member is IMethodSymbol method && existing is IMethodSymbol existingMethod)
            {
                if (SignaturesMatch(method, existingMethod))
                    return true;
                continue;
            }

            if (member is IPropertySymbol { IsIndexer: true } indexer
                && existing is IPropertySymbol { IsIndexer: true } existingIndexer)
            {
                if (IndexerSignaturesMatch(indexer, existingIndexer))
                    return true;
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// True when two methods share parameter count, type-parameter count,
    /// parameter types, and by-ref mode. C# forbids overloads that differ
    /// only by <c>ref</c>/<c>in</c>/<c>out</c> (CS0663), so those RefKinds
    /// collapse to one by-reference mode; by-value stays distinct.
    /// </summary>
    internal static bool SignaturesMatch(IMethodSymbol left, IMethodSymbol right)
    {
        if (left.Parameters.Length != right.Parameters.Length)
            return false;

        if (left.TypeParameters.Length != right.TypeParameters.Length)
            return false;

        for (var i = 0; i < left.Parameters.Length; i++)
        {
            // Method type parameters are distinct symbols across declarations
            // (M<T>(T) vs M<U>(U)); compare structurally by ordinal so CS0111
            // pairs collide — same as ParameterTypeMatchHelpers.
            if (!ParameterTypeMatchHelpers.ParameterTypesMatch(
                    left.Parameters[i].Type, right.Parameters[i].Type))
                return false;
            if (!SameDeclarationRefMode(left.Parameters[i].RefKind, right.Parameters[i].RefKind))
                return false;
        }

        return true;
    }

    /// <summary>
    /// True when two indexers share parameter count, parameter types, and
    /// by-ref mode (<c>ref</c>/<c>in</c>/<c>out</c> collapse; see
    /// <see cref="SignaturesMatch"/>).
    /// </summary>
    internal static bool IndexerSignaturesMatch(IPropertySymbol left, IPropertySymbol right)
    {
        if (left.Parameters.Length != right.Parameters.Length)
            return false;

        for (var i = 0; i < left.Parameters.Length; i++)
        {
            if (!ParameterTypeMatchHelpers.ParameterTypesMatch(
                    left.Parameters[i].Type, right.Parameters[i].Type))
                return false;
            if (!SameDeclarationRefMode(left.Parameters[i].RefKind, right.Parameters[i].RefKind))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Declaration-signature by-ref identity: by-value vs by-ref. All non-
    /// <see cref="RefKind.None"/> kinds (<c>ref</c>/<c>in</c>/<c>out</c>/
    /// <c>ref readonly</c>) share one mode so CS0663 pairs collide.
    /// </summary>
    private static bool SameDeclarationRefMode(RefKind left, RefKind right) =>
        (left == RefKind.None) == (right == RefKind.None);
}
