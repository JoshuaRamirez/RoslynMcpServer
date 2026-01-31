namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_overrides tool.
/// </summary>
public sealed class GenerateOverridesParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to generate overrides for.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Names of specific members to override. If null, shows available members.
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// Include base.Method() call in generated overrides. Default: true.
    /// </summary>
    public bool CallBase { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
