namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the convert_anonymous_to_class tool.
/// </summary>
public sealed class ConvertAnonymousToClassParams
{
    /// <summary>
    /// Absolute path to the source file containing the anonymous object creation.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, walk every C# document (or the optional single
    /// <see cref="SourceFile"/>) and convert every distinct eligible
    /// anonymous-type shape, naming each type from sanitized property names.
    /// When true, cannot be combined with <see cref="Line"/>,
    /// <see cref="Column"/>, or <see cref="NewTypeName"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// 1-based line number of the anonymous object creation (<c>new { ... }</c>).
    /// Single-site only. When <see cref="Column"/> is omitted, matching stays
    /// today's line pick (single covering candidate returns; several on the
    /// line stay SymbolAmbiguous).
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Name of the class or record to create. Single-site only.
    /// </summary>
    public string? NewTypeName { get; init; }

    /// <summary>
    /// 1-based column on the anonymous object creation. When set with
    /// <see cref="Line"/>, selects the creation whose span covers that column
    /// (exclusive-end; today's unique covering match, else CannotConvert /
    /// SymbolAmbiguous). Omitted keeps today's line pick. Single-site only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Create a <c>record</c> instead of a <c>class</c>. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool AsRecord { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}
