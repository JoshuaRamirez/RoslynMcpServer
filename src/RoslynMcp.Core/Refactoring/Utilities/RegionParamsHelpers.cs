using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared <c>sourceFile</c> + <c>startLine</c>/<c>endLine</c> (+ optional
/// <c>startColumn</c>/<c>endColumn</c>) region params gate used by
/// analyze_control_flow / analyze_data_flow (previously two identical
/// <c>Validate</c> bodies on the operation types).
/// </summary>
internal static class RegionParamsHelpers
{
    /// <summary>
    /// Requires an absolute existing <c>.cs</c> <paramref name="sourceFile"/>,
    /// 1-based <paramref name="startLine"/> &lt;= <paramref name="endLine"/>,
    /// and optional 1-based columns where <paramref name="startColumn"/>
    /// &lt;= <paramref name="endColumn"/> when both are set on the same line.
    /// Checks run in this order: sourceFile required, path shape, startLine,
    /// endLine, line order, startColumn, endColumn, same-line column order,
    /// file exists.
    /// </summary>
    internal static void ValidateRegionParams(
        string? sourceFile,
        int startLine,
        int endLine,
        int? startColumn,
        int? endColumn)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        SourceFilePathHelpers.ValidateSourceFilePath(sourceFile);

        if (startLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "startLine must be >= 1.");

        if (endLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "endLine must be >= 1.");

        if (startLine > endLine)
            throw new RefactoringException(ErrorCodes.InvalidRegion, "startLine must be <= endLine.");

        if (startColumn.HasValue && startColumn.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "startColumn must be >= 1.");

        if (endColumn.HasValue && endColumn.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "endColumn must be >= 1.");

        if (startLine == endLine &&
            startColumn.HasValue &&
            endColumn.HasValue &&
            startColumn.Value > endColumn.Value)
        {
            throw new RefactoringException(ErrorCodes.InvalidRegion, "startColumn must be <= endColumn when both are set on the same line.");
        }

        if (!File.Exists(sourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {sourceFile}");
    }
}
