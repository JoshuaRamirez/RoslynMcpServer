namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the add_parameter tool.
/// </summary>
public sealed class AddParameterParams
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
    /// Name for the new parameter.
    /// </summary>
    public required string ParameterName { get; init; }

    /// <summary>
    /// C# type of the new parameter.
    /// </summary>
    public required string ParameterType { get; init; }

    /// <summary>
    /// Default value used at existing call sites (and on the declaration when provided).
    /// </summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// 0-based insertion position. -1 (default) inserts at the end of required
    /// parameters (before optionals and <c>params</c>).
    /// </summary>
    public int Position { get; init; } = -1;

    /// <summary>
    /// Line number for disambiguation if multiple methods have the same name (1-based).
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Column number for disambiguation (1-based).
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
