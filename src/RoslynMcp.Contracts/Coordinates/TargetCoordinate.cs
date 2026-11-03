using System.Text.Json.Serialization;

namespace RoslynMcp.Contracts.Coordinates;

/// <summary>
/// Normalization mode applied after resolving an anchor to a syntax node.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EnclosingNormalization
{
    /// <summary>Keep the exact resolved syntax node and span.</summary>
    Exact = 0,

    /// <summary>Promote the target to the innermost enclosing statement syntax node.</summary>
    EnclosingStatement = 1,

    /// <summary>Promote the target to the innermost enclosing block syntax node.</summary>
    EnclosingBlock = 2,

    /// <summary>Promote the target to the innermost enclosing member declaration syntax node.</summary>
    EnclosingMember = 3,

    /// <summary>Promote the target to the innermost enclosing type declaration syntax node.</summary>
    EnclosingType = 4
}

/// <summary>
/// Constrains which documents in a Roslyn solution are eligible for coordinate resolution or predicate evaluation.
/// </summary>
public sealed record DocumentScope
{
    /// <summary>
    /// Gets an unrestricted scope matching all C# source documents in the solution.
    /// </summary>
    public static DocumentScope All { get; } = new();

    /// <summary>
    /// Optional explicit file paths (absolute or solution-relative) to include.
    /// </summary>
    [JsonPropertyName("filePaths")]
    public IReadOnlyList<string>? FilePaths { get; init; }

    /// <summary>
    /// Optional project names (exact or wildcard glob) to include.
    /// </summary>
    [JsonPropertyName("projectNames")]
    public IReadOnlyList<string>? ProjectNames { get; init; }

    /// <summary>
    /// Optional directory or file glob patterns (e.g. <c>**/Services/**</c>, <c>src/Core/*.cs</c>) to include.
    /// </summary>
    [JsonPropertyName("directoryGlobs")]
    public IReadOnlyList<string>? DirectoryGlobs { get; init; }

    /// <summary>
    /// Creates a document scope restricted to the specified file paths.
    /// </summary>
    /// <param name="filePaths">File paths to include.</param>
    /// <returns>A configured <see cref="DocumentScope"/>.</returns>
    public static DocumentScope FromFiles(params string[] filePaths) =>
        new() { FilePaths = filePaths };

    /// <summary>
    /// Creates a document scope restricted to the specified project names.
    /// </summary>
    /// <param name="projectNames">Project names to include.</param>
    /// <returns>A configured <see cref="DocumentScope"/>.</returns>
    public static DocumentScope FromProjects(params string[] projectNames) =>
        new() { ProjectNames = projectNames };

    /// <summary>
    /// Creates a document scope restricted to the specified directory/file globs.
    /// </summary>
    /// <param name="directoryGlobs">Glob patterns to include.</param>
    /// <returns>A configured <see cref="DocumentScope"/>.</returns>
    public static DocumentScope FromGlobs(params string[] directoryGlobs) =>
        new() { DirectoryGlobs = directoryGlobs };
}

/// <summary>
/// 1-based line and column span anchor within a document.
/// </summary>
/// <param name="StartLine">1-based start line number.</param>
/// <param name="StartColumn">1-based start column number (defaults to 1).</param>
/// <param name="EndLine">Optional 1-based inclusive end line number (defaults to <paramref name="StartLine"/>).</param>
/// <param name="EndColumn">Optional 1-based inclusive end column number (defaults to <paramref name="StartColumn"/>).</param>
public sealed record LineSpanAnchor(
    [property: JsonPropertyName("startLine")] int StartLine,
    [property: JsonPropertyName("startColumn")] int StartColumn = 1,
    [property: JsonPropertyName("endLine")] int? EndLine = null,
    [property: JsonPropertyName("endColumn")] int? EndColumn = null);

/// <summary>
/// 0-based character offset and length span anchor within a document.
/// </summary>
/// <param name="Start">0-based character start offset.</param>
/// <param name="Length">Number of characters in the span.</param>
public sealed record TextSpanAnchor(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("length")] int Length);

/// <summary>
/// Identifies a syntax or symbol location within scoped documents via line span, character span,
/// hierarchical symbol path (<c>Namespace.Type.Member</c>), or preorder syntax node index.
/// </summary>
public sealed record Anchor
{
    /// <summary>
    /// Optional compact-schema discriminator kind (<c>LineSpan</c>, <c>TextSpan</c>, <c>SymbolPath</c>, <c>SyntaxIndex</c>).
    /// </summary>
    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    /// <summary>
    /// Optional 1-based line/column span anchor.
    /// </summary>
    [JsonPropertyName("lineSpan")]
    public LineSpanAnchor? LineSpan { get; init; }

    /// <summary>
    /// Optional 0-based character offset and length anchor.
    /// </summary>
    [JsonPropertyName("textSpan")]
    public TextSpanAnchor? TextSpan { get; init; }

    /// <summary>
    /// Optional dotted symbol path (<c>Namespace.Type.Member</c>, <c>Type.Member</c>, or <c>SymbolName</c>).
    /// </summary>
    [JsonPropertyName("symbolPath")]
    public string? SymbolPath { get; init; }

    /// <summary>
    /// Compact-schema alias for <see cref="SymbolPath"/> (<c>path</c>).
    /// </summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path
    {
        get => null;
        init
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                SymbolPath = value;
            }
        }
    }

    /// <summary>
    /// Optional 0-based preorder index into the document's syntax tree (filtered by <see cref="SyntaxKind"/> when set).
    /// </summary>
    [JsonPropertyName("syntaxIndex")]
    public int? SyntaxIndex { get; init; }

    /// <summary>
    /// Compact-schema alias for <see cref="SyntaxIndex"/> (<c>index</c>).
    /// </summary>
    [JsonPropertyName("index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Index
    {
        get => null;
        init
        {
            if (value.HasValue)
            {
                SyntaxIndex = value;
            }
        }
    }

    /// <summary>
    /// Optional Roslyn <c>SyntaxKind</c> name used to filter nodes before indexing with <see cref="SyntaxIndex"/>.
    /// </summary>
    [JsonPropertyName("syntaxKind")]
    public string? SyntaxKind { get; init; }

    /// <summary>
    /// Creates a 1-based line/column span anchor.
    /// </summary>
    /// <param name="startLine">1-based start line.</param>
    /// <param name="startColumn">1-based start column.</param>
    /// <param name="endLine">Optional 1-based end line.</param>
    /// <param name="endColumn">Optional 1-based end column.</param>
    /// <returns>A configured <see cref="Anchor"/>.</returns>
    public static Anchor FromLineSpan(int startLine, int startColumn = 1, int? endLine = null, int? endColumn = null) =>
        new() { LineSpan = new LineSpanAnchor(startLine, startColumn, endLine, endColumn) };

    /// <summary>
    /// Creates a 0-based character text span anchor.
    /// </summary>
    /// <param name="start">0-based start offset.</param>
    /// <param name="length">Character length.</param>
    /// <returns>A configured <see cref="Anchor"/>.</returns>
    public static Anchor FromTextSpan(int start, int length) =>
        new() { TextSpan = new TextSpanAnchor(start, length) };

    /// <summary>
    /// Creates a hierarchical symbol path anchor (<c>Namespace.Type.Member</c>).
    /// </summary>
    /// <param name="symbolPath">Dotted symbol path.</param>
    /// <returns>A configured <see cref="Anchor"/>.</returns>
    public static Anchor FromSymbolPath(string symbolPath) =>
        new() { SymbolPath = symbolPath };

    /// <summary>
    /// Creates a preorder syntax node index anchor.
    /// </summary>
    /// <param name="index">0-based node index.</param>
    /// <param name="syntaxKind">Optional syntax kind name filter.</param>
    /// <returns>A configured <see cref="Anchor"/>.</returns>
    public static Anchor FromSyntaxIndex(int index, string? syntaxKind = null) =>
        new() { SyntaxIndex = index, SyntaxKind = syntaxKind };
}

/// <summary>
/// Universal Layer 1 coordinate (<c>DocumentScope × Anchor × EnclosingNormalization</c>)
/// locating exact syntax nodes and spans across a Roslyn solution.
/// </summary>
public sealed record TargetCoordinate
{
    /// <summary>
    /// Document scope filter selecting candidate documents in the solution.
    /// </summary>
    [JsonPropertyName("scope")]
    public DocumentScope Scope { get; init; } = DocumentScope.All;

    /// <summary>
    /// Anchor selecting the target span, symbol, or syntax node inside scoped documents.
    /// </summary>
    [JsonPropertyName("anchor")]
    public Anchor Anchor { get; init; } = new();

    /// <summary>
    /// Enclosing syntax normalization applied to the resolved anchor node.
    /// </summary>
    [JsonPropertyName("normalization")]
    public EnclosingNormalization Normalization { get; init; } = EnclosingNormalization.Exact;
}
