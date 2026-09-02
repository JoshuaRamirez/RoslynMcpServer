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
        Assert.DoesNotContain("sourceFile", requiredFields);
        Assert.DoesNotContain("typeName", requiredFields);
        Assert.DoesNotContain("allFiles", requiredFields);
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

    [Fact]
    public void GetDefinition_CallSuperProperty_DefaultsToFalse()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var callSuper = doc.RootElement.GetProperty("properties").GetProperty("callSuper");

        Assert.Equal("boolean", callSuper.GetProperty("type").GetString());
        Assert.False(callSuper.GetProperty("default").GetBoolean());
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
        Assert.True(properties.TryGetProperty("typeName", out _));
        Assert.True(properties.TryGetProperty("line", out _));
        Assert.True(properties.TryGetProperty("column", out _));
        Assert.True(properties.TryGetProperty("fields", out _));
        Assert.True(properties.TryGetProperty("includeProperties", out _));
        Assert.True(properties.TryGetProperty("format", out _));
        Assert.True(properties.TryGetProperty("includeInheritedMembers", out _));
        Assert.True(properties.TryGetProperty("replaceExisting", out _));
        Assert.True(properties.TryGetProperty("callSuper", out _));
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
        Assert.Contains("typeName", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fields", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDefinition_DescriptionMentionsAllFiles()
    {
        Assert.Contains("allFiles", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", _tool.Description, StringComparison.OrdinalIgnoreCase);
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
