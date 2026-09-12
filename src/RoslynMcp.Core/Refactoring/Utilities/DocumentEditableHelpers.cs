using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared document-editability gate used by Generate / Inline / Move / Extract
/// operations (SourceGeneratedDocument, on-disk FilePath, ChangeDocument).
/// </summary>
internal static class DocumentEditableHelpers
{
    /// <summary>
    /// True when <paramref name="document"/> is not source-generated, has a
    /// non-empty FilePath that exists on disk, and
    /// <paramref name="workspace"/> can apply <see cref="ApplyChangesKind.ChangeDocument"/>.
    /// Same body as the Generate / Inline / Move / Extract copies
    /// (excludes RenameFileToMatchType, which also ORs ChangeDocumentInfo).
    /// </summary>
    internal static bool IsDocumentEditable(Document document, Microsoft.CodeAnalysis.Workspace workspace)
    {
        if (document is SourceGeneratedDocument)
            return false;

        if (string.IsNullOrWhiteSpace(document.FilePath) || !File.Exists(document.FilePath))
            return false;

        return workspace.CanApplyChange(ApplyChangesKind.ChangeDocument);
    }
}
