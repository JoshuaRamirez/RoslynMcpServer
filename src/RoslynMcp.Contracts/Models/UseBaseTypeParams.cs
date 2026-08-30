namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the use_base_type tool.
/// </summary>
public sealed class UseBaseTypeParams
{
    /// <summary>
    /// Absolute path to the source file containing the derived type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the derived class, struct, or interface whose references
    /// should be rewritten to a compatible base type.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation when several types share
    /// <see cref="TypeName"/>. When set, selects the type whose identifier
    /// or declaration span covers that line (identifier preferred, then
    /// smallest containing type). Omitted keeps today's typeName first-match
    /// on <c>TypeDeclarationSyntax</c> (simple name / single match) or FQN
    /// semantic <c>TypeNameMatches</c> narrowing.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Target base class or interface name. If omitted, uses the nearest base
    /// class, or the single implemented interface when there is no class base.
    /// </summary>
    public string? TargetBaseType { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
