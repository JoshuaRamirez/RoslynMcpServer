using RoslynMcp.Contracts.Models;

namespace RoslynMcp.Core.Query.Base;

/// <summary>
/// Interface for read-only query operations that return data without modifying files.
/// </summary>
/// <typeparam name="TParams">Parameter type for this query.</typeparam>
/// <typeparam name="TResult">Result data type.</typeparam>
public interface IQueryOperation<in TParams, TResult>
{
    /// <summary>
    /// Executes the query and returns results.
    /// </summary>
    Task<QueryResult<TResult>> ExecuteAsync(TParams @params, CancellationToken cancellationToken = default);
}
