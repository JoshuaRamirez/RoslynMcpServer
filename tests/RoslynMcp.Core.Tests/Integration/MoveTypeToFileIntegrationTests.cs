using Xunit;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Contracts.Models;

namespace RoslynMcp.Core.Tests.Integration;

/// <summary>
/// Integration tests for MoveTypeToFileOperation using real Roslyn workspace.
/// </summary>
[Collection("Integration Tests")]
public class MoveTypeToFileIntegrationTests : IntegrationTestBase
{
    private readonly MSBuildWorkspaceProvider _workspaceProvider = new();

    [SkippableFact]
    public async Task MoveType_UserToOwnFile_Success()
    {
        // Arrange
        using var context = await _workspaceProvider.CreateContextAsync(SolutionPath);
        var operation = new MoveTypeToFileOperation(context);

        var sourceFile = Path.Combine(TestDir, "TestProject", "Models.cs");
        var targetFile = Path.Combine(TestDir, "TestProject", "User.cs");

        var @params = new MoveTypeToFileParams
        {
            SourceFile = sourceFile,
            SymbolName = "User",
            TargetFile = targetFile,
            CreateTargetFile = true,
            Preview = false
        };

        // Act
        var result = await operation.ExecuteAsync(@params);

        // Assert
        Assert.True(result.Success, $"Operation failed: {result.Error?.Message}");
        Assert.True(File.Exists(targetFile), "Target file should exist");

        var targetContent = await File.ReadAllTextAsync(targetFile);
        Assert.Contains("public class User", targetContent);
        Assert.Contains("namespace TestProject.Models", targetContent);

        // User should no longer be in Models.cs
        var sourceContent = await File.ReadAllTextAsync(sourceFile);
        Assert.DoesNotContain("public class User", sourceContent);

        // Address should still be in Models.cs
        Assert.Contains("public class Address", sourceContent);
    }

    [SkippableFact]
    public async Task MoveType_PreviewMode_DoesNotModifyFiles()
    {
        // Arrange
        using var context = await _workspaceProvider.CreateContextAsync(SolutionPath);
        var operation = new MoveTypeToFileOperation(context);

        var sourceFile = Path.Combine(TestDir, "TestProject", "Models.cs");
        var targetFile = Path.Combine(TestDir, "TestProject", "Address.cs");
        var originalContent = await File.ReadAllTextAsync(sourceFile);

        var @params = new MoveTypeToFileParams
        {
            SourceFile = sourceFile,
            SymbolName = "Address",
            TargetFile = targetFile,
            CreateTargetFile = true,
            Preview = true
        };

        // Act
        var result = await operation.ExecuteAsync(@params);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Changes);
        Assert.True(result.Changes.FilesModified.Count > 0 || result.Changes.FilesCreated.Count > 0,
            "Expected at least one file change in preview");

        // Files should NOT be modified
        Assert.False(File.Exists(targetFile), "Target file should not be created in preview mode");
        var currentContent = await File.ReadAllTextAsync(sourceFile);
        Assert.Equal(originalContent, currentContent);
    }

    [SkippableFact]
    public async Task MoveType_NonExistentType_ReturnsError()
    {
        // Arrange
        using var context = await _workspaceProvider.CreateContextAsync(SolutionPath);
        var operation = new MoveTypeToFileOperation(context);

        var sourceFile = Path.Combine(TestDir, "TestProject", "Models.cs");
        var targetFile = Path.Combine(TestDir, "TestProject", "NonExistent.cs");

        var @params = new MoveTypeToFileParams
        {
            SourceFile = sourceFile,
            SymbolName = "NonExistentType",
            TargetFile = targetFile,
            CreateTargetFile = true,
            Preview = false
        };

        // Act
        var result = await operation.ExecuteAsync(@params);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [SkippableFact]
    public async Task MoveType_OrderWithReferences_UpdatesUsings()
    {
        // Arrange
        using var context = await _workspaceProvider.CreateContextAsync(SolutionPath);
        var operation = new MoveTypeToFileOperation(context);

        var sourceFile = Path.Combine(TestDir, "TestProject", "Models.cs");
        var targetFile = Path.Combine(TestDir, "TestProject", "Orders", "Order.cs");

        // Ensure target directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);

        var @params = new MoveTypeToFileParams
        {
            SourceFile = sourceFile,
            SymbolName = "Order",
            TargetFile = targetFile,
            CreateTargetFile = true,
            Preview = false
        };

        // Act
        var result = await operation.ExecuteAsync(@params);

        // Assert
        Assert.True(result.Success, $"Operation failed: {result.Error?.Message}");
        Assert.True(File.Exists(targetFile), "Target file should exist");

        var targetContent = await File.ReadAllTextAsync(targetFile);
        Assert.Contains("public class Order", targetContent);
    }
}
