using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Finds all references to a symbol across the solution.
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
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<FindReferencesResult>> ExecuteCoreAsync(
        Guid operationId,
        FindReferencesParams @params,
        CancellationToken cancellationToken)
    {
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

        foreach (var referencedSymbol in referencedSymbols)
        {
            // Add the definition itself (skipped entirely when includeDeclaration is false)
            var definitionLocations = includeDeclaration
                ? referencedSymbol.Definition.Locations.Where(l => l.IsInSource)
                : Enumerable.Empty<Location>();
            foreach (var defLocation in definitionLocations)
            {
                var lineSpan = defLocation.GetLineSpan();

                // Optional referenceFile filter runs before maxResults so TotalCount / Truncated
                // reflect the filtered set.
                if (!IsInReferenceFile(lineSpan.Path)) continue;

                totalCount++;
                if (locations.Count < maxResults)
                {
                    var snippet = await GetContextSnippetAsync(defLocation, cancellationToken);

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

                // Same filter as definition locations, applied to the reported File path.
                if (!IsInReferenceFile(file)) continue;

                totalCount++;

                if (locations.Count < maxResults)
                {
                    var snippet = await GetContextSnippetAsync(refLocation.Location, cancellationToken);

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

    private static async Task<string?> GetContextSnippetAsync(Location location, CancellationToken cancellationToken)
    {
        if (!location.IsInSource) return null;

        var tree = location.SourceTree;
        if (tree == null) return null;

        var text = await tree.GetTextAsync(cancellationToken);
        var lineSpan = location.GetLineSpan();
        var lineIndex = lineSpan.StartLinePosition.Line;

        if (lineIndex < 0 || lineIndex >= text.Lines.Count) return null;

        return text.Lines[lineIndex].ToString().Trim();
    }
}
