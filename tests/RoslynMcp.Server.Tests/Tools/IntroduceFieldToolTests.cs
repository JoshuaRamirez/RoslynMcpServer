using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for IntroduceFieldTool.
/// Tests tool definition and argument validation.
/// </summary>
public class IntroduceFieldToolTests
{
    private readonly IntroduceFieldTool _tool;

    public IntroduceFieldToolTests()
    {
        _tool = new IntroduceFieldTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        Assert.Equal("introduce_field", _tool.Name);
    }

    [Fact]
    public void GetDefinition_ReturnsNonEmptyDescription()
    {
        Assert.NotNull(_tool.Description);
        Assert.NotEmpty(_tool.Description);
        Assert.Contains("allFiles", _tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDefinition_ReturnsCorrectSchema()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.True(root.TryGetProperty("properties", out _));
        Assert.True(root.TryGetProperty("required", out _));
    }

    [Fact]
    public void GetDefinition_HasRequiredFields()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var required = doc.RootElement.GetProperty("required");

        var requiredFields = new List<string>();
        foreach (var item in required.EnumerateArray())
        {
            requiredFields.Add(item.GetString()!);
        }

        Assert.Contains("solutionPath", requiredFields);
        Assert.DoesNotContain("sourceFile", requiredFields);
        Assert.DoesNotContain("startLine", requiredFields);
        Assert.DoesNotContain("startColumn", requiredFields);
        Assert.DoesNotContain("endLine", requiredFields);
        Assert.DoesNotContain("endColumn", requiredFields);
        Assert.DoesNotContain("fieldName", requiredFields);
        Assert.DoesNotContain("allFiles", requiredFields);
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
        var description = allFiles.GetProperty("description").GetString();
        Assert.Contains("sourceFile", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fieldName", description, StringComparison.OrdinalIgnoreCase);
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
        Assert.True(properties.TryGetProperty("allFiles", out _));
        Assert.True(properties.TryGetProperty("startLine", out _));
        Assert.True(properties.TryGetProperty("startColumn", out _));
        Assert.True(properties.TryGetProperty("endLine", out _));
        Assert.True(properties.TryGetProperty("endColumn", out _));
        Assert.True(properties.TryGetProperty("fieldName", out _));
        Assert.True(properties.TryGetProperty("isReadonly", out _));
        Assert.True(properties.TryGetProperty("isStatic", out _));
        Assert.True(properties.TryGetProperty("initializeInConstructor", out _));
        Assert.True(properties.TryGetProperty("replaceAll", out _));
        Assert.True(properties.TryGetProperty("preview", out _));
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
        var args = JsonDocument.Parse("""
            {
                "solutionPath": "C:/test/test.sln",
                "sourceFile": "C:/test/Test.cs",
                "startLine": 10,
                "startColumn": 5,
                "endLine": 10,
                "endColumn": 20
            }
            """).RootElement;

        var result = await _tool.ExecuteAsync(args);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_AllFilesTrueWithoutSourceFile_AcceptsArgs()
    {
        var args = JsonDocument.Parse("""
            {
                "solutionPath": "C:/test/test.sln",
                "allFiles": true
            }
            """).RootElement;

        var result = await _tool.ExecuteAsync(args);

        // ThrowingWorkspaceProvider rejects workspace creation; args including allFiles parsed.
        Assert.True(result.IsError);
        Assert.DoesNotContain("Failed to parse arguments", GetResultText(result), StringComparison.Ordinal);
    }

    #endregion

    #region Helper Methods

    private static string GetResultText(ToolResult result)
    {
        return result.Content.FirstOrDefault()?.Text ?? string.Empty;
    }

    #endregion
}
