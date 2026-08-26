namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the implement_abstract tool.
/// </summary>
public sealed class ImplementAbstractParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the class to implement inherited abstract members on.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Names of specific abstract members to implement. If null, implements all missing members.
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
