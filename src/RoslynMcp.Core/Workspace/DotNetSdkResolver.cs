using System.Diagnostics;

namespace RoslynMcp.Core.Workspace;

internal sealed record DotNetSdkInfo(string Version, string Path);

internal static class DotNetSdkResolver
{
    private static readonly TimeSpan DotNetCommandTimeout = TimeSpan.FromSeconds(5);

    public static DotNetSdkInfo? FindLatest()
    {
        var sdkPaths = FindFromKnownRoots()
            .Concat(FindFromDotNetHost())
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

    private static IReadOnlyList<string> FindFromKnownRoots()
    {
        var roots = new List<string?>
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)"),
            GetDotNetHostDirectory(),
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

    private static IReadOnlyList<string> FindFromDotNetHost()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                Arguments = "--list-sdks",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process == null)
            {
                return Array.Empty<string>();
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeoutCts = new CancellationTokenSource(DotNetCommandTimeout);

            try
            {
                process.WaitForExitAsync(timeoutCts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                process.WaitForExitAsync().GetAwaiter().GetResult();
                _ = outputTask.GetAwaiter().GetResult();
                _ = errorTask.GetAwaiter().GetResult();
                return Array.Empty<string>();
            }

            var output = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            var sdkPaths = new List<string>();
            foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                var separatorIndex = line.IndexOf(" [", StringComparison.Ordinal);
                if (separatorIndex <= 0 || !line.EndsWith(']'))
                {
                    continue;
                }

                var version = line[..separatorIndex].Trim();
                var sdkRoot = line[(separatorIndex + 2)..^1];
                var sdkPath = Path.Combine(sdkRoot, version);
                if (Directory.Exists(sdkPath))
                {
                    sdkPaths.Add(sdkPath);
                }
            }

            return sdkPaths;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Array.Empty<string>();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
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
