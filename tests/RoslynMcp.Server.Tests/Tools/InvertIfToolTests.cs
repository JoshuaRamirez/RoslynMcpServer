using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for InvertIfTool.
/// Tests tool definition and argument validation.
/// </summary>
public class InvertIfToolTests
{
    private readonly InvertIfTool _tool;

    public InvertIfToolTests()
    {
        _tool = new InvertIfTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        Assert.Equal("invert_if", _tool.Name);
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
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.True(root.TryGetProperty("properties", out _));
        Assert.True(root.TryGetProperty("required", out _));
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
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
        Assert.DoesNotContain("allFiles", requiredFields);
        Assert.DoesNotContain("line", requiredFields);
        Assert.DoesNotContain("column", requiredFields);
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
        Assert.True(properties.TryGetProperty("line", out _));
        Assert.True(properties.TryGetProperty("column", out _));
        Assert.True(properties.TryGetProperty("preview", out _));
        Assert.False(RequiredFieldsContains(doc, "column"));

        var column = properties.GetProperty("column");
        Assert.Equal("integer", column.GetProperty("type").GetString());
        Assert.Contains("covers that column", column.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("exclusive-end", column.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("SpanStart", column.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("required-line", column.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GetDefinition_DescriptionMentionsExclusiveEndColumn()
    {
        Assert.Contains("covers that column", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exclusive-end", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SpanStart", _tool.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void GetDefinition_AllFilesProperty_DefaultsToFalse()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var allFiles = doc.RootElement.GetProperty("properties").GetProperty("allFiles");

        Assert.Equal("boolean", allFiles.GetProperty("type").GetString());
        Assert.False(allFiles.GetProperty("default").GetBoolean());
        var description = allFiles.GetProperty("description").GetString();
        Assert.Contains("sourceFile is optional", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDefinition_DescriptionMentionsAllFiles()
    {
        Assert.Contains("allFiles", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", _tool.Description, StringComparison.OrdinalIgnoreCase);
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
        Assert.DoesNotContain("Failed to parse arguments", GetResultText(result));
        Assert.DoesNotContain("Arguments required", GetResultText(result));
    }

    #endregion

    #region Helper Methods

    private static string GetResultText(ToolResult result)
    {
        return result.Content.FirstOrDefault()?.Text ?? string.Empty;
    }

    #endregion
}
