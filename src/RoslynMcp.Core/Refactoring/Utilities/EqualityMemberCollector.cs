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
    /// appended after this type's members, immediate base first.
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
                if (IsObjectOrValueType(baseType))
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
                members.Add(prop);
            }
        }
    }

    private static bool IsObjectOrValueType(INamedTypeSymbol type) =>
        type.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType;

    /// <summary>
    /// True when <paramref name="member"/> can be read as <c>this.Name</c> from
    /// <paramref name="fromType"/> (public / protected / protected-internal;
    /// internal and private-protected when the same assembly).
    /// </summary>
    private static bool IsAccessibleFrom(ISymbol member, INamedTypeSymbol fromType)
    {
        var accessibility = member.DeclaredAccessibility;
        if (member is IPropertySymbol { GetMethod: { } getter })
            accessibility = MoreRestrictive(accessibility, getter.DeclaredAccessibility);

        return accessibility switch
        {
            Accessibility.Public => true,
            Accessibility.Protected => true,
            Accessibility.ProtectedOrInternal => true,
            Accessibility.Internal => SameAssembly(member, fromType),
            Accessibility.ProtectedAndInternal => SameAssembly(member, fromType),
            _ => false
        };
    }

    private static Accessibility MoreRestrictive(Accessibility left, Accessibility right)
    {
        return AccessibilityRank(left) <= AccessibilityRank(right) ? left : right;
    }

    private static int AccessibilityRank(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Private => 0,
        Accessibility.ProtectedAndInternal => 1,
        Accessibility.Internal => 2,
        Accessibility.Protected => 3,
        Accessibility.ProtectedOrInternal => 4,
        Accessibility.Public => 5,
        _ => 0
    };

    private static bool SameAssembly(ISymbol member, INamedTypeSymbol fromType) =>
        SymbolEqualityComparer.Default.Equals(member.ContainingAssembly, fromType.ContainingAssembly);
}
