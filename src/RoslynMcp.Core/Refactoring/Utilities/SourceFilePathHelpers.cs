using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared <c>sourceFile</c> absolute-.cs path validation used by Extract /
/// Hierarchy / Signature / Rename / Convert / Generate / Inline / Organize /
/// Format / Encapsulate / MoveTypeToNamespace / MakeStatic / MakeNonStatic /
/// UseBaseType / RenameFileToMatchType / IntroduceParameter params gates, plus
/// Query ops and SafeDelete that previously inlined the same two
/// <see cref="PathResolver"/> checks (absolute path + <c>.cs</c> extension,
/// throwing <see cref="RefactoringException"/> with
/// <see cref="ErrorCodes.InvalidSourcePath"/>).
/// <c>MoveTypeToFile</c> keeps interleaved source/target checks so relative
/// <c>targetFile</c> still yields <c>InvalidTargetPath</c> ahead of a non-.cs
/// <c>sourceFile</c>.
/// </summary>
internal static class SourceFilePathHelpers
{
    /// <summary>
    /// Requires <paramref name="sourceFile"/> to be an absolute path ending in
    /// <c>.cs</c> (ordinal-ignore-case). Throws
    /// <see cref="ErrorCodes.InvalidSourcePath"/> with the same messages as the
    /// prior private copies ("sourceFile must be an absolute path." /
    /// "sourceFile must be a .cs file.").
    /// </summary>
    internal static void ValidateSourceFilePath(string sourceFile)
    {
        if (!PathResolver.IsAbsolutePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(sourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");
    }
}
