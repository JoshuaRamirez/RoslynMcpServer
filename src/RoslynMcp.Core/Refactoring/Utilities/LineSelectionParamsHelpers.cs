using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared <c>allFiles</c>-or-<c>sourceFile</c>+<c>line</c> params gate used by
/// invert_if / convert_foreach_linq / convert_to_pattern_matching /
/// convert_to_interpolated_string (previously four identical private
/// <c>Validate</c> bodies).
/// </summary>
internal static class LineSelectionParamsHelpers
{
    /// <summary>
    /// AllFiles rejects <paramref name="line"/> / <paramref name="column"/>
    /// and only shape-checks an optional <paramref name="sourceFile"/>
    /// (the document filter throws SourceFileNotFound / SourceNotInWorkspace).
    /// Single-file requires an absolute existing <c>.cs</c>
    /// <paramref name="sourceFile"/> and a 1-based <paramref name="line"/>,
    /// with an optional 1-based <paramref name="column"/>.
    /// </summary>
    internal static void ValidateAllFilesOrLine(
        bool allFiles,
        string? sourceFile,
        int? line,
        int? column)
    {
        if (allFiles)
        {
            if (line.HasValue || column.HasValue)
            {
                throw new RefactoringException(
                    ErrorCodes.MissingRequiredParam,
                    "allFiles cannot be combined with line or column.");
            }

            // When processing all files, sourceFile is optional; validate path
            // shape only (filter throws SourceFileNotFound / SourceNotInWorkspace).
            if (!string.IsNullOrWhiteSpace(sourceFile))
                SourceFilePathHelpers.ValidateSourceFilePath(sourceFile!);

            return;
        }

        if (string.IsNullOrWhiteSpace(sourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        SourceFilePathHelpers.ValidateSourceFilePath(sourceFile);

        if (!line.HasValue)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "line is required.");

        if (line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (column.HasValue && column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "column must be >= 1.");

        if (!File.Exists(sourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {sourceFile}");
    }
}
