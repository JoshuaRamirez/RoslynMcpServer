using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Tools;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

public class DiagnoseToolTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsReadyWithWarnings_WhenWorkspaceLoadsWithSkippedFailures()
    {
        using var workspace = MSBuildWorkspace.Create();
        var solution = workspace.CurrentSolution
            .AddProject(ProjectId.CreateNewId(), "TestProject", "TestProject", LanguageNames.CSharp);

        using var context = new WorkspaceContext(
            workspace,
            solution,
            @"D:\repo\Test.sln",
            loadWarnings:
            [
                "Cannot open project 'D:\\repo\\docker-compose.dcproj' because the file extension '.dcproj' is not associated with a language."
            ]);

        var tool = new DiagnoseTool(new FakeWorkspaceProvider(context));
        var args = JsonDocument.Parse(@"{""solutionPath"":""D:\\repo\\Test.sln""}").RootElement;

        var result = await tool.ExecuteAsync(args);
        Assert.False(result.IsError);

        var payload = JsonSerializer.Deserialize<DiagnoseResult>(GetResultText(result), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(payload);
        Assert.True(payload!.Healthy);
        Assert.Equal(WorkspaceState.Ready, payload.Workspace.State);
        Assert.True(payload.Workspace.SolutionLoaded);
        Assert.Single(payload.Warnings);
        Assert.Empty(payload.Errors);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsUnhealthy_WhenWorkspaceProviderThrows()
    {
        var tool = new DiagnoseTool(new ThrowingWorkspaceProvider("Failed to load solution"));
        var args = JsonDocument.Parse(@"{""solutionPath"":""D:\\repo\\Test.sln""}").RootElement;

        var result = await tool.ExecuteAsync(args);
        Assert.False(result.IsError);

        var payload = JsonSerializer.Deserialize<DiagnoseResult>(GetResultText(result), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(payload);
        Assert.False(payload!.Healthy);
        Assert.Equal(WorkspaceState.Error, payload.Workspace.State);
        Assert.Single(payload.Errors);
    }

    private static string GetResultText(RoslynMcp.Server.Transport.ToolResult result) =>
        result.Content.FirstOrDefault()?.Text ?? string.Empty;

    private sealed class FakeWorkspaceProvider(WorkspaceContext context) : IWorkspaceProvider
    {
        public EnvironmentDiagnostics CheckEnvironment() => new()
        {
            MsBuildFound = true,
            DotnetSdkVersion = Environment.Version.ToString(),
            MsBuildVersion = "1.0"
        };

        public Task<WorkspaceContext> CreateContextAsync(string projectOrSolutionPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(context);
    }

    private sealed class ThrowingWorkspaceProvider(string message) : IWorkspaceProvider
    {
        public EnvironmentDiagnostics CheckEnvironment() => new()
        {
            MsBuildFound = true,
            DotnetSdkVersion = Environment.Version.ToString(),
            MsBuildVersion = "1.0"
        };

        public Task<WorkspaceContext> CreateContextAsync(string projectOrSolutionPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(message);
    }
}
