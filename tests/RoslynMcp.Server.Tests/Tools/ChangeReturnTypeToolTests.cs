using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for ChangeReturnTypeTool.
/// Tests tool definition and argument validation.
/// </summary>
public class ChangeReturnTypeToolTests
{
    private readonly ChangeReturnTypeTool _tool;

    public ChangeReturnTypeToolTests()
    {
        // Fails loudly if workspace creation is attempted; argument validation is exercised via ExecuteAsync error paths.
        _tool = new ChangeReturnTypeTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        Assert.Equal("change_return_type", _tool.Name);
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
        Assert.Contains("newReturnType", requiredFields);
        Assert.DoesNotContain("sourceFile", requiredFields);
        Assert.DoesNotContain("methodName", requiredFields);
        Assert.DoesNotContain("allFiles", requiredFields);
    }

    [Fact]
    public void GetDefinition_DescriptionMentionsAllFiles()
    {
        Assert.Contains("allFiles", _tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDefinition_HasOptionalAllFiles()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var allFiles = doc.RootElement.GetProperty("properties").GetProperty("allFiles");
        Assert.Equal("boolean", allFiles.GetProperty("type").GetString());
        Assert.False(allFiles.GetProperty("default").GetBoolean());
        var description = allFiles.GetProperty("description").GetString();
        Assert.Contains("eligible method", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_AllFilesTrueWithoutSourceFile_AcceptsArgs()
    {
        // ThrowingWorkspaceProvider fails on CreateContext — proves args deserialize
        // past schema-required fields (solutionPath + newReturnType only).
        var args = JsonDocument.Parse("""
            {
                "solutionPath": "C:/test/test.sln",
                "allFiles": true,
                "newReturnType": "long"
            }
            """).RootElement;

        var result = await _tool.ExecuteAsync(args);
        Assert.True(result.IsError);
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
        Assert.True(properties.TryGetProperty("methodName", out _));
        Assert.True(properties.TryGetProperty("newReturnType", out _));
        Assert.True(properties.TryGetProperty("line", out _));
        Assert.True(properties.TryGetProperty("column", out _));
        Assert.True(properties.TryGetProperty("updateOverrides", out _));
        Assert.True(properties.TryGetProperty("updateImplementations", out _));
        Assert.True(properties.TryGetProperty("convertReturnStatements", out _));
        Assert.True(properties.TryGetProperty("preview", out _));
        Assert.False(RequiredFieldsContains(doc, "column"));
        Assert.False(RequiredFieldsContains(doc, "allFiles"));
        Assert.False(RequiredFieldsContains(doc, "sourceFile"));

        var column = properties.GetProperty("column");
        Assert.Equal("integer", column.GetProperty("type").GetString());
        Assert.Contains("smallest method", column.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("covers that column", column.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("start-line", column.GetProperty("description").GetString(), StringComparison.Ordinal);
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
    public async Task ExecuteAsync_MissingRequiredField_ReturnsError()
    {
        var args = JsonDocument.Parse("""
            {
                "solutionPath": "C:/test/test.sln",
                "sourceFile": "C:/test/Test.cs",
                "methodName": "DoWork"
            }
            """).RootElement;

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
