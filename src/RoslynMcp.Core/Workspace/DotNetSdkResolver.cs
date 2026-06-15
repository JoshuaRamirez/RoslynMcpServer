namespace RoslynMcp.Core.Workspace;

internal static class DotNetSdkResolver
{
    public static string? FindLatestSdkPath(string sdkBasePath)
    {
        if (!Directory.Exists(sdkBasePath))
        {
            return null;
        }

        return TryFindLatestSdkPath(Directory.GetDirectories(sdkBasePath));
    }

    internal static string? TryFindLatestSdkPath(IEnumerable<string> sdkDirectories)
    {
        return sdkDirectories
            .Select(CreateCandidate)
            .Where(candidate => candidate is not null)
            .OrderByDescending(candidate => candidate!.NumericVersion)
            .ThenBy(candidate => candidate!.IsPrerelease)
            .ThenByDescending(candidate => candidate!.OriginalVersion, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate!.Path)
            .FirstOrDefault();
    }

    private static SdkCandidate? CreateCandidate(string sdkDirectory)
    {
        var directoryName = Path.GetFileName(sdkDirectory);
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            return null;
        }

        var versionParts = directoryName.Split('-', 2, StringSplitOptions.TrimEntries);
        if (!Version.TryParse(versionParts[0], out var numericVersion))
        {
            return null;
        }

        return new SdkCandidate(
            sdkDirectory,
            directoryName,
            numericVersion,
            versionParts.Length > 1);
    }

    private sealed record SdkCandidate(
        string Path,
        string OriginalVersion,
        Version NumericVersion,
        bool IsPrerelease);
}
