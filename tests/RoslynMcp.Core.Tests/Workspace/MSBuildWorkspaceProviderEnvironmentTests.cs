using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

public class MSBuildWorkspaceProviderEnvironmentTests
{
    [Fact]
    public void CreateEnvironmentDiagnosticsForNoInstances_ReturnsMsBuildFound_WhenSdkPathExists()
    {
        var result = MSBuildWorkspaceProvider.CreateEnvironmentDiagnosticsForNoInstances(
            @"C:\Program Files\dotnet\sdk\9.0.308",
            "9.0.16");

        Assert.True(result.MsBuildFound);
        Assert.Equal(@"C:\Program Files\dotnet\sdk\9.0.308", result.MsBuildPath);
        Assert.Equal("9.0.16", result.DotnetSdkVersion);
        Assert.Contains(@"C:\Program Files\dotnet\sdk\9.0.308", result.SearchPaths!);
    }

    [Fact]
    public void CreateEnvironmentDiagnosticsForNoInstances_ReturnsMsBuildNotFound_WhenSdkPathMissing()
    {
        var result = MSBuildWorkspaceProvider.CreateEnvironmentDiagnosticsForNoInstances(
            null,
            "9.0.16");

        Assert.False(result.MsBuildFound);
        Assert.Equal("MSBuild not found. Install Visual Studio, Build Tools, or .NET SDK.", result.ErrorMessage);
    }
}
