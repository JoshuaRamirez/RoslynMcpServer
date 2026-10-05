using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared member-signature matching used by implement_interface /
/// implement_abstract when deciding whether two members (methods,
/// properties / indexers, events) describe the same slot. The two
/// operations differ only in how a member name is read, so callers pass a
/// <c>nameOf</c> selector: implement_interface strips an explicit-interface
/// qualifier (<c>IFoo.M</c> becomes <c>M</c>); implement_abstract uses
/// <see cref="ISymbol.Name"/> as-is. Parameter lists go through
/// <see cref="ParameterTypeMatchHelpers.ParameterListsMatch"/> (exact
/// <see cref="RefKind"/>). generate_overrides keeps its own, simpler copies.
/// </summary>
internal static class MemberSignatureMatchHelpers
{
    /// <summary>
    /// True when <paramref name="left"/> and <paramref name="right"/> are the
    /// same kind of member with matching signatures. Methods compare name,
    /// arity and parameter lists; indexers compare parameter lists;
    /// non-indexer properties and events compare name only. Mismatched member
    /// kinds return <see langword="false"/>.
    /// </summary>
    internal static bool SignaturesMatch(ISymbol left, ISymbol right, Func<ISymbol, string> nameOf)
    {
        if (left is IMethodSymbol leftMethod && right is IMethodSymbol rightMethod)
            return MethodSignaturesMatch(leftMethod, rightMethod, nameOf);

        if (left is IPropertySymbol leftProp && right is IPropertySymbol rightProp)
            return PropertySignaturesMatch(leftProp, rightProp, nameOf);

        if (left is IEventSymbol leftEvent && right is IEventSymbol rightEvent)
            return string.Equals(nameOf(leftEvent), nameOf(rightEvent), StringComparison.Ordinal);

        return false;
    }

    /// <summary>
    /// True when the methods share a name (via <paramref name="nameOf"/>),
    /// arity, and parameter list.
    /// </summary>
    internal static bool MethodSignaturesMatch(IMethodSymbol left, IMethodSymbol right, Func<ISymbol, string> nameOf)
    {
        if (!string.Equals(nameOf(left), nameOf(right), StringComparison.Ordinal))
            return false;
        if (left.Arity != right.Arity)
            return false;
        return ParameterTypeMatchHelpers.ParameterListsMatch(left.Parameters, right.Parameters);
    }

    /// <summary>
    /// True when both are indexers with matching parameter lists, or both are
    /// ordinary properties sharing a name (via <paramref name="nameOf"/>).
    /// </summary>
    internal static bool PropertySignaturesMatch(IPropertySymbol left, IPropertySymbol right, Func<ISymbol, string> nameOf)
    {
        if (left.IsIndexer != right.IsIndexer)
            return false;
        if (!left.IsIndexer)
            return string.Equals(nameOf(left), nameOf(right), StringComparison.Ordinal);
        return ParameterTypeMatchHelpers.ParameterListsMatch(left.Parameters, right.Parameters);
    }
}
