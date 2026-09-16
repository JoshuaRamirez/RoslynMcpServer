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
    /// Returns a stable comparison key for a path. If the path exists, the key
    /// uses the filesystem's canonical segment casing so wrong-cased aliases on
    /// case-insensitive volumes compare equal while case-distinct files on
    /// case-sensitive volumes remain different.
    /// </summary>
    public static string GetPathComparisonKey(string path)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return normalizedPath;

        if (!File.Exists(normalizedPath) && !Directory.Exists(normalizedPath))
            return normalizedPath;

        try
        {
            return TryResolveExistingPathCasing(normalizedPath, out var resolved)
                ? resolved
                : normalizedPath;
        }
        catch (IOException)
        {
            return normalizedPath;
        }
        catch (UnauthorizedAccessException)
        {
            return normalizedPath;
        }
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

    private static bool TryResolveExistingPathCasing(string normalizedPath, out string resolvedPath)
    {
        var root = Path.GetPathRoot(normalizedPath);
        if (string.IsNullOrEmpty(root))
        {
            resolvedPath = normalizedPath;
            return false;
        }

        var current = root;

        var remainder = normalizedPath[root.Length..];
        if (string.IsNullOrEmpty(remainder))
        {
            resolvedPath = root;
            return true;
        }

        // Always resolve each segment via directory enumeration. File.Exists /
        // Directory.Exists succeed for wrong-cased aliases on case-insensitive
        // volumes, so taking that fast path would preserve caller spelling and
        // split GetPathComparisonKey for the same physical file (Codex P1).
        foreach (var segment in remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string? candidate = null;
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, segment, StringComparison.Ordinal))
                {
                    candidate = entry;
                    break;
                }

                if (candidate == null &&
                    string.Equals(name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    candidate = entry;
                }
            }

            if (candidate == null)
            {
                resolvedPath = normalizedPath;
                return false;
            }

            current = candidate.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        resolvedPath = current;
        return true;
    }

    private static string EnsureTrailingSlash(string path)
    {
        if (!path.EndsWith(Path.DirectorySeparatorChar))
            return path + Path.DirectorySeparatorChar;
        return path;
    }
}
