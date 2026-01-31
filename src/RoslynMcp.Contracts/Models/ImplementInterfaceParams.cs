namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the implement_interface tool.
/// </summary>
public sealed class ImplementInterfaceParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to implement interface on.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Name of the interface to implement (simple or fully qualified).
    /// </summary>
    public required string InterfaceName { get; init; }

    /// <summary>
    /// Use explicit interface implementation. Default: false.
    /// </summary>
    public bool ExplicitImplementation { get; init; }

    /// <summary>
    /// Names of specific members to implement. If null, implements all missing members.
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// Throw NotImplementedException in method bodies. Default: true.
    /// </summary>
    public bool ThrowNotImplemented { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
