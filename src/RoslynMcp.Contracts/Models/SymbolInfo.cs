using RoslynMcp.Contracts.Enums;

namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Information about a symbol that was moved.
/// </summary>
public sealed class SymbolInfo
{
    /// <summary>
    /// Simple name of the symbol.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Fully qualified name including namespace.
    /// </summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>
    /// Kind of symbol (Class, Struct, etc.).
    /// </summary>
    public required SymbolKind Kind { get; init; }

    /// <summary>
    /// Previous location before the move.
    /// </summary>
    public SymbolLocation? PreviousLocation { get; init; }

    /// <summary>
    /// New location after the move.
    /// </summary>
    public SymbolLocation? NewLocation { get; init; }

    /// <summary>
    /// Previous namespace (for namespace moves).
    /// </summary>
    public string? PreviousNamespace { get; init; }

    /// <summary>
    /// New namespace (for namespace moves).
    /// </summary>
    public string? NewNamespace { get; init; }
}
