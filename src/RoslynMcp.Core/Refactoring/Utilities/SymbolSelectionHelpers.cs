using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring.Extract;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared selected-symbol confirmation, definition-location, and selection
/// resolution helpers used by MakeStatic / MakeNonStatic / SafeDelete.
/// </summary>
internal static class SymbolSelectionHelpers
{
    /// <summary>
    /// Returns <paramref name="symbol"/> when <paramref name="expectedName"/> is
    /// null/whitespace or equals <see cref="ISymbol.Name"/>; otherwise throws
    /// <see cref="RefactoringException"/> with <see cref="ErrorCodes.SymbolNotFound"/>.
    /// </summary>
    internal static ISymbol ConfirmSymbolName(ISymbol symbol, string? expectedName)
    {
        if (!string.IsNullOrWhiteSpace(expectedName) && symbol.Name != expectedName)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"No symbol named '{expectedName}' found at the specified selection.");
        }

        return symbol;
    }

    /// <summary>
    /// True when any source location of <paramref name="symbol"/> shares the same
    /// <see cref="Location.SourceTree"/> and <see cref="Location.SourceSpan"/> as
    /// <paramref name="location"/>.
    /// </summary>
    internal static bool IsDefinitionLocation(ISymbol symbol, Location location)
    {
        return symbol.Locations.Any(definition =>
            definition.IsInSource &&
            definition.SourceTree == location.SourceTree &&
            definition.SourceSpan == location.SourceSpan);
    }

    /// <summary>
    /// Resolves the symbol at <paramref name="span"/>: token-overlap declared /
    /// identifier symbol info first, then innermost declared symbol whose
    /// declaration identifier overlaps the span. Optional
    /// <paramref name="symbolName"/> is confirmed via <see cref="ConfirmSymbolName"/>.
    /// Same body as the prior private copies on MakeStatic / MakeNonStatic.
    /// </summary>
    internal static ISymbol ResolveSelectedSymbol(
        SyntaxNode root,
        SemanticModel semanticModel,
        TextSpan span,
        string? symbolName,
        CancellationToken cancellationToken)
    {
        var token = root.FindToken(span.Start);
        if (token.Span.OverlapsWith(span) || span.OverlapsWith(token.Span))
        {
            var tokenNode = token.Parent;
            if (tokenNode != null)
            {
                var declaredOnToken = semanticModel.GetDeclaredSymbol(tokenNode, cancellationToken);
                if (declaredOnToken != null && DeclarationIdentifiers.IdentifierOverlaps(tokenNode, span))
                    return ConfirmSymbolName(declaredOnToken, symbolName);

                if (token.IsKind(SyntaxKind.IdentifierToken))
                {
                    var tokenSymbol = semanticModel.GetSymbolInfo(tokenNode, cancellationToken).Symbol;
                    if (tokenSymbol != null)
                        return ConfirmSymbolName(tokenSymbol, symbolName);
                }
            }
        }

        var node = root.FindNode(span, getInnermostNodeForTie: true);
        var declared = semanticModel.GetDeclaredSymbol(node, cancellationToken);
        if (declared != null && DeclarationIdentifiers.IdentifierOverlaps(node, span))
            return ConfirmSymbolName(declared, symbolName);

        throw new RefactoringException(
            ErrorCodes.SymbolNotFound,
            "No symbol found at the specified selection.");
    }
}
