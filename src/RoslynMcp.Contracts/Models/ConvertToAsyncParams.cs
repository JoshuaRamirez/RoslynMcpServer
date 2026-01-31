namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the convert_to_async tool.
/// </summary>
public sealed class ConvertToAsyncParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the method to convert.
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// Line number for disambiguation if multiple methods have the same name (1-based).
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Rename method by adding Async suffix. Default: true.
    /// </summary>
    public bool RenameToAsync { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
