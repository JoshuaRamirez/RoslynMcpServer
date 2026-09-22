using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for ExtractMethodTool.
/// Tests tool definition and argument validation.
/// </summary>
public class ExtractMethodToolTests
{
    private readonly ExtractMethodTool _tool;

    public ExtractMethodToolTests()
    {
        _tool = new ExtractMethodTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        Assert.Equal("extract_method", _tool.Name);
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
        Assert.True(root.TryGetProperty("oneOf", out _));
    }

    [Fact]
    public void GetDefinition_UsesConditionalRequiredFields()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var required = root.GetProperty("required");

        var requiredFields = new List<string>();
        foreach (var item in required.EnumerateArray())
        {
            requiredFields.Add(item.GetString()!);
        }

        Assert.Contains("solutionPath", requiredFields);
        Assert.DoesNotContain("sourceFile", requiredFields);

        var branches = root.GetProperty("oneOf");
        Assert.Equal(2, branches.GetArrayLength());

        var singleSiteRequired = ReadStrings(branches[0].GetProperty("required"));
        Assert.Contains("solutionPath", singleSiteRequired);
        Assert.Contains("sourceFile", singleSiteRequired);
        Assert.Contains("startLine", singleSiteRequired);
        Assert.Contains("startColumn", singleSiteRequired);
        Assert.Contains("endLine", singleSiteRequired);
        Assert.Contains("endColumn", singleSiteRequired);
        Assert.Contains("methodName", singleSiteRequired);

        var allFilesRequired = ReadStrings(branches[1].GetProperty("required"));
        Assert.Contains("solutionPath", allFilesRequired);
        Assert.Contains("allFiles", allFilesRequired);
        Assert.DoesNotContain("sourceFile", allFilesRequired);
        Assert.DoesNotContain("methodName", allFilesRequired);
        Assert.Equal(
            JsonValueKind.True,
            branches[1].GetProperty("properties").GetProperty("allFiles").GetProperty("const").ValueKind);
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
        Assert.Contains("methodName", description, StringComparison.OrdinalIgnoreCase);
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
        Assert.True(properties.TryGetProperty("methodName", out _));
        Assert.True(properties.TryGetProperty("visibility", out _));
        Assert.True(properties.TryGetProperty("makeStatic", out _));
        Assert.True(properties.TryGetProperty("preview", out _));
    }

    [Fact]
    public void GetDefinition_VisibilityProperty_HasEnumValues()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var visibility = doc.RootElement.GetProperty("properties").GetProperty("visibility");

        Assert.True(visibility.TryGetProperty("enum", out var enumValues));
        var values = new List<string>();
        foreach (var v in enumValues.EnumerateArray())
        {
            values.Add(v.GetString()!);
        }
        Assert.Contains("private", values);
        Assert.Contains("internal", values);
        Assert.Contains("protected", values);
        Assert.Contains("public", values);
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
                "startColumn": 1,
                "endLine": 15,
                "endColumn": 1
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

        Assert.True(result.IsError);
        Assert.DoesNotContain("Failed to parse arguments", GetResultText(result), StringComparison.Ordinal);
    }

    #endregion

    #region Helper Methods

    private static string GetResultText(ToolResult result)
    {
        return result.Content.FirstOrDefault()?.Text ?? string.Empty;
    }

    private static List<string> ReadStrings(JsonElement array)
    {
        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            values.Add(item.GetString()!);
        }

        return values;
    }

    #endregion
}
