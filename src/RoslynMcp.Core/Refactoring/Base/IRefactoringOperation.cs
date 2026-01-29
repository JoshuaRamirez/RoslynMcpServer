using RoslynMcp.Contracts.Models;

namespace RoslynMcp.Core.Refactoring.Base;

/// <summary>
/// Contract for a refactoring operation with typed parameters.
/// </summary>
/// <typeparam name="TParams">Parameter type for this operation.</typeparam>
public interface IRefactoringOperation<in TParams>
{
    /// <summary>
    /// Executes the refactoring operation.
    /// </summary>
    /// <param name="params">Operation parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Refactoring result.</returns>
    Task<RefactoringResult> ExecuteAsync(TParams @params, CancellationToken cancellationToken = default);
}
