using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Refactoring;
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

            // Use GetSymbolsWithName with substring predicate
            var symbols = compilation.GetSymbolsWithName(
                name => name.Contains(query, StringComparison.OrdinalIgnoreCase),
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

                // Skip duplicates (same symbol can appear in multiple compilations)
                var fqn = symbol.ToDisplayString();
                if (entries.Any(e => e.FullyQualifiedName == fqn)) continue;

                totalCount++;

                if (entries.Count < maxResults)
                {
                    var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
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

    private static Contracts.Enums.SymbolKind? ParseKindFilter(string? kindFilter)
    {
        if (string.IsNullOrWhiteSpace(kindFilter)) return null;

        return System.Enum.TryParse<Contracts.Enums.SymbolKind>(kindFilter, ignoreCase: true, out var kind)
            ? kind
            : null;
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
