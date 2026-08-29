namespace RoslynMcp.Contracts.Models;

/// <summary>
/// A convert_to_async call site that was not wrapped in await.
/// </summary>
public sealed class SkippedCaller
{
    /// <summary>
    /// Enclosing callable name (method, local function, or lambda).
    /// </summary>
    public required string Caller { get; init; }

    /// <summary>
    /// Why the call site was not awaited.
    /// </summary>
    public required string Reason { get; init; }
}
