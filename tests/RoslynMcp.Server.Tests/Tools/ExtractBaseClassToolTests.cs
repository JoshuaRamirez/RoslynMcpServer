using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for ExtractBaseClassTool.
/// Tests tool definition and argument validation.
/// </summary>
public class ExtractBaseClassToolTests
{
    private readonly ExtractBaseClassTool _tool;

    public ExtractBaseClassToolTests()
    {
        // Fails loudly if workspace creation is attempted; argument validation is exercised via ExecuteAsync error paths.
        _tool = new ExtractBaseClassTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        Assert.Equal("extract_base_class", _tool.Name);
    }

    [Fact]
    public void GetDefinition_ReturnsNonEmptyDescription()
    {
        Assert.NotNull(_tool.Description);
        Assert.NotEmpty(_tool.Description);
        Assert.Contains("indexer", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("makeAbstract", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", _tool.Description);
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
        Assert.Contains("baseClassName", requiredFields);
        Assert.Contains("members", requiredFields);
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
        Assert.True(properties.TryGetProperty("baseClassName", out _));
        Assert.True(properties.TryGetProperty("members", out _));

        // Assert - Optional properties
        Assert.True(properties.TryGetProperty("targetFile", out _));
        Assert.True(properties.TryGetProperty("separateFile", out _));
        Assert.True(properties.TryGetProperty("makeAbstract", out _));
        Assert.True(properties.TryGetProperty("preview", out _));
        Assert.True(properties.TryGetProperty("line", out _));
        Assert.True(properties.TryGetProperty("column", out _));

        var membersDescription = properties.GetProperty("members").GetProperty("description").GetString();
        Assert.NotNull(membersDescription);
        Assert.Contains("Item", membersDescription);
        Assert.Contains("this[]", membersDescription);
        Assert.Contains("this[int i]", membersDescription);
    }

    [Fact]
    public void GetDefinition_SeparateFileProperty_DefaultsToFalse()
    {
        // Act
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var separateFile = doc.RootElement.GetProperty("properties").GetProperty("separateFile");

        // Assert
        Assert.Equal("boolean", separateFile.GetProperty("type").GetString());
        Assert.False(separateFile.GetProperty("default").GetBoolean());
    }

    [Fact]
    public void GetDefinition_MembersProperty_IsArrayOfStrings()
    {
        // Act
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var members = doc.RootElement.GetProperty("properties").GetProperty("members");

        // Assert
        Assert.Equal("array", members.GetProperty("type").GetString());
        Assert.Equal("string", members.GetProperty("items").GetProperty("type").GetString());
    }

    [Fact]
    public void GetDefinition_HasOptionalLine()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var properties = doc.RootElement.GetProperty("properties");
        var required = doc.RootElement.GetProperty("required");

        var requiredFields = new List<string>();
        foreach (var item in required.EnumerateArray())
            requiredFields.Add(item.GetString()!);

        Assert.True(properties.TryGetProperty("line", out var line));
        Assert.Equal("integer", line.GetProperty("type").GetString());
        Assert.Equal(1, line.GetProperty("minimum").GetInt32());
        Assert.DoesNotContain("line", requiredFields);
        Assert.DoesNotContain("column", requiredFields);
        var description = line.GetProperty("description").GetString();
        Assert.Contains("1-based", description);
        Assert.Contains("FirstOrDefault", description);
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
        Assert.Contains("first-match", description);
    }

    [Fact]
    public void GetDefinition_Description_MentionsLine()
    {
        Assert.Contains("line", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", _tool.Description);
    }

    [Fact]
    public void GetDefinition_Description_MentionsColumn()
    {
        Assert.Contains("column", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("first-match", _tool.Description);
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
        // Arrange - Missing baseClassName and members
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Test.cs"",
            ""typeName"": ""MyClass""
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
