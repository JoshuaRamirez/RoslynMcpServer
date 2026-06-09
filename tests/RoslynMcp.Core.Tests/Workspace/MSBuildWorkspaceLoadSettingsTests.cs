using Microsoft.CodeAnalysis;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

public class MSBuildWorkspaceLoadSettingsTests
{
    [Fact]
    public void BuildGlobalProperties_MergesKnownAndConfiguredEnvironmentProperties()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NuGetAudit"] = "false",
            ["TreatWarningsAsErrors"] = "false",
            ["WarningsNotAsErrors"] = "CS1591"
        };

        var properties = MSBuildWorkspaceLoadSettings.BuildGlobalProperties(
            name => environment.TryGetValue(name, out var value) ? value : null,
            []);

        Assert.Equal("true", properties["CheckForSystemRuntimeDependency"]);
        Assert.Equal("true", properties["DesignTimeBuild"]);
        Assert.Equal("true", properties["BuildingInsideVisualStudio"]);
        Assert.Equal("false", properties["NuGetAudit"]);
        Assert.Equal("false", properties["TreatWarningsAsErrors"]);
        Assert.Equal("CS1591", properties["WarningsNotAsErrors"]);
    }

    [Fact]
    public void GetFatalDiagnostics_IgnoresAuditOnlyFailures()
    {
        var diagnostics = new[]
        {
            new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, "error NU1903: package has a known vulnerability")
        };

        var fatalDiagnostics = MSBuildWorkspaceLoadSettings.GetFatalDiagnostics(diagnostics);

        Assert.Empty(fatalDiagnostics);
    }

    [Fact]
    public void GetFatalDiagnostics_KeepsRealFailures()
    {
        var diagnostics = new[]
        {
            new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, "error NU1902: package has a known vulnerability"),
            new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, "The SDK 'Microsoft.NET.Sdk' specified could not be found.")
        };

        var fatalDiagnostics = MSBuildWorkspaceLoadSettings.GetFatalDiagnostics(diagnostics);

        var fatalDiagnostic = Assert.Single(fatalDiagnostics);
        Assert.Contains("Microsoft.NET.Sdk", fatalDiagnostic.Message);
    }

    [Fact]
    public void BuildGlobalProperties_UsesStandardCommandLineProperties()
    {
        var properties = MSBuildWorkspaceLoadSettings.BuildGlobalProperties(
            _ => null,
            ["-p:NuGetAudit=false", "/property:WarningsNotAsErrors=NU1901;NU1902;NU1903;NU1904", "-p:TreatWarningsAsErrors=false"]);

        Assert.Equal("false", properties["NuGetAudit"]);
        Assert.Equal("false", properties["TreatWarningsAsErrors"]);
        Assert.Equal("NU1901;NU1902;NU1903;NU1904", properties["WarningsNotAsErrors"]);
    }

    [Fact]
    public void BuildGlobalProperties_CommandLinePropertiesOverrideEnvironmentProperties()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NuGetAudit"] = "true",
            ["WarningsNotAsErrors"] = "CS1591"
        };

        var properties = MSBuildWorkspaceLoadSettings.BuildGlobalProperties(
            name => environment.TryGetValue(name, out var value) ? value : null,
            ["-p:NuGetAudit=false;WarningsNotAsErrors=NU1901;NU1902;NU1903;NU1904"]);

        Assert.Equal("false", properties["NuGetAudit"]);
        Assert.Equal("NU1901;NU1902;NU1903;NU1904", properties["WarningsNotAsErrors"]);
    }
}
