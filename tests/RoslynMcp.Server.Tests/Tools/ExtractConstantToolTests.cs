using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for ExtractConstantTool.
/// Tests tool definition and argument validation.
/// </summary>
public class ExtractConstantToolTests
{
    private readonly ExtractConstantTool _tool;

    public ExtractConstantToolTests()
    {
        // Fails loudly if workspace creation is attempted; argument validation is exercised via ExecuteAsync error paths.
        _tool = new ExtractConstantTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        Assert.Equal("extract_constant", _tool.Name);
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
        Assert.Contains("startLine", requiredFields);
        Assert.Contains("startColumn", requiredFields);
        Assert.Contains("endLine", requiredFields);
        Assert.Contains("endColumn", requiredFields);
        Assert.Contains("constantName", requiredFields);
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
        Assert.True(properties.TryGetProperty("startLine", out _));
        Assert.True(properties.TryGetProperty("startColumn", out _));
        Assert.True(properties.TryGetProperty("endLine", out _));
        Assert.True(properties.TryGetProperty("endColumn", out _));
        Assert.True(properties.TryGetProperty("constantName", out _));

        // Assert - Optional properties
        Assert.True(properties.TryGetProperty("visibility", out _));
        Assert.True(properties.TryGetProperty("replaceAll", out _));
        Assert.True(properties.TryGetProperty("preview", out _));
    }

    [Fact]
    public void GetDefinition_VisibilityProperty_HasEnumValuesAndDefault()
    {
        // Act
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var visibility = doc.RootElement.GetProperty("properties").GetProperty("visibility");

        // Assert
        Assert.True(visibility.TryGetProperty("enum", out var enumValues));
        var values = new List<string>();
        foreach (var v in enumValues.EnumerateArray())
        {
            values.Add(v.GetString()!);
        }
        Assert.Contains("private", values);
        Assert.Contains("protected", values);
        Assert.Contains("internal", values);
        Assert.Contains("public", values);
        Assert.Equal("private", visibility.GetProperty("default").GetString());
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
        // Arrange - Missing constantName
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Test.cs"",
            ""startLine"": 10,
            ""startColumn"": 5,
            ""endLine"": 10,
            ""endColumn"": 15
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
