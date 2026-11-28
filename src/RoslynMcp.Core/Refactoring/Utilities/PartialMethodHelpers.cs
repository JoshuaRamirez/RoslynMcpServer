using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared partial-method part walk used by make_static / make_non_static.
/// Same bodies as the two identical MakeStatic / MakeNonStatic copies.
/// Named PartialMethodHelpers (not MethodSymbolHelpers) because this cluster
/// is the partial definition/implementation walk used together by those
/// operations.
/// </summary>
internal static class PartialMethodHelpers
{
    /// <summary>
    /// Prefers the implementation part of a partial method so the walk
    /// treats definition + implementation as one method. Same body as the
    /// two MakeStatic / MakeNonStatic copies.
    /// </summary>
    internal static IMethodSymbol CanonicalPartialMethod(IMethodSymbol method)
    {
        var implementation = method.PartialImplementationPart
            ?? method.PartialDefinitionPart?.PartialImplementationPart;
        return implementation ?? method.PartialDefinitionPart ?? method;
    }

    /// <summary>
    /// Both partial definition and implementation (when present), plus
    /// <paramref name="method"/> itself. Same body as the two MakeStatic /
    /// MakeNonStatic copies.
    /// </summary>
    internal static IEnumerable<IMethodSymbol> GetPartialMethodParts(IMethodSymbol method)
    {
        var parts = new List<IMethodSymbol> { method };
        if (method.PartialDefinitionPart != null)
            parts.Add(method.PartialDefinitionPart);
        if (method.PartialImplementationPart != null)
            parts.Add(method.PartialImplementationPart);
        if (method.PartialDefinitionPart?.PartialImplementationPart != null)
            parts.Add(method.PartialDefinitionPart.PartialImplementationPart);
        if (method.PartialImplementationPart?.PartialDefinitionPart != null)
            parts.Add(method.PartialImplementationPart.PartialDefinitionPart);
        return parts.Distinct<IMethodSymbol>(SymbolEqualityComparer.Default);
    }

    /// <summary>
    /// Declaring syntax references across all partial parts of
    /// <paramref name="method"/>. Same body as the two MakeStatic /
    /// MakeNonStatic copies (promoted from private to internal).
    /// </summary>
    internal static IEnumerable<SyntaxReference> EnumerateDeclaringSyntaxReferences(IMethodSymbol method)
    {
        foreach (var part in GetPartialMethodParts(method))
        {
            foreach (var reference in part.DeclaringSyntaxReferences)
                yield return reference;
        }
    }

    /// <summary>
    /// Drops the bodiless defining declaration of a partial method, property or
    /// indexer from a by-name candidate list when its implementing declaration
    /// (same containing type, name and parameter types) is also a candidate, so
    /// a memberName lookup resolves to the part that has a body (#3321). A
    /// defining declaration without a matching implementation in the list is
    /// kept, so it still reports its own error.
    /// </summary>
    internal static IEnumerable<T> PreferPartialImplementations<T>(IEnumerable<T> candidates)
        where T : SyntaxNode
    {
        var list = candidates.ToList();
        var implementationKeys = new HashSet<string>(
            list.Where(IsPartialImplementation).Select(PartialPartKey).OfType<string>(),
            StringComparer.Ordinal);
        if (implementationKeys.Count == 0)
            return list;

        return list.Where(node =>
            !(IsBodilessPartialDefinition(node) && PartialPartKey(node) is { } key && implementationKeys.Contains(key)));
    }

    /// <summary>
    /// True for a <c>partial</c> method, property or indexer declaration with no
    /// body, expression body or accessor bodies (the defining part).
    /// </summary>
    internal static bool IsBodilessPartialDefinition(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => IsPartial(m.Modifiers) && m.Body == null && m.ExpressionBody == null,
        BasePropertyDeclarationSyntax p => IsPartial(p.Modifiers) && !HasBody(p),
        _ => false
    };

    private static bool IsPartialImplementation(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => IsPartial(m.Modifiers) && (m.Body != null || m.ExpressionBody != null),
        BasePropertyDeclarationSyntax p => IsPartial(p.Modifiers) && HasBody(p),
        _ => false
    };

    private static bool IsPartial(SyntaxTokenList modifiers) => modifiers.Any(SyntaxKind.PartialKeyword);

    private static bool HasBody(BasePropertyDeclarationSyntax property)
    {
        var expressionBody = property switch
        {
            PropertyDeclarationSyntax p => p.ExpressionBody,
            IndexerDeclarationSyntax i => i.ExpressionBody,
            _ => null
        };
        return expressionBody != null ||
               (property.AccessorList?.Accessors.Any(a => a.Body != null || a.ExpressionBody != null) ?? false);
    }

    /// <summary>
    /// Containing-type chain + kind + name + parameter types (whitespace-free),
    /// which the defining and implementing parts of one partial member share.
    /// Parameter names may differ between parts, so they are not included.
    /// </summary>
    private static string? PartialPartKey(SyntaxNode node)
    {
        string? member = node switch
        {
            MethodDeclarationSyntax m =>
                $"M:{m.Identifier.ValueText}`{m.TypeParameterList?.Parameters.Count ?? 0}({ParameterTypes(m.ParameterList.Parameters)})",
            PropertyDeclarationSyntax p => $"P:{p.Identifier.ValueText}",
            IndexerDeclarationSyntax i => $"I:[{ParameterTypes(i.ParameterList.Parameters)}]",
            _ => null
        };
        if (member == null)
            return null;

        var types = node.Ancestors().OfType<BaseTypeDeclarationSyntax>()
            .Select(t => t is TypeDeclarationSyntax td
                ? $"{t.Identifier.ValueText}`{td.TypeParameterList?.Parameters.Count ?? 0}"
                : t.Identifier.ValueText)
            .Reverse();
        var ns = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Select(n => n.Name.ToString())
            .Reverse();
        return string.Join(".", ns) + "::" + string.Join("+", types) + "::" + member;
    }

    private static string ParameterTypes(SeparatedSyntaxList<ParameterSyntax> parameters) =>
        string.Join(",", parameters.Select(p =>
            string.Concat(p.Modifiers.Select(t => t.ValueText + " ")) +
            (p.Type?.WithoutTrivia().ToString().Replace(" ", "", StringComparison.Ordinal) ?? "")));
}
