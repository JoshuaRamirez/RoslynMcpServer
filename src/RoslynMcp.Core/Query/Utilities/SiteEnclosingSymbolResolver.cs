using Microsoft.CodeAnalysis;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Resolves the symbol enclosing a reference / call / diagnostic site (the member, type, or namespace
/// declaration enclosing a position, via <see cref="SemanticModel.GetEnclosingSymbol"/>). Shared by the
/// site filters of <c>find_references</c> / <c>find_callers</c> / <c>get_diagnostics</c>
/// (<see cref="NamespaceSiteScope"/>) and <c>find_references</c> / <c>get_diagnostics</c>
/// <c>accessibilityFilter</c> so every site filter uses the same enclosing-symbol notion. Semantic models are cached per syntax tree so
/// each file is bound at most once per resolver.
/// </summary>
internal sealed class SiteEnclosingSymbolResolver
{
    private readonly WorkspaceContext _context;
    private readonly Dictionary<SyntaxTree, SemanticModel?> _semanticModelCache = new();

    public SiteEnclosingSymbolResolver(WorkspaceContext context)
    {
        _context = context;
    }

    /// <summary>
    /// The symbol enclosing <paramref name="position"/> in <paramref name="tree"/> (whose document is
    /// <paramref name="document"/>, or <c>null</c> for a tree that is not a regular document). Null when
    /// there is no tree, no semantic model, or no enclosing symbol.
    /// </summary>
    public async Task<ISymbol?> GetEnclosingSymbolAsync(
        SyntaxTree? tree, Document? document, int position, CancellationToken cancellationToken)
    {
        var semanticModel = await GetCachedSemanticModelAsync(tree, document, cancellationToken);
        return semanticModel?.GetEnclosingSymbol(position, cancellationToken);
    }

    /// <summary>
    /// Symbol for a diagnostic site at <paramref name="position"/>: the nearest declared symbol
    /// whose syntax contains that position (so a declaration-attached diagnostic such as CS0169 on a
    /// field identifier resolves to that field rather than the enclosing type — a field without an
    /// initializer does not establish a member semantic scope for
    /// <see cref="SemanticModel.GetEnclosingSymbol"/>), falling back to
    /// <see cref="SemanticModel.GetEnclosingSymbol"/> when no declaration covers the position
    /// (expression / statement body sites). Null when there is no tree, no semantic model, or neither
    /// path yields a symbol.
    /// </summary>
    public async Task<ISymbol?> GetDeclaredOrEnclosingSymbolAsync(
        SyntaxTree? tree, Document? document, int position, CancellationToken cancellationToken)
    {
        var semanticModel = await GetCachedSemanticModelAsync(tree, document, cancellationToken);
        if (semanticModel == null || tree == null)
            return null;

        var root = await tree.GetRootAsync(cancellationToken);
        var token = root.FindToken(position);
        for (SyntaxNode? node = token.Parent; node != null; node = node.Parent)
        {
            var declared = semanticModel.GetDeclaredSymbol(node, cancellationToken);
            if (declared != null)
                return declared;
        }

        return semanticModel.GetEnclosingSymbol(position, cancellationToken);
    }

    private async Task<SemanticModel?> GetCachedSemanticModelAsync(
        SyntaxTree? tree, Document? document, CancellationToken cancellationToken)
    {
        if (tree == null) return null;

        if (!_semanticModelCache.TryGetValue(tree, out var semanticModel))
        {
            semanticModel = await GetSemanticModelAsync(tree, document, cancellationToken);
            _semanticModelCache[tree] = semanticModel;
        }

        return semanticModel;
    }

    /// <summary>
    /// Semantic model for a site tree: from its document when it has one, otherwise (e.g. a
    /// source-generated partial declaration, which is not a regular document) from the compilation of
    /// the project whose generator output contains that exact tree. Compilations are cached by the
    /// workspace, and the locations come from those same compilations.
    /// </summary>
    private async Task<SemanticModel?> GetSemanticModelAsync(
        SyntaxTree tree, Document? document, CancellationToken cancellationToken)
    {
        if (document != null)
            return await document.GetSemanticModelAsync(cancellationToken);

        foreach (var project in _context.Solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation != null && compilation.ContainsSyntaxTree(tree))
                return compilation.GetSemanticModel(tree);
        }

        return null;
    }
}
