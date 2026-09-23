namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the rename_namespace tool.
/// </summary>
public sealed class RenameNamespaceParams
{
    /// <summary>
    /// Absolute path to a source file that declares the namespace.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, rename every eligible top-level namespace declaration in
    /// every C# document (or the optional single <see cref="SourceFile"/>)
    /// to <see cref="NewName"/> under today's single-site validation.
    /// When true, cannot be combined with <see cref="NamespaceName"/>,
    /// <see cref="Line"/>, or <see cref="Column"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Current namespace name (simple or fully qualified).
    /// Required when <see cref="AllFiles"/> is false.
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// </summary>
    public string? NamespaceName { get; init; }

    /// <summary>
    /// New namespace name (simple or fully qualified).
    /// Required for both single-site and allFiles.
    /// </summary>
    public required string NewName { get; init; }

    /// <summary>
    /// 1-based line number used to select a namespace declaration when the file has more than one.
    /// When <see cref="Column"/> is omitted, matching stays today's covering-span line pick.
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set with <see cref="Line"/>,
    /// selects the smallest namespace whose matching name segment or
    /// declaration span covers that column (the identifier for that
    /// candidate preferred, then smallest covering declaration).
    /// Omitted keeps today's namespaceName + optional line pick. Column
    /// without line keeps today's omitted-line path.
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Also move folders whose path matches the old namespace so they
    /// match <see cref="NewName"/>. Default: false (rewrite only).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool UpdateFolders { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}
