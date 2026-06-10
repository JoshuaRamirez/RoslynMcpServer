using Microsoft.CodeAnalysis;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

public class WorkspaceLoadFailureEvaluatorTests
{
    [Fact]
    public void Evaluate_ReturnsError_WhenSkipDisabledAndFailureIsUnrecognizedProject()
    {
        var result = WorkspaceLoadFailureEvaluator.Evaluate(
            [new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, "Cannot open project 'D:\\repo\\docker-compose.dcproj' because the file extension '.dcproj' is not associated with a language.")],
            new WorkspaceLoadOptions { SkipUnrecognizedProjects = false },
            projectCount: 1);

        Assert.True(result.HasErrors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Evaluate_ReturnsWarning_WhenSkipEnabledAndOnlyUnrecognizedProjectsFailed()
    {
        var result = WorkspaceLoadFailureEvaluator.Evaluate(
            [new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, "Cannot open project 'D:\\repo\\docker-compose.dcproj' because the file extension '.dcproj' is not associated with a language.")],
            new WorkspaceLoadOptions { SkipUnrecognizedProjects = true },
            projectCount: 1);

        Assert.False(result.HasErrors);
        Assert.Single(result.Warnings);
        Assert.Equal("Cannot open project 'D:\\repo\\docker-compose.dcproj' because the file extension '.dcproj' is not associated with a language.", result.Warnings[0]);
    }

    [Fact]
    public void Evaluate_ReturnsError_WhenNoProjectsLoaded()
    {
        var result = WorkspaceLoadFailureEvaluator.Evaluate(
            [new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, "Cannot open project 'D:\\repo\\docker-compose.dcproj' because the file extension '.dcproj' is not associated with a language.")],
            new WorkspaceLoadOptions { SkipUnrecognizedProjects = true },
            projectCount: 0);

        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Evaluate_ReturnsError_WhenUnrecognizedProjectFailureAndOtherFailureAreMixed()
    {
        var result = WorkspaceLoadFailureEvaluator.Evaluate(
            [
                new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, "Cannot open project 'D:\\repo\\docker-compose.dcproj' because the file extension '.dcproj' is not associated with a language."),
                new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, "warning NU1903: Package 'Foo' has a known vulnerability.")
            ],
            new WorkspaceLoadOptions { SkipUnrecognizedProjects = true },
            projectCount: 1);

        Assert.True(result.HasErrors);
        Assert.Single(result.Warnings);
    }
}
