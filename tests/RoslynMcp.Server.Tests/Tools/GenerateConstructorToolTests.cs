using System.Text.Json;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for GenerateConstructorTool.
/// Tests tool definition and argument validation.
/// </summary>
public class GenerateConstructorToolTests
{
    private readonly GenerateConstructorTool _tool;

    public GenerateConstructorToolTests()
    {
        _tool = new GenerateConstructorTool(null!);
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        Assert.Equal("generate_constructor", _tool.Name);
    }

    [Fact]
    public void GetDefinition_ReturnsNonEmptyDescription()
    {
        Assert.NotNull(_tool.Description);
        Assert.NotEmpty(_tool.Description);
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
    }

    [Fact]
    public void GetDefinition_HasRequiredFields()
    {
        // Act
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var required = doc.RootElement.GetProperty("required");

        var requiredFields = new List<string>();
        foreach (var item in required.EnumerateArray())
        {
            requiredFields.Add(item.GetString()!);
        }

        // Assert
        Assert.Contains("solutionPath", requiredFields);
        Assert.Contains("sourceFile", requiredFields);
        Assert.Contains("typeName", requiredFields);
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
        Assert.True(properties.TryGetProperty("typeName", out _));

        // Assert - Optional properties
        Assert.True(properties.TryGetProperty("members", out _));
        Assert.True(properties.TryGetProperty("addNullChecks", out _));
        Assert.True(properties.TryGetProperty("preview", out _));
    }

    [Fact]
    public void GetDefinition_MembersProperty_IsArray()
    {
        // Act
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var members = doc.RootElement.GetProperty("properties").GetProperty("members");

        // Assert
        Assert.Equal("array", members.GetProperty("type").GetString());
        Assert.True(members.TryGetProperty("items", out var items));
        Assert.Equal("string", items.GetProperty("type").GetString());
    }

    #endregion

    #region ExecuteAsync Argument Validation Tests

    [Fact]
    public async Task ExecuteAsync_NullArguments_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(null);

        Assert.True(result.IsError);
        Assert.Contains("Arguments required", GetResultText(result));
    }

    [Fact]
    public async Task ExecuteAsync_EmptyArguments_ReturnsError()
    {
        var args = JsonDocument.Parse("{}").RootElement;

        var result = await _tool.ExecuteAsync(args);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_MissingRequiredField_ReturnsError()
    {
        // Arrange - Missing typeName
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Test.cs""
        }").RootElement;

        var result = await _tool.ExecuteAsync(args);

        Assert.True(result.IsError);
    }

    #endregion

    #region Helper Methods

    private static string GetResultText(ToolResult result)
    {
        return result.Content.FirstOrDefault()?.Text ?? string.Empty;
    }

    #endregion
}
