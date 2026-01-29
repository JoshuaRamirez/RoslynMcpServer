using Xunit;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Contracts.Models;

namespace RoslynMcp.Core.Tests.Integration;

/// <summary>
/// Integration tests for MoveTypeToNamespaceOperation using real Roslyn workspace.
/// </summary>
[Collection("Integration Tests")]
public class MoveTypeToNamespaceIntegrationTests : IntegrationTestBase
{
    private readonly MSBuildWorkspaceProvider _workspaceProvider = new();

    [SkippableFact]
    public async Task MoveTypeToNamespace_User_UpdatesNamespaceAndReferences()
    {
        // Arrange
        using var context = await _workspaceProvider.CreateContextAsync(SolutionPath);
        var operation = new MoveTypeToNamespaceOperation(context);

        var sourceFile = Path.Combine(TestDir, "TestProject", "Models.cs");

        var @params = new MoveTypeToNamespaceParams
        {
            SourceFile = sourceFile,
            SymbolName = "User",
            TargetNamespace = "TestProject.Domain.Entities",
            UpdateFileLocation = false,
            Preview = false
        };

        // Act
        var result = await operation.ExecuteAsync(@params);

        // Assert
        Assert.True(result.Success, $"Operation failed: {result.Error?.Message}");

        // Check that references were updated
        var userServiceFile = Path.Combine(TestDir, "TestProject", "Services", "UserService.cs");
        var userServiceContent = await File.ReadAllTextAsync(userServiceFile);

        // Should have using for new namespace
        Assert.Contains("TestProject.Domain.Entities", userServiceContent);
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_PreviewMode_DoesNotModifyFiles()
    {
        // Arrange
        using var context = await _workspaceProvider.CreateContextAsync(SolutionPath);
        var operation = new MoveTypeToNamespaceOperation(context);

        var sourceFile = Path.Combine(TestDir, "TestProject", "Models.cs");
        var originalContent = await File.ReadAllTextAsync(sourceFile);

        var @params = new MoveTypeToNamespaceParams
        {
            SourceFile = sourceFile,
            SymbolName = "Address",
            TargetNamespace = "TestProject.Common",
            UpdateFileLocation = false,
            Preview = true
        };

        // Act
        var result = await operation.ExecuteAsync(@params);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Changes);
        Assert.True(result.Changes.FilesModified.Count > 0,
            "Expected at least one file modification in preview");

        // Source file should NOT be modified
        var currentContent = await File.ReadAllTextAsync(sourceFile);
        Assert.Equal(originalContent, currentContent);
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_NonExistentType_ReturnsError()
    {
        // Arrange
        using var context = await _workspaceProvider.CreateContextAsync(SolutionPath);
        var operation = new MoveTypeToNamespaceOperation(context);

        var sourceFile = Path.Combine(TestDir, "TestProject", "Models.cs");

        var @params = new MoveTypeToNamespaceParams
        {
            SourceFile = sourceFile,
            SymbolName = "NonExistentType",
            TargetNamespace = "TestProject.NewNamespace",
            UpdateFileLocation = false,
            Preview = false
        };

        // Act
        var result = await operation.ExecuteAsync(@params);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_SameNamespace_ReturnsError()
    {
        // Arrange
        using var context = await _workspaceProvider.CreateContextAsync(SolutionPath);
        var operation = new MoveTypeToNamespaceOperation(context);

        var sourceFile = Path.Combine(TestDir, "TestProject", "Models.cs");

        var @params = new MoveTypeToNamespaceParams
        {
            SourceFile = sourceFile,
            SymbolName = "User",
            TargetNamespace = "TestProject.Models", // Same as current
            UpdateFileLocation = false,
            Preview = false
        };

        // Act
        var result = await operation.ExecuteAsync(@params);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }
}
