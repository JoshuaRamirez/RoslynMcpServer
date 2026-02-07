using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Gets the type hierarchy (base types, derived types, interfaces) for a type symbol.
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

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

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

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<GetTypeHierarchyResult>> ExecuteCoreAsync(
        Guid operationId,
        GetTypeHierarchyParams @params,
        CancellationToken cancellationToken)
    {
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
            var derived = await SymbolFinder.FindDerivedClassesAsync(
                typeSymbol, Context.Solution, cancellationToken: cancellationToken);

            foreach (var d in derived)
            {
                derivedTypes.Add(CreateEntry(d));
            }
        }

        // Interfaces (always included)
        foreach (var iface in typeSymbol.AllInterfaces)
        {
            interfaces.Add(CreateEntry(iface));
        }

        var result = new GetTypeHierarchyResult
        {
            TypeName = typeSymbol.Name,
            FullyQualifiedName = typeSymbol.ToDisplayString(),
            Kind = typeSymbol.TypeKind.ToString(),
            BaseTypes = baseTypes,
            DerivedTypes = derivedTypes,
            Interfaces = interfaces
        };

        return QueryResult<GetTypeHierarchyResult>.Succeeded(operationId, result);
    }

    private static HierarchyDirection ParseDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction))
            return HierarchyDirection.Both;

        return Enum.Parse<HierarchyDirection>(direction, ignoreCase: true);
    }

    private static TypeHierarchyEntry CreateEntry(INamedTypeSymbol typeSymbol)
    {
        var location = typeSymbol.Locations.FirstOrDefault(l => l.IsInSource);
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
