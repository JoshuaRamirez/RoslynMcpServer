using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Resolution;

/// <summary>
/// General-purpose symbol resolver that can find any symbol (types, methods, properties,
/// fields, locals, parameters) by position or name.
/// Extracted from RenameSymbolOperation.FindSymbolAsync() to be reusable across all query tools.
/// </summary>
public sealed class SymbolResolver
{
    private readonly WorkspaceContext _context;

    /// <summary>
    /// Creates a new general-purpose symbol resolver.
    /// </summary>
    public SymbolResolver(WorkspaceContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Resolves a symbol using position-first strategy: tries line/column, falls back to name.
    /// </summary>
    /// <param name="sourceFile">Absolute path to the source file.</param>
    /// <param name="symbolName">Symbol name (for name-based resolution or validation).</param>
    /// <param name="line">Optional 1-based line number.</param>
    /// <param name="column">Optional 1-based column number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved symbol and its containing document.</returns>
    public async Task<GeneralSymbolResolutionResult> ResolveSymbolAsync(
        string sourceFile,
        string? symbolName = null,
        int? line = null,
        int? column = null,
        CancellationToken cancellationToken = default)
    {
        var document = GetDocumentOrThrow(sourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Position-based resolution (most precise)
        if (line.HasValue)
        {
            return await ResolveByPositionAsync(document, root, semanticModel, symbolName, line.Value, column ?? 1, cancellationToken);
        }

        // Name-based resolution (fallback)
        if (!string.IsNullOrWhiteSpace(symbolName))
        {
            return ResolveByName(document, root, semanticModel, symbolName, cancellationToken);
        }

        throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either line/column or symbolName must be provided.");
    }

    /// <summary>
    /// Resolves a symbol at a specific text position within a document.
    /// Useful when caller already has the document and semantic model.
    /// </summary>
    public async Task<GeneralSymbolResolutionResult> ResolveAtPositionAsync(
        Document document,
        int line,
        int column,
        CancellationToken cancellationToken = default)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        return await ResolveByPositionAsync(document, root, semanticModel, null, line, column, cancellationToken);
    }

    private async Task<GeneralSymbolResolutionResult> ResolveByPositionAsync(
        Document document,
        SyntaxNode root,
        SemanticModel semanticModel,
        string? expectedName,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var position = GetPosition(root, line, column);
        var token = root.FindToken(position);

        // First try: SymbolFinder.FindSymbolAtPositionAsync — most reliable for references
        var symbolAtPosition = await SymbolFinder.FindSymbolAtPositionAsync(
            semanticModel, position, _context.Workspace, cancellationToken);

        if (symbolAtPosition != null)
        {
            if (expectedName == null || symbolAtPosition.Name == expectedName)
            {
                return new GeneralSymbolResolutionResult
                {
                    Symbol = symbolAtPosition,
                    Document = document
                };
            }
        }

        // Second try: walk up from token to find declared or referenced symbol
        var node = token.Parent;
        while (node != null)
        {
            var declaredSymbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
            if (declaredSymbol != null)
            {
                if (expectedName == null || declaredSymbol.Name == expectedName)
                {
                    return new GeneralSymbolResolutionResult
                    {
                        Symbol = declaredSymbol,
                        Document = document
                    };
                }
            }

            var symbolInfo = semanticModel.GetSymbolInfo(node, cancellationToken);
            if (symbolInfo.Symbol != null)
            {
                if (expectedName == null || symbolInfo.Symbol.Name == expectedName)
                {
                    return new GeneralSymbolResolutionResult
                    {
                        Symbol = symbolInfo.Symbol,
                        Document = document
                    };
                }
            }

            node = node.Parent;
        }

        var nameMsg = expectedName != null ? $" named '{expectedName}'" : "";
        throw new RefactoringException(
            ErrorCodes.SymbolNotFound,
            $"No symbol{nameMsg} found at line {line}, column {column}.");
    }

    private static GeneralSymbolResolutionResult ResolveByName(
        Document document,
        SyntaxNode root,
        SemanticModel semanticModel,
        string symbolName,
        CancellationToken cancellationToken)
    {
        var candidates = new List<ISymbol>();

        foreach (var node in root.DescendantNodes())
        {
            var symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
            if (symbol != null && symbol.Name == symbolName)
            {
                candidates.Add(symbol);
            }
        }

        if (candidates.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"No symbol named '{symbolName}' found in file.");
        }

        if (candidates.Count > 1)
        {
            var locations = candidates.Select(c =>
            {
                var loc = c.Locations.FirstOrDefault(l => l.IsInSource);
                if (loc != null)
                {
                    var span = loc.GetLineSpan();
                    return $"line {span.StartLinePosition.Line + 1}";
                }
                return "unknown location";
            }).ToList();

            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Multiple symbols named '{symbolName}' found. Provide line number to disambiguate.",
                new Dictionary<string, object>
                {
                    ["candidateCount"] = candidates.Count,
                    ["locations"] = locations
                });
        }

        return new GeneralSymbolResolutionResult
        {
            Symbol = candidates[0],
            Document = document
        };
    }

    /// <summary>
    /// Converts 1-based line/column to absolute position with bounds validation.
    /// </summary>
    /// <param name="root">Syntax root to get text from.</param>
    /// <param name="line">1-based line number.</param>
    /// <param name="column">1-based column number.</param>
    /// <returns>Absolute position in text.</returns>
    public static int GetPosition(SyntaxNode root, int line, int column)
    {
        var text = root.GetText();
        var lineIndex = line - 1;

        if (lineIndex < 0 || lineIndex >= text.Lines.Count)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidLineNumber,
                $"Line {line} is out of range. File has {text.Lines.Count} lines.");
        }

        var lineInfo = text.Lines[lineIndex];
        var columnIndex = column - 1;
        var lineLength = lineInfo.End - lineInfo.Start;

        if (columnIndex < 0 || columnIndex > lineLength)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidColumnNumber,
                $"Column {column} is out of range for line {line} (line has {lineLength} characters).");
        }

        return lineInfo.Start + columnIndex;
    }

    private Document GetDocumentOrThrow(string filePath)
    {
        var doc = _context.GetDocumentByPath(filePath);
        if (doc == null)
        {
            throw new RefactoringException(
                ErrorCodes.SourceNotInWorkspace,
                $"File not found in workspace: {filePath}");
        }
        return doc;
    }
}

/// <summary>
/// Result of general-purpose symbol resolution.
/// </summary>
public sealed class GeneralSymbolResolutionResult
{
    /// <summary>
    /// The resolved symbol (any kind: type, method, property, field, etc.).
    /// </summary>
    public required ISymbol Symbol { get; init; }

    /// <summary>
    /// The document containing the symbol.
    /// </summary>
    public required Document Document { get; init; }
}
