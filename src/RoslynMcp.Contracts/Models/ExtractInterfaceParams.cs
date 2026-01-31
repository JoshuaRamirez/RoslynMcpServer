namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the extract_interface tool.
/// </summary>
public sealed class ExtractInterfaceParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to extract interface from.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Name for the new interface.
    /// </summary>
    public required string InterfaceName { get; init; }

    /// <summary>
    /// Names of members to include in interface. If null, includes all public instance members.
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// Absolute path for the interface file. If null, creates in same file.
    /// </summary>
    public string? TargetFile { get; init; }

    /// <summary>
    /// Add the interface to the type's base list. Default: true.
    /// </summary>
    public bool AddInterfaceToType { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
