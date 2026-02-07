namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the format_document tool.
/// </summary>
public sealed class FormatDocumentParams
{
    /// <summary>
    /// Absolute path to the source file to format.
    /// </summary>
    public required string SourceFile { get; init; }
}
