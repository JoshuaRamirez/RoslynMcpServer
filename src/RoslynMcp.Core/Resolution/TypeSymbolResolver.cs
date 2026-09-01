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
    public Task<SymbolResolutionResult> FindTypeInFileAsync(
        string filePath,
        string symbolName,
        int? line = null,
        CancellationToken cancellationToken = default)
        => FindTypeInFileAsync(filePath, symbolName, line, column: null, cancellationToken);

    /// <summary>
    /// Finds a type symbol in a specific file, optionally at a specific line
    /// and column. Omitted <paramref name="column"/> keeps today's
    /// <paramref name="symbolName"/> + optional <paramref name="line"/>
    /// pick (start-line equality when several top-level types share the
    /// name; <c>SymbolAmbiguous</c> when line is omitted and several
    /// types share the name; a single match ignores line). Column without
    /// line keeps that omitted-line path. When column is set with line,
    /// picks the top-level type whose identifier or declaration span
    /// covers that 1-based column (identifier preferred, then smallest
    /// covering type). Nested types stay unmoveable.
    /// </summary>
    /// <param name="filePath">Path to the source file.</param>
    /// <param name="symbolName">Symbol name (simple or qualified).</param>
    /// <param name="line">Optional 1-based line number for disambiguation.</param>
    /// <param name="column">Optional 1-based column for disambiguation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolution result with the symbol and its declaration.</returns>
    public async Task<SymbolResolutionResult> FindTypeInFileAsync(
        string filePath,
        string symbolName,
        int? line,
        int? column,
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

        // Column without line is not a source position: substituting each
        // candidate's own start line would match every equally-aligned
        // same-name type. Keep today's omitted-line path (SymbolAmbiguous
        // when several types share the name; single-match ignores line).
        if (column.HasValue && line.HasValue)
        {
            // Do not require the declaration to start on `line` — a split
            // type's identifier may live on a continuation line whose
            // declaration span still covers that column. Prefer the
            // identifier hit, then the smallest covering top-level type.
            // Do not silently pick the first when a covering node exists
            // elsewhere — scan every candidate. If nothing covers this
            // position, keep today's not-found rather than inventing a
            // first-match.
            declaration = FindCoveringType(matchingDeclarations, line.Value, column.Value)
                ?? throw new RefactoringException(
                    ErrorCodes.SymbolNotFound,
                    $"No type named '{symbolName}' found at line {line}.");
        }
        else if (matchingDeclarations.Count > 1)
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

    /// <summary>
    /// Picks the top-level type whose identifier or declaration span
    /// covers the 1-based <paramref name="line"/> and
    /// <paramref name="column"/> (identifier preferred, then smallest
    /// covering type). Returns null when nothing covers that position.
    /// </summary>
    internal static TypeDeclarationSyntax? FindCoveringType(
        IReadOnlyList<TypeDeclarationSyntax> matches,
        int line,
        int column) =>
        matches
            .Where(type => TypeCoversColumn(type, line, column))
            .OrderBy(type => IdentifierCoversColumn(type, line, column) ? 0 : 1)
            .ThenBy(type => type.Span.Length)
            .FirstOrDefault();

    private static bool TypeCoversColumn(TypeDeclarationSyntax type, int line, int column) =>
        IdentifierCoversColumn(type, line, column) ||
        SpanCoversColumn(type.GetLocation().GetLineSpan(), line, column);

    private static bool IdentifierCoversColumn(TypeDeclarationSyntax type, int line, int column) =>
        SpanCoversColumn(type.Identifier.GetLocation().GetLineSpan(), line, column);

    /// <summary>
    /// 1-based line/column coverage. <see cref="FileLinePositionSpan.EndLinePosition"/>
    /// is exclusive, so <paramref name="column"/> must be strictly before the
    /// exclusive end (reject <c>column &gt;= endCol</c>). Treating the end as
    /// inclusive would let the first character of an adjacent type also
    /// match the previous declaration. Same helper as
    /// <c>UseBaseTypeOperation.SpanCoversColumn</c> /
    /// <c>EncapsulateFieldOperation.SpanCoversColumn</c> /
    /// <c>PushMembersDownOperation.SpanCoversColumn</c>.
    /// </summary>
    internal static bool SpanCoversColumn(FileLinePositionSpan span, int line, int column)
    {
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        if (line < startLine || line > endLine)
            return false;
        if (line == startLine && column < startCol)
            return false;
        if (line == endLine && column >= endCol)
            return false;
        return true;
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
