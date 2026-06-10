using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

public class MSBuildWorkspaceProviderTests
{
    [Fact]
    public void GetGlobalProperties_UsesProviderCommandLineArgs()
    {
        var provider = new MSBuildWorkspaceProvider(
            commandLineArgs: ["-p:NuGetAudit=false", "-p:TreatWarningsAsErrors=false"]);

        var properties = provider.GetGlobalProperties(_ => null);

        Assert.Equal("true", properties["DesignTimeBuild"]);
        Assert.Equal("true", properties["CheckForSystemRuntimeDependency"]);
        Assert.Equal("true", properties["BuildingInsideVisualStudio"]);
        Assert.Equal("false", properties["NuGetAudit"]);
        Assert.Equal("false", properties["TreatWarningsAsErrors"]);
    }

    [Fact]
    public void DescribeGlobalProperties_LogsNamesWithoutValues()
    {
        var description = MSBuildWorkspaceProvider.DescribeGlobalProperties(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NuGetAudit"] = "false",
                ["SecretToken"] = "super-secret-value"
            });

        Assert.Contains("NuGetAudit", description);
        Assert.Contains("SecretToken", description);
        Assert.DoesNotContain("false", description);
        Assert.DoesNotContain("super-secret-value", description);
    }
}
