namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the encapsulate_field tool.
/// </summary>
public sealed class EncapsulateFieldParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the field to encapsulate.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation when several fields share
    /// <see cref="FieldName"/>. When set, selects the field whose identifier
    /// or declaration span covers that line (identifier preferred, then
    /// smallest covering declarator/field). Nested types participate.
    /// Omitted keeps today's fieldName first-match on field
    /// <c>VariableDeclaratorSyntax</c>. Locals and other non-field
    /// declarators stay excluded.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Name for the property. If null, derives from field name.
    /// </summary>
    public string? PropertyName { get; init; }

    /// <summary>
    /// Create read-only property (getter only). Default: false.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// Update external references to use the new property. Default: true
    /// (rewrite external callers; same-class references stay on the field).
    /// </summary>
    public bool UpdateReferences { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
