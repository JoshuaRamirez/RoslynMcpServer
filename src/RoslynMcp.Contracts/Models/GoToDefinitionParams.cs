namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for navigating to a symbol's definition.
/// </summary>
public sealed class GoToDefinitionParams
{
    /// <summary>
    /// Absolute path to the source file containing the symbol reference.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the symbol to find the definition of.
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
}
