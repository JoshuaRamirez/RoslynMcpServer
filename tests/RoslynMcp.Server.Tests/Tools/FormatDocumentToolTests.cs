using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for FormatDocumentTool.
/// Tests tool definition and argument validation.
/// </summary>
public class FormatDocumentToolTests
{
    private readonly FormatDocumentTool _tool;

    public FormatDocumentToolTests()
    {
        // Fails loudly if workspace creation is attempted; argument validation is exercised via ExecuteAsync error paths.
        _tool = new FormatDocumentTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        // Assert
        Assert.Equal("format_document", _tool.Name);
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

        // Assert - solutionPath is required; sourceFile is optional (allFiles mode)
        Assert.Contains("solutionPath", requiredFields);
        Assert.DoesNotContain("sourceFile", requiredFields);
        Assert.DoesNotContain("allFiles", requiredFields);
        Assert.Single(requiredFields);
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
        Assert.True(properties.TryGetProperty("preview", out _));
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
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
    }

    [Fact]
    public void GetDefinition_PreviewProperty_DefaultsToFalse()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var preview = doc.RootElement.GetProperty("properties").GetProperty("preview");

        Assert.Equal("boolean", preview.GetProperty("type").GetString());
        Assert.False(preview.GetProperty("default").GetBoolean());
    }

    [Fact]
    public void GetDefinition_Description_MentionsPreview()
    {
        Assert.Contains("preview", _tool.Description, StringComparison.OrdinalIgnoreCase);
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
