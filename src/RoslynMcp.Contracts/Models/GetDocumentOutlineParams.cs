namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the get_document_outline query.
/// </summary>
public sealed class GetDocumentOutlineParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Maximum number of outline nodes to return (DFS pre-order budget). Omit for no cap (return full tree).
    /// </summary>
    public int? MaxResults { get; init; }
}
