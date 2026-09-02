namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the encapsulate_field tool.
/// </summary>
public sealed class EncapsulateFieldParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process all C# documents in the solution instead of a single field.
    /// When true, <see cref="SourceFile"/> is optional. Cannot be combined with
    /// <see cref="FieldName"/>, <see cref="Line"/>, <see cref="Column"/>, or
    /// <see cref="PropertyName"/> (bulk cannot apply one propertyName).
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Name of the field to encapsulate. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? FieldName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation when several fields share
    /// <see cref="FieldName"/>. When set, selects the field whose identifier
    /// or declaration span covers that line (identifier preferred, then
    /// smallest covering declarator/field). Nested types participate.
    /// Omitted keeps today's fieldName first-match on field
    /// <c>VariableDeclaratorSyntax</c>. Locals and other non-field
    /// declarators stay excluded. Single-site only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set with <see cref="Line"/>,
    /// selects the field whose identifier or declaration span covers that
    /// column (identifier preferred, then smallest covering declarator/field).
    /// Omitted keeps today's fieldName + optional line pick. Column without
    /// line keeps today's omitted-line <c>FirstOrDefault</c> after the
    /// fieldName filter (field <c>VariableDeclaratorSyntax</c> only).
    /// Single-site only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Name for the property. If null, derives from field name.
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
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
