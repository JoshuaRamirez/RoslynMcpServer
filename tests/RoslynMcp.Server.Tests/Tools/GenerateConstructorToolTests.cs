using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for GenerateConstructorTool.
/// Tests tool definition and argument validation.
/// </summary>
public class GenerateConstructorToolTests
{
    private readonly GenerateConstructorTool _tool;

    public GenerateConstructorToolTests()
    {
        _tool = new GenerateConstructorTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        Assert.Equal("generate_constructor", _tool.Name);
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
        Assert.Contains("typeName", requiredFields);
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

        // Assert - Optional properties
        Assert.True(properties.TryGetProperty("line", out _));
        Assert.True(properties.TryGetProperty("members", out _));
        Assert.True(properties.TryGetProperty("includeProperties", out _));
        Assert.True(properties.TryGetProperty("includeInheritedMembers", out _));
        Assert.True(properties.TryGetProperty("addNullChecks", out _));
        Assert.True(properties.TryGetProperty("replaceExisting", out _));
        Assert.True(properties.TryGetProperty("visibility", out _));
        Assert.True(properties.TryGetProperty("copyConstructor", out _));
        Assert.True(properties.TryGetProperty("classBaseCopy", out _));
        Assert.True(properties.TryGetProperty("callBase", out _));
        Assert.True(properties.TryGetProperty("preview", out _));
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
        Assert.False(properties.TryGetProperty("column", out _));
        var description = line.GetProperty("description").GetString();
        Assert.Contains("1-based", description);
        Assert.Contains("FirstOrDefault", description);
    }

    [Fact]
    public void GetDefinition_Description_MentionsLine()
    {
        Assert.Contains("line", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", _tool.Description);
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
    public void GetDefinition_VisibilityProperty_IsOptionalStringDefaultingToPublic()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var visibility = doc.RootElement.GetProperty("properties").GetProperty("visibility");
        var required = doc.RootElement.GetProperty("required");

        var requiredFields = new List<string>();
        foreach (var item in required.EnumerateArray())
            requiredFields.Add(item.GetString()!);

        Assert.Equal("string", visibility.GetProperty("type").GetString());
        Assert.Equal("public", visibility.GetProperty("default").GetString());
        Assert.DoesNotContain("visibility", requiredFields);
    }

    [Fact]
    public void GetDefinition_CopyConstructorProperty_DefaultsToFalse()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var copyConstructor = doc.RootElement.GetProperty("properties").GetProperty("copyConstructor");
        var required = doc.RootElement.GetProperty("required");

        var requiredFields = new List<string>();
        foreach (var item in required.EnumerateArray())
            requiredFields.Add(item.GetString()!);

        Assert.Equal("boolean", copyConstructor.GetProperty("type").GetString());
        Assert.False(copyConstructor.GetProperty("default").GetBoolean());
        Assert.DoesNotContain("copyConstructor", requiredFields);
    }

    [Fact]
    public void GetDefinition_ClassBaseCopyProperty_DefaultsToFalse()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var classBaseCopy = doc.RootElement.GetProperty("properties").GetProperty("classBaseCopy");
        var required = doc.RootElement.GetProperty("required");

        var requiredFields = new List<string>();
        foreach (var item in required.EnumerateArray())
            requiredFields.Add(item.GetString()!);

        Assert.Equal("boolean", classBaseCopy.GetProperty("type").GetString());
        Assert.False(classBaseCopy.GetProperty("default").GetBoolean());
        Assert.DoesNotContain("classBaseCopy", requiredFields);
    }

    [Fact]
    public void GetDefinition_CallBaseProperty_DefaultsToFalse()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var callBase = doc.RootElement.GetProperty("properties").GetProperty("callBase");
        var required = doc.RootElement.GetProperty("required");

        var requiredFields = new List<string>();
        foreach (var item in required.EnumerateArray())
            requiredFields.Add(item.GetString()!);

        Assert.Equal("boolean", callBase.GetProperty("type").GetString());
        Assert.False(callBase.GetProperty("default").GetBoolean());
        Assert.DoesNotContain("callBase", requiredFields);
        var description = callBase.GetProperty("description").GetString();
        Assert.Contains("record class", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Records, structs, and record structs ignore", description, StringComparison.Ordinal);
    }

    [Fact]
    public void GetDefinition_MembersProperty_IsArray()
    {
        // Act
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var members = doc.RootElement.GetProperty("properties").GetProperty("members");

        // Assert
        Assert.Equal("array", members.GetProperty("type").GetString());
        Assert.True(members.TryGetProperty("items", out var items));
        Assert.Equal("string", items.GetProperty("type").GetString());
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
        // Arrange - Missing typeName
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Test.cs""
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
