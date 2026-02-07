namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a get_code_metrics query.
/// </summary>
public sealed class GetCodeMetricsResult
{
    /// <summary>
    /// Name of the analyzed symbol or file.
    /// </summary>
    public required string SymbolName { get; init; }

    /// <summary>
    /// Fully qualified name.
    /// </summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>
    /// Cyclomatic complexity.
    /// </summary>
    public required int CyclomaticComplexity { get; init; }

    /// <summary>
    /// Lines of code (excluding whitespace and comments).
    /// </summary>
    public required int LinesOfCode { get; init; }

    /// <summary>
    /// Maintainability index (0-100, higher is more maintainable).
    /// </summary>
    public required int MaintainabilityIndex { get; init; }

    /// <summary>
    /// Number of distinct types referenced.
    /// </summary>
    public required int ClassCoupling { get; init; }

    /// <summary>
    /// Depth of inheritance tree.
    /// </summary>
    public required int DepthOfInheritance { get; init; }
}
