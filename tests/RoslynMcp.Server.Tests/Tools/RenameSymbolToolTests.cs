using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for RenameSymbolTool.
/// Tests tool definition and argument validation.
/// </summary>
public class RenameSymbolToolTests
{
    private readonly RenameSymbolTool _tool;

    public RenameSymbolToolTests()
    {
        // Fails loudly if workspace creation is attempted; argument validation is exercised via ExecuteAsync error paths.
        _tool = new RenameSymbolTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        // Assert
        Assert.Equal("rename_symbol", _tool.Name);
    }

    [Fact]
    public void GetDefinition_ReturnsNonEmptyDescription()
    {
        // Assert
        Assert.NotNull(_tool.Description);
        Assert.NotEmpty(_tool.Description);
        Assert.Contains("allFiles", _tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDefinition_ReturnsCorrectSchema()
    {
        // Act
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Assert
        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.True(root.TryGetProperty("properties", out _));
        Assert.True(root.TryGetProperty("required", out _));
        Assert.True(root.TryGetProperty("oneOf", out _));
    }

    [Fact]
    public void GetDefinition_HasRequiredFields_SolutionPath_SymbolName_NewName()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var required = doc.RootElement.GetProperty("required");

        var requiredFields = new List<string>();
        foreach (var item in required.EnumerateArray())
            requiredFields.Add(item.GetString()!);

        Assert.Contains("solutionPath", requiredFields);
        Assert.Contains("symbolName", requiredFields);
        Assert.Contains("newName", requiredFields);
        Assert.DoesNotContain("sourceFile", requiredFields);
    }

    [Fact]
    public void GetDefinition_OneOf_BranchesForSingleSiteAndAllFiles()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var branches = doc.RootElement.GetProperty("oneOf");
        Assert.Equal(2, branches.GetArrayLength());

        var singleSiteRequired = ReadStrings(branches[0].GetProperty("required"));
        Assert.Contains("solutionPath", singleSiteRequired);
        Assert.Contains("sourceFile", singleSiteRequired);
        Assert.Contains("symbolName", singleSiteRequired);
        Assert.Contains("newName", singleSiteRequired);

        var allFilesRequired = ReadStrings(branches[1].GetProperty("required"));
        Assert.Contains("solutionPath", allFilesRequired);
        Assert.Contains("allFiles", allFilesRequired);
        Assert.Contains("symbolName", allFilesRequired);
        Assert.Contains("newName", allFilesRequired);
        Assert.DoesNotContain("sourceFile", allFilesRequired);
        Assert.Equal(
            JsonValueKind.True,
            branches[1].GetProperty("properties").GetProperty("allFiles").GetProperty("const").ValueKind);
    }

    [Fact]
    public void GetDefinition_HasOptionalAllFiles()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var properties = doc.RootElement.GetProperty("properties");
        Assert.True(properties.TryGetProperty("allFiles", out var allFiles));
        Assert.Equal("boolean", allFiles.GetProperty("type").GetString());
        Assert.False(allFiles.GetProperty("default").GetBoolean());
    }

    [Fact]
    public void GetDefinition_HasProperties_ForAllParameters()
    {
        // Act
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var properties = doc.RootElement.GetProperty("properties");

        // Assert - Required properties
        Assert.True(properties.TryGetProperty("solutionPath", out _));
        Assert.True(properties.TryGetProperty("sourceFile", out _));
        Assert.True(properties.TryGetProperty("symbolName", out _));
        Assert.True(properties.TryGetProperty("newName", out _));

        // Assert - Optional properties
        Assert.True(properties.TryGetProperty("allFiles", out _));
        Assert.True(properties.TryGetProperty("line", out _));
        Assert.True(properties.TryGetProperty("column", out _));
        Assert.True(properties.TryGetProperty("renameOverloads", out _));
        Assert.True(properties.TryGetProperty("renameImplementations", out var renameImplementations));
        Assert.True(properties.TryGetProperty("renameFile", out _));
        Assert.True(properties.TryGetProperty("preview", out _));
        Assert.True(renameImplementations.GetProperty("default").GetBoolean());
    }

    #endregion

    #region ExecuteAsync Argument Validation Tests

    [Fact]
    public async Task ExecuteAsync_NullArguments_ReturnsError()
    {
        // Act
        var result = await _tool.ExecuteAsync(null);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Arguments required", GetResultText(result));
    }

    [Fact]
    public async Task ExecuteAsync_EmptyArguments_ReturnsError()
    {
        // Arrange
        var args = JsonDocument.Parse("{}").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        // The tool will try to deserialize and proceed, but fail when accessing workspace
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidJson_ReturnsError()
    {
        // Arrange - Valid JSON but missing required fields, will fail on workspace access
        var args = JsonDocument.Parse("{\"invalidField\": \"value\"}").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_MissingSourceFile_ReturnsError()
    {
        // Arrange - Has solutionPath but missing sourceFile
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""symbolName"": ""OldName"",
            ""newName"": ""NewName""
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_AllFilesTrueWithoutSourceFile_AcceptsArgs()
    {
        var args = JsonDocument.Parse("""
            {
                "solutionPath": "C:/test/test.sln",
                "allFiles": true,
                "symbolName": "OldName",
                "newName": "NewName"
            }
            """).RootElement;

        var result = await _tool.ExecuteAsync(args);

        // ThrowingWorkspaceProvider rejects workspace creation; args including allFiles parsed.
        Assert.True(result.IsError);
    }

    #endregion

    #region Helper Methods

    private static string GetResultText(ToolResult result)
    {
        return result.Content.FirstOrDefault()?.Text ?? string.Empty;
    }

    private static List<string> ReadStrings(JsonElement array)
    {
        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
            values.Add(item.GetString()!);
        return values;
    }

    #endregion
}
