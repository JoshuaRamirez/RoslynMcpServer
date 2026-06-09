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

        Assert.Equal("false", properties["NuGetAudit"]);
        Assert.Equal("false", properties["TreatWarningsAsErrors"]);
    }
}
