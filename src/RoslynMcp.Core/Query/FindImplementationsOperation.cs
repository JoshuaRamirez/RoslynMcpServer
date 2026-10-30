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
/// Finds all implementations of an interface, abstract class, or virtual/abstract member across the
/// solution (or within one project when <see cref="FindImplementationsParams.ProjectPath"/> is set;
/// only direct implementers of an interface when <see cref="FindImplementationsParams.Transitive"/>
/// is false; only implementations in one namespace subtree when
/// <see cref="FindImplementationsParams.NamespaceFilter"/> is set; only implementations with a given
/// declared accessibility when <see cref="FindImplementationsParams.AccessibilityFilter"/> is set;
/// only implementations of a given kind when <see cref="FindImplementationsParams.KindFilter"/> is set;
/// only implementations whose name contains a substring when
/// <see cref="FindImplementationsParams.NameFilter"/> is set; only static or only instance
/// implementations when <see cref="FindImplementationsParams.StaticFilter"/> is set; only abstract or
/// only non-abstract implementations when <see cref="FindImplementationsParams.AbstractFilter"/> is set).
/// Delegates to Roslyn's SymbolFinder.FindImplementationsAsync.
/// </summary>
public sealed class FindImplementationsOperation : QueryOperationBase<FindImplementationsParams, FindImplementationsResult>
{
    /// <inheritdoc />
    public FindImplementationsOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(FindImplementationsParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        SourceFilePathHelpers.ValidateSourceFilePath(@params.SourceFile);

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        SymbolTargetValidator.Validate(@params.Line, @params.Column, @params.SymbolName);

        if (@params.MaxResults.HasValue && @params.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");

        ResultFileFilter.Validate(@params.ImplementationFile, "implementationFile");

        ProjectPathFilter.Validate(@params.ProjectPath);

        AccessibilityFilterParser.Parse(@params.AccessibilityFilter);

        // Same parsing and InvalidSymbolKind error as search_symbols / get_document_outline /
        // get_symbol_info kindFilter.
        SymbolKindFilterParser.Parse(@params.KindFilter);
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<FindImplementationsResult>> ExecuteCoreAsync(
        Guid operationId,
        FindImplementationsParams @params,
        CancellationToken cancellationToken)
    {
        // Optional projectPath scope (same validation / matching as get_diagnostics, search_symbols,
        // find_references and find_callers): resolved up front so a project that is not in the
        // workspace fails with SourceNotInWorkspace instead of an empty result. Every target-framework
        // variant of a multi-targeted project shares that path, so all of their ProjectIds are in scope.
        var projectScope = string.IsNullOrWhiteSpace(@params.ProjectPath)
            ? null
            : ProjectPathFilter.SelectProjects(Context.Solution, @params.ProjectPath)
                .Select(project => project.Id)
                .ToHashSet();

        var resolved = await SymbolResolver.ResolveSymbolAsync(
            @params.SourceFile, @params.SymbolName, @params.Line, @params.Column, cancellationToken);

        var symbol = resolved.Symbol;
        var implementations = new List<ImplementationInfo>();
        var maxResults = @params.MaxResults ?? int.MaxValue;

        // Find implementations based on symbol kind. Optional transitive (default true = every
        // implementing type, today's behavior); false on an interface type asks Roslyn for the direct
        // implementers only (types listing the interface in their own base list), dropping those that
        // inherit the implementation from a base class or implement it through a derived interface.
        // Members and non-interface types keep the ISymbol overload, where transitive has no meaning.
        IEnumerable<ISymbol> implSymbols = @params.Transitive == false
            && symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface } interfaceType
            ? await SymbolFinder.FindImplementationsAsync(
                interfaceType, Context.Solution, transitive: false, cancellationToken: cancellationToken)
            : await SymbolFinder.FindImplementationsAsync(
                symbol, Context.Solution, cancellationToken: cancellationToken);

        var totalCount = 0;
        // Optional implementationFile filter: compare canonical path keys so aliases such as
        // "src/../Impl.cs" still match Roslyn's canonical location paths, and compare those keys
        // with OrdinalIgnoreCase (the documented contract, same as find_references referenceFile /
        // find_callers callerFile) so paths that differ only by letter case also match, even on
        // case-sensitive volumes.
        var implementationFileKey = string.IsNullOrWhiteSpace(@params.ImplementationFile)
            ? null
            : PathResolver.GetPathComparisonKey(@params.ImplementationFile);
        var locationKeyCache = implementationFileKey == null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal);

        bool IsInScopedProject(Location location)
        {
            var documentId = Context.Solution.GetDocumentId(location.SourceTree);
            return documentId != null && projectScope!.Contains(documentId.ProjectId);
        }

        bool IsInImplementationFile(Location location)
        {
            var locationPath = location.GetLineSpan().Path;
            if (string.IsNullOrWhiteSpace(locationPath)) return false;

            if (!locationKeyCache!.TryGetValue(locationPath, out var locationKey))
            {
                locationKey = PathResolver.GetPathComparisonKey(locationPath);
                locationKeyCache[locationPath] = locationKey;
            }

            return string.Equals(locationKey, implementationFileKey, StringComparison.OrdinalIgnoreCase);
        }

        var filtered = implementationFileKey != null || projectScope != null;

        // Optional namespaceFilter / accessibilityFilter / kindFilter / nameFilter / staticFilter /
        // abstractFilter (same values / parsing as search_symbols / get_type_hierarchy), judged on each
        // returned implementation itself: containing namespace (for member targets, the namespace of the
        // member's containing type) equal to or nested in the filter, exact DeclaredAccessibility, reported
        // Kind (SymbolKindMapper.Map, as in ImplementationInfo), reported Name (ordinal case-insensitive
        // contains), ISymbol.IsStatic (implementing types are never static) and ISymbol.IsAbstract (true
        // keeps abstract implementing types / members, including an abstract re-abstraction in a derived
        // interface). Omitted / blank values impose no filter; invalid values already rejected in
        // ValidateParams.
        var symbolFilter = ResultSymbolFilter.Create(
            @params.NamespaceFilter,
            @params.AccessibilityFilter,
            @params.KindFilter,
            @params.NameFilter,
            @params.StaticFilter,
            @params.AbstractFilter);

        foreach (var impl in implSymbols)
        {
            // Symbol filters run before maxResults (and before the location filters below) so
            // TotalCount / Truncated reflect the filtered set.
            if (!symbolFilter.Matches(impl))
                continue;

            Location? location;
            if (!filtered)
            {
                totalCount++;
                if (implementations.Count >= maxResults) continue;

                location = impl.Locations.FirstOrDefault(l => l.IsInSource);
                if (location == null) continue;
            }
            else
            {
                // Optional implementationFile / projectPath filters run before maxResults so
                // TotalCount / Truncated reflect the filtered set. An implementation declared across
                // several files (partial type) matches when any of its in-source locations satisfies
                // both filters (in implementationFile and in a document of the scoped project), and is
                // reported at that location.
                location = impl.Locations.FirstOrDefault(l =>
                    l.IsInSource &&
                    (implementationFileKey == null || IsInImplementationFile(l)) &&
                    (projectScope == null || IsInScopedProject(l)));
                if (location == null) continue;

                totalCount++;
                if (implementations.Count >= maxResults) continue;
            }

            var lineSpan = location.GetLineSpan();
            implementations.Add(new ImplementationInfo
            {
                Name = impl.Name,
                FullyQualifiedName = impl.ToDisplayString(),
                Kind = SymbolKindMapper.Map(impl),
                File = lineSpan.Path,
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1
            });
        }

        var result = new FindImplementationsResult
        {
            SymbolName = symbol.Name,
            FullyQualifiedName = symbol.ToDisplayString(),
            Implementations = implementations,
            TotalCount = totalCount,
            Truncated = totalCount > implementations.Count
        };

        return QueryResult<FindImplementationsResult>.Succeeded(operationId, result);
    }
}
