namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the change_signature tool.
/// </summary>
public sealed class ChangeSignatureParams
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
    /// (or the optional single <see cref="SourceFile"/>) whose signature
    /// matches the requested <see cref="Parameters"/> list.
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
    /// Parameter changes to apply. Required for both single-site and allFiles.
    /// </summary>
    public required IReadOnlyList<ParameterChange> Parameters { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}

/// <summary>
/// Describes a change to a method parameter.
/// </summary>
public sealed class ParameterChange
{
    /// <summary>
    /// Original parameter name (null for new parameters).
    /// </summary>
    public string? OriginalName { get; init; }

    /// <summary>
    /// New parameter name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Parameter type (required for new parameters).
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Default value for the parameter.
    /// </summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// New position in parameter list (0-based).
    /// </summary>
    public int? NewPosition { get; init; }

    /// <summary>
    /// If true, removes this parameter.
    /// </summary>
    public bool Remove { get; init; }
}
