namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the change_return_type tool.
/// </summary>
public sealed class ChangeReturnTypeParams
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
    /// New return type as C# type syntax.
    /// </summary>
    public required string NewReturnType { get; init; }

    /// <summary>
    /// Line number for disambiguation if multiple methods have the same name (1-based).
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Column number for disambiguation (1-based).
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Update the virtual/override chain together. Default: true.
    /// </summary>
    public bool UpdateOverrides { get; init; } = true;

    /// <summary>
    /// Update interface declarations and implementations together. Default: true.
    /// </summary>
    public bool UpdateImplementations { get; init; } = true;

    /// <summary>
    /// Attempt to convert return statements to the new type. Default: true.
    /// </summary>
    public bool ConvertReturnStatements { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
