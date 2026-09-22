namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the extract_base_class tool.
/// </summary>
public sealed class ExtractBaseClassParams
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
    /// <see cref="Line"/>, <see cref="Column"/>, <see cref="BaseClassName"/>,
    /// <see cref="Members"/>, or <see cref="TargetFile"/>.
    /// Bulk derives each base class as <c>{TypeName}Base</c> and writes
    /// <c>{TypeName}Base.cs</c> next to the source (separate-file default).
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Name of the type to extract base class from. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation when several types share
    /// <see cref="TypeName"/>. When set, selects the type whose identifier
    /// or declaration span covers that line (identifier preferred, then
    /// smallest containing type). Omitted keeps today's typeName
    /// <c>FirstOrDefault</c> pick on <c>ClassDeclarationSyntax</c>.
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set with <see cref="Line"/>,
    /// selects the type whose identifier or declaration span covers that
    /// column (identifier preferred, then smallest containing type).
    /// Omitted keeps today's typeName + optional line pick. Column without
    /// line keeps today's first-match after the typeName filter
    /// (<c>ClassDeclarationSyntax</c> only).
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Name for the new base class. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, each base class is named
    /// <c>{TypeName}Base</c>.
    /// </summary>
    public string? BaseClassName { get; init; }

    /// <summary>
    /// Names of members to move to base class. Indexers match metadata name
    /// (<c>Item</c>), Roslyn name (<c>this[]</c>), and conventional display
    /// (<c>this[int i]</c>).
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// Absolute path for the base class file. If set, always wins over <see cref="SeparateFile"/>.
    /// If null and <see cref="SeparateFile"/> is false, creates the base class in the source file.
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// </summary>
    public string? TargetFile { get; init; }

    /// <summary>
    /// When true and <see cref="TargetFile"/> is omitted, write the base class to
    /// <c>{BaseClassName}.cs</c> in the same directory as <see cref="SourceFile"/>.
    /// Default: false (same-file extract unless <see cref="TargetFile"/> is set).
    /// When <see cref="AllFiles"/> is true, bulk always writes a separate
    /// <c>{TypeName}Base.cs</c> next to each source (this flag is ignored for destination).
    /// Valid with <see cref="AllFiles"/> as a documented no-op for destination.
    /// </summary>
    public bool SeparateFile { get; init; }

    /// <summary>
    /// When true, the new base class is abstract and extracted methods,
    /// properties, events, and indexers become abstract on that base while
    /// the derived type keeps override implementations. Fields still move
    /// as concrete. Default: false (move, derived loses the member).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool MakeAbstract { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}
