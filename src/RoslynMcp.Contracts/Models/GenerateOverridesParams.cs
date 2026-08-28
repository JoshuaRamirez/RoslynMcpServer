namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_overrides tool.
/// </summary>
public sealed class GenerateOverridesParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to generate overrides for.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Names of specific members to override. If null, shows available members.
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// Include base.Method() call in generated overrides. Default: true.
    /// </summary>
    public bool CallBase { get; init; } = true;

    /// <summary>
    /// When true, already-overridden members of this type become eligible
    /// alongside missing overridable members. Existing override declarations
    /// (methods and properties, including on other partials) that match a
    /// selected member by signature are removed and a standard generated
    /// override is inserted. Match methods by name + parameter types in order
    /// + <c>RefKind</c>, and properties by name. Two existing overrides that
    /// share a name with no exact signature match fail with
    /// <c>OverrideExists</c> — this flag does not guess an overload.
    /// <c>new</c> hiders, explicit interface implementations, non-override
    /// methods, and primary constructors are never replaced.
    /// Extra modifiers on the old override (<c>sealed</c>, <c>async</c>,
    /// attributes) are not copied.
    /// Default: false (skip members this type already overrides; a named
    /// member that is already overridden fails with
    /// <c>OverrideTargetNotFound</c>).
    /// </summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
