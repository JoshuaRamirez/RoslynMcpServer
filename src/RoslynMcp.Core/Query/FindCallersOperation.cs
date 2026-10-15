using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Finds all callers of a symbol using Roslyn's SymbolFinder.FindCallersAsync(), across the solution
/// (or within one project when <see cref="FindCallersParams.ProjectPath"/> is set), optionally
/// narrowed to call sites in one namespace (<see cref="FindCallersParams.NamespaceFilter"/>),
/// optionally to calling symbols with a given declared accessibility
/// (<see cref="FindCallersParams.AccessibilityFilter"/>), optionally to calling symbols whose
/// simple name contains a substring (<see cref="FindCallersParams.NameFilter"/>), and optionally to
/// calling symbols of one symbol kind (<see cref="FindCallersParams.KindFilter"/>).
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

        AccessibilityFilterParser.Parse(@params.AccessibilityFilter);

        // Same parsing and InvalidSymbolKind error as search_symbols / get_document_outline /
        // get_symbol_info / find_implementations / get_type_hierarchy kindFilter.
        SymbolKindFilterParser.Parse(@params.KindFilter);
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
        // inside it. The target symbol's own namespace is irrelevant. Null when no filter applies.
        var namespaceScope = NamespaceSiteScope.Create(Context, @params.NamespaceFilter);

        // Optional accessibilityFilter (same values / parsing as search_symbols /
        // find_implementations / get_type_hierarchy accessibilityFilter): keep only call sites
        // whose calling symbol's DeclaredAccessibility equals the filter exactly. Null when
        // omitted / blank (no accessibility filtering). Invalid values already rejected in
        // ValidateParams.
        var accessibilityFilter = AccessibilityFilterParser.Parse(@params.AccessibilityFilter);

        // Optional nameFilter (same matching as get_document_outline / get_symbol_info /
        // find_implementations / get_type_hierarchy / find_references nameFilter): keep only call
        // sites whose calling symbol (the same CallingSymbol accessibilityFilter judges) has a
        // simple Name containing the trimmed value, ordinal case-insensitive. Null when omitted /
        // blank (no name filtering). Free text — no validation error.
        var nameFilter = string.IsNullOrWhiteSpace(@params.NameFilter) ? null : @params.NameFilter.Trim();

        // Optional kindFilter (same values / parsing as search_symbols / get_document_outline /
        // get_symbol_info / find_implementations / get_type_hierarchy kindFilter): keep only call
        // sites whose calling symbol (the same CallingSymbol accessibilityFilter / nameFilter judge)
        // maps to that kind via SymbolKindMapper (methods / constructors / lambdas → Method, property
        // bodies → Property, field initializers → Field or Constant). Null when omitted / blank.
        // Invalid values already rejected in ValidateParams.
        var kindFilter = SymbolKindFilterParser.Parse(@params.KindFilter);

        foreach (var caller in callerResults)
        {
            // Accessibility / name / kind filters run on the calling symbol before location filters /
            // maxResults so TotalCount / Truncated reflect the filtered set. Exact
            // DeclaredAccessibility match (implicit defaults count; protected internal ≠
            // protected / internal). Name is ISymbol.Name substring (OrdinalIgnoreCase). Kind is
            // SymbolKindMapper.Map equality.
            if (accessibilityFilter.HasValue
                && caller.CallingSymbol.DeclaredAccessibility != accessibilityFilter.Value)
                continue;

            if (nameFilter != null
                && !caller.CallingSymbol.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (kindFilter.HasValue && SymbolKindMapper.Map(caller.CallingSymbol) != kindFilter.Value)
                continue;

            foreach (var location in caller.Locations)
            {
                if (!location.IsInSource) continue;

                var lineSpan = location.GetLineSpan();

                // Optional callerFile / projectPath / namespaceFilter filters run before maxResults
                // (accessibilityFilter / nameFilter / kindFilter already applied on the calling symbol above) so
                // TotalCount / Truncated reflect the filtered set.
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

                if (namespaceScope != null
                    && !await namespaceScope.ContainsAsync(
                        location.SourceTree,
                        Context.Solution.GetDocument(location.SourceTree),
                        location.SourceSpan.Start,
                        cancellationToken))
                    continue;

                totalCount++;

                if (callers.Count < maxResults)
                {
                    var snippet = await LocationSnippetHelpers.GetLineSnippetAsync(location, cancellationToken);

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
}
