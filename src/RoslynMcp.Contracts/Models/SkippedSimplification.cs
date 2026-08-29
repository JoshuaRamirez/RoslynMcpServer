namespace RoslynMcp.Contracts.Models;

/// <summary>
/// A qualified name that was not simplified, with the reason.
/// </summary>
public sealed class SkippedSimplification
{
    /// <summary>
    /// The qualified name text that was left unchanged.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Why the name was not simplified (ambiguity, different binding, required global::).
    /// </summary>
    public required string Reason { get; init; }
}
