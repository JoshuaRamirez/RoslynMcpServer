using System.Diagnostics;
using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Base;

/// <summary>
/// Base class for refactoring operations providing common infrastructure.
/// </summary>
/// <typeparam name="TParams">Parameter type for this operation.</typeparam>
public abstract class RefactoringOperationBase<TParams> : IRefactoringOperation<TParams>
{
    /// <summary>
    /// The workspace context for this operation.
    /// </summary>
    protected WorkspaceContext Context { get; }

    /// <summary>
    /// Type symbol resolver for this workspace.
    /// </summary>
    protected TypeSymbolResolver TypeResolver { get; }

    /// <summary>
    /// Reference tracker for finding symbol usages.
    /// </summary>
    protected ReferenceTracker ReferenceTracker { get; }

    /// <summary>
    /// Creates a new refactoring operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    protected RefactoringOperationBase(WorkspaceContext context)
    {
        Context = context;
        TypeResolver = context.CreateSymbolResolver();
        ReferenceTracker = context.CreateReferenceTracker();
    }

    /// <summary>
    /// Executes the refactoring with standard error handling and timing.
    /// </summary>
    public async Task<RefactoringResult> ExecuteAsync(TParams @params, CancellationToken cancellationToken = default)
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
    /// Validates the operation parameters. Override to add custom validation.
    /// </summary>
    /// <param name="params">Parameters to validate.</param>
    protected abstract void ValidateParams(TParams @params);

    /// <summary>
    /// Executes the core operation logic.
    /// </summary>
    /// <param name="operationId">Unique operation identifier.</param>
    /// <param name="params">Operation parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Refactoring result.</returns>
    protected abstract Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        TParams @params,
        CancellationToken cancellationToken);

    /// <summary>
    /// Commits solution changes to the filesystem.
    /// </summary>
    /// <param name="newSolution">Solution with changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Commit result.</returns>
    protected async Task<CommitResult> CommitChangesAsync(Solution newSolution, CancellationToken cancellationToken)
    {
        var result = await Context.CommitChangesAsync(newSolution, cancellationToken);
        if (!result.Success)
        {
            throw new RefactoringException(
                ErrorCodes.FilesystemError,
                $"Failed to write files: {result.Error}");
        }
        return result;
    }

    /// <summary>
    /// Gets a document by file path, throwing if not found.
    /// </summary>
    /// <param name="filePath">Absolute file path.</param>
    /// <returns>The document.</returns>
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

    private static RefactoringResult WithTiming(RefactoringResult result, long elapsedMs)
    {
        // If already has timing, return as-is (preview results don't need timing)
        if (result.ExecutionTimeMs > 0 || result.Preview)
            return result;

        return new RefactoringResult
        {
            Success = result.Success,
            OperationId = result.OperationId,
            Preview = result.Preview,
            Changes = result.Changes,
            Symbol = result.Symbol,
            ReferencesUpdated = result.ReferencesUpdated,
            UsingDirectivesAdded = result.UsingDirectivesAdded,
            UsingDirectivesRemoved = result.UsingDirectivesRemoved,
            ExecutionTimeMs = elapsedMs,
            Error = result.Error,
            PendingChanges = result.PendingChanges
        };
    }
}
