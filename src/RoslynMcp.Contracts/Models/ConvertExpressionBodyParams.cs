namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the convert_expression_body tool.
/// </summary>
public sealed class ConvertExpressionBodyParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the member to convert.
    /// </summary>
    public string? MemberName { get; init; }

    /// <summary>
    /// 1-based line number for position-based resolution.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Conversion direction: ToExpressionBody or ToBlockBody.
    /// </summary>
    public required string Direction { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
