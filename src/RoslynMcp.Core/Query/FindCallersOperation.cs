using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Finds all callers of a symbol using Roslyn's SymbolFinder.FindCallersAsync(), across the solution
/// (or within one project when <see cref="FindCallersParams.ProjectPath"/> is set), optionally
/// narrowed to call sites in one namespace (<see cref="FindCallersParams.NamespaceFilter"/>).
/// </summary>
public sealed class FindCallersOperation : QueryOperationBase<FindCallersParams, FindCallersResult>
{
    /// <inheritdoc />
    public FindCallersOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(FindCallersParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        SourceFilePathHelpers.ValidateSourceFilePath(@params.SourceFile);

        if (!@params.Line.HasValue && string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either line/column or symbolName must be provided.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");

        if (@params.MaxResults.HasValue && @params.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!string.IsNullOrWhiteSpace(@params.CallerFile))
        {
            if (!PathResolver.IsAbsolutePath(@params.CallerFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "callerFile must be an absolute path.");

            if (!PathResolver.IsValidCSharpFilePath(@params.CallerFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "callerFile must be a .cs file.");

            if (!File.Exists(@params.CallerFile))
                throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.CallerFile}");
        }

        ProjectPathFilter.Validate(@params.ProjectPath);
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<FindCallersResult>> ExecuteCoreAsync(
        Guid operationId,
        FindCallersParams @params,
        CancellationToken cancellationToken)
    {
        // Optional projectPath scope (same validation / matching as get_diagnostics, search_symbols and
        // find_references): resolved up front so a project that is not in the workspace fails with
        // SourceNotInWorkspace instead of an empty result. Every target-framework variant of a
        // multi-targeted project shares that path, so all of their ProjectIds are in scope.
        var projectScope = string.IsNullOrWhiteSpace(@params.ProjectPath)
            ? null
            : ProjectPathFilter.SelectProjects(Context.Solution, @params.ProjectPath)
                .Select(project => project.Id)
                .ToHashSet();

        var resolved = await SymbolResolver.ResolveSymbolAsync(
            @params.SourceFile, @params.SymbolName, @params.Line, @params.Column, cancellationToken);

        var symbol = resolved.Symbol;
        var callerResults = await SymbolFinder.FindCallersAsync(symbol, Context.Solution, cancellationToken);

        var callers = new List<CallerInfo>();
        var totalCount = 0;
        var maxResults = @params.MaxResults ?? int.MaxValue;
        // Compare canonical path keys so aliases such as "src/../Caller.cs" still match Roslyn's
        // canonical location paths, and compare those keys with OrdinalIgnoreCase (the documented
        // contract, same as search_symbols / get_diagnostics sourceFile) so paths that differ only
        // by letter case also match, even on case-sensitive volumes.
        var callerFileKey = string.IsNullOrWhiteSpace(@params.CallerFile)
            ? null
            : PathResolver.GetPathComparisonKey(@params.CallerFile);
        var locationKeyCache = callerFileKey == null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal);

        // Optional namespaceFilter (same normalization / matching as search_symbols /
        // find_implementations / find_references): keep only call sites whose enclosing symbol
        // (member, type, or namespace declaration) lives in the filter namespace or one nested
        // inside it. The target symbol's own namespace is irrelevant. Semantic models are cached
        // per syntax tree so each file is bound at most once.
        var namespaceFilter = NamespaceFilter.Normalize(@params.NamespaceFilter);
        var semanticModelCache = namespaceFilter == null
            ? null
            : new Dictionary<SyntaxTree, SemanticModel?>();

        async Task<bool> IsInNamespaceScopeAsync(SyntaxTree? tree, Document? document, int position)
        {
            if (namespaceFilter == null) return true;
            if (tree == null) return false;

            if (!semanticModelCache!.TryGetValue(tree, out var semanticModel))
            {
                semanticModel = await GetSemanticModelAsync(tree, document, cancellationToken);
                semanticModelCache[tree] = semanticModel;
            }

            var enclosingSymbol = semanticModel?.GetEnclosingSymbol(position, cancellationToken);
            return enclosingSymbol != null && NamespaceFilter.IsSiteInNamespace(enclosingSymbol, namespaceFilter);
        }

        foreach (var caller in callerResults)
        {
            foreach (var location in caller.Locations)
            {
                if (!location.IsInSource) continue;

                var lineSpan = location.GetLineSpan();

                // Optional callerFile / projectPath / namespaceFilter filters run before maxResults
                // so TotalCount / Truncated reflect the filtered set.
                if (callerFileKey != null)
                {
                    var locationPath = lineSpan.Path;
                    if (!locationKeyCache!.TryGetValue(locationPath, out var locationKey))
                    {
                        locationKey = string.IsNullOrWhiteSpace(locationPath)
                            ? locationPath
                            : PathResolver.GetPathComparisonKey(locationPath);
                        locationKeyCache[locationPath] = locationKey;
                    }

                    if (!string.Equals(locationKey, callerFileKey, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                // Optional projectPath filter: keep only call sites whose document belongs to a
                // matched project, so a file linked into several projects is reported only for the
                // scoped project's document.
                if (projectScope != null)
                {
                    var documentId = Context.Solution.GetDocumentId(location.SourceTree);
                    if (documentId == null || !projectScope.Contains(documentId.ProjectId))
                        continue;
                }

                if (namespaceFilter != null
                    && !await IsInNamespaceScopeAsync(
                        location.SourceTree,
                        Context.Solution.GetDocument(location.SourceTree),
                        location.SourceSpan.Start))
                    continue;

                totalCount++;

                if (callers.Count < maxResults)
                {
                    var snippet = await GetSnippetAsync(location, cancellationToken);

                    callers.Add(new CallerInfo
                    {
                        CallerName = caller.CallingSymbol.Name,
                        CallerFullyQualifiedName = caller.CallingSymbol.ToDisplayString(),
                        File = lineSpan.Path,
                        Line = lineSpan.StartLinePosition.Line + 1,
                        Column = lineSpan.StartLinePosition.Character + 1,
                        Snippet = snippet
                    });
                }
            }
        }

        var result = new FindCallersResult
        {
            SymbolName = symbol.Name,
            FullyQualifiedName = symbol.ToDisplayString(),
            Callers = callers,
            TotalCount = totalCount,
            Truncated = totalCount > callers.Count
        };

        return QueryResult<FindCallersResult>.Succeeded(operationId, result);
    }


    /// <summary>
    /// Semantic model for a call-site tree: from its document when it has one, otherwise from the
    /// compilation of the project whose generator output contains that exact tree (same fallback as
    /// <c>find_references</c>). Compilations are cached by the workspace.
    /// </summary>
    private async Task<SemanticModel?> GetSemanticModelAsync(
        SyntaxTree tree, Document? document, CancellationToken cancellationToken)
    {
        if (document != null)
            return await document.GetSemanticModelAsync(cancellationToken);

        foreach (var project in Context.Solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation != null && compilation.ContainsSyntaxTree(tree))
                return compilation.GetSemanticModel(tree);
        }

        return null;
    }

    private static async Task<string?> GetSnippetAsync(Location location, CancellationToken cancellationToken)
    {
        if (!location.IsInSource || location.SourceTree == null) return null;

        var text = await location.SourceTree.GetTextAsync(cancellationToken);
        var lineSpan = location.GetLineSpan();
        var lineIndex = lineSpan.StartLinePosition.Line;

        if (lineIndex < 0 || lineIndex >= text.Lines.Count) return null;
        return text.Lines[lineIndex].ToString().Trim();
    }
}
