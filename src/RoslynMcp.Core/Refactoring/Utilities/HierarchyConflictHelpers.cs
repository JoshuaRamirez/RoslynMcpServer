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
    /// parameter types, and <see cref="RefKind"/>.
    /// </summary>
    internal static bool SignaturesMatch(IMethodSymbol left, IMethodSymbol right)
    {
        if (left.Parameters.Length != right.Parameters.Length)
            return false;

        if (left.TypeParameters.Length != right.TypeParameters.Length)
            return false;

        for (var i = 0; i < left.Parameters.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(left.Parameters[i].Type, right.Parameters[i].Type))
                return false;
            if (left.Parameters[i].RefKind != right.Parameters[i].RefKind)
                return false;
        }

        return true;
    }

    /// <summary>
    /// True when two indexers share parameter count, parameter types, and
    /// <see cref="RefKind"/>.
    /// </summary>
    internal static bool IndexerSignaturesMatch(IPropertySymbol left, IPropertySymbol right)
    {
        if (left.Parameters.Length != right.Parameters.Length)
            return false;

        for (var i = 0; i < left.Parameters.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(left.Parameters[i].Type, right.Parameters[i].Type))
                return false;
            if (left.Parameters[i].RefKind != right.Parameters[i].RefKind)
                return false;
        }

        return true;
    }
}
