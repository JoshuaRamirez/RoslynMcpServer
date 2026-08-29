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
    /// 1-based column for disambiguation when more than one matching method
    /// shares a line, or when the identifier lives on a continuation line
    /// of a split signature. Optional. When set, selects the method whose
    /// identifier or declaration span covers that column. Omitted keeps
    /// today's MethodName + Line pick.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Rename method by adding Async suffix. Default: true.
    /// </summary>
    public bool RenameToAsync { get; init; } = true;

    /// <summary>
    /// Update callers to await the converted method. Default: false
    /// (convert the method and, when renaming, rewrite call-site
    /// identifiers; do not wrap callers in await or make them async).
    /// </summary>
    public bool UpdateCallers { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
