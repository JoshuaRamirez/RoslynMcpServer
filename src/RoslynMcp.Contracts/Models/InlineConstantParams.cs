namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the inline_constant tool.
/// </summary>
public sealed class InlineConstantParams
{
    /// <summary>
    /// Absolute path to the source file containing the constant.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible const field in every C# document
    /// (or the optional single <see cref="SourceFile"/>).
    /// When true, cannot be combined with <see cref="ConstantName"/>,
    /// <see cref="TypeName"/>, <see cref="Line"/>, or <see cref="Column"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Name of the constant field to inline. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? ConstantName { get; init; }

    /// <summary>
    /// Containing type name for disambiguation when multiple constants share a name.
    /// Additive filter when supplied; <see cref="Line"/> / <see cref="Column"/>
    /// do not replace it. Single-site only.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation when several constants share
    /// <see cref="ConstantName"/>. When set, selects the const field whose
    /// identifier or declaration span covers that line (identifier preferred,
    /// then smallest covering declarator/field). Nested types participate.
    /// Omitted keeps today's constantName + optional typeName path
    /// (including <c>SymbolAmbiguous</c> → provide typeName). Locals and
    /// other non-field declarators stay excluded. Single-site only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set with <see cref="Line"/>,
    /// selects the const field whose identifier or declaration span covers
    /// that column (identifier preferred, then smallest covering
    /// declarator/field). Omitted keeps today's constantName + optional
    /// typeName + optional line pick. Column without line keeps today's
    /// omitted-line path after the name/typeName filter (do not invent a
    /// new omitted-line FirstOrDefault across types). Single-site only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Remove the constant declaration after inlining when no remaining references exist.
    /// Default: true. Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool RemoveConstant { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}
