using System.Text.Json;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Tools;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

public class DiagnoseToolTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsCapabilitiesFromRegistry()
    {
        var capabilities = new[] { "diagnose", "rename_symbol", "search_symbols" };
        var tool = new DiagnoseTool(
            new HealthyWorkspaceProvider(),
            () => capabilities);

        var result = await tool.ExecuteAsync(null);
        var json = result.Content.Single().Text;
        Assert.NotNull(json);
        using var document = JsonDocument.Parse(json);
        var reportedCapabilities = document.RootElement
            .GetProperty("capabilities")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.False(result.IsError);
        Assert.Equal(capabilities, reportedCapabilities);
    }

    private sealed class HealthyWorkspaceProvider : IWorkspaceProvider
    {
        public Task<WorkspaceContext> CreateContextAsync(
            string projectOrSolutionPath,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public EnvironmentDiagnostics CheckEnvironment()
        {
            return new EnvironmentDiagnostics
            {
                MsBuildFound = true,
                MsBuildVersion = "17.0",
                DotnetSdkVersion = "10.0.100"
            };
        }
    }
}
