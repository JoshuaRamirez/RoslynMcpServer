using RoslynMcp.Contracts.Enums;

namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Represents a pending change for preview mode.
/// </summary>
public sealed class PendingChange
{
    /// <summary>
    /// Path to the file being changed.
    /// </summary>
    public required string File { get; init; }

    /// <summary>
    /// Type of change (Create, Modify, Delete).
    /// </summary>
    public required ChangeKind ChangeType { get; init; }

    /// <summary>
    /// Human-readable description of the change.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Unified diff for modifications.
    /// </summary>
    public string? Diff { get; init; }

    /// <summary>
    /// Full content for file creations.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Code snippet before the change (for preview).
    /// Shows a focused excerpt around the affected lines.
    /// </summary>
    public string? BeforeSnippet { get; init; }

    /// <summary>
    /// Code snippet after the change (for preview).
    /// Shows the result of applying this change.
    /// </summary>
    public string? AfterSnippet { get; init; }

    /// <summary>
    /// Starting line number in the file (1-based) where the change begins.
    /// </summary>
    public int? StartLine { get; init; }

    /// <summary>
    /// Ending line number in the file (1-based) where the change ends.
    /// </summary>
    public int? EndLine { get; init; }
}
