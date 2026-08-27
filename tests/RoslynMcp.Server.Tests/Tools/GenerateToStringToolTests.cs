using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for GenerateToStringTool.
/// Tests tool definition and argument validation.
/// </summary>
public class GenerateToStringToolTests
{
    private readonly GenerateToStringTool _tool;

    public GenerateToStringToolTests()
    {
        // Fails loudly if workspace creation is attempted; argument validation is exercised via ExecuteAsync error paths.
        _tool = new GenerateToStringTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        // Assert
        Assert.Equal("generate_tostring", _tool.Name);
    }

    [Fact]
    public void GetDefinition_ReturnsNonEmptyDescription()
    {
        // Assert
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

        // Assert
        var requiredFields = new List<string>();
        foreach (var item in required.EnumerateArray())
        {
            requiredFields.Add(item.GetString()!);
        }

        Assert.Contains("solutionPath", requiredFields);
        Assert.Contains("sourceFile", requiredFields);
        Assert.Contains("typeName", requiredFields);
    }

    [Fact]
    public void GetDefinition_FormatEnum_IsInterpolatedOrStringBuilder()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var format = doc.RootElement.GetProperty("properties").GetProperty("format");

        Assert.Equal("string", format.GetProperty("type").GetString());
        var values = format.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("interpolated", values);
        Assert.Contains("stringbuilder", values);
        Assert.Equal(2, values.Count);
    }

    [Fact]
    public void GetDefinition_IncludePropertiesProperty_DefaultsToTrue()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var includeProperties = doc.RootElement.GetProperty("properties").GetProperty("includeProperties");

        Assert.Equal("boolean", includeProperties.GetProperty("type").GetString());
        Assert.True(includeProperties.GetProperty("default").GetBoolean());
    }

    [Fact]
    public void GetDefinition_IncludeInheritedMembersProperty_DefaultsToFalse()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var includeInheritedMembers = doc.RootElement.GetProperty("properties").GetProperty("includeInheritedMembers");

        Assert.Equal("boolean", includeInheritedMembers.GetProperty("type").GetString());
        Assert.False(includeInheritedMembers.GetProperty("default").GetBoolean());
    }

    [Fact]
    public void GetDefinition_ReplaceExistingProperty_DefaultsToFalse()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var replaceExisting = doc.RootElement.GetProperty("properties").GetProperty("replaceExisting");

        Assert.Equal("boolean", replaceExisting.GetProperty("type").GetString());
        Assert.False(replaceExisting.GetProperty("default").GetBoolean());
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

    #endregion

    #region Helper Methods

    private static string GetResultText(ToolResult result)
    {
        return result.Content.FirstOrDefault()?.Text ?? string.Empty;
    }

    #endregion
}
