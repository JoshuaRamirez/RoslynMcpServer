using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Resolution;

/// <summary>
/// Finds all references to a symbol across the workspace.
/// </summary>
public sealed class ReferenceTracker
{
    private readonly WorkspaceContext _context;

    /// <summary>
    /// Creates a new reference tracker.
    /// </summary>
    /// <param name="context">Workspace context to search.</param>
    public ReferenceTracker(WorkspaceContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Finds all references to a symbol across the solution.
    /// </summary>
    /// <param name="symbol">Symbol to find references for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reference information grouped by document.</returns>
    public async Task<ReferenceSearchResult> FindAllReferencesAsync(
        ISymbol symbol,
        CancellationToken cancellationToken = default)
    {
        var references = await SymbolFinder.FindReferencesAsync(
            symbol,
            _context.Solution,
            cancellationToken);

        var documentReferences = new Dictionary<DocumentId, List<ReferenceLocation>>();
        var totalCount = 0;

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                if (location.Document == null) continue;

                if (!documentReferences.TryGetValue(location.Document.Id, out var list))
                {
                    list = [];
                    documentReferences[location.Document.Id] = list;
                }

                list.Add(location);
                totalCount++;
            }
        }

        return new ReferenceSearchResult
        {
            Symbol = symbol,
            ReferencesByDocument = documentReferences,
            TotalReferenceCount = totalCount
        };
    }

    /// <summary>
    /// Gets all documents that reference the given symbol's namespace.
    /// Used to determine which files need using directive updates.
    /// </summary>
    /// <param name="symbol">Symbol whose namespace to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Documents that may need using directive updates.</returns>
    public async Task<IReadOnlyList<Document>> GetDocumentsReferencingNamespaceAsync(
        INamedTypeSymbol symbol,
        CancellationToken cancellationToken = default)
    {
        var references = await FindAllReferencesAsync(symbol, cancellationToken);
        var documents = new List<Document>();

        foreach (var docId in references.ReferencesByDocument.Keys)
        {
            var doc = _context.Solution.GetDocument(docId);
            if (doc != null)
            {
                documents.Add(doc);
            }
        }

        return documents;
    }
}

/// <summary>
/// Result of a reference search.
/// </summary>
public sealed class ReferenceSearchResult
{
    /// <summary>
    /// The symbol that was searched.
    /// </summary>
    public required ISymbol Symbol { get; init; }

    /// <summary>
    /// References grouped by document.
    /// </summary>
    public required IReadOnlyDictionary<DocumentId, List<ReferenceLocation>> ReferencesByDocument { get; init; }

    /// <summary>
    /// Total number of references found.
    /// </summary>
    public required int TotalReferenceCount { get; init; }
}
