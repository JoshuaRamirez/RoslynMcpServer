using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a query operation that returns data without modifying files.
/// </summary>
/// <typeparam name="TData">Type of the query result data.</typeparam>
public sealed class QueryResult<TData>
{
    /// <summary>
    /// Whether the query succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Unique identifier for this operation.
    /// </summary>
    public required Guid OperationId { get; init; }

    /// <summary>
    /// The query result data (null if failed).
    /// </summary>
    public TData? Data { get; init; }

    /// <summary>
    /// Operation duration in milliseconds.
    /// </summary>
    public long ExecutionTimeMs { get; init; }

    /// <summary>
    /// Error information (null if succeeded).
    /// </summary>
    public RefactoringError? Error { get; init; }

    /// <summary>
    /// Creates a successful query result.
    /// </summary>
    public static QueryResult<TData> Succeeded(Guid operationId, TData data, long executionTimeMs = 0) => new()
    {
        Success = true,
        OperationId = operationId,
        Data = data,
        ExecutionTimeMs = executionTimeMs
    };

    /// <summary>
    /// Creates a failed query result.
    /// </summary>
    public static QueryResult<TData> Failed(Guid operationId, RefactoringError error) => new()
    {
        Success = false,
        OperationId = operationId,
        Error = error
    };
}
