namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_constructor tool.
/// </summary>
public sealed class GenerateConstructorParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to add constructor to.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Names of members to initialize. If null, uses all uninitialized fields and properties.
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// Add null checks for reference type parameters. Default: false.
    /// </summary>
    public bool AddNullChecks { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
