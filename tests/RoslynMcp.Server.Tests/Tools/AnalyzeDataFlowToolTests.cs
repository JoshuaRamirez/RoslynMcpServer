using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for AnalyzeDataFlowTool.
/// Tests tool definition and argument validation.
/// </summary>
public class AnalyzeDataFlowToolTests
{
    private readonly AnalyzeDataFlowTool _tool;

    public AnalyzeDataFlowToolTests()
    {
        // Fails loudly if workspace creation is attempted; argument validation is exercised via ExecuteAsync error paths.
        _tool = new AnalyzeDataFlowTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        // Assert
        Assert.Equal("analyze_data_flow", _tool.Name);
    }

    [Fact]
    public void GetDefinition_ReturnsNonEmptyDescription()
    {
        // Assert
        Assert.NotNull(_tool.Description);
        Assert.NotEmpty(_tool.Description);
        Assert.Contains("startColumn", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("endColumn", _tool.Description, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("startLine", requiredFields);
        Assert.Contains("endLine", requiredFields);
        Assert.DoesNotContain("startColumn", requiredFields);
        Assert.DoesNotContain("endColumn", requiredFields);
    }

    [Fact]
    public void GetDefinition_HasProperties_ForAllParameters()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var properties = doc.RootElement.GetProperty("properties");

        Assert.True(properties.TryGetProperty("solutionPath", out _));
        Assert.True(properties.TryGetProperty("sourceFile", out _));
        Assert.True(properties.TryGetProperty("startLine", out _));
        Assert.True(properties.TryGetProperty("endLine", out _));
        Assert.True(properties.TryGetProperty("startColumn", out _));
        Assert.True(properties.TryGetProperty("endColumn", out _));
    }

    [Fact]
    public void GetDefinition_HasOptionalStartAndEndColumn()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var properties = doc.RootElement.GetProperty("properties");
        var required = doc.RootElement.GetProperty("required");

        var requiredFields = new List<string>();
        foreach (var item in required.EnumerateArray())
            requiredFields.Add(item.GetString()!);

        Assert.True(properties.TryGetProperty("startColumn", out var startColumn));
        Assert.Equal("integer", startColumn.GetProperty("type").GetString());
        Assert.Equal(1, startColumn.GetProperty("minimum").GetInt32());
        Assert.DoesNotContain("startColumn", requiredFields);
        var startDescription = startColumn.GetProperty("description").GetString();
        Assert.Contains("1-based", startDescription);
        Assert.Contains("column", startDescription, StringComparison.OrdinalIgnoreCase);

        Assert.True(properties.TryGetProperty("endColumn", out var endColumn));
        Assert.Equal("integer", endColumn.GetProperty("type").GetString());
        Assert.Equal(1, endColumn.GetProperty("minimum").GetInt32());
        Assert.DoesNotContain("endColumn", requiredFields);
        var endDescription = endColumn.GetProperty("description").GetString();
        Assert.Contains("1-based", endDescription);
        Assert.Contains("column", endDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDefinition_Description_MentionsColumns()
    {
        Assert.Contains("startColumn", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("endColumn", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("whole-line", _tool.Description, StringComparison.OrdinalIgnoreCase);
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
