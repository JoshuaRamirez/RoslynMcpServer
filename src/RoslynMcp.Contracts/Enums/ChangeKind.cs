namespace RoslynMcp.Contracts.Enums;

/// <summary>
/// Represents the type of modification to a document.
/// </summary>
public enum ChangeKind
{
    /// <summary>New file created.</summary>
    Create,

    /// <summary>Existing file modified.</summary>
    Modify,

    /// <summary>File deleted (was emptied after extraction).</summary>
    Delete
}
