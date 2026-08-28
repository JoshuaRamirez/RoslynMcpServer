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
    /// Throw NotImplementedException in method, property, and indexer stub bodies. Default: true.
    /// When false, methods and getters use a default-return body and setters / init setters use an empty block.
    /// </summary>
    public bool ThrowNotImplemented { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
