using RoslynMcp.Contracts.Enums;

namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Location of a symbol's definition.
/// </summary>
public sealed class DefinitionLocation
{
    /// <summary>
    /// Absolute file path containing the definition.
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
    /// Simple name of the symbol.
    /// </summary>
    public required string SymbolName { get; init; }

    /// <summary>
    /// Fully qualified name.
    /// </summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>
    /// Kind of the symbol.
    /// </summary>
    public required SymbolKind Kind { get; init; }

    /// <summary>
    /// Display signature (e.g., "void Foo(int x, string y)").
    /// </summary>
    public string? Signature { get; init; }
}
