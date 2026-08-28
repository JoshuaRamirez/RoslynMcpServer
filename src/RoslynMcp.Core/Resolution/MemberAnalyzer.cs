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
    /// Gets abstract methods, properties, indexers, and events inherited from
    /// base types that the selected type has not yet implemented.
    /// </summary>
    /// <param name="type">The type to analyze.</param>
    /// <returns>Unimplemented inherited abstract members (methods, properties, indexers, and events).</returns>
    public static IEnumerable<ISymbol> GetUnimplementedAbstractMembers(INamedTypeSymbol type)
    {
        var declared = new HashSet<string>(
            type.GetMembers()
                .Where(m => m is IMethodSymbol { MethodKind: MethodKind.Ordinary }
                    or IPropertySymbol
                    or IEventSymbol { ExplicitInterfaceImplementations.Length: 0 })
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
                    case IEventSymbol evt:
                        signature = GetMemberSignature(evt);
                        isAbstract = evt.IsAbstract;
                        isConcreteOverride = evt.IsOverride && !evt.IsAbstract;
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
                {
                    // Intermediate event hiders (new / new virtual / ordinary
                    // same-name) occupy this signature. An override emitted
                    // for the ancestor abstract event would bind the hider's
                    // slot and leave the abstract slot unimplemented.
                    if (member is IEventSymbol)
                        declared.Add(signature);
                    continue;
                }

                declared.Add(signature);
                yield return member;
            }

            current = current.BaseType;
        }
    }

    /// <summary>
    /// Gets virtual/abstract/override (non-sealed) members from base classes
    /// that can be overridden — ordinary methods, properties/indexers, and
    /// events. Members this type already overrides are skipped. Intermediate
    /// concrete event overrides and same-name event hiders (<c>new</c> /
    /// <c>new virtual</c> / ordinary) occupy the signature so a misleading
    /// override is not emitted for a distinct ancestor slot.
    /// </summary>
    /// <param name="type">The type to analyze.</param>
    /// <returns>Overridable members from base classes.</returns>
    public static IEnumerable<ISymbol> GetOverridableMembers(INamedTypeSymbol type)
    {
        var alreadyOverridden = new HashSet<string>(
            type.GetMembers()
                .Where(m => m is IMethodSymbol { IsOverride: true }
                    or IPropertySymbol { IsOverride: true }
                    or IEventSymbol { IsOverride: true })
                .Select(GetMemberSignature));

        // Non-override events on this type (new / ordinary hiders) occupy
        // the signature so a misleading override is not emitted. Explicit
        // interface events do not count.
        foreach (var member in type.GetMembers())
        {
            if (member is IEventSymbol { IsOverride: false, ExplicitInterfaceImplementations.Length: 0 } evt
                && !evt.IsImplicitlyDeclared)
            {
                alreadyOverridden.Add(GetMemberSignature(evt));
            }
        }

        var baseType = type.BaseType;
        while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
        {
            foreach (var member in baseType.GetMembers())
            {
                if (member.IsImplicitlyDeclared) continue;

                if (member is IEventSymbol evt)
                {
                    var eventSignature = GetMemberSignature(evt);
                    if (alreadyOverridden.Contains(eventSignature))
                        continue;

                    if (!IsOverridable(evt, type))
                    {
                        // Intermediate new / ordinary same-name event hiders
                        // occupy this signature. An override emitted for an
                        // ancestor virtual/abstract event would bind the
                        // hider (or fail CS0115) rather than the ancestor slot.
                        // Inaccessible virtual events (internal / private
                        // protected from another assembly) must not occupy —
                        // they are not hiders and must not hide an ancestor
                        // public/protected slot.
                        if (!HasOverridableModifiers(evt)
                            && evt.ExplicitInterfaceImplementations.Length == 0
                            && IsAccessibleFrom(evt, type))
                            alreadyOverridden.Add(eventSignature);
                        continue;
                    }

                    alreadyOverridden.Add(eventSignature);
                    yield return evt;
                    continue;
                }

                if (!IsOverridable(member, type)) continue;

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

    private static bool IsOverridable(ISymbol member, INamedTypeSymbol fromType) =>
        HasOverridableModifiers(member) && IsAccessibleFrom(member, fromType);

    private static bool HasOverridableModifiers(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol method => (method.IsVirtual || method.IsAbstract || method.IsOverride) &&
                                    !method.IsSealed &&
                                    method.MethodKind == MethodKind.Ordinary,
            IPropertySymbol prop => (prop.IsVirtual || prop.IsAbstract || prop.IsOverride) &&
                                    !prop.IsSealed,
            IEventSymbol evt => (evt.IsVirtual || evt.IsAbstract || evt.IsOverride) &&
                                !evt.IsSealed,
            _ => false
        };
    }

    /// <summary>
    /// True when <paramref name="member"/> is visible for override from
    /// <paramref name="fromType"/>: public / protected / protected-internal
    /// always; internal and private-protected only in the same assembly.
    /// Same switch as constructor / equals inherited-member collection —
    /// not a new accessibility subsystem.
    /// </summary>
    private static bool IsAccessibleFrom(ISymbol member, INamedTypeSymbol fromType) =>
        member.DeclaredAccessibility switch
        {
            Accessibility.Public => true,
            Accessibility.Protected => true,
            Accessibility.ProtectedOrInternal => true,
            Accessibility.Internal => SameAssembly(member, fromType),
            Accessibility.ProtectedAndInternal => SameAssembly(member, fromType),
            _ => false
        };

    private static bool SameAssembly(ISymbol member, INamedTypeSymbol fromType) =>
        SymbolEqualityComparer.Default.Equals(member.ContainingAssembly, fromType.ContainingAssembly);

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
            IEventSymbol evt => evt.Name,
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
