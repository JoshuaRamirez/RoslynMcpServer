namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the extract_base_class tool.
/// </summary>
public sealed class ExtractBaseClassParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to extract base class from.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation when several types share
    /// <see cref="TypeName"/>. When set, selects the type whose identifier
    /// or declaration span covers that line (identifier preferred, then
    /// smallest containing type). Omitted keeps today's typeName
    /// <c>FirstOrDefault</c> pick on <c>ClassDeclarationSyntax</c>.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Name for the new base class.
    /// </summary>
    public required string BaseClassName { get; init; }

    /// <summary>
    /// Names of members to move to base class. Indexers match metadata name
    /// (<c>Item</c>), Roslyn name (<c>this[]</c>), and conventional display
    /// (<c>this[int i]</c>).
    /// </summary>
    public required IReadOnlyList<string> Members { get; init; }

    /// <summary>
    /// Absolute path for the base class file. If set, always wins over <see cref="SeparateFile"/>.
    /// If null and <see cref="SeparateFile"/> is false, creates the base class in the source file.
    /// </summary>
    public string? TargetFile { get; init; }

    /// <summary>
    /// When true and <see cref="TargetFile"/> is omitted, write the base class to
    /// <c>{BaseClassName}.cs</c> in the same directory as <see cref="SourceFile"/>.
    /// Default: false (same-file extract unless <see cref="TargetFile"/> is set).
    /// </summary>
    public bool SeparateFile { get; init; }

    /// <summary>
    /// When true, the new base class is abstract and extracted methods,
    /// properties, events, and indexers become abstract on that base while
    /// the derived type keeps override implementations. Fields still move
    /// as concrete. Default: false (move, derived loses the member).
    /// </summary>
    public bool MakeAbstract { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
