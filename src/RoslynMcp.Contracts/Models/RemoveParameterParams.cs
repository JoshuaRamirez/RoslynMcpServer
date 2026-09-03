namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the remove_parameter tool.
/// </summary>
public sealed class RemoveParameterParams
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
    /// Name of the parameter to remove.
    /// </summary>
    public required string ParameterName { get; init; }

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
    /// Remove the parameter even if it is referenced in the method body.
    /// Body usages are replaced only when the solution stays compiling.
    /// Default: false.
    /// </summary>
    public bool Force { get; init; }

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
