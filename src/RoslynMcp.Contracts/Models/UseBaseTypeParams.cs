namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the use_base_type tool.
/// </summary>
public sealed class UseBaseTypeParams
{
    /// <summary>
    /// Absolute path to the source file containing the derived type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the derived class, struct, or interface whose references
    /// should be rewritten to a compatible base type.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Target base class or interface name. If omitted, uses the nearest base
    /// class, or the single implemented interface when there is no class base.
    /// </summary>
    public string? TargetBaseType { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
