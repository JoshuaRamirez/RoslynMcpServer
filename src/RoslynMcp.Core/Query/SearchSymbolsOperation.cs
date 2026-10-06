using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Searches for symbols by name pattern across all projects in the solution.
/// Uses Roslyn's Compilation.GetSymbolsWithName for efficient symbol lookup.
/// </summary>
public sealed class SearchSymbolsOperation : QueryOperationBase<SearchSymbolsParams, SearchSymbolsResult>
{
    /// <inheritdoc />
    public SearchSymbolsOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(SearchSymbolsParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.Query))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "query is required.");

        if (@params.MaxResults.HasValue && @params.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");

        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
        {
            SourceFilePathHelpers.ValidateSourceFilePath(@params.SourceFile);

            if (!File.Exists(@params.SourceFile))
                throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
        }
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<SearchSymbolsResult>> ExecuteCoreAsync(
        Guid operationId,
        SearchSymbolsParams @params,
        CancellationToken cancellationToken)
    {
        var query = @params.Query;
        var maxResults = @params.MaxResults ?? 50;
        var kindFilter = ParseKindFilter(@params.KindFilter);
        var nameComparison = @params.CaseSensitive == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var exactMatch = @params.ExactMatch == true;
        var namespaceFilter = NormalizeNamespaceFilter(@params.NamespaceFilter);

        var entries = new List<SymbolSearchEntry>();
        var totalCount = 0;

        // Determine the SymbolFilter based on kindFilter
        var symbolFilter = kindFilter.HasValue
            ? GetSymbolFilter(kindFilter.Value)
            : SymbolFilter.All;

        foreach (var project in Context.Solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            // Use GetSymbolsWithName with a substring predicate, or whole-name equality when
            // exactMatch is set (case-sensitive when requested). The predicate is the only name
            // filter, so TotalCount / Truncated below reflect the comparison-specific match set.
            var symbols = compilation.GetSymbolsWithName(
                name => exactMatch
                    ? string.Equals(name, query, nameComparison)
                    : name.Contains(query, nameComparison),
                symbolFilter,
                cancellationToken);

            foreach (var symbol in symbols)
            {
                // Skip compiler-generated symbols
                if (symbol.IsImplicitlyDeclared) continue;
                if (!symbol.CanBeReferencedByName) continue;

                // Apply kind filter
                if (kindFilter.HasValue && SymbolKindMapper.Map(symbol) != kindFilter.Value)
                    continue;

                // Apply namespace filter (before maxResults so TotalCount / Truncated reflect it)
                if (namespaceFilter != null && !IsInNamespace(symbol, namespaceFilter))
                    continue;

                // Skip duplicates (same symbol can appear in multiple compilations)
                var fqn = symbol.ToDisplayString();
                if (entries.Any(e => e.FullyQualifiedName == fqn)) continue;

                Location? location;
                if (!string.IsNullOrWhiteSpace(@params.SourceFile))
                {
                    // Match any in-source declaration (partial types/methods may declare
                    // across files). FirstOrDefault on Locations alone would miss a later part.
                    location = symbol.Locations.FirstOrDefault(l =>
                        l.IsInSource &&
                        string.Equals(
                            l.GetLineSpan().Path,
                            @params.SourceFile,
                            StringComparison.OrdinalIgnoreCase));
                    if (location == null) continue;
                }
                else
                {
                    location = null;
                }

                totalCount++;

                if (entries.Count < maxResults)
                {
                    // Prefer the sourceFile-matching location when filtering; otherwise first in-source.
                    location ??= symbol.Locations.FirstOrDefault(l => l.IsInSource);
                    if (location == null) continue;

                    var lineSpan = location.GetLineSpan();
                    entries.Add(new SymbolSearchEntry
                    {
                        Name = symbol.Name,
                        FullyQualifiedName = fqn,
                        Kind = SymbolKindMapper.Map(symbol),
                        File = lineSpan.Path,
                        Line = lineSpan.StartLinePosition.Line + 1,
                        Column = lineSpan.StartLinePosition.Character + 1,
                        ContainerName = symbol.ContainingType?.Name ?? symbol.ContainingNamespace?.ToDisplayString()
                    });
                }
            }
        }

        var result = new SearchSymbolsResult
        {
            Query = query,
            Symbols = entries,
            TotalCount = totalCount,
            Truncated = totalCount > entries.Count
        };

        return QueryResult<SearchSymbolsResult>.Succeeded(operationId, result);
    }

    private const string GlobalPrefix = "global::";

    /// <summary>
    /// Trims the namespace filter and strips an optional leading <c>global::</c>.
    /// Returns <c>null</c> (no namespace filtering) when the filter is omitted, blank, or a bare
    /// <c>global::</c>, since every namespace is nested in the root namespace.
    /// </summary>
    private static string? NormalizeNamespaceFilter(string? namespaceFilter)
    {
        if (string.IsNullOrWhiteSpace(namespaceFilter)) return null;

        var trimmed = namespaceFilter.Trim();
        if (trimmed.StartsWith(GlobalPrefix, StringComparison.Ordinal))
            trimmed = trimmed[GlobalPrefix.Length..].Trim();

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Whether the symbol's containing namespace equals <paramref name="namespaceFilter"/> or is
    /// nested inside it (ordinal). Symbols in the global namespace never match.
    /// </summary>
    private static bool IsInNamespace(ISymbol symbol, string namespaceFilter)
    {
        var containingNamespace = symbol.ContainingNamespace;
        if (containingNamespace == null || containingNamespace.IsGlobalNamespace) return false;

        var name = containingNamespace.ToDisplayString();
        return name.Length == namespaceFilter.Length
            ? string.Equals(name, namespaceFilter, StringComparison.Ordinal)
            : name.Length > namespaceFilter.Length
              && name[namespaceFilter.Length] == '.'
              && name.StartsWith(namespaceFilter, StringComparison.Ordinal);
    }

    private static Contracts.Enums.SymbolKind? ParseKindFilter(string? kindFilter)
    {
        if (string.IsNullOrWhiteSpace(kindFilter)) return null;

        if (!System.Enum.TryParse<Contracts.Enums.SymbolKind>(kindFilter, ignoreCase: true, out var kind))
        {
            var validKinds = string.Join(", ", System.Enum.GetNames<Contracts.Enums.SymbolKind>());
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Invalid kindFilter '{kindFilter}'. Valid values: {validKinds}");
        }

        return kind;
    }

    private static SymbolFilter GetSymbolFilter(Contracts.Enums.SymbolKind kind)
    {
        return kind switch
        {
            Contracts.Enums.SymbolKind.Class or
            Contracts.Enums.SymbolKind.Struct or
            Contracts.Enums.SymbolKind.Interface or
            Contracts.Enums.SymbolKind.Enum or
            Contracts.Enums.SymbolKind.Record or
            Contracts.Enums.SymbolKind.Delegate => SymbolFilter.Type,
            Contracts.Enums.SymbolKind.Namespace => SymbolFilter.Namespace,
            _ => SymbolFilter.Member
        };
    }
}
