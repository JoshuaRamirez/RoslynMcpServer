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
}
