namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_property tool.
/// </summary>
public sealed class GeneratePropertyParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible type in every C# document
    /// (or the optional single <see cref="SourceFile"/>).
    /// When true, cannot be combined with <see cref="TypeName"/>,
    /// <see cref="Line"/>, or <see cref="Column"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Name of the type to add the property to.
    /// Single-site only. Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation when several types share
    /// <see cref="TypeName"/>. When set, selects the type whose identifier
    /// or declaration span covers that line (identifier preferred, then
    /// smallest containing type). Omitted keeps today's typeName
    /// <c>FirstOrDefault</c> pick. Single-site only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set with <see cref="Line"/>,
    /// selects the type whose identifier or declaration span covers that
    /// column (identifier preferred, then smallest containing type).
    /// Omitted keeps today's typeName + optional line pick. Column without
    /// line keeps today's first-match after the typeName filter.
    /// Single-site only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Name of the property to generate. When omitted and <see cref="FieldName"/> is set,
    /// the name is derived from the field (leading underscores stripped, first letter capitalized).
    /// Required for auto-properties unless <see cref="FieldName"/> is set.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public string? PropertyName { get; init; }

    /// <summary>
    /// C# type of the property (for example <c>string</c> or <c>int</c>).
    /// Required for auto-properties; inferred from the field when <see cref="FieldName"/> is set.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public string? PropertyType { get; init; }

    /// <summary>
    /// Optional field to wrap. When set, generates
    /// <c>{ get =&gt; field; set =&gt; field = value; }</c> instead of an auto-property.
    /// Valid with <see cref="AllFiles"/> (types without that field are skipped).
    /// </summary>
    public string? FieldName { get; init; }

    /// <summary>
    /// Accessibility of the generated property. Default: public.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public string? Visibility { get; init; }

    /// <summary>
    /// Generate an init-only setter (<c>{ get; init; }</c> or <c>init =&gt;</c>). Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool InitOnly { get; init; }

    /// <summary>
    /// When true, remove an existing property declaration that matches
    /// <see cref="PropertyName"/> (including on other partials of the same
    /// type) before inserting a freshly generated property. Auto / init-only
    /// / backing-field form still follows <see cref="FieldName"/> and
    /// <see cref="InitOnly"/>. Fields and methods of the same name are never
    /// removed. Two same-named properties with no single target fail with
    /// <c>NameCollision</c> — this flag does not guess. Record / primary
    /// constructor properties (no <c>PropertyDeclarationSyntax</c>) are not
    /// replaced.
    /// Default: false (fail if a member with the target name already exists).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}
