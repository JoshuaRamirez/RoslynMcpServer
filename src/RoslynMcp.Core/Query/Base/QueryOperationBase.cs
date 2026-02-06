using System.Diagnostics;
using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query.Base;

/// <summary>
/// Base class for read-only query operations providing common infrastructure.
/// Mirrors RefactoringOperationBase but returns QueryResult instead of RefactoringResult,
/// and has no commit/preview logic since queries don't modify files.
/// </summary>
/// <typeparam name="TParams">Parameter type for this query.</typeparam>
/// <typeparam name="TResult">Result data type.</typeparam>
public abstract class QueryOperationBase<TParams, TResult> : IQueryOperation<TParams, TResult>
{
    /// <summary>
    /// The workspace context for this operation.
    /// </summary>
    protected WorkspaceContext Context { get; }

    /// <summary>
    /// General-purpose symbol resolver for this workspace.
    /// </summary>
    protected SymbolResolver SymbolResolver { get; }

    /// <summary>
    /// Reference tracker for finding symbol usages.
    /// </summary>
    protected ReferenceTracker ReferenceTracker { get; }

    /// <summary>
    /// Creates a new query operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    protected QueryOperationBase(WorkspaceContext context)
    {
        Context = context;
        SymbolResolver = context.CreateGeneralSymbolResolver();
        ReferenceTracker = context.CreateReferenceTracker();
    }

    /// <summary>
    /// Executes the query with standard error handling and timing.
    /// </summary>
    public async Task<QueryResult<TResult>> ExecuteAsync(TParams @params, CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            ValidateParams(@params);
            var result = await ExecuteCoreAsync(operationId, @params, cancellationToken);
            stopwatch.Stop();
            return WithTiming(result, stopwatch.ElapsedMilliseconds);
        }
        catch (RefactoringException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new RefactoringException(ErrorCodes.Timeout, "Operation was cancelled.");
        }
        catch (Exception ex)
        {
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                $"Unexpected error: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Validates the query parameters.
    /// </summary>
    protected abstract void ValidateParams(TParams @params);

    /// <summary>
    /// Executes the core query logic.
    /// </summary>
    protected abstract Task<QueryResult<TResult>> ExecuteCoreAsync(
        Guid operationId,
        TParams @params,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets a document by file path, throwing if not found.
    /// </summary>
    protected Document GetDocumentOrThrow(string filePath)
    {
        var doc = Context.GetDocumentByPath(filePath);
        if (doc == null)
        {
            throw new RefactoringException(
                ErrorCodes.SourceNotInWorkspace,
                $"File not found in workspace: {filePath}");
        }
        return doc;
    }

    private static QueryResult<TResult> WithTiming(QueryResult<TResult> result, long elapsedMs)
    {
        if (result.ExecutionTimeMs > 0)
            return result;

        return new QueryResult<TResult>
        {
            Success = result.Success,
            OperationId = result.OperationId,
            Data = result.Data,
            ExecutionTimeMs = elapsedMs,
            Error = result.Error
        };
    }
}
