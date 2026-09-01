using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for IntroduceParameterTool.
/// Tests tool definition and argument validation.
/// </summary>
public class IntroduceParameterToolTests
{
    private readonly IntroduceParameterTool _tool;

    public IntroduceParameterToolTests()
    {
        // Fails loudly if workspace creation is attempted; argument validation is exercised via ExecuteAsync error paths.
        _tool = new IntroduceParameterTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        // Assert
        Assert.Equal("introduce_parameter", _tool.Name);
    }

    [Fact]
    public void GetDefinition_ReturnsNonEmptyDescription()
    {
        // Assert
        Assert.NotNull(_tool.Description);
        Assert.NotEmpty(_tool.Description);
        Assert.Contains("column", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", _tool.Description, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("variableName", requiredFields);
        Assert.Contains("line", requiredFields);
        Assert.DoesNotContain("column", requiredFields);
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
