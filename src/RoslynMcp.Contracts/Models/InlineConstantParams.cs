namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the inline_constant tool.
/// </summary>
public sealed class InlineConstantParams
{
    /// <summary>
    /// Absolute path to the source file containing the constant.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the constant field to inline.
    /// </summary>
    public required string ConstantName { get; init; }

    /// <summary>
    /// Containing type name for disambiguation when multiple constants share a name.
    /// Additive filter when supplied; <see cref="Line"/> / <see cref="Column"/>
    /// do not replace it.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation when several constants share
    /// <see cref="ConstantName"/>. When set, selects the const field whose
    /// identifier or declaration span covers that line (identifier preferred,
    /// then smallest covering declarator/field). Nested types participate.
    /// Omitted keeps today's constantName + optional typeName path
    /// (including <c>SymbolAmbiguous</c> → provide typeName). Locals and
    /// other non-field declarators stay excluded.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set with <see cref="Line"/>,
    /// selects the const field whose identifier or declaration span covers
    /// that column (identifier preferred, then smallest covering
    /// declarator/field). Omitted keeps today's constantName + optional
    /// typeName + optional line pick. Column without line keeps today's
    /// omitted-line path after the name/typeName filter (do not invent a
    /// new omitted-line FirstOrDefault across types).
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Remove the constant declaration after inlining when no remaining references exist.
    /// Default: true.
    /// </summary>
    public bool RemoveConstant { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
