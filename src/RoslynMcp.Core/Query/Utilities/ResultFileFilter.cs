using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Validates the optional per-tool result-file filter shared by <c>go_to_definition</c>
/// (<c>definitionFile</c>), <c>find_implementations</c> (<c>implementationFile</c>),
/// <c>find_references</c> (<c>referenceFile</c>), <c>find_callers</c> (<c>callerFile</c>), and
/// <c>get_type_hierarchy</c> (<c>derivedFile</c>): an absolute path to an existing <c>.cs</c> file
/// (previously five identical inline blocks in the operations' <c>ValidateParams</c>).
/// </summary>
internal static class ResultFileFilter
{
    /// <summary>
    /// No-op when <paramref name="filePath"/> is omitted / blank (no filter). Otherwise throws
    /// <see cref="ErrorCodes.InvalidSourcePath"/> when it is not an absolute path, then
    /// <see cref="ErrorCodes.InvalidSourcePath"/> when it is not a <c>.cs</c> file, then
    /// <see cref="ErrorCodes.SourceFileNotFound"/> when it does not exist. Messages name the tool's
    /// parameter via <paramref name="paramName"/> (for example <c>"callerFile must be an absolute path."</c>).
    /// </summary>
    public static void Validate(string? filePath, string paramName)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        if (!PathResolver.IsAbsolutePath(filePath))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, $"{paramName} must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(filePath))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, $"{paramName} must be a .cs file.");

        if (!File.Exists(filePath))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {filePath}");
    }
}
