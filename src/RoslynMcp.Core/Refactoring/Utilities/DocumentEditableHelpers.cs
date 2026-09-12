using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared document-editability gates used by Generate / Inline / Move / Extract /
/// Convert / Signature / Rename / Hierarchy operations (SourceGeneratedDocument,
/// on-disk FilePath, ChangeDocument). Excludes RenameFileToMatchType, which also
/// ORs <see cref="ApplyChangesKind.ChangeDocumentInfo"/>.
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

    /// <summary>
    /// Rejects documents that cannot receive source edits (throws
    /// <see cref="ErrorCodes.DocumentNotEditable"/>). Same ChangeDocument-only
    /// gate as the 21 Convert / Extract / Generate / Hierarchy / Inline / Rename /
    /// Signature copies (excludes RenameFileToMatchType, which also ORs
    /// ChangeDocumentInfo).
    /// </summary>
    internal static void ValidateDocumentIsEditable(Document document, Microsoft.CodeAnalysis.Workspace workspace)
    {
        if (document is SourceGeneratedDocument)
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable (source-generated).");
        }

        if (string.IsNullOrWhiteSpace(document.FilePath) || !File.Exists(document.FilePath))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable.");
        }

        if (!workspace.CanApplyChange(ApplyChangesKind.ChangeDocument))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Document '{document.Name}' is not editable (workspace cannot apply changes).");
        }
    }
}
