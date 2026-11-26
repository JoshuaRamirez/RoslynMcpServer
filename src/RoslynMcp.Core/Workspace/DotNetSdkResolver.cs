namespace RoslynMcp.Core.Workspace;

internal sealed record DotNetSdkInfo(string Version, string Path);

/// <summary>
/// Finds the installed .NET SDKs by reading the SDK folders of each dotnet root (DOTNET_ROOT, the host the server runs
/// under, the dotnet found on PATH with its links followed, ~/.dotnet and the standard install folders). It starts no
/// program: `dotnet --list-sdks` reads the same folders, and starting it forked the long-lived server and handed the
/// child its whole environment on every workspace load (his rule, 26 Nov 2026: no clone-first starts; read directly).
/// </summary>
internal static class DotNetSdkResolver
{
    public static DotNetSdkInfo? FindLatest()
    {
        var sdkPaths = FindFromKnownRoots()
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return SelectLatest(sdkPaths);
    }

    internal static DotNetSdkInfo? SelectLatest(IEnumerable<string> sdkPaths)
    {
        return sdkPaths
            .Select(path => new
            {
                Path = path,
                VersionText = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
            })
            .Select(candidate => new
            {
                candidate.Path,
                candidate.VersionText,
                Version = DotNetSdkVersion.TryParse(candidate.VersionText, out var version)
                    ? version
                    : null
            })
            .Where(candidate => candidate.Version != null)
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => new
            {
                candidate.Path,
                candidate.VersionText,
                candidate.Version
            })
            .Select(candidate => new DotNetSdkInfo(candidate.VersionText!, candidate.Path))
            .FirstOrDefault();
    }

    internal static IReadOnlyList<string> FindFromKnownRoots()
    {
        var roots = new List<string?>
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)"),
            GetDotNetHostDirectory(),
            GetDotNetOnPathDirectory(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"),
            "/usr/local/share/dotnet",
            "/usr/share/dotnet"
        };

        if (OperatingSystem.IsWindows())
        {
            AddProgramFilesRoot(roots, Environment.SpecialFolder.ProgramFiles);
            AddProgramFilesRoot(roots, Environment.SpecialFolder.ProgramFilesX86);
        }

        var sdkPaths = new List<string>();
        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            var sdkRoot = Path.Combine(root!, "sdk");
            if (!Directory.Exists(sdkRoot))
            {
                continue;
            }

            try
            {
                sdkPaths.AddRange(Directory.EnumerateDirectories(sdkRoot));
            }
            catch (IOException)
            {
                // Continue probing other configured SDK roots.
            }
            catch (UnauthorizedAccessException)
            {
                // Continue probing other configured SDK roots.
            }
            catch (System.Security.SecurityException)
            {
                // Continue probing other configured SDK roots.
            }
        }

        return sdkPaths;
    }

    /// <summary>The folder of the dotnet found on PATH, its links followed (Homebrew's dotnet is a link into its root).</summary>
    internal static string? GetDotNetOnPathDirectory()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = new FileInfo(Path.Combine(folder, name));
                if (!candidate.Exists)
                {
                    continue;
                }

                var target = candidate.LinkTarget is null ? candidate : candidate.ResolveLinkTarget(returnFinalTarget: true) as FileInfo ?? candidate;
                return target.DirectoryName;
            }
            catch (IOException)
            {
                // a broken link or unreadable folder: try the next one
            }
            catch (UnauthorizedAccessException)
            {
                // try the next one
            }
        }

        return null;
    }

    private static void AddProgramFilesRoot(
        ICollection<string?> roots,
        Environment.SpecialFolder specialFolder)
    {
        var programFiles = Environment.GetFolderPath(specialFolder);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            roots.Add(Path.Combine(programFiles, "dotnet"));
        }
    }

    private static string? GetDotNetHostDirectory()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(hostPath) ? null : Path.GetDirectoryName(hostPath);
    }

    private sealed class DotNetSdkVersion : IComparable<DotNetSdkVersion>
    {
        private readonly Version _numericVersion;
        private readonly string[] _prereleaseIdentifiers;

        private DotNetSdkVersion(Version numericVersion, string[] prereleaseIdentifiers)
        {
            _numericVersion = numericVersion;
            _prereleaseIdentifiers = prereleaseIdentifiers;
        }

        public static bool TryParse(string? value, out DotNetSdkVersion? version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var versionParts = value.Split('-', 2);
            if (!Version.TryParse(versionParts[0], out var numericVersion))
            {
                return false;
            }

            var prereleaseIdentifiers = versionParts.Length == 1
                ? Array.Empty<string>()
                : versionParts[1].Split('.', StringSplitOptions.RemoveEmptyEntries);
            version = new DotNetSdkVersion(numericVersion, prereleaseIdentifiers);
            return true;
        }

        public int CompareTo(DotNetSdkVersion? other)
        {
            if (other == null)
            {
                return 1;
            }

            var numericComparison = _numericVersion.CompareTo(other._numericVersion);
            if (numericComparison != 0)
            {
                return numericComparison;
            }

            if (_prereleaseIdentifiers.Length == 0 || other._prereleaseIdentifiers.Length == 0)
            {
                return other._prereleaseIdentifiers.Length.CompareTo(_prereleaseIdentifiers.Length);
            }

            for (var index = 0;
                 index < Math.Min(_prereleaseIdentifiers.Length, other._prereleaseIdentifiers.Length);
                 index++)
            {
                var identifierComparison = CompareIdentifier(
                    _prereleaseIdentifiers[index],
                    other._prereleaseIdentifiers[index]);
                if (identifierComparison != 0)
                {
                    return identifierComparison;
                }
            }

            return _prereleaseIdentifiers.Length.CompareTo(other._prereleaseIdentifiers.Length);
        }

        private static int CompareIdentifier(string left, string right)
        {
            var leftIsNumeric = left.All(char.IsAsciiDigit);
            var rightIsNumeric = right.All(char.IsAsciiDigit);

            if (leftIsNumeric && rightIsNumeric)
            {
                var normalizedLeft = left.TrimStart('0');
                var normalizedRight = right.TrimStart('0');
                normalizedLeft = normalizedLeft.Length == 0 ? "0" : normalizedLeft;
                normalizedRight = normalizedRight.Length == 0 ? "0" : normalizedRight;

                var lengthComparison = normalizedLeft.Length.CompareTo(normalizedRight.Length);
                return lengthComparison != 0
                    ? lengthComparison
                    : string.Compare(normalizedLeft, normalizedRight, StringComparison.Ordinal);
            }

            if (leftIsNumeric != rightIsNumeric)
            {
                return leftIsNumeric ? -1 : 1;
            }

            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
