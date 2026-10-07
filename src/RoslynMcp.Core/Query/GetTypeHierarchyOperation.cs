using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Enums;
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
/// Gets the type hierarchy (base types, derived types, interfaces) for a type symbol. Derived types
/// are searched across the solution (or reported only within one project when
/// <see cref="GetTypeHierarchyParams.ProjectPath"/> is set).
/// </summary>
public sealed class GetTypeHierarchyOperation : QueryOperationBase<GetTypeHierarchyParams, GetTypeHierarchyResult>
{
    /// <inheritdoc />
    public GetTypeHierarchyOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GetTypeHierarchyParams @params)
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

        if (!string.IsNullOrWhiteSpace(@params.Direction) &&
            !Enum.TryParse<HierarchyDirection>(@params.Direction, ignoreCase: true, out _))
        {
            var valid = string.Join(", ", Enum.GetNames<HierarchyDirection>());
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, $"Invalid direction. Valid values: {valid}");
        }

        if (@params.MaxResults.HasValue && @params.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!string.IsNullOrWhiteSpace(@params.DerivedFile))
        {
            if (!PathResolver.IsAbsolutePath(@params.DerivedFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "derivedFile must be an absolute path.");

            if (!PathResolver.IsValidCSharpFilePath(@params.DerivedFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "derivedFile must be a .cs file.");

            if (!File.Exists(@params.DerivedFile))
                throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.DerivedFile}");
        }

        ProjectPathFilter.Validate(@params.ProjectPath);
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<GetTypeHierarchyResult>> ExecuteCoreAsync(
        Guid operationId,
        GetTypeHierarchyParams @params,
        CancellationToken cancellationToken)
    {
        // Optional projectPath scope (same validation / matching as get_diagnostics, search_symbols,
        // find_references, find_callers and find_implementations): resolved up front so a project that
        // is not in the workspace fails with SourceNotInWorkspace instead of an empty result. Every
        // target-framework variant of a multi-targeted project shares that path, so all of their
        // ProjectIds are in scope.
        var projectScope = string.IsNullOrWhiteSpace(@params.ProjectPath)
            ? null
            : ProjectPathFilter.SelectProjects(Context.Solution, @params.ProjectPath)
                .Select(project => project.Id)
                .ToHashSet();

        var resolved = await SymbolResolver.ResolveSymbolAsync(
            @params.SourceFile, @params.SymbolName, @params.Line, @params.Column, cancellationToken);

        var symbol = resolved.Symbol;

        // Must be a named type
        if (symbol is not INamedTypeSymbol typeSymbol)
        {
            // If it's a member, use its containing type
            if (symbol.ContainingType != null)
                typeSymbol = symbol.ContainingType;
            else
                throw new RefactoringException(ErrorCodes.TypeNotFound, "Symbol is not a type.");
        }

        var direction = ParseDirection(@params.Direction);
        var baseTypes = new List<TypeHierarchyEntry>();
        var derivedTypes = new List<TypeHierarchyEntry>();
        var interfaces = new List<TypeHierarchyEntry>();

        // Walk ancestors
        if (direction is HierarchyDirection.Ancestors or HierarchyDirection.Both)
        {
            var current = typeSymbol.BaseType;
            while (current != null && current.SpecialType != SpecialType.System_Object)
            {
                baseTypes.Add(CreateEntry(current));
                current = current.BaseType;
            }
        }

        // Find descendants
        if (direction is HierarchyDirection.Descendants or HierarchyDirection.Both)
        {
            // Optional transitive (default true = every descendant, today's behavior); false
            // returns only direct subclasses. The derivedFile / projectPath filters and maxResults
            // below then apply to whichever set was found.
            var derived = await SymbolFinder.FindDerivedClassesAsync(
                typeSymbol, Context.Solution, transitive: @params.Transitive ?? true, cancellationToken: cancellationToken);

            // Optional derivedFile filter: compare canonical path keys so aliases such as
            // "src/../Derived.cs" still match Roslyn's canonical location paths, and compare those
            // keys with OrdinalIgnoreCase (the documented contract, same as find_implementations
            // implementationFile / find_references referenceFile / find_callers callerFile) so
            // paths that differ only by letter case also match, even on case-sensitive volumes.
            var derivedFileKey = string.IsNullOrWhiteSpace(@params.DerivedFile)
                ? null
                : PathResolver.GetPathComparisonKey(@params.DerivedFile);
            var locationKeyCache = derivedFileKey == null
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal);

            bool IsInDerivedFile(Location location)
            {
                var locationPath = location.GetLineSpan().Path;
                if (string.IsNullOrWhiteSpace(locationPath)) return false;

                if (!locationKeyCache!.TryGetValue(locationPath, out var locationKey))
                {
                    locationKey = PathResolver.GetPathComparisonKey(locationPath);
                    locationKeyCache[locationPath] = locationKey;
                }

                return string.Equals(locationKey, derivedFileKey, StringComparison.OrdinalIgnoreCase);
            }

            bool IsInScopedProject(Location location)
            {
                var documentId = Context.Solution.GetDocumentId(location.SourceTree);
                return documentId != null && projectScope!.Contains(documentId.ProjectId);
            }

            var filtered = derivedFileKey != null || projectScope != null;

            foreach (var d in derived)
            {
                if (!filtered)
                {
                    derivedTypes.Add(CreateEntry(d));
                    continue;
                }

                // Optional derivedFile / projectPath filters run before maxResults so TotalCount /
                // Truncated reflect the filtered set. A derived type declared across several files
                // (partial type) matches when any of its in-source locations satisfies both filters
                // (in derivedFile and in a document of the scoped project), and is reported at that
                // location. BaseTypes and Interfaces are never filtered.
                var location = d.Locations.FirstOrDefault(l =>
                    l.IsInSource &&
                    (derivedFileKey == null || IsInDerivedFile(l)) &&
                    (projectScope == null || IsInScopedProject(l)));
                if (location == null) continue;

                derivedTypes.Add(CreateEntry(d, location));
            }
        }

        // Interfaces (always included)
        foreach (var iface in typeSymbol.AllInterfaces)
        {
            interfaces.Add(CreateEntry(iface));
        }

        var totalCount = derivedTypes.Count;
        IReadOnlyList<TypeHierarchyEntry> returnedDerived = derivedTypes;
        if (@params.MaxResults.HasValue && derivedTypes.Count > @params.MaxResults.Value)
        {
            returnedDerived = derivedTypes.Take(@params.MaxResults.Value).ToList();
        }

        var result = new GetTypeHierarchyResult
        {
            TypeName = typeSymbol.Name,
            FullyQualifiedName = typeSymbol.ToDisplayString(),
            Kind = typeSymbol.TypeKind.ToString(),
            BaseTypes = baseTypes,
            DerivedTypes = returnedDerived,
            Interfaces = interfaces,
            TotalCount = totalCount,
            Truncated = totalCount > returnedDerived.Count
        };

        return QueryResult<GetTypeHierarchyResult>.Succeeded(operationId, result);
    }

    private static HierarchyDirection ParseDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction))
            return HierarchyDirection.Both;

        return Enum.Parse<HierarchyDirection>(direction, ignoreCase: true);
    }

    private static TypeHierarchyEntry CreateEntry(INamedTypeSymbol typeSymbol, Location? preferredLocation = null)
    {
        var location = preferredLocation ?? typeSymbol.Locations.FirstOrDefault(l => l.IsInSource);
        string? file = null;
        int? line = null;

        if (location != null)
        {
            var lineSpan = location.GetLineSpan();
            file = lineSpan.Path;
            line = lineSpan.StartLinePosition.Line + 1;
        }

        return new TypeHierarchyEntry
        {
            TypeName = typeSymbol.Name,
            FullyQualifiedName = typeSymbol.ToDisplayString(),
            Kind = typeSymbol.TypeKind.ToString(),
            File = file,
            Line = line
        };
    }
}
