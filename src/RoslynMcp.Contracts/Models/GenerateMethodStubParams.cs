namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_method_stub tool.
/// </summary>
public sealed class GenerateMethodStubParams
{
    /// <summary>
    /// Absolute path to the file containing the call site.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// 1-based line number of the call site.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// 1-based column number within the method name.
    /// </summary>
    public required int Column { get; init; }

    /// <summary>
    /// Method name override when the name is not inferable from the location.
    /// </summary>
    public string? MethodName { get; init; }

    /// <summary>
    /// Explicit return type override when usage does not constrain the type.
    /// </summary>
    public string? ReturnType { get; init; }

    /// <summary>
    /// Accessibility of the generated method. Default: private on the same type, public on another type.
    /// </summary>
    public string? Visibility { get; init; }

    /// <summary>
    /// Force async method generation (<c>Task</c> / <c>Task&lt;T&gt;</c>). Default: false.
    /// </summary>
    public bool GenerateAsync { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
