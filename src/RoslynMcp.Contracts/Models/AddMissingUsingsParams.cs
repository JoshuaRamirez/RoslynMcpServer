namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the add_missing_usings tool.
/// </summary>
public sealed class AddMissingUsingsParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
