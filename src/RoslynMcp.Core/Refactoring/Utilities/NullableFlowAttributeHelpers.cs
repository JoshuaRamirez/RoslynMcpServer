using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Copies well-known <c>System.Diagnostics.CodeAnalysis</c> nullable
/// flow-analysis attributes onto generated overrides and implementations
/// so the derived member keeps the base contract (#3401).
/// </summary>
internal static class NullableFlowAttributeHelpers
{
    private static readonly HashSet<string> NullableFlowAttributeNames = new(StringComparer.Ordinal)
    {
        "AllowNullAttribute",
        "DisallowNullAttribute",
        "DoesNotReturnAttribute",
        "DoesNotReturnIfAttribute",
        "MaybeNullAttribute",
        "MaybeNullWhenAttribute",
        "MemberNotNullAttribute",
        "MemberNotNullWhenAttribute",
        "NotNullAttribute",
        "NotNullIfNotNullAttribute",
        "NotNullWhenAttribute"
    };

    internal static ParameterSyntax WithNullableFlowAttributes(
        ParameterSyntax syntax,
        IParameterSymbol parameter)
    {
        var lists = ToAttributeLists(parameter.GetAttributes(), returnTarget: false, scope: default);
        return lists.Count == 0 ? syntax : syntax.WithAttributeLists(syntax.AttributeLists.AddRange(lists));
    }

    internal static MethodDeclarationSyntax WithNullableFlowAttributes(
        MethodDeclarationSyntax syntax,
        IMethodSymbol method,
        INamedTypeSymbol? emittingType = null)
    {
        var scope = new MemberScope(method.ContainingType, emittingType);
        var lists = new List<AttributeListSyntax>();
        lists.AddRange(ToAttributeLists(method.GetAttributes(), returnTarget: false, scope));
        lists.AddRange(ToAttributeLists(method.GetReturnTypeAttributes(), returnTarget: true, scope));
        return lists.Count == 0 ? syntax : syntax.WithAttributeLists(syntax.AttributeLists.AddRange(lists));
    }

    internal static PropertyDeclarationSyntax WithNullableFlowAttributes(
        PropertyDeclarationSyntax syntax,
        IPropertySymbol property,
        INamedTypeSymbol? emittingType = null)
    {
        var scope = new MemberScope(property.ContainingType, emittingType);
        var lists = ToAttributeLists(property.GetAttributes(), returnTarget: false, scope);
        if (lists.Count > 0)
            syntax = syntax.WithAttributeLists(syntax.AttributeLists.AddRange(lists));
        return syntax.WithAccessorList(WithGetterReturnAttributes(syntax.AccessorList, property, scope));
    }

    internal static IndexerDeclarationSyntax WithNullableFlowAttributes(
        IndexerDeclarationSyntax syntax,
        IPropertySymbol indexer,
        INamedTypeSymbol? emittingType = null)
    {
        var scope = new MemberScope(indexer.ContainingType, emittingType);
        var lists = ToAttributeLists(indexer.GetAttributes(), returnTarget: false, scope);
        if (lists.Count > 0)
            syntax = syntax.WithAttributeLists(syntax.AttributeLists.AddRange(lists));
        return syntax.WithAccessorList(WithGetterReturnAttributes(syntax.AccessorList, indexer, scope));
    }

    /// <summary>
    /// Getter return attributes (<c>[return: NotNull]</c>, ...) are only
    /// valid on the <c>get</c> accessor, not on the property (CS0657).
    /// </summary>
    private static AccessorListSyntax? WithGetterReturnAttributes(
        AccessorListSyntax? accessorList,
        IPropertySymbol property,
        MemberScope scope)
    {
        if (accessorList == null || property.GetMethod == null)
            return accessorList;

        var lists = ToAttributeLists(property.GetMethod.GetReturnTypeAttributes(), returnTarget: true, scope);
        if (lists.Count == 0)
            return accessorList;

        var getter = accessorList.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        return getter == null
            ? accessorList
            : accessorList.ReplaceNode(getter, getter.WithAttributeLists(getter.AttributeLists.AddRange(lists)));
    }

    /// <summary>
    /// True when the method carries a copied postcondition attribute
    /// (<c>[DoesNotReturn]</c>, <c>[DoesNotReturnIf]</c>,
    /// <c>[MemberNotNull(When)]</c>, <c>[return: NotNull]</c>,
    /// <c>[return: NotNullIfNotNull]</c>, <c>[NotNull]</c> /
    /// <c>[NotNullWhen]</c> on a parameter) that a default-valued stub body
    /// cannot honor, so the stub must throw instead (CS8763 / CS8774 /
    /// CS8762 / CS8825).
    /// </summary>
    internal static bool HasPostconditionAttributes(IMethodSymbol method, INamedTypeSymbol? emittingType = null)
    {
        var scope = new MemberScope(method.ContainingType, emittingType);
        if (method.GetAttributes().Any(data => IsCopiedPostcondition(data, scope,
                "DoesNotReturnAttribute", "MemberNotNullAttribute", "MemberNotNullWhenAttribute")))
            return true;

        // An override inherits the base [MemberNotNull] obligation even when
        // the attribute itself cannot be copied (CS8774).
        if (method.ContainingType?.TypeKind != TypeKind.Interface
            && method.GetAttributes().Any(data => IsFlowAttribute(data, "MemberNotNullAttribute", "MemberNotNullWhenAttribute")))
            return true;

        if (method.GetReturnTypeAttributes().Any(data => IsCopiedPostcondition(data, scope,
                "NotNullAttribute", "NotNullIfNotNullAttribute")))
            return true;

        return method.Parameters.Any(parameter => parameter.GetAttributes().Any(data => IsCopiedPostcondition(data, default,
            "DoesNotReturnIfAttribute", "NotNullAttribute", "NotNullWhenAttribute")));
    }

    /// <summary>
    /// True when the property / indexer getter carries a copied
    /// postcondition (<c>[NotNull]</c> on the property, or
    /// <c>[return: NotNull]</c> / <c>[return: NotNullIfNotNull]</c> on the
    /// getter) that a default-valued getter cannot honor, so the getter
    /// must throw instead.
    /// </summary>
    internal static bool HasGetterPostconditionAttributes(IPropertySymbol property, INamedTypeSymbol? emittingType = null)
    {
        if (property.GetMethod == null)
            return false;

        var scope = new MemberScope(property.ContainingType, emittingType);
        return property.GetAttributes().Any(data => IsCopiedPostcondition(data, scope, "NotNullAttribute"))
            || property.GetMethod.GetReturnTypeAttributes().Any(data => IsCopiedPostcondition(data, scope,
                "NotNullAttribute", "NotNullIfNotNullAttribute"));
    }

    private static bool IsFlowAttribute(AttributeData data, params string[] names) =>
        data.AttributeClass is { } type
        && type.ContainingNamespace?.ToDisplayString() == "System.Diagnostics.CodeAnalysis"
        && names.Contains(type.Name, StringComparer.Ordinal);

    private static bool IsCopiedPostcondition(AttributeData data, MemberScope scope, params string[] names) =>
        data.AttributeClass is { } type
        && names.Contains(type.Name, StringComparer.Ordinal)
        && TryCreateAttribute(data, scope) != null;

    /// <summary>
    /// Declaring type of the source member and the type that will declare
    /// the generated copy; used to drop <c>[MemberNotNull]</c> targets that
    /// are not members of the generated declaration's type (CS8776).
    /// </summary>
    private readonly record struct MemberScope(INamedTypeSymbol? DeclaringType, INamedTypeSymbol? EmittingType);

    private static List<AttributeListSyntax> ToAttributeLists(
        IEnumerable<AttributeData> attributes,
        bool returnTarget,
        MemberScope scope)
    {
        var lists = new List<AttributeListSyntax>();
        foreach (var data in attributes)
        {
            var attribute = TryCreateAttribute(data, scope);
            if (attribute == null)
                continue;

            var list = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute));
            if (returnTarget)
            {
                list = list.WithTarget(
                    SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.ReturnKeyword)));
            }

            lists.Add(list);
        }

        return lists;
    }

    private static AttributeSyntax? TryCreateAttribute(AttributeData data, MemberScope scope)
    {
        var type = data.AttributeClass;
        if (type == null || type.TypeKind == TypeKind.Error)
            return null;

        if (type.ContainingNamespace?.ToDisplayString() != "System.Diagnostics.CodeAnalysis")
            return null;
        if (!NullableFlowAttributeNames.Contains(type.Name))
            return null;

        var shortName = type.Name.EndsWith("Attribute", StringComparison.Ordinal)
            ? type.Name[..^"Attribute".Length]
            : type.Name;
        var name = SyntaxFactory.ParseName("global::System.Diagnostics.CodeAnalysis." + shortName);

        var constructorArguments = ExpandConstructorArguments(data.ConstructorArguments).ToList();
        if (type.Name is "MemberNotNullAttribute" or "MemberNotNullWhenAttribute")
        {
            // Keep only member names the generated declaration can target;
            // a base-class private field is not a valid target (CS8776).
            var leading = type.Name == "MemberNotNullWhenAttribute" ? 1 : 0;
            var kept = constructorArguments.Take(leading).ToList();
            kept.AddRange(constructorArguments.Skip(leading).Where(argument =>
                argument.Value is string memberName && IsValidMemberNotNullTarget(memberName, scope)));
            if (kept.Count == leading)
                return null;
            constructorArguments = kept;
        }

        var arguments = new List<AttributeArgumentSyntax>();
        foreach (var argument in constructorArguments)
            arguments.Add(SyntaxFactory.AttributeArgument(ToExpression(argument)));

        foreach (var named in data.NamedArguments)
        {
            arguments.Add(
                SyntaxFactory.AttributeArgument(ToExpression(named.Value))
                    .WithNameEquals(SyntaxFactory.NameEquals(named.Key)));
        }

        var syntax = SyntaxFactory.Attribute(name);
        if (arguments.Count > 0)
        {
            syntax = syntax.WithArgumentList(
                SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(arguments)));
        }

        return syntax;
    }

    private static bool IsValidMemberNotNullTarget(string memberName, MemberScope scope)
    {
        // The compiler resolves [MemberNotNull] names against the declaring
        // type only; inherited members, even protected ones, are rejected
        // (CS8776). An override still inherits the base obligation, so the
        // attribute is safe to drop. Without a known emitting type (e.g.
        // implement_interface) the destination may not declare the member,
        // or only implement it explicitly, so the target is dropped too.
        var target = scope.EmittingType;
        if (target == null)
            return false;

        return target.GetMembers(memberName).Any(member => member is IFieldSymbol or IPropertySymbol);
    }

    private static IEnumerable<TypedConstant> ExpandConstructorArguments(
        IReadOnlyList<TypedConstant> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument.Kind == TypedConstantKind.Array)
            {
                foreach (var item in argument.Values)
                    yield return item;
            }
            else
            {
                yield return argument;
            }
        }
    }

    private static ExpressionSyntax ToExpression(TypedConstant constant)
    {
        if (constant.IsNull)
            return SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);

        return constant.Kind switch
        {
            TypedConstantKind.Primitive => constant.Value switch
            {
                bool value => SyntaxFactory.LiteralExpression(
                    value ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),
                string value => SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value)),
                int value => SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value)),
                _ => SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(constant.Value?.ToString() ?? string.Empty))
            },
            TypedConstantKind.Type when constant.Value is ITypeSymbol type =>
                SyntaxFactory.TypeOfExpression(
                    SyntaxFactory.ParseTypeName(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))),
            _ => SyntaxFactory.ParseExpression(constant.Value?.ToString() ?? "default")
        };
    }
}
