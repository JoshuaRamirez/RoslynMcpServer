namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for getting detailed information about a symbol.
/// </summary>
public sealed class GetSymbolInfoParams
{
    /// <summary>
    /// Absolute path to the source file containing the symbol.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the symbol to examine.
    /// </summary>
    public string? SymbolName { get; init; }

    /// <summary>
    /// 1-based line number for position-based resolution.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column number for position-based resolution.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Maximum number of members to return for a type symbol. Omit for no cap (return all members).
    /// Only caps <c>Members</c>; Interfaces, Parameters, Modifiers and other fields are not capped,
    /// and it has no effect on non-type symbols. Applied after <see cref="KindFilter"/> so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the kind-filtered set.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Optional member kind to restrict the <c>Members</c> list of a named type to: a single
    /// <c>SymbolKind</c> name shared with <c>search_symbols</c> / <c>get_document_outline</c>
    /// <c>kindFilter</c> (Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field,
    /// Event, Constant, Local, Parameter, Namespace; case-insensitive). Applied before
    /// <see cref="MaxResults"/>. Omit, empty, or whitespace = all members. Interfaces, Parameters,
    /// and other fields are not filtered; no effect on non-type symbols.
    /// </summary>
    public string? KindFilter { get; init; }
}
