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
        if (tree == null) return null;

        if (!_semanticModelCache.TryGetValue(tree, out var semanticModel))
        {
            semanticModel = await GetSemanticModelAsync(tree, document, cancellationToken);
            _semanticModelCache[tree] = semanticModel;
        }

        return semanticModel?.GetEnclosingSymbol(position, cancellationToken);
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
