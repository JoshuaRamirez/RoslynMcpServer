namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the extract_method tool.
/// </summary>
public sealed class ExtractMethodParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// 1-based start line of selection.
    /// </summary>
    public required int StartLine { get; init; }

    /// <summary>
    /// 1-based start column of selection.
    /// </summary>
    public required int StartColumn { get; init; }

    /// <summary>
    /// 1-based end line of selection.
    /// </summary>
    public required int EndLine { get; init; }

    /// <summary>
    /// 1-based end column of selection.
    /// </summary>
    public required int EndColumn { get; init; }

    /// <summary>
    /// Name for the new method.
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// Visibility for the new method (private, internal, protected, public). Default: private.
    /// </summary>
    public string Visibility { get; init; } = "private";

    /// <summary>
    /// Force the method to be static. Default: false (auto-detect).
    /// </summary>
    public bool? MakeStatic { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
