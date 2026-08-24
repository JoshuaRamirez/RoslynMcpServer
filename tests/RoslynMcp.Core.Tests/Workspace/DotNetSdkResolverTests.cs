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
}
