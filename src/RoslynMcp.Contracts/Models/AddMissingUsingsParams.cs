namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the add_missing_usings tool.
/// </summary>
public sealed class AddMissingUsingsParams
{
    /// <summary>
    /// Absolute path to the source file. Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process all C# documents in the solution instead of a single file.
    /// When true, <see cref="SourceFile"/> is optional.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
