namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_overrides tool.
/// </summary>
public sealed class GenerateOverridesParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible type in every C# document
    /// (or the optional single <see cref="SourceFile"/>).
    /// When true, cannot be combined with <see cref="TypeName"/>,
    /// <see cref="Members"/>, <see cref="Line"/>, or <see cref="Column"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Name of the type to generate overrides for.
    /// Single-site only. Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation when several types share
    /// <see cref="TypeName"/>. When set, selects the type whose identifier
    /// or declaration span covers that line (identifier preferred, then
    /// smallest containing type). Omitted keeps today's typeName
    /// <c>FirstOrDefault</c> pick.
    /// Single-site only.
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
    /// Names of specific members to override. If null or empty, generates
    /// all missing overridable members (today's collect, including Object
    /// <c>ToString</c> / <c>Equals(object)</c> / <c>GetHashCode</c> when
    /// not already overridden). Single-site only; cannot be combined with
    /// <see cref="AllFiles"/>.
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// Include <c>base.Method(...)</c> / <c>base.Prop</c> / <c>base[i]</c> calls
    /// in generated overrides. Default: true. Abstract members still throw
    /// <c>NotImplementedException</c> (no legal base implementation). Events
    /// always use empty add/remove regardless of this flag.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool CallBase { get; init; } = true;

    /// <summary>
    /// When true, already-overridden members of this type become eligible
    /// alongside missing overridable members. Existing override declarations
    /// (methods, properties, and events, including on other partials) that
    /// match a selected member by signature are removed and a standard
    /// generated override is inserted. Match methods by name + parameter
    /// types in order + <c>RefKind</c>, and properties and events by name.
    /// Two existing overrides that share a name with no exact signature
    /// match fail with <c>OverrideExists</c> — this flag does not guess an
    /// overload. <c>new</c> hiders, explicit interface implementations,
    /// non-override methods, and primary constructors are never replaced.
    /// Extra modifiers on the old override (<c>sealed</c>, <c>async</c>,
    /// attributes) are not copied. The new override uses the inherited
    /// accessibility and each parameter's <c>RefKind</c>.
    /// Default: false (skip members this type already overrides; a named
    /// member that is already overridden fails with
    /// <c>OverrideTargetNotFound</c>).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}
