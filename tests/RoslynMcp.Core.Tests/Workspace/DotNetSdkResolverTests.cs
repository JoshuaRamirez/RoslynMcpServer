using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

public class DotNetSdkResolverTests
{
    [Fact]
    public void TryFindLatestSdkPath_PrefersSemanticVersionOrdering()
    {
        var sdkBasePath = @"C:\dotnet\sdk";
        var sdkDirectories = new[]
        {
            Path.Combine(sdkBasePath, "9.0.308"),
            Path.Combine(sdkBasePath, "10.0.100"),
            Path.Combine(sdkBasePath, "10.0.301")
        };

        var result = DotNetSdkResolver.TryFindLatestSdkPath(sdkDirectories);

        Assert.Equal(Path.Combine(sdkBasePath, "10.0.301"), result);
    }

    [Fact]
    public void TryFindLatestSdkPath_ParsesPreviewVersionFolders()
    {
        var sdkBasePath = @"C:\dotnet\sdk";
        var sdkDirectories = new[]
        {
            Path.Combine(sdkBasePath, "9.0.308"),
            Path.Combine(sdkBasePath, "10.0.200-preview.0.26103.119"),
            Path.Combine(sdkBasePath, "10.0.100")
        };

        var result = DotNetSdkResolver.TryFindLatestSdkPath(sdkDirectories);

        Assert.Equal(Path.Combine(sdkBasePath, "10.0.200-preview.0.26103.119"), result);
    }
}
