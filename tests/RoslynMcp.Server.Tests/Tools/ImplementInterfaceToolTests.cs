using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for ImplementInterfaceTool.
/// Tests tool definition and argument validation.
/// </summary>
public class ImplementInterfaceToolTests
{
    private readonly ImplementInterfaceTool _tool;

    public ImplementInterfaceToolTests()
    {
        // Fails loudly if workspace creation is attempted; argument validation is exercised via ExecuteAsync error paths.
        _tool = new ImplementInterfaceTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        Assert.Equal("implement_interface", _tool.Name);
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
        Assert.Contains("interfaceName", requiredFields);
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
        Assert.True(properties.TryGetProperty("interfaceName", out _));

        // Assert - Optional properties
        Assert.True(properties.TryGetProperty("line", out _));
        Assert.True(properties.TryGetProperty("explicitImplementation", out _));
        Assert.True(properties.TryGetProperty("members", out _));
        Assert.True(properties.TryGetProperty("throwNotImplemented", out _));
        Assert.True(properties.TryGetProperty("replaceExisting", out _));
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
    public void GetDefinition_ExplicitImplementationProperty_DefaultsToFalse()
    {
        // Act
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var explicitImplementation = doc.RootElement.GetProperty("properties").GetProperty("explicitImplementation");

        // Assert
        Assert.Equal("boolean", explicitImplementation.GetProperty("type").GetString());
        Assert.False(explicitImplementation.GetProperty("default").GetBoolean());
    }

    [Fact]
    public void GetDefinition_ThrowNotImplementedProperty_DefaultsToTrue()
    {
        // Act
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var throwNotImplemented = doc.RootElement.GetProperty("properties").GetProperty("throwNotImplemented");

        // Assert
        Assert.Equal("boolean", throwNotImplemented.GetProperty("type").GetString());
        Assert.True(throwNotImplemented.GetProperty("default").GetBoolean());
    }

    [Fact]
    public void GetDefinition_ReplaceExistingProperty_DefaultsToFalse()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var replaceExisting = doc.RootElement.GetProperty("properties").GetProperty("replaceExisting");
        var required = doc.RootElement.GetProperty("required");

        var requiredFields = new List<string>();
        foreach (var item in required.EnumerateArray())
            requiredFields.Add(item.GetString()!);

        Assert.Equal("boolean", replaceExisting.GetProperty("type").GetString());
        Assert.False(replaceExisting.GetProperty("default").GetBoolean());
        Assert.DoesNotContain("replaceExisting", requiredFields);
        var description = replaceExisting.GetProperty("description").GetString();
        Assert.Contains("Replace already-implemented", description);
    }

    [Fact]
    public void GetDefinition_Description_MentionsReplaceExisting()
    {
        Assert.Contains("replaceExisting", _tool.Description);
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
        // Arrange - Missing interfaceName
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
