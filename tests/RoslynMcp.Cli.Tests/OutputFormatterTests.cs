using System.Text.Json;
using RoslynMcp.Cli;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using Xunit;

namespace RoslynMcp.Cli.Tests;

public class OutputFormatterTests
{
    [Fact]
    public void FormatJson_RefactoringResult_ProducesValidJson()
    {
        var result = RefactoringResult.Succeeded(
            Guid.NewGuid(),
            new FileChanges
            {
                FilesModified = ["Foo.cs", "Bar.cs"],
                FilesCreated = ["Baz.cs"],
                FilesDeleted = []
            },
            null,
            referencesUpdated: 5,
            executionTimeMs: 123);

        var json = OutputFormatter.FormatJson(result);
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void FormatJson_FailedResult_IncludesError()
    {
        var result = RefactoringResult.Failed(
            Guid.NewGuid(),
            RefactoringError.Create("TEST_ERROR", "Something went wrong"));

        var json = OutputFormatter.FormatJson(result);
        Assert.Contains("TEST_ERROR", json);
        Assert.Contains("Something went wrong", json);
    }

    [Fact]
    public void FormatText_SuccessfulRefactoring_StartsWithOK()
    {
        var result = RefactoringResult.Succeeded(
            Guid.NewGuid(),
            new FileChanges
            {
                FilesModified = ["Foo.cs"],
                FilesCreated = [],
                FilesDeleted = []
            },
            null, 0, 50);

        var text = OutputFormatter.FormatText(result);
        Assert.StartsWith("OK", text);
        Assert.Contains("Duration: 50ms", text);
    }

    [Fact]
    public void FormatText_FailedRefactoring_StartsWithFailed()
    {
        var result = RefactoringResult.Failed(
            Guid.NewGuid(),
            RefactoringError.Create("ERR", "Broken"));

        var text = OutputFormatter.FormatText(result);
        Assert.StartsWith("FAILED", text);
        Assert.Contains("[ERR] Broken", text);
    }

    [Fact]
    public void FormatText_PreviewResult_ShowsPreviewMode()
    {
        var result = RefactoringResult.PreviewResult(
            Guid.NewGuid(),
            [new PendingChange { File = "Foo.cs", ChangeType = Contracts.Enums.ChangeKind.Modify, Description = "Test change" }]);

        var text = OutputFormatter.FormatText(result);
        Assert.Contains("Preview", text);
    }

    [Fact]
    public void FormatJson_QueryResult_ProducesValidJson()
    {
        var result = QueryResult<FindReferencesResult>.Succeeded(
            Guid.NewGuid(),
            new FindReferencesResult
            {
                SymbolName = "Foo",
                FullyQualifiedName = "Namespace.Foo",
                References = [],
                TotalCount = 0
            },
            100);

        var json = OutputFormatter.FormatJson(result);
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void FormatText_DiagnoseResult_ShowsHealthy()
    {
        var result = new DiagnoseResult
        {
            Healthy = true,
            Components = new ComponentStatus
            {
                RoslynAvailable = true,
                RoslynVersion = "4.8.0",
                MsBuildFound = true,
                MsBuildVersion = "17.8",
                DotnetSdkAvailable = true,
                DotnetSdkVersion = "9.0.100"
            },
            Workspace = new WorkspaceStatus
            {
                State = Contracts.Enums.WorkspaceState.Unloaded,
                SolutionLoaded = false
            },
            Capabilities = ["diagnose"],
            Errors = [],
            Warnings = []
        };

        var text = OutputFormatter.FormatText(result);
        Assert.StartsWith("HEALTHY", text);
        Assert.Contains("Roslyn: available", text);
        Assert.Contains("MSBuild: found", text);
    }

    [Fact]
    public void FormatJson_EnumValues_SerializeAsStrings()
    {
        var result = new DiagnoseResult
        {
            Healthy = true,
            Components = new ComponentStatus
            {
                RoslynAvailable = true,
                MsBuildFound = true,
                DotnetSdkAvailable = true
            },
            Workspace = new WorkspaceStatus
            {
                State = Contracts.Enums.WorkspaceState.Ready,
                SolutionLoaded = true,
                SolutionPath = "Test.sln",
                ProjectCount = 1,
                DocumentCount = 1
            },
            Capabilities = [],
            Errors = [],
            Warnings = []
        };

        var json = OutputFormatter.FormatJson(result);
        // Enum should serialize as "Ready", not as integer 2
        Assert.Contains("\"Ready\"", json);
        Assert.DoesNotContain("\"state\": 2", json);
    }
}
