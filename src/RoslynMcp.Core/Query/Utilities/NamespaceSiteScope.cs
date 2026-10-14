using Microsoft.CodeAnalysis;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Applies the optional <c>namespaceFilter</c> of <c>find_references</c>, <c>find_callers</c>, and
/// <c>get_diagnostics</c> to reference / call / diagnostic sites: a site matches when the symbol enclosing it (member, type, or namespace
/// declaration) lives in the filter namespace or one nested inside it (see
/// <see cref="NamespaceFilter.IsSiteInNamespace"/>). The target symbol's own namespace is irrelevant.
/// Semantic models are cached per syntax tree so each file is bound at most once per scope.
/// </summary>
internal sealed class NamespaceSiteScope
{
    private readonly WorkspaceContext _context;
    private readonly string _normalizedFilter;
    private readonly Dictionary<SyntaxTree, SemanticModel?> _semanticModelCache = new();

    private NamespaceSiteScope(WorkspaceContext context, string normalizedFilter)
    {
        _context = context;
        _normalizedFilter = normalizedFilter;
    }

    /// <summary>
    /// Returns <c>null</c> (no namespace filtering) when <paramref name="namespaceFilter"/> normalizes to
    /// nothing (omitted, blank, or a bare <c>global::</c>; see <see cref="NamespaceFilter.Normalize"/>);
    /// otherwise a scope for the normalized filter.
    /// </summary>
    public static NamespaceSiteScope? Create(WorkspaceContext context, string? namespaceFilter)
    {
        var normalizedFilter = NamespaceFilter.Normalize(namespaceFilter);
        return normalizedFilter == null ? null : new NamespaceSiteScope(context, normalizedFilter);
    }

    /// <summary>
    /// Whether the site at <paramref name="position"/> in <paramref name="tree"/> (whose document is
    /// <paramref name="document"/>, or <c>null</c> for a tree that is not a regular document) lives in
    /// the filter namespace or one nested inside it. False when there is no tree, no semantic model,
    /// or no enclosing symbol.
    /// </summary>
    public async Task<bool> ContainsAsync(
        SyntaxTree? tree, Document? document, int position, CancellationToken cancellationToken)
    {
        if (tree == null) return false;

        if (!_semanticModelCache.TryGetValue(tree, out var semanticModel))
        {
            semanticModel = await GetSemanticModelAsync(tree, document, cancellationToken);
            _semanticModelCache[tree] = semanticModel;
        }

        var enclosingSymbol = semanticModel?.GetEnclosingSymbol(position, cancellationToken);
        return enclosingSymbol != null && NamespaceFilter.IsSiteInNamespace(enclosingSymbol, _normalizedFilter);
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
