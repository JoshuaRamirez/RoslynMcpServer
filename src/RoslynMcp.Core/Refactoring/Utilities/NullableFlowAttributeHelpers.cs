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
        var lists = ToAttributeLists(parameter.GetAttributes(), returnTarget: false);
        return lists.Count == 0 ? syntax : syntax.WithAttributeLists(syntax.AttributeLists.AddRange(lists));
    }

    internal static MethodDeclarationSyntax WithNullableFlowAttributes(
        MethodDeclarationSyntax syntax,
        IMethodSymbol method)
    {
        var lists = new List<AttributeListSyntax>();
        lists.AddRange(ToAttributeLists(method.GetAttributes(), returnTarget: false));
        lists.AddRange(ToAttributeLists(method.GetReturnTypeAttributes(), returnTarget: true));
        return lists.Count == 0 ? syntax : syntax.WithAttributeLists(syntax.AttributeLists.AddRange(lists));
    }

    internal static PropertyDeclarationSyntax WithNullableFlowAttributes(
        PropertyDeclarationSyntax syntax,
        IPropertySymbol property)
    {
        var lists = ToAttributeLists(property.GetAttributes(), returnTarget: false);
        if (property.GetMethod != null)
            lists.AddRange(ToAttributeLists(property.GetMethod.GetReturnTypeAttributes(), returnTarget: true));
        return lists.Count == 0 ? syntax : syntax.WithAttributeLists(syntax.AttributeLists.AddRange(lists));
    }

    internal static IndexerDeclarationSyntax WithNullableFlowAttributes(
        IndexerDeclarationSyntax syntax,
        IPropertySymbol indexer)
    {
        var lists = ToAttributeLists(indexer.GetAttributes(), returnTarget: false);
        if (indexer.GetMethod != null)
            lists.AddRange(ToAttributeLists(indexer.GetMethod.GetReturnTypeAttributes(), returnTarget: true));
        return lists.Count == 0 ? syntax : syntax.WithAttributeLists(syntax.AttributeLists.AddRange(lists));
    }

    private static List<AttributeListSyntax> ToAttributeLists(
        IEnumerable<AttributeData> attributes,
        bool returnTarget)
    {
        var lists = new List<AttributeListSyntax>();
        foreach (var data in attributes)
        {
            var attribute = TryCreateAttribute(data);
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

    private static AttributeSyntax? TryCreateAttribute(AttributeData data)
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

        var arguments = new List<AttributeArgumentSyntax>();
        foreach (var argument in ExpandConstructorArguments(data.ConstructorArguments))
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
