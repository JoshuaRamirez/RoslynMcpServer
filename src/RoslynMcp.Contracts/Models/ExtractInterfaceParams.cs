namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the extract_interface tool.
/// </summary>
public sealed class ExtractInterfaceParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to extract interface from.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation when several types share
    /// <see cref="TypeName"/>. When set, selects the type whose identifier
    /// or declaration span covers that line (identifier preferred, then
    /// smallest containing type). Omitted keeps today's typeName
    /// <c>FirstOrDefault</c> pick on <c>TypeDeclarationSyntax</c>.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Name for the new interface.
    /// </summary>
    public required string InterfaceName { get; init; }

    /// <summary>
    /// Names of members to include in interface. If null, includes all public
    /// instance members. Indexers match metadata name (<c>Item</c>), Roslyn
    /// name (<c>this[]</c>), and conventional display (<c>this[int i]</c>).
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// Absolute path for the interface file. If set, always wins over <see cref="SeparateFile"/>.
    /// If null and <see cref="SeparateFile"/> is false, creates the interface in the source file.
    /// </summary>
    public string? TargetFile { get; init; }

    /// <summary>
    /// When true and <see cref="TargetFile"/> is omitted, write the interface to
    /// <c>{InterfaceName}.cs</c> in the same directory as <see cref="SourceFile"/>.
    /// Default: false (same-file extract unless <see cref="TargetFile"/> is set).
    /// </summary>
    public bool SeparateFile { get; init; }

    /// <summary>
    /// Add the interface to the type's base list. Default: true.
    /// </summary>
    public bool AddInterfaceToType { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
