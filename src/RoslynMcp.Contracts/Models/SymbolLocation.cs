namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Represents a location in source code.
/// </summary>
public sealed class SymbolLocation
{
    /// <summary>
    /// Absolute path to the file.
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
