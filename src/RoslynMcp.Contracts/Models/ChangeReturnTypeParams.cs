namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the change_return_type tool.
/// </summary>
public sealed class ChangeReturnTypeParams
{
    /// <summary>
    /// Absolute path to the source file containing the method.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible method in every C# document
    /// (or the optional single <see cref="SourceFile"/>) whose current
    /// return type can safely become <see cref="NewReturnType"/> under
    /// today's single-site validation.
    /// When true, cannot be combined with <see cref="MethodName"/>,
    /// <see cref="Line"/>, or <see cref="Column"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Name of the method to modify. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? MethodName { get; init; }

    /// <summary>
    /// New return type as C# type syntax.
    /// Required for both single-site and allFiles.
    /// </summary>
    public required string NewReturnType { get; init; }

    /// <summary>
    /// Line number for disambiguation if multiple methods have the same name (1-based).
    /// Single-site only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set, selects the smallest method
    /// whose identifier or declaration span covers that column. Omitted keeps
    /// today's MethodName and/or Line start-line pick. Single-site only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Update the virtual/override chain together. Default: true.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool UpdateOverrides { get; init; } = true;

    /// <summary>
    /// Update interface declarations and implementations together. Default: true.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool UpdateImplementations { get; init; } = true;

    /// <summary>
    /// Attempt to convert return statements to the new type. Default: true.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool ConvertReturnStatements { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}
