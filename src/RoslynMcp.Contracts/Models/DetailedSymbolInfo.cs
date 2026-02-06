using RoslynMcp.Contracts.Enums;

namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Rich metadata about a symbol, including type hierarchy, members, and documentation.
/// </summary>
public sealed class DetailedSymbolInfo
{
    /// <summary>
    /// Simple name of the symbol.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Fully qualified name.
    /// </summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>
    /// Kind of symbol.
    /// </summary>
    public required SymbolKind Kind { get; init; }

    /// <summary>
    /// Accessibility (public, private, etc.).
    /// </summary>
    public required string Accessibility { get; init; }

    /// <summary>
    /// Modifiers (static, abstract, sealed, virtual, override, etc.).
    /// </summary>
    public required IReadOnlyList<string> Modifiers { get; init; }

    /// <summary>
    /// Containing type name (null for top-level types).
    /// </summary>
    public string? ContainingType { get; init; }

    /// <summary>
    /// Containing namespace.
    /// </summary>
    public string? ContainingNamespace { get; init; }

    /// <summary>
    /// Base type name (for classes/structs).
    /// </summary>
    public string? BaseType { get; init; }

    /// <summary>
    /// Implemented interface names.
    /// </summary>
    public IReadOnlyList<string>? Interfaces { get; init; }

    /// <summary>
    /// Member signatures (for types).
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// Display signature (e.g., method signature with parameters).
    /// </summary>
    public string? Signature { get; init; }

    /// <summary>
    /// Return type (for methods/properties).
    /// </summary>
    public string? ReturnType { get; init; }

    /// <summary>
    /// Parameter information (for methods).
    /// </summary>
    public IReadOnlyList<ParameterInfo>? Parameters { get; init; }

    /// <summary>
    /// XML documentation summary.
    /// </summary>
    public string? DocumentationSummary { get; init; }

    /// <summary>
    /// Definition location.
    /// </summary>
    public SymbolLocation? Location { get; init; }
}

/// <summary>
/// Information about a method parameter.
/// </summary>
public sealed class ParameterInfo
{
    /// <summary>
    /// Parameter name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Parameter type.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Whether the parameter has a default value.
    /// </summary>
    public bool HasDefaultValue { get; init; }

    /// <summary>
    /// Default value as string (null if none).
    /// </summary>
    public string? DefaultValue { get; init; }
}
