using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Refactoring;
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

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!@params.Line.HasValue && string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either line/column or symbolName must be provided.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");
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

        foreach (var location in symbol.Locations.Where(l => l.IsInSource))
        {
            var lineSpan = location.GetLineSpan();
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

        if (definitions.Count == 0)
        {
            // Symbol exists but is from metadata (external assembly)
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

        var result = new GoToDefinitionResult { Definitions = definitions };
        return QueryResult<GoToDefinitionResult>.Succeeded(operationId, result);
    }
}
