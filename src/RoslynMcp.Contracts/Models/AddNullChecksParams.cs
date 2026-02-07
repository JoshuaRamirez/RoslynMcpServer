namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the add_null_checks tool.
/// </summary>
public sealed class AddNullChecksParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the method/constructor to add null checks to.
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Style: "throw" (ArgumentNullException.ThrowIfNull) or "guard" (if-throw).
    /// Default: "throw".
    /// </summary>
    public string? Style { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
