using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Collects fields and properties suitable for equality/toString generation.
/// </summary>
public static class EqualityMemberCollector
{
    /// <summary>
    /// Gets all fields and auto-properties suitable for equality comparison.
    /// Excludes static, const, and implicitly declared members.
    /// Forwards to the three-parameter overload with <c>includeProperties: true</c>
    /// so existing NuGet callers keep a stable two-parameter IL signature.
    /// The three-parameter overload then forwards <c>includeInheritedMembers: false</c>.
    /// </summary>
    public static List<ISymbol> CollectMembers(INamedTypeSymbol typeSymbol, IReadOnlyList<string>? requestedFields = null)
        => CollectMembers(typeSymbol, requestedFields, includeProperties: true);

    /// <summary>
    /// Gets fields and properties suitable for equality comparison.
    /// Excludes static, const, and implicitly declared members.
    /// When <paramref name="includeProperties"/> is false and no requested names are given,
    /// only instance fields are collected. A non-empty <paramref name="requestedFields"/>
    /// list is authoritative and is resolved against both fields and properties.
    /// Forwards <c>includeInheritedMembers: false</c> so existing NuGet callers keep a
    /// stable three-parameter IL signature.
    /// </summary>
    public static List<ISymbol> CollectMembers(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<string>? requestedFields,
        bool includeProperties)
        => CollectMembers(typeSymbol, requestedFields, includeProperties, includeInheritedMembers: false);

    /// <summary>
    /// Gets fields and properties suitable for equality comparison.
    /// Excludes static, const, and implicitly declared members.
    /// When <paramref name="includeProperties"/> is false and no requested names are given,
    /// only instance fields are collected. A non-empty <paramref name="requestedFields"/>
    /// list is authoritative and is resolved against both fields and properties
    /// (including accessible inherited members when <paramref name="includeInheritedMembers"/> is true).
    /// When <paramref name="includeInheritedMembers"/> is true, accessible instance members
    /// declared on base types (until <c>System.Object</c> / <c>System.ValueType</c>) are
    /// appended after this type's members, immediate base first. Inherited members hidden
    /// by a closer non-implicit member of the same name (field, property, event, method,
    /// or nested type) are skipped.
    /// </summary>
    public static List<ISymbol> CollectMembers(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<string>? requestedFields,
        bool includeProperties,
        bool includeInheritedMembers)
    {
        var members = new List<ISymbol>();
        var hasRequestedFields = requestedFields != null && requestedFields.Count > 0;

        CollectDeclaredMembers(typeSymbol, typeSymbol, members, includeProperties, hasRequestedFields, requireAccessible: false);

        if (includeInheritedMembers)
        {
            for (var baseType = typeSymbol.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                if (NamedTypeHelpers.IsObjectOrValueType(baseType))
                    break;

                CollectDeclaredMembers(baseType, typeSymbol, members, includeProperties, hasRequestedFields, requireAccessible: true);
            }
        }

        if (hasRequestedFields)
        {
            var requestedSet = new HashSet<string>(requestedFields!);
            members = members.Where(m => requestedSet.Contains(m.Name)).ToList();
        }

        return members;
    }

    /// <summary>
    /// Gets the type of a field or property member.
    /// </summary>
    public static ITypeSymbol GetMemberType(ISymbol member) => member switch
    {
        IFieldSymbol f => f.Type,
        IPropertySymbol p => p.Type,
        _ => throw new InvalidOperationException($"Unexpected member type: {member.GetType()}")
    };

    private static void CollectDeclaredMembers(
        INamedTypeSymbol declaringType,
        INamedTypeSymbol fromType,
        List<ISymbol> members,
        bool includeProperties,
        bool hasRequestedFields,
        bool requireAccessible)
    {
        foreach (var field in declaringType.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst || field.IsImplicitlyDeclared)
                continue;
            if (requireAccessible && !IsAccessibleFrom(field, fromType))
                continue;
            if (requireAccessible && MemberHidingHelpers.IsHiddenFrom(field, fromType))
                continue;
            members.Add(field);
        }

        if (includeProperties || hasRequestedFields)
        {
            foreach (var prop in declaringType.GetMembers().OfType<IPropertySymbol>())
            {
                if (prop.IsStatic || prop.IsImplicitlyDeclared || prop.GetMethod == null || prop.IsIndexer)
                    continue;
                if (requireAccessible && !IsAccessibleFrom(prop, fromType))
                    continue;
                if (requireAccessible && MemberHidingHelpers.IsHiddenFrom(prop, fromType))
                    continue;
                members.Add(prop);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="member"/> can be read as <c>this.Name</c> from
    /// <paramref name="fromType"/> (public / protected / protected-internal;
    /// internal and private-protected when the same assembly).
    /// </summary>
    private static bool IsAccessibleFrom(ISymbol member, INamedTypeSymbol fromType)
    {
        var accessibility = member.DeclaredAccessibility;
        if (member is IPropertySymbol { GetMethod: { } getter })
            accessibility = MoreRestrictiveAccessibilityHelpers.MoreRestrictive(accessibility, getter.DeclaredAccessibility);

        return accessibility switch
        {
            Accessibility.Public => true,
            Accessibility.Protected => true,
            Accessibility.ProtectedOrInternal => true,
            Accessibility.Internal => MoreRestrictiveAccessibilityHelpers.SameAssembly(member, fromType),
            Accessibility.ProtectedAndInternal => MoreRestrictiveAccessibilityHelpers.SameAssembly(member, fromType),
            _ => false
        };
    }

}
