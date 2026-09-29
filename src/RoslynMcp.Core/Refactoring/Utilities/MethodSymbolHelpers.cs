using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared helpers for method-symbol normalization and related checks used by
/// make_static / make_non_static.
/// </summary>
internal static class MethodSymbolHelpers
{
    /// <summary>
    /// True when <paramref name="node"/> is inside a <c>nameof(...)</c>
    /// invocation. Same body as the two private copies on make_static /
    /// make_non_static.
    /// </summary>
    internal static bool IsInNameof(SyntaxNode node)
    {
        foreach (var ancestor in node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (ancestor.Expression is IdentifierNameSyntax identifier &&
                identifier.Identifier.Text == "nameof")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when any declaring <see cref="MethodDeclarationSyntax"/> has the
    /// <c>virtual</c> modifier. Same body as the two private copies on
    /// make_static / make_non_static.
    /// </summary>
    internal static bool HasVirtualModifier(IMethodSymbol method)
    {
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is MethodDeclarationSyntax declaration &&
                declaration.Modifiers.Any(SyntaxKind.VirtualKeyword))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the <see cref="IMethodSymbol"/> <see cref="ISymbol.OriginalDefinition"/>
    /// of <paramref name="symbol"/>, rejecting property/event accessors and
    /// non-methods. Same body as the two private copies on make_static /
    /// make_non_static.
    /// </summary>
    internal static IMethodSymbol NormalizeMethodSymbol(ISymbol symbol)
    {
        symbol = symbol.OriginalDefinition;

        if (symbol is IMethodSymbol { AssociatedSymbol: { } associated } &&
            associated.Kind is Microsoft.CodeAnalysis.SymbolKind.Property or Microsoft.CodeAnalysis.SymbolKind.Event)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Symbol '{associated.Name}' is not a method.");
        }

        if (symbol is not IMethodSymbol method)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Symbol '{symbol.Name}' is not a method.");
        }

        return method;
    }
}
