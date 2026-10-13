using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared sibling-overload collision checks used by change_signature /
/// add_parameter / remove_parameter. Same bodies as the three former
/// <c>WouldCollideWithSibling</c> copies (containing-type null → false;
/// same-name <see cref="IMethodSymbol"/> siblings; skip self; length +
/// <see cref="IMethodSymbol.TypeParameters"/> arity gates; per-parameter
/// <see cref="RefKind"/> + <see cref="TypeEquivalenceHelpers.TypesEquivalent"/>).
/// </summary>
internal static class SiblingOverloadCollisionHelpers
{
    /// <summary>
    /// True when applying <paramref name="prospectiveParameters"/> to
    /// <paramref name="method"/> would collide with another method of the
    /// same name in the containing type (same arity + bound type symbols +
    /// <see cref="RefKind"/>). Core shared by the insert / full-replace /
    /// remove thin overloads.
    /// </summary>
    internal static bool WouldCollideWithSibling(
        IMethodSymbol method,
        IReadOnlyList<(ITypeSymbol Type, RefKind RefKind)> prospectiveParameters)
    {
        var containingType = method.ContainingType;
        if (containingType == null)
            return false;

        foreach (var sibling in containingType.GetMembers(method.Name).OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(sibling, method))
                continue;
            if (sibling.Parameters.Length != prospectiveParameters.Count)
                continue;
            // Method type parameters are distinct symbols per overload; compare
            // by arity + ordinal via TypeEquivalenceHelpers (Codex).
            if (sibling.TypeParameters.Length != method.TypeParameters.Length)
                continue;

            var collision = true;
            for (var i = 0; i < prospectiveParameters.Count; i++)
            {
                if (sibling.Parameters[i].RefKind != prospectiveParameters[i].RefKind)
                {
                    collision = false;
                    break;
                }

                if (!TypeEquivalenceHelpers.TypesEquivalent(
                        sibling.Parameters[i].Type,
                        prospectiveParameters[i].Type))
                {
                    collision = false;
                    break;
                }
            }

            if (collision)
                return true;
        }

        return false;
    }

    /// <summary>
    /// change_signature path: speculative-bind each type name at
    /// <paramref name="position"/>; error/missing type → false; prospective
    /// <see cref="RefKind"/> is always <see cref="RefKind.None"/> (bulk
    /// CreateParameterSyntax emits None).
    /// </summary>
    internal static bool WouldCollideWithSibling(
        IMethodSymbol method,
        IReadOnlyList<string> parameterTypeNames,
        SemanticModel semanticModel,
        int position)
    {
        if (method.ContainingType == null)
            return false;

        var prospective = new (ITypeSymbol Type, RefKind RefKind)[parameterTypeNames.Count];
        for (var i = 0; i < parameterTypeNames.Count; i++)
        {
            var typeInfo = semanticModel.GetSpeculativeTypeInfo(
                position,
                SyntaxFactory.ParseTypeName(parameterTypeNames[i]),
                SpeculativeBindingOption.BindAsTypeOrNamespace);
            if (typeInfo.Type == null || typeInfo.Type is IErrorTypeSymbol)
                return false;
            prospective[i] = (typeInfo.Type, RefKind.None);
        }

        return WouldCollideWithSibling(method, prospective);
    }

    /// <summary>
    /// add_parameter path: speculative-bind the inserted type; map
    /// <c>i == insertIndex</c> → new type else original
    /// <c>i &lt; insertIndex ? i : i - 1</c>; prospective
    /// <see cref="RefKind"/> is always <see cref="RefKind.None"/>.
    /// </summary>
    internal static bool WouldCollideWithSiblingAfterInsert(
        IMethodSymbol method,
        MethodDeclarationSyntax methodDecl,
        SemanticModel semanticModel,
        string parameterType,
        int insertIndex)
    {
        if (method.ContainingType == null)
            return false;

        var newTypeInfo = semanticModel.GetSpeculativeTypeInfo(
            methodDecl.SpanStart,
            SyntaxFactory.ParseTypeName(parameterType),
            SpeculativeBindingOption.BindAsTypeOrNamespace);
        if (newTypeInfo.Type == null || newTypeInfo.Type is IErrorTypeSymbol)
            return false;

        var prospectiveLength = method.Parameters.Length + 1;
        var prospective = new (ITypeSymbol Type, RefKind RefKind)[prospectiveLength];
        for (var i = 0; i < prospectiveLength; i++)
        {
            if (i == insertIndex)
            {
                prospective[i] = (newTypeInfo.Type, RefKind.None);
            }
            else
            {
                var originalIndex = i < insertIndex ? i : i - 1;
                prospective[i] = (method.Parameters[originalIndex].Type, RefKind.None);
            }
        }

        return WouldCollideWithSibling(method, prospective);
    }

    /// <summary>
    /// remove_parameter path: no speculative bind; map
    /// <c>originalIndex = i &lt; removeIndex ? i : i + 1</c>; compare sibling
    /// <see cref="RefKind"/> to the expected original <see cref="RefKind"/>;
    /// invalid <paramref name="removeIndex"/> → false.
    /// </summary>
    internal static bool WouldCollideWithSiblingAfterRemove(
        IMethodSymbol method,
        int removeIndex)
    {
        if (method.ContainingType == null)
            return false;

        if (removeIndex < 0 || removeIndex >= method.Parameters.Length)
            return false;

        var prospectiveLength = method.Parameters.Length - 1;
        var prospective = new (ITypeSymbol Type, RefKind RefKind)[prospectiveLength];
        for (var i = 0; i < prospectiveLength; i++)
        {
            var originalIndex = i < removeIndex ? i : i + 1;
            prospective[i] = (
                method.Parameters[originalIndex].Type,
                method.Parameters[originalIndex].RefKind);
        }

        return WouldCollideWithSibling(method, prospective);
    }
}
