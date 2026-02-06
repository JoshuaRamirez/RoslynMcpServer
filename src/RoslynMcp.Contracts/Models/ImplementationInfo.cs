using RoslynMcp.Contracts.Enums;

namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Information about a type that implements an interface or overrides a base member.
/// </summary>
public sealed class ImplementationInfo
{
    /// <summary>
    /// Name of the implementing type or member.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Fully qualified name.
    /// </summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>
    /// Kind of implementing symbol.
    /// </summary>
    public required SymbolKind Kind { get; init; }

    /// <summary>
    /// File containing the implementation.
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
}
