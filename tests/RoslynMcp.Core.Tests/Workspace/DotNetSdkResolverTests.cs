using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

public class DotNetSdkResolverTests
{
    [Fact]
    public void SelectLatest_PrefersDotNet10OverDotNet9()
    {
        var result = DotNetSdkResolver.SelectLatest(new[]
        {
            Path.Combine("dotnet", "sdk", "9.0.311"),
            Path.Combine("dotnet", "sdk", "10.0.100"),
            Path.Combine("dotnet", "sdk", "8.0.408")
        });

        Assert.NotNull(result);
        Assert.Equal("10.0.100", result.Version);
    }

    [Fact]
    public void SelectLatest_SupportsPrereleaseSdkVersions()
    {
        var result = DotNetSdkResolver.SelectLatest(new[]
        {
            Path.Combine("dotnet", "sdk", "10.0.100"),
            Path.Combine("dotnet", "sdk", "10.0.200-preview.0.26103.119")
        });

        Assert.NotNull(result);
        Assert.Equal("10.0.200-preview.0.26103.119", result.Version);
    }

    [Fact]
    public void SelectLatest_PrefersStableSdkForSameNumericVersion()
    {
        var result = DotNetSdkResolver.SelectLatest(new[]
        {
            Path.Combine("dotnet", "sdk", "10.0.200-preview.0.26103.119"),
            Path.Combine("dotnet", "sdk", "10.0.200")
        });

        Assert.NotNull(result);
        Assert.Equal("10.0.200", result.Version);
    }

    [Fact]
    public void SelectLatest_OrdersPrereleaseIdentifiersNumerically()
    {
        var result = DotNetSdkResolver.SelectLatest(new[]
        {
            Path.Combine("dotnet", "sdk", "10.0.100-preview.9"),
            Path.Combine("dotnet", "sdk", "10.0.100-preview.10")
        });

        Assert.NotNull(result);
        Assert.Equal("10.0.100-preview.10", result.Version);
    }

    [Fact]
    public void SelectLatest_IgnoresNonSdkDirectories()
    {
        var result = DotNetSdkResolver.SelectLatest(new[]
        {
            Path.Combine("dotnet", "sdk", "metadata"),
            Path.Combine("dotnet", "sdk", "9.0.311")
        });

        Assert.NotNull(result);
        Assert.Equal("9.0.311", result.Version);
    }

    [Fact]
    public void FindLatest_ReadsTheSdkFoldersAndFindsAnInstalledSdk()
    {
        // the test host itself runs on an installed SDK's runtime, so a folder read must find one
        var found = DotNetSdkResolver.FindFromKnownRoots();
        var latest = DotNetSdkResolver.FindLatest();

        Assert.NotEmpty(found);
        Assert.NotNull(latest);
        Assert.True(Directory.Exists(latest.Path));
        Assert.Contains(found, p => string.Equals(p, latest.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheDotNetOnPathIsFollowedToItsRoot()
    {
        var folder = DotNetSdkResolver.GetDotNetOnPathDirectory();
        if (folder is null)
        {
            return; // no dotnet on PATH here: nothing to follow
        }

        Assert.True(File.Exists(Path.Combine(folder, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")));
        Assert.Null(new FileInfo(Path.Combine(folder, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")).LinkTarget);
    }

    [Fact]
    public void TheResolverStartsNoProgram()
    {
        // his rule, 26 Nov 2026: no clone-first starts; the SDK folders are read directly
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "RoslynMcp.Core", "Workspace", "DotNetSdkResolver.cs"));

        Assert.DoesNotContain("Process.Start", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", source, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RoslynMcp.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("RoslynMcp.sln not found above the test folder");
    }
}
