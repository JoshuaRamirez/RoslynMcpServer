using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for MoveTypeToNamespaceTool.
/// Tests tool definition and argument validation.
/// </summary>
public class MoveTypeToNamespaceToolTests
{
    private readonly MoveTypeToNamespaceTool _tool;

    public MoveTypeToNamespaceToolTests()
    {
        // Fails loudly if workspace creation is attempted; argument validation is exercised via ExecuteAsync error paths.
        _tool = new MoveTypeToNamespaceTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        Assert.Equal("move_type_to_namespace", _tool.Name);
    }

    [Fact]
    public void GetDefinition_ReturnsNonEmptyDescription()
    {
        Assert.NotNull(_tool.Description);
        Assert.NotEmpty(_tool.Description);
        Assert.Contains("line", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("omitted-line", _tool.Description);
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
        Assert.Contains("symbolName", requiredFields);
        Assert.Contains("targetNamespace", requiredFields);
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
        Assert.True(properties.TryGetProperty("targetNamespace", out _));

        // Assert - Optional properties
        Assert.True(properties.TryGetProperty("line", out _));
        Assert.True(properties.TryGetProperty("column", out _));
        Assert.True(properties.TryGetProperty("updateFileLocation", out _));
        Assert.True(properties.TryGetProperty("preview", out _));
        Assert.False(RequiredFieldsContains(doc, "line"));
        Assert.False(RequiredFieldsContains(doc, "column"));
    }

    [Fact]
    public void GetDefinition_UpdateFileLocationProperty_DefaultsToFalse()
    {
        // Act
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var updateFileLocation = doc.RootElement.GetProperty("properties").GetProperty("updateFileLocation");

        // Assert
        Assert.Equal("boolean", updateFileLocation.GetProperty("type").GetString());
        Assert.False(updateFileLocation.GetProperty("default").GetBoolean());
    }

    [Fact]
    public void GetDefinition_HasOptionalColumn()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var properties = doc.RootElement.GetProperty("properties");
        var required = doc.RootElement.GetProperty("required");

        var requiredFields = new List<string>();
        foreach (var item in required.EnumerateArray())
            requiredFields.Add(item.GetString()!);

        Assert.True(properties.TryGetProperty("column", out var column));
        Assert.Equal("integer", column.GetProperty("type").GetString());
        Assert.Equal(1, column.GetProperty("minimum").GetInt32());
        Assert.DoesNotContain("column", requiredFields);
        var description = column.GetProperty("description").GetString();
        Assert.Contains("1-based", description);
        Assert.Contains("column", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDefinition_Description_MentionsColumn()
    {
        Assert.Contains("column", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("omitted-line", _tool.Description);
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
        // Arrange - Missing targetNamespace
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Test.cs"",
            ""symbolName"": ""MyClass""
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

    private static bool RequiredFieldsContains(JsonDocument doc, string name)
    {
        foreach (var item in doc.RootElement.GetProperty("required").EnumerateArray())
        {
            if (item.GetString() == name)
                return true;
        }

        return false;
    }

    #endregion
}
