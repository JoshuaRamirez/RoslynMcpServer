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
/// solution (or within one project when <see cref="FindImplementationsParams.ProjectPath"/> is set).
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

        if (!@params.Line.HasValue && string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either line/column or symbolName must be provided.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");

        if (@params.MaxResults.HasValue && @params.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");

        if (!string.IsNullOrWhiteSpace(@params.ImplementationFile))
        {
            if (!PathResolver.IsAbsolutePath(@params.ImplementationFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "implementationFile must be an absolute path.");

            if (!PathResolver.IsValidCSharpFilePath(@params.ImplementationFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "implementationFile must be a .cs file.");

            if (!File.Exists(@params.ImplementationFile))
                throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.ImplementationFile}");
        }

        ProjectPathFilter.Validate(@params.ProjectPath);
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

        // Find implementations based on symbol kind
        var implSymbols = await SymbolFinder.FindImplementationsAsync(
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

        foreach (var impl in implSymbols)
        {
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
