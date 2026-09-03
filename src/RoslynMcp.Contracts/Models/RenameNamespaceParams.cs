namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the rename_namespace tool.
/// </summary>
public sealed class RenameNamespaceParams
{
    /// <summary>
    /// Absolute path to a source file that declares the namespace.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Current namespace name (simple or fully qualified).
    /// </summary>
    public required string NamespaceName { get; init; }

    /// <summary>
    /// New namespace name (simple or fully qualified).
    /// </summary>
    public required string NewName { get; init; }

    /// <summary>
    /// 1-based line number used to select a namespace declaration when the file has more than one.
    /// When <see cref="Column"/> is omitted, matching stays today's covering-span line pick.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set with <see cref="Line"/>,
    /// selects the smallest namespace whose name or declaration span covers
    /// that column (name preferred, then smallest covering declaration).
    /// Omitted keeps today's namespaceName + optional line pick. Column
    /// without line keeps today's omitted-line path.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Also move folders whose path matches the old namespace so they
    /// match <see cref="NewName"/>. Default: false (rewrite only).
    /// </summary>
    public bool UpdateFolders { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
