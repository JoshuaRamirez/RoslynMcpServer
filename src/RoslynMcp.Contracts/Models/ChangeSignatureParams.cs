namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the change_signature tool.
/// </summary>
public sealed class ChangeSignatureParams
{
    /// <summary>
    /// Absolute path to the source file containing the method.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the method to modify.
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// Line number for disambiguation if multiple methods have the same name (1-based).
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Parameter changes to apply.
    /// </summary>
    public required IReadOnlyList<ParameterChange> Parameters { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
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
