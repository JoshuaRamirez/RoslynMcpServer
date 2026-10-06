using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Navigates to a symbol's definition. Resolves a symbol at a given position or by name,
/// then returns its definition location(s). Handles partial classes with multiple locations.
/// </summary>
public sealed class GoToDefinitionOperation : QueryOperationBase<GoToDefinitionParams, GoToDefinitionResult>
{
    /// <inheritdoc />
    public GoToDefinitionOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GoToDefinitionParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        SourceFilePathHelpers.ValidateSourceFilePath(@params.SourceFile);

        if (@params.MaxResults.HasValue && @params.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!@params.Line.HasValue && string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either line/column or symbolName must be provided.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");

        if (!string.IsNullOrWhiteSpace(@params.DefinitionFile))
        {
            if (!PathResolver.IsAbsolutePath(@params.DefinitionFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "definitionFile must be an absolute path.");

            if (!PathResolver.IsValidCSharpFilePath(@params.DefinitionFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "definitionFile must be a .cs file.");

            if (!File.Exists(@params.DefinitionFile))
                throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.DefinitionFile}");
        }
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<GoToDefinitionResult>> ExecuteCoreAsync(
        Guid operationId,
        GoToDefinitionParams @params,
        CancellationToken cancellationToken)
    {
        var resolved = await SymbolResolver.ResolveSymbolAsync(
            @params.SourceFile, @params.SymbolName, @params.Line, @params.Column, cancellationToken);

        var symbol = resolved.Symbol;
        var definitions = new List<DefinitionLocation>();

        // Optional definitionFile filter: compare canonical path keys so aliases such as
        // "src/../Customer.cs" still match Roslyn's canonical location paths, and compare those
        // keys with OrdinalIgnoreCase (the documented contract, same as get_type_hierarchy
        // derivedFile / find_implementations implementationFile / find_references referenceFile /
        // find_callers callerFile) so paths that differ only by letter case also match, even on
        // case-sensitive volumes.
        var definitionFileKey = string.IsNullOrWhiteSpace(@params.DefinitionFile)
            ? null
            : PathResolver.GetPathComparisonKey(@params.DefinitionFile);

        // GetPathComparisonKey walks the filesystem, so cache keys by raw location path when
        // many declarations share a file (same as the peer per-file filters).
        var locationKeyCache = definitionFileKey == null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal);

        // With a filter, the candidate set also covers the other half of a partial method /
        // property / event: Roslyn models the defining declaration and the implementation as
        // distinct linked symbols, so symbol.Locations alone never reaches the other part's
        // file. Without a filter the reported set is unchanged.
        var candidateLocations = definitionFileKey == null
            ? symbol.Locations.Where(l => l.IsInSource)
            : GetLocationsIncludingPartialParts(symbol);

        foreach (var location in candidateLocations)
        {
            var lineSpan = location.GetLineSpan();

            // Filter runs before maxResults so TotalCount / Truncated reflect the filtered set.
            if (definitionFileKey != null)
            {
                var locationPath = lineSpan.Path;
                if (string.IsNullOrWhiteSpace(locationPath))
                    continue;

                if (!locationKeyCache!.TryGetValue(locationPath, out var locationKey))
                {
                    locationKey = PathResolver.GetPathComparisonKey(locationPath);
                    locationKeyCache[locationPath] = locationKey;
                }

                if (!string.Equals(locationKey, definitionFileKey, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            definitions.Add(new DefinitionLocation
            {
                File = lineSpan.Path,
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                SymbolName = symbol.Name,
                FullyQualifiedName = symbol.ToDisplayString(),
                Kind = SymbolKindMapper.Map(symbol),
                Signature = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            });
        }

        if (definitions.Count == 0 && definitionFileKey == null)
        {
            // Symbol exists but is from metadata (external assembly). Metadata definitions have
            // no file, so they are excluded when definitionFile is set.
            definitions.Add(new DefinitionLocation
            {
                File = "(metadata)",
                Line = 0,
                Column = 0,
                SymbolName = symbol.Name,
                FullyQualifiedName = symbol.ToDisplayString(),
                Kind = SymbolKindMapper.Map(symbol),
                Signature = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            });
        }

        var totalCount = definitions.Count;
        IReadOnlyList<DefinitionLocation> returnedDefinitions = definitions;
        if (@params.MaxResults.HasValue && definitions.Count > @params.MaxResults.Value)
        {
            returnedDefinitions = definitions.Take(@params.MaxResults.Value).ToList();
        }

        var result = new GoToDefinitionResult
        {
            Definitions = returnedDefinitions,
            TotalCount = totalCount,
            Truncated = totalCount > returnedDefinitions.Count
        };
        return QueryResult<GoToDefinitionResult>.Succeeded(operationId, result);
    }

    /// <summary>
    /// In-source locations of <paramref name="symbol"/> plus those of its linked partial parts
    /// (partial method definition / implementation, including partial constructors, and partial
    /// property / event definition / implementation), deduplicated, with the resolved symbol's
    /// own locations first.
    /// </summary>
    private static IEnumerable<Location> GetLocationsIncludingPartialParts(ISymbol symbol)
    {
        IEnumerable<ISymbol> parts = symbol switch
        {
            IMethodSymbol method => PartialMethodHelpers.GetPartialMethodParts(method),
            IPropertySymbol property => new ISymbol?[]
            {
                property,
                property.PartialDefinitionPart,
                property.PartialImplementationPart
            }.OfType<ISymbol>(),
            IEventSymbol @event => new ISymbol?[]
            {
                @event,
                @event.PartialDefinitionPart,
                @event.PartialImplementationPart
            }.OfType<ISymbol>(),
            _ => [symbol]
        };

        return parts
            .SelectMany(part => part.Locations)
            .Where(l => l.IsInSource)
            .Distinct();
    }
}
