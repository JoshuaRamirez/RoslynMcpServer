using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Validates and applies the optional <c>projectPath</c> scope shared by <c>get_diagnostics</c>,
/// <c>search_symbols</c>, <c>find_references</c>, <c>find_callers</c>, and <c>find_implementations</c>:
/// an absolute path to a <c>.csproj</c> in the loaded solution.
/// </summary>
internal static class ProjectPathFilter
{
    /// <summary>
    /// Throws <see cref="ErrorCodes.InvalidSourcePath"/> when <paramref name="projectPath"/> is set but is
    /// not a fully qualified path or does not end in <c>.csproj</c>. Omitted / blank is valid (no scope).
    /// </summary>
    public static void Validate(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return;

        // Path.IsPathFullyQualified also rejects Windows drive-relative ("C:Lib.csproj") and
        // root-relative ("\\Lib.csproj") forms, which IsAbsolutePath (Path.IsPathRooted) accepts but
        // GetPathComparisonKey would then resolve against the process's current drive/directory.
        if (!PathResolver.IsAbsolutePath(projectPath) || !Path.IsPathFullyQualified(projectPath))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "projectPath must be an absolute path.");

        if (!projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "projectPath must be a .csproj file.");
    }

    /// <summary>
    /// Returns every project in <paramref name="solution"/> when <paramref name="projectPath"/> is omitted;
    /// otherwise only the projects whose <see cref="Project.FilePath"/> equals it by
    /// <see cref="PathResolver.GetPathComparisonKey"/> (ordinal on the filesystem-canonical path, so
    /// wrong-cased aliases match on case-insensitive volumes while case-distinct projects stay
    /// distinct on case-sensitive ones), which includes every target-framework variant of a
    /// multi-targeted project. Throws
    /// <see cref="ErrorCodes.SourceNotInWorkspace"/> when no project matches.
    /// </summary>
    public static IReadOnlyList<Project> SelectProjects(Solution solution, string? projectPath)
    {
        var projects = solution.Projects.ToList();
        if (string.IsNullOrWhiteSpace(projectPath))
            return projects;

        var targetKey = PathResolver.GetPathComparisonKey(projectPath);
        var matches = projects
            .Where(project => !string.IsNullOrWhiteSpace(project.FilePath) &&
                string.Equals(PathResolver.GetPathComparisonKey(project.FilePath), targetKey, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0)
            throw new RefactoringException(ErrorCodes.SourceNotInWorkspace, $"Project not found in workspace: {projectPath}");

        return matches;
    }
}
