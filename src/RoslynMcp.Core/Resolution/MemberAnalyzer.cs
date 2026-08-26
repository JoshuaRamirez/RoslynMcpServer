using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Resolution;

/// <summary>
/// Analyzes type members for extraction, implementation, and override operations.
/// </summary>
public static class MemberAnalyzer
{
    /// <summary>
    /// Gets members that can be extracted to an interface.
    /// </summary>
    /// <param name="type">The type to analyze.</param>
    /// <returns>Public instance members suitable for interface extraction.</returns>
    public static IEnumerable<ISymbol> GetExtractableMembers(INamedTypeSymbol type)
    {
        return type.GetMembers()
            .Where(m => !m.IsStatic &&
                        !m.IsImplicitlyDeclared &&
                        m.DeclaredAccessibility == Accessibility.Public &&
                        IsExtractableKind(m));
    }

    /// <summary>
    /// Gets members from an interface that are not implemented by a type.
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <param name="iface">The interface to check against.</param>
    /// <returns>Unimplemented interface members.</returns>
    public static IEnumerable<ISymbol> GetUnimplementedMembers(INamedTypeSymbol type, INamedTypeSymbol iface)
    {
        var implemented = new HashSet<ISymbol>(type.GetMembers(), SymbolEqualityComparer.Default);

        foreach (var member in iface.GetMembers())
        {
            if (member.IsImplicitlyDeclared) continue;

            var implementation = type.FindImplementationForInterfaceMember(member);
            if (implementation == null)
            {
                yield return member;
            }
        }
    }

    /// <summary>
    /// Gets abstract methods, properties, and indexers inherited from base types
    /// that the selected type has not yet implemented.
    /// </summary>
    /// <param name="type">The type to analyze.</param>
    /// <returns>Unimplemented inherited abstract members (methods, properties, and indexers).</returns>
    public static IEnumerable<ISymbol> GetUnimplementedAbstractMembers(INamedTypeSymbol type)
    {
        var declared = new HashSet<string>(
            type.GetMembers()
                .Where(m => m is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol)
                .Select(GetMemberSignature));

        var current = type.BaseType;
        while (current != null && current.SpecialType != SpecialType.System_Object)
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsImplicitlyDeclared || member.IsStatic)
                    continue;

                string? signature = null;
                var isAbstract = false;
                var isConcreteOverride = false;

                switch (member)
                {
                    case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                        signature = GetMemberSignature(method);
                        isAbstract = method.IsAbstract;
                        isConcreteOverride = method.IsOverride && !method.IsAbstract;
                        break;
                    case IPropertySymbol property:
                        signature = GetMemberSignature(property);
                        isAbstract = property.IsAbstract;
                        isConcreteOverride = property.IsOverride && !property.IsAbstract;
                        break;
                }

                if (signature == null || declared.Contains(signature))
                    continue;

                if (isConcreteOverride)
                {
                    declared.Add(signature);
                    continue;
                }

                if (!isAbstract)
                    continue;

                declared.Add(signature);
                yield return member;
            }

            current = current.BaseType;
        }
    }

    /// <summary>
    /// Gets virtual/abstract members from base classes that can be overridden.
    /// </summary>
    /// <param name="type">The type to analyze.</param>
    /// <returns>Overridable members from base classes.</returns>
    public static IEnumerable<ISymbol> GetOverridableMembers(INamedTypeSymbol type)
    {
        var baseType = type.BaseType;
        var alreadyOverridden = new HashSet<string>(
            type.GetMembers()
                .Where(m => m is IMethodSymbol { IsOverride: true } or IPropertySymbol { IsOverride: true })
                .Select(m => GetMemberSignature(m)));

        while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
        {
            foreach (var member in baseType.GetMembers())
            {
                if (member.IsImplicitlyDeclared) continue;
                if (!IsOverridable(member)) continue;

                var signature = GetMemberSignature(member);
                if (!alreadyOverridden.Contains(signature))
                {
                    yield return member;
                }
            }

            baseType = baseType.BaseType;
        }

        // Also include Object methods that can be overridden
        foreach (var member in GetObjectOverrides())
        {
            var signature = GetMemberSignature(member);
            if (!alreadyOverridden.Contains(signature))
            {
                yield return member;
            }
        }
    }

    /// <summary>
    /// Determines if an expression is safe to inline (no side effects).
    /// </summary>
    /// <param name="expr">The expression to analyze.</param>
    /// <param name="model">The semantic model.</param>
    /// <returns>True if the expression can be safely inlined.</returns>
    public static bool IsSafeToInline(ExpressionSyntax expr, SemanticModel model)
    {
        // Check for method invocations (potential side effects)
        if (expr.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any())
        {
            return false;
        }

        // Check for increment/decrement
        if (expr.DescendantNodesAndSelf().OfType<PostfixUnaryExpressionSyntax>().Any() ||
            expr.DescendantNodesAndSelf().OfType<PrefixUnaryExpressionSyntax>().Any(p =>
                p.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreIncrementExpression) ||
                p.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreDecrementExpression)))
        {
            return false;
        }

        // Check for assignments within expression
        if (expr.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>().Any())
        {
            return false;
        }

        // Check for object creation (potential side effects in constructor)
        if (expr.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>().Any())
        {
            return false;
        }

        // Check for await expressions
        if (expr.DescendantNodesAndSelf().OfType<AwaitExpressionSyntax>().Any())
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Gets members that can be moved to a base class.
    /// </summary>
    /// <param name="type">The type to analyze.</param>
    /// <returns>Members suitable for extraction to base class.</returns>
    public static IEnumerable<ISymbol> GetMembersForBaseClass(INamedTypeSymbol type)
    {
        return type.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared &&
                        !m.IsStatic &&
                        CanMoveToBase(m));
    }

    private static bool IsExtractableKind(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol method => !method.IsConstructor() &&
                                    method.MethodKind == MethodKind.Ordinary,
            IPropertySymbol => true,
            IEventSymbol => true,
            _ => false
        };
    }

    private static bool IsOverridable(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol method => (method.IsVirtual || method.IsAbstract || method.IsOverride) &&
                                    !method.IsSealed &&
                                    method.MethodKind == MethodKind.Ordinary,
            IPropertySymbol prop => (prop.IsVirtual || prop.IsAbstract || prop.IsOverride) &&
                                    !prop.IsSealed,
            _ => false
        };
    }

    private static bool CanMoveToBase(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol method => method.MethodKind == MethodKind.Ordinary &&
                                    method.DeclaredAccessibility != Accessibility.Private,
            IPropertySymbol prop => prop.DeclaredAccessibility != Accessibility.Private,
            IFieldSymbol field => field.DeclaredAccessibility != Accessibility.Private,
            _ => false
        };
    }

    private static string GetMemberSignature(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol method => $"{method.Name}({string.Join(",", method.Parameters.Select(FormatParameterSignature))})",
            IPropertySymbol { IsIndexer: true } indexer =>
                $"this[{string.Join(",", indexer.Parameters.Select(FormatParameterSignature))}]",
            IPropertySymbol prop => prop.Name,
            _ => member.Name
        };
    }

    private static string FormatParameterSignature(IParameterSymbol parameter)
    {
        var type = parameter.Type.ToDisplayString();
        return parameter.RefKind switch
        {
            RefKind.Ref => $"ref {type}",
            RefKind.Out => $"out {type}",
            RefKind.In => $"in {type}",
            RefKind.RefReadOnlyParameter => $"ref readonly {type}",
            _ => type
        };
    }

    private static IEnumerable<ISymbol> GetObjectOverrides()
    {
        // Return placeholder symbols for ToString, Equals, GetHashCode
        // These are handled specially in generate_overrides
        yield break;
    }
}

/// <summary>
/// Extension methods for IMethodSymbol.
/// </summary>
public static class MethodSymbolExtensions
{
    /// <summary>
    /// Determines if the method is a constructor.
    /// </summary>
    public static bool IsConstructor(this IMethodSymbol method)
    {
        return method.MethodKind == MethodKind.Constructor ||
               method.MethodKind == MethodKind.StaticConstructor;
    }
}
