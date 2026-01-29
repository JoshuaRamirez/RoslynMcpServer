using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a refactoring operation.
/// </summary>
public sealed class RefactoringResult
{
    /// <summary>
    /// Whether the operation succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Unique identifier for this operation.
    /// </summary>
    public required Guid OperationId { get; init; }

    /// <summary>
    /// Whether this was a preview-only operation.
    /// </summary>
    public bool Preview { get; init; }

    /// <summary>
    /// Summary of file changes (null if failed).
    /// </summary>
    public FileChanges? Changes { get; init; }

    /// <summary>
    /// Information about the moved symbol (null if failed).
    /// </summary>
    public SymbolInfo? Symbol { get; init; }

    /// <summary>
    /// Number of references that were updated.
    /// </summary>
    public int ReferencesUpdated { get; init; }

    /// <summary>
    /// Number of using directives added (for namespace moves).
    /// </summary>
    public int UsingDirectivesAdded { get; init; }

    /// <summary>
    /// Number of using directives removed.
    /// </summary>
    public int UsingDirectivesRemoved { get; init; }

    /// <summary>
    /// Operation duration in milliseconds.
    /// </summary>
    public long ExecutionTimeMs { get; init; }

    /// <summary>
    /// Error information (null if succeeded).
    /// </summary>
    public RefactoringError? Error { get; init; }

    /// <summary>
    /// Pending changes for preview mode.
    /// </summary>
    public IReadOnlyList<PendingChange>? PendingChanges { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static RefactoringResult Succeeded(
        Guid operationId,
        FileChanges changes,
        SymbolInfo? symbol,
        int referencesUpdated,
        long executionTimeMs) => new()
    {
        Success = true,
        OperationId = operationId,
        Changes = changes,
        Symbol = symbol,
        ReferencesUpdated = referencesUpdated,
        ExecutionTimeMs = executionTimeMs
    };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static RefactoringResult Failed(Guid operationId, RefactoringError error) => new()
    {
        Success = false,
        OperationId = operationId,
        Error = error
    };

    /// <summary>
    /// Creates a preview result.
    /// </summary>
    public static RefactoringResult PreviewResult(
        Guid operationId,
        IReadOnlyList<PendingChange> pendingChanges) => new()
    {
        Success = true,
        OperationId = operationId,
        Preview = true,
        PendingChanges = pendingChanges
    };
}
