namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the reorder_parameters tool.
/// </summary>
public sealed class ReorderParametersParams
{
    /// <summary>
    /// Absolute path to the source file containing the method.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the method to modify.
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// New parameter order as a 0-based permutation of 0..n-1.
    /// <c>newOrder[i]</c> is the original index that moves to position <c>i</c>.
    /// </summary>
    public required int[] NewOrder { get; init; }

    /// <summary>
    /// Line number for disambiguation if multiple methods have the same name (1-based).
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set, selects the smallest method
    /// whose identifier or declaration span covers that column. Omitted keeps
    /// today's MethodName and/or Line start-line pick.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Update the virtual/override chain together. Default: true.
    /// </summary>
    public bool UpdateOverrides { get; init; } = true;

    /// <summary>
    /// Update interface declarations and implementations together. Default: true.
    /// </summary>
    public bool UpdateImplementations { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
