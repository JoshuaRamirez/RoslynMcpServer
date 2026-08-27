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
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column number used with <see cref="Line"/> when multiple declarations share a line.
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
