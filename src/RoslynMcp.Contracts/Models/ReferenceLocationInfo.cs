namespace RoslynMcp.Contracts.Models;

/// <summary>
/// A single reference location for a symbol.
/// </summary>
public sealed class ReferenceLocationInfo
{
    /// <summary>
    /// Absolute file path containing the reference.
    /// </summary>
    public required string File { get; init; }

    /// <summary>
    /// 1-based line number.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// 1-based column number.
    /// </summary>
    public required int Column { get; init; }

    /// <summary>
    /// Code snippet around the reference for context.
    /// </summary>
    public string? ContextSnippet { get; init; }

    /// <summary>
    /// Whether this reference is a write (assignment) to the symbol.
    /// </summary>
    public bool IsWriteAccess { get; init; }

    /// <summary>
    /// Whether this is the symbol's definition site.
    /// </summary>
    public bool IsDefinition { get; init; }
}
