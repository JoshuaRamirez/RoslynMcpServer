namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_property tool.
/// </summary>
public sealed class GeneratePropertyParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to add the property to.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Name of the property to generate. When omitted and <see cref="FieldName"/> is set,
    /// the name is derived from the field (leading underscores stripped, first letter capitalized).
    /// </summary>
    public string? PropertyName { get; init; }

    /// <summary>
    /// C# type of the property (for example <c>string</c> or <c>int</c>).
    /// Required for auto-properties; inferred from the field when <see cref="FieldName"/> is set.
    /// </summary>
    public string? PropertyType { get; init; }

    /// <summary>
    /// Optional field to wrap. When set, generates
    /// <c>{ get =&gt; field; set =&gt; field = value; }</c> instead of an auto-property.
    /// </summary>
    public string? FieldName { get; init; }

    /// <summary>
    /// Accessibility of the generated property. Default: public.
    /// </summary>
    public string? Visibility { get; init; }

    /// <summary>
    /// Generate an init-only setter (<c>{ get; init; }</c> or <c>init =&gt;</c>). Default: false.
    /// </summary>
    public bool InitOnly { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
