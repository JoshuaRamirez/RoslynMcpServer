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
                VersionText = Path.GetFileName(Path.TrimEndingDirectorySeparator(path))
            })
            .Where(candidate => TryParseSdkVersion(candidate.VersionText, out _))
            .Select(candidate => new
            {
                candidate.Path,
                candidate.VersionText,
                Version = ParseSdkVersion(candidate.VersionText),
                IsPrerelease = candidate.VersionText!.Contains('-')
            })
            .OrderByDescending(candidate => candidate.Version)
            .ThenBy(candidate => candidate.IsPrerelease)
            .ThenByDescending(candidate => candidate.VersionText, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new DotNetSdkInfo(candidate.VersionText!, candidate.Path))
            .FirstOrDefault();
    }

    private static IEnumerable<string> FindFromKnownRoots()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)"),
            GetDotNetHostDirectory(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"),
            "/usr/local/share/dotnet",
            "/usr/share/dotnet"
        };

        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            var sdkRoot = Path.Combine(root!, "sdk");
            if (!Directory.Exists(sdkRoot))
            {
                continue;
            }

            foreach (var sdkPath in Directory.EnumerateDirectories(sdkRoot))
            {
                yield return sdkPath;
            }
        }
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

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit((int)DotNetCommandTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return Array.Empty<string>();
            }

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

    private static string? GetDotNetHostDirectory()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(hostPath) ? null : Path.GetDirectoryName(hostPath);
    }

    private static Version ParseSdkVersion(string? version)
    {
        TryParseSdkVersion(version, out var parsed);
        return parsed!;
    }

    private static bool TryParseSdkVersion(string? version, out Version? parsed)
    {
        var numericVersion = version?.Split('-', 2)[0];
        return Version.TryParse(numericVersion, out parsed);
    }
}
