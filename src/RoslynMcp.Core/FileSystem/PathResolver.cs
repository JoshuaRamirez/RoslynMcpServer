namespace RoslynMcp.Core.FileSystem;

/// <summary>
/// Cross-platform path resolution utilities.
/// </summary>
public static class PathResolver
{
    /// <summary>
    /// Normalizes a path to the current platform's format.
    /// </summary>
    /// <param name="path">Path to normalize.</param>
    /// <returns>Normalized absolute path.</returns>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        // Get full path and normalize separators
        var fullPath = Path.GetFullPath(path);
        return fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Makes a path relative to a base path.
    /// </summary>
    /// <param name="basePath">Base path (directory).</param>
    /// <param name="fullPath">Full path to make relative.</param>
    /// <returns>Relative path.</returns>
    public static string MakeRelative(string basePath, string fullPath)
    {
        var baseUri = new Uri(EnsureTrailingSlash(NormalizePath(basePath)));
        var fullUri = new Uri(NormalizePath(fullPath));

        var relativeUri = baseUri.MakeRelativeUri(fullUri);
        var relativePath = Uri.UnescapeDataString(relativeUri.ToString());

        return relativePath.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Checks if a path is absolute.
    /// </summary>
    /// <param name="path">Path to check.</param>
    /// <returns>True if absolute.</returns>
    public static bool IsAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return Path.IsPathRooted(path);
    }

    /// <summary>
    /// Validates that a path is a valid C# source file path.
    /// </summary>
    /// <param name="path">Path to validate.</param>
    /// <returns>True if valid C# file path.</returns>
    public static bool IsValidCSharpFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!IsAbsolutePath(path))
            return false;

        return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates that a path is a valid solution or project path.
    /// </summary>
    /// <param name="path">Path to validate.</param>
    /// <returns>True if valid solution/project path.</returns>
    public static bool IsValidSolutionOrProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!IsAbsolutePath(path))
            return false;

        return path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the directory containing a file path.
    /// </summary>
    /// <param name="filePath">File path.</param>
    /// <returns>Directory path.</returns>
    public static string GetDirectory(string filePath)
    {
        return Path.GetDirectoryName(NormalizePath(filePath)) ?? string.Empty;
    }

    /// <summary>
    /// Combines path segments.
    /// </summary>
    /// <param name="paths">Path segments to combine.</param>
    /// <returns>Combined path.</returns>
    public static string Combine(params string[] paths)
    {
        return NormalizePath(Path.Combine(paths));
    }

    private static string EnsureTrailingSlash(string path)
    {
        if (!path.EndsWith(Path.DirectorySeparatorChar))
            return path + Path.DirectorySeparatorChar;
        return path;
    }
}
