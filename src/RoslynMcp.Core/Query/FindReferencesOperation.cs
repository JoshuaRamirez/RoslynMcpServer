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
/// Finds all references to a symbol across the solution (or within one project when
/// <see cref="FindReferencesParams.ProjectPath"/> is set), optionally narrowed to reference sites in one
/// namespace (<see cref="FindReferencesParams.NamespaceFilter"/>), optionally to reference sites
/// whose enclosing symbol has a given declared accessibility
/// (<see cref="FindReferencesParams.AccessibilityFilter"/>), and optionally to reference sites whose
/// enclosing symbol's simple name contains a substring (<see cref="FindReferencesParams.NameFilter"/>).
/// Delegates to Roslyn's SymbolFinder.FindReferencesAsync.
/// </summary>
public sealed class FindReferencesOperation : QueryOperationBase<FindReferencesParams, FindReferencesResult>
{
    /// <inheritdoc />
    public FindReferencesOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(FindReferencesParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        SourceFilePathHelpers.ValidateSourceFilePath(@params.SourceFile);

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!@params.Line.HasValue && string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either line/column or symbolName must be provided.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");

        if (@params.MaxResults.HasValue && @params.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");

        if (!string.IsNullOrWhiteSpace(@params.ReferenceFile))
        {
            if (!PathResolver.IsAbsolutePath(@params.ReferenceFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "referenceFile must be an absolute path.");

            if (!PathResolver.IsValidCSharpFilePath(@params.ReferenceFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "referenceFile must be a .cs file.");

            if (!File.Exists(@params.ReferenceFile))
                throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.ReferenceFile}");
        }

        ProjectPathFilter.Validate(@params.ProjectPath);

        AccessibilityFilterParser.Parse(@params.AccessibilityFilter);
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<FindReferencesResult>> ExecuteCoreAsync(
        Guid operationId,
        FindReferencesParams @params,
        CancellationToken cancellationToken)
    {
        // Optional projectPath scope (same validation / matching as get_diagnostics and search_symbols):
        // resolved up front so a project that is not in the workspace fails with SourceNotInWorkspace
        // instead of an empty result. Every target-framework variant of a multi-targeted project
        // shares that path, so all of their ProjectIds are in scope.
        var projectScope = string.IsNullOrWhiteSpace(@params.ProjectPath)
            ? null
            : ProjectPathFilter.SelectProjects(Context.Solution, @params.ProjectPath)
                .Select(project => project.Id)
                .ToHashSet();

        // Resolve the symbol
        var resolved = await SymbolResolver.ResolveSymbolAsync(
            @params.SourceFile, @params.SymbolName, @params.Line, @params.Column, cancellationToken);

        var symbol = resolved.Symbol;

        // Find all references via Roslyn
        var referencedSymbols = await SymbolFinder.FindReferencesAsync(
            symbol, Context.Solution, cancellationToken);

        var locations = new List<ReferenceLocationInfo>();
        var totalCount = 0;
        var maxResults = @params.MaxResults ?? int.MaxValue;
        var includeDeclaration = @params.IncludeDeclaration ?? true;
        // Optional referenceFile filter: compare canonical path keys so aliases such as
        // "src/../Consumer.cs" still match Roslyn's canonical location paths, and compare those
        // keys with OrdinalIgnoreCase (the documented contract, same as find_callers callerFile /
        // search_symbols sourceFile) so paths that differ only by letter case also match, even on
        // case-sensitive volumes. Definition locations (includeDeclaration) are filtered the same
        // way: the declaration is only reported when it is located in referenceFile.
        var referenceFileKey = string.IsNullOrWhiteSpace(@params.ReferenceFile)
            ? null
            : PathResolver.GetPathComparisonKey(@params.ReferenceFile);
        var locationKeyCache = referenceFileKey == null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal);

        bool IsInReferenceFile(string? locationPath)
        {
            if (referenceFileKey == null) return true;
            if (string.IsNullOrWhiteSpace(locationPath)) return false;

            if (!locationKeyCache!.TryGetValue(locationPath, out var locationKey))
            {
                locationKey = PathResolver.GetPathComparisonKey(locationPath);
                locationKeyCache[locationPath] = locationKey;
            }

            return string.Equals(locationKey, referenceFileKey, StringComparison.OrdinalIgnoreCase);
        }

        // Optional projectPath filter: keep only locations whose document belongs to a matched project.
        bool IsInProjectScope(DocumentId? documentId) =>
            projectScope == null || (documentId != null && projectScope.Contains(documentId.ProjectId));

        // Optional accessibilityFilter (same values / parsing as search_symbols / find_callers
        // accessibilityFilter): keep only locations whose site symbol has a DeclaredAccessibility equal
        // to the filter exactly. For a usage that is the member / type / namespace enclosing it (the
        // same enclosing-symbol notion namespaceFilter uses, not the target symbol); for a definition
        // location it is the declared symbol itself (see <see cref="DeclaringSiteSymbol"/>). Null when omitted /
        // blank (no accessibility filtering). Invalid values already rejected in ValidateParams.
        var accessibilityFilter = AccessibilityFilterParser.Parse(@params.AccessibilityFilter);

        // All site filters share one resolver so each file is bound at most once.
        var siteResolver = new SiteEnclosingSymbolResolver(Context);

        // Optional namespaceFilter (same normalization / matching as search_symbols and
        // find_implementations): keep only locations whose reference site, i.e. the symbol enclosing
        // the location (member, type, or namespace declaration), lives in the filter namespace or one
        // nested inside it. The target symbol's own namespace is irrelevant (it is the same for every
        // reference). Null when no filter applies.
        var namespaceScope = NamespaceSiteScope.Create(Context, @params.NamespaceFilter, siteResolver);

        // Namespaces have no accessibility modifier but Roslyn reports INamespaceSymbol as Public (same
        // guard as search_symbols), so a namespace-level site (e.g. a using alias, or a namespace's own
        // declaration) never matches a named filter.
        bool MatchesAccessibility(ISymbol? siteSymbol) =>
            !accessibilityFilter.HasValue
            || (siteSymbol is not null and not INamespaceSymbol
                && siteSymbol.DeclaredAccessibility == accessibilityFilter.Value);

        // Optional nameFilter (same matching as get_document_outline / get_symbol_info /
        // find_implementations / get_type_hierarchy nameFilter): keep only locations whose site symbol
        // (the same DeclaringSiteSymbol accessibilityFilter judges) has a simple Name containing the
        // trimmed value, ordinal case-insensitive. Namespace-level sites (using aliases, a top-level
        // type's base list, top-level statements, a namespace's own declaration) never match, same as
        // accessibilityFilter. Null when omitted / blank (no name filtering).
        var nameFilter = string.IsNullOrWhiteSpace(@params.NameFilter) ? null : @params.NameFilter.Trim();

        bool MatchesName(ISymbol? siteSymbol) =>
            nameFilter == null
            || (siteSymbol is not null and not INamespaceSymbol
                && siteSymbol.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));

        // Both enclosing-symbol filters judge the same site symbol.
        bool MatchesSiteSymbol(ISymbol? siteSymbol) => MatchesAccessibility(siteSymbol) && MatchesName(siteSymbol);
        var filtersBySiteSymbol = accessibilityFilter.HasValue || nameFilter != null;

        foreach (var referencedSymbol in referencedSymbols)
        {
            // Every definition location of this entry declares the same symbol, so its accessibility /
            // name site is computed once.
            var definitionSiteSymbol = DeclaringSiteSymbol.Resolve(referencedSymbol.Definition);

            // Add the definition itself (skipped entirely when includeDeclaration is false)
            var definitionLocations = includeDeclaration
                ? referencedSymbol.Definition.Locations.Where(l => l.IsInSource)
                : Enumerable.Empty<Location>();
            foreach (var defLocation in definitionLocations)
            {
                var lineSpan = defLocation.GetLineSpan();

                // Optional referenceFile / projectPath / namespaceFilter / accessibilityFilter /
                // nameFilter filters run before maxResults so TotalCount / Truncated reflect the
                // filtered set.
                if (!IsInReferenceFile(lineSpan.Path)) continue;
                if (projectScope != null && !IsInProjectScope(Context.Solution.GetDocumentId(defLocation.SourceTree))) continue;
                if (namespaceScope != null
                    && !await namespaceScope.ContainsAsync(
                        defLocation.SourceTree, Context.Solution.GetDocument(defLocation.SourceTree), defLocation.SourceSpan.Start,
                        cancellationToken))
                    continue;
                if (!MatchesSiteSymbol(definitionSiteSymbol)) continue;

                totalCount++;
                if (locations.Count < maxResults)
                {
                    var snippet = await LocationSnippetHelpers.GetLineSnippetAsync(defLocation, cancellationToken);

                    locations.Add(new ReferenceLocationInfo
                    {
                        File = lineSpan.Path,
                        Line = lineSpan.StartLinePosition.Line + 1,
                        Column = lineSpan.StartLinePosition.Character + 1,
                        ContextSnippet = snippet,
                        IsWriteAccess = false,
                        IsDefinition = true
                    });
                }
            }

            // Add each reference
            foreach (var refLocation in referencedSymbol.Locations)
            {
                if (refLocation.Document == null) continue;

                var span = refLocation.Location.GetLineSpan();
                var file = refLocation.Document.FilePath ?? span.Path;

                // Same filters as definition locations, applied to the reported File path and the
                // reference's own document.
                if (!IsInReferenceFile(file)) continue;
                if (!IsInProjectScope(refLocation.Document.Id)) continue;
                if (namespaceScope != null
                    && !await namespaceScope.ContainsAsync(
                        refLocation.Location.SourceTree, refLocation.Document, refLocation.Location.SourceSpan.Start,
                        cancellationToken))
                    continue;
                if (filtersBySiteSymbol
                    && !MatchesSiteSymbol(DeclaringSiteSymbol.Resolve(await siteResolver.GetEnclosingSymbolAsync(
                        refLocation.Location.SourceTree, refLocation.Document, refLocation.Location.SourceSpan.Start,
                        cancellationToken))))
                    continue;

                totalCount++;

                if (locations.Count < maxResults)
                {
                    var snippet = await LocationSnippetHelpers.GetLineSnippetAsync(refLocation.Location, cancellationToken);

                    locations.Add(new ReferenceLocationInfo
                    {
                        File = file,
                        Line = span.StartLinePosition.Line + 1,
                        Column = span.StartLinePosition.Character + 1,
                        ContextSnippet = snippet,
                        IsWriteAccess = false,
                        IsDefinition = false
                    });
                }
            }
        }

        var result = new FindReferencesResult
        {
            SymbolName = symbol.Name,
            FullyQualifiedName = symbol.ToDisplayString(),
            References = locations,
            TotalCount = totalCount,
            Truncated = totalCount > locations.Count
        };

        return QueryResult<FindReferencesResult>.Succeeded(operationId, result);
    }
}
