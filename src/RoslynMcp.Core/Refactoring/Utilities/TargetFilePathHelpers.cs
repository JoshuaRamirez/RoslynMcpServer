using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared <c>targetFile</c> absolute-.cs path validation used by ExtractBaseClass /
/// ExtractInterface optional target params gates that previously inlined the same
/// two <see cref="PathResolver"/> checks (absolute path + <c>.cs</c> extension,
/// throwing <see cref="RefactoringException"/> with
/// <see cref="ErrorCodes.InvalidTargetPath"/>).
/// <c>MoveTypeToFile</c> keeps interleaved source/target checks so relative
/// <c>targetFile</c> still yields <c>InvalidTargetPath</c> ahead of a non-.cs
/// <c>sourceFile</c>.
/// Also hosts the shared destination resolver
/// (<see cref="ResolveTargetFile"/>) and the computed-sibling existence gate
/// (<see cref="ThrowIfSiblingTargetExists"/>).
/// </summary>
internal static class TargetFilePathHelpers
{
    /// <summary>
    /// Requires <paramref name="targetFile"/> to be an absolute path ending in
    /// <c>.cs</c> (ordinal-ignore-case). Throws
    /// <see cref="ErrorCodes.InvalidTargetPath"/> with the same messages as the
    /// prior private copies ("targetFile must be an absolute path." /
    /// "targetFile must be a .cs file.").
    /// </summary>
    internal static void ValidateTargetFilePath(string targetFile)
    {
        if (!PathResolver.IsAbsolutePath(targetFile))
            throw new RefactoringException(ErrorCodes.InvalidTargetPath, "targetFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(targetFile))
            throw new RefactoringException(ErrorCodes.InvalidTargetPath, "targetFile must be a .cs file.");
    }

    /// <summary>
    /// Resolves the destination file for an extracted type (ExtractBaseClass /
    /// ExtractInterface). An explicit <paramref name="targetFile"/> always wins;
    /// inline extraction (neither <paramref name="separateFile"/> nor
    /// <paramref name="allFiles"/>) returns <paramref name="sourceFile"/>;
    /// otherwise (allFiles forces a sibling) returns
    /// <c>{directory of sourceFile}/{siblingTypeName}.cs</c>. Throws
    /// <see cref="ErrorCodes.InvalidSourcePath"/> ("sourceFile must have a
    /// parent directory.") when <paramref name="sourceFile"/> has no parent
    /// directory, matching the prior private copies.
    /// </summary>
    internal static string ResolveTargetFile(
        string? targetFile,
        string? sourceFile,
        bool separateFile,
        bool allFiles,
        string? siblingTypeName)
    {
        if (!string.IsNullOrWhiteSpace(targetFile))
            return targetFile!;

        if (!separateFile && !allFiles)
            return sourceFile!;

        var directory = Path.GetDirectoryName(sourceFile!);
        if (string.IsNullOrEmpty(directory))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSourcePath,
                "sourceFile must have a parent directory.");
        }

        return PathResolver.Combine(directory, siblingTypeName! + ".cs");
    }

    /// <summary>
    /// Rejects a computed sibling <paramref name="targetFile"/> that already
    /// exists on disk. Explicit <paramref name="explicitTargetFile"/> keeps
    /// today's path, and inline (non-<paramref name="separateFile"/>) extraction
    /// never checks. Throws <see cref="ErrorCodes.TargetFileExists"/> with the
    /// same message as the prior ExtractInterface / ExtractBaseClass private
    /// copies ("Destination file already exists: {targetFile}").
    /// </summary>
    internal static void ThrowIfSiblingTargetExists(string? explicitTargetFile, bool separateFile, string targetFile)
    {
        // Explicit targetFile keeps today's path; only the computed sibling is rejected.
        if (!string.IsNullOrWhiteSpace(explicitTargetFile) || !separateFile)
            return;

        if (!File.Exists(targetFile))
            return;

        throw new RefactoringException(
            ErrorCodes.TargetFileExists,
            $"Destination file already exists: {targetFile}");
    }
}
