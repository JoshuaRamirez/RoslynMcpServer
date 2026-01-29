using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Resolution;

/// <summary>
/// Resolves type symbols by name across workspace.
/// </summary>
public sealed class TypeSymbolResolver
{
    private readonly WorkspaceContext _context;

    /// <summary>
    /// Creates a new type symbol resolver.
    /// </summary>
    /// <param name="context">Workspace context to search.</param>
    public TypeSymbolResolver(WorkspaceContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Finds a type symbol by fully qualified name.
    /// </summary>
    /// <param name="fullyQualifiedName">Fully qualified type name (e.g., "MyNamespace.MyClass").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The type symbol if found.</returns>
    public async Task<INamedTypeSymbol?> FindTypeByNameAsync(
        string fullyQualifiedName,
        CancellationToken cancellationToken = default)
    {
        foreach (var project in _context.Solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            // Try exact match first (most common case)
            var symbol = compilation.GetTypeByMetadataName(fullyQualifiedName);
            if (symbol != null) return symbol;

            // Fall back to searching by simple name if not fully qualified
            if (!fullyQualifiedName.Contains('.'))
            {
                var candidates = compilation.GetSymbolsWithName(
                    name => name == fullyQualifiedName,
                    SymbolFilter.Type,
                    cancellationToken);

                var typeSymbol = candidates.OfType<INamedTypeSymbol>().FirstOrDefault();
                if (typeSymbol != null) return typeSymbol;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a type symbol in a specific file, optionally at a specific line.
    /// </summary>
    /// <param name="filePath">Path to the source file.</param>
    /// <param name="symbolName">Symbol name (simple or qualified).</param>
    /// <param name="line">Optional 1-based line number for disambiguation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolution result with the symbol and its declaration.</returns>
    public async Task<SymbolResolutionResult> FindTypeInFileAsync(
        string filePath,
        string symbolName,
        int? line = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = PathResolver.NormalizePath(filePath);
        var document = _context.GetDocumentByPath(normalizedPath);

        if (document == null)
        {
            throw new RefactoringException(
                ErrorCodes.SourceNotInWorkspace,
                $"File not found in workspace: {filePath}");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                "Could not parse file.");
        }

        // Find all type declarations in the file
        var typeDeclarations = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax) // Top-level only
            .ToList();

        // Filter by name
        var simpleName = symbolName.Contains('.') ? symbolName.Split('.').Last() : symbolName;
        var matchingDeclarations = typeDeclarations
            .Where(t => t.Identifier.Text == simpleName)
            .ToList();

        if (matchingDeclarations.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"No type named '{symbolName}' found in file.",
                new Dictionary<string, object>
                {
                    ["file"] = filePath,
                    ["availableTypes"] = typeDeclarations.Select(t => t.Identifier.Text).ToList()
                });
        }

        TypeDeclarationSyntax declaration;

        if (matchingDeclarations.Count > 1)
        {
            if (line == null)
            {
                throw new RefactoringException(
                    ErrorCodes.SymbolAmbiguous,
                    $"Multiple types named '{symbolName}' found. Provide line number to disambiguate.",
                    new Dictionary<string, object>
                    {
                        ["matches"] = matchingDeclarations.Select(t =>
                            root.GetLocation().GetLineSpan().StartLinePosition.Line + 1).ToList()
                    });
            }

            // Find by line number
            declaration = matchingDeclarations
                .FirstOrDefault(t =>
                {
                    var lineSpan = t.GetLocation().GetLineSpan();
                    var declarationLine = lineSpan.StartLinePosition.Line + 1; // Convert to 1-based
                    return declarationLine == line.Value;
                })
                ?? throw new RefactoringException(
                    ErrorCodes.SymbolNotFound,
                    $"No type named '{symbolName}' found at line {line}.");
        }
        else
        {
            declaration = matchingDeclarations[0];
        }

        // Get the symbol
        var symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken) as INamedTypeSymbol;

        if (symbol == null)
        {
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                "Could not resolve symbol from declaration.");
        }

        // Validate it's a moveable type
        if (symbol.ContainingType != null)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolIsNested,
                "Nested types cannot be moved independently. Move the containing type instead.");
        }

        return new SymbolResolutionResult
        {
            Symbol = symbol,
            Declaration = declaration,
            Document = document
        };
    }
}

/// <summary>
/// Result of symbol resolution.
/// </summary>
public sealed class SymbolResolutionResult
{
    /// <summary>
    /// The resolved type symbol.
    /// </summary>
    public required INamedTypeSymbol Symbol { get; init; }

    /// <summary>
    /// The syntax declaration node.
    /// </summary>
    public required TypeDeclarationSyntax Declaration { get; init; }

    /// <summary>
    /// The document containing the declaration.
    /// </summary>
    public required Document Document { get; init; }
}
