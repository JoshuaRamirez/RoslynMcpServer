using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace RoslynMcp.Core.Tests;

/// <summary>
/// Module initializer to register MSBuild before any tests run.
/// This must run before any Microsoft.CodeAnalysis types are loaded.
/// </summary>
internal static class ModuleInitializer
{
    public static bool MsBuildAvailable { get; private set; }
    public static string? MsBuildError { get; private set; }

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            if (MSBuildLocator.IsRegistered)
            {
                MsBuildAvailable = true;
                return;
            }

            var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();

            if (instances.Length == 0)
            {
                // Try to find .NET SDK manually
                var dotnetPath = FindDotNetSdk();
                if (dotnetPath != null)
                {
                    MSBuildLocator.RegisterMSBuildPath(dotnetPath);
                    MsBuildAvailable = true;
                    return;
                }

                MsBuildError = "MSBuild not found. Install Visual Studio, Build Tools, or .NET SDK.";
                MsBuildAvailable = false;
                return;
            }

            // Prefer .NET SDK
            var preferred = instances
                .OrderByDescending(i => i.DiscoveryType == DiscoveryType.DotNetSdk)
                .ThenByDescending(i => i.Version)
                .First();

            MSBuildLocator.RegisterInstance(preferred);
            MsBuildAvailable = true;
        }
        catch (Exception ex)
        {
            MsBuildError = ex.Message;
            MsBuildAvailable = false;
        }
    }

    private static string? FindDotNetSdk()
    {
        // Check common SDK locations
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var sdkBase = Path.Combine(programFiles, "dotnet", "sdk");

        if (!Directory.Exists(sdkBase))
        {
            return null;
        }

        // Find the latest SDK version
        var sdkVersions = Directory.GetDirectories(sdkBase)
            .Select(Path.GetFileName)
            .Where(d => d != null && char.IsDigit(d[0]))
            .OrderByDescending(v => v)
            .ToList();

        if (sdkVersions.Count == 0)
        {
            return null;
        }

        var latestSdk = Path.Combine(sdkBase, sdkVersions[0]!);
        return Directory.Exists(latestSdk) ? latestSdk : null;
    }
}
