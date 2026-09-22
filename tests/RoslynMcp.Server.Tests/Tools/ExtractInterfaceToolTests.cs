using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for ExtractInterfaceTool.
/// Tests tool definition and argument validation.
/// </summary>
public class ExtractInterfaceToolTests
{
    private readonly ExtractInterfaceTool _tool;

    public ExtractInterfaceToolTests()
    {
        // Fails loudly if workspace creation is attempted; argument validation is exercised via ExecuteAsync error paths.
        _tool = new ExtractInterfaceTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        Assert.Equal("extract_interface", _tool.Name);
    }

    [Fact]
    public void GetDefinition_ReturnsNonEmptyDescription()
    {
        Assert.NotNull(_tool.Description);
        Assert.NotEmpty(_tool.Description);
        Assert.Contains("indexer", _tool.Description, StringComparison.OrdinalIgnoreCase);
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
            requiredFields.Add(item.GetString()!);

        Assert.Contains("solutionPath", requiredFields);
        Assert.DoesNotContain("sourceFile", requiredFields);
        Assert.DoesNotContain("typeName", requiredFields);
        Assert.DoesNotContain("interfaceName", requiredFields);

        var branches = root.GetProperty("oneOf");
        Assert.Equal(2, branches.GetArrayLength());

        var singleSiteRequired = ReadStrings(branches[0].GetProperty("required"));
        Assert.Contains("solutionPath", singleSiteRequired);
        Assert.Contains("sourceFile", singleSiteRequired);
        Assert.Contains("typeName", singleSiteRequired);
        Assert.Contains("interfaceName", singleSiteRequired);

        var allFilesRequired = ReadStrings(branches[1].GetProperty("required"));
        Assert.Contains("solutionPath", allFilesRequired);
        Assert.Contains("allFiles", allFilesRequired);
        Assert.DoesNotContain("sourceFile", allFilesRequired);
        Assert.DoesNotContain("typeName", allFilesRequired);
        Assert.DoesNotContain("interfaceName", allFilesRequired);
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
        Assert.Contains("typeName", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("interfaceName", description, StringComparison.OrdinalIgnoreCase);
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
        Assert.True(properties.TryGetProperty("interfaceName", out _));
        Assert.True(properties.TryGetProperty("members", out _));
        Assert.True(properties.TryGetProperty("targetFile", out _));
        Assert.True(properties.TryGetProperty("separateFile", out _));
        Assert.True(properties.TryGetProperty("addInterfaceToType", out _));
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
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var separateFile = doc.RootElement.GetProperty("properties").GetProperty("separateFile");

        Assert.Equal("boolean", separateFile.GetProperty("type").GetString());
        Assert.False(separateFile.GetProperty("default").GetBoolean());
    }

    [Fact]
    public void GetDefinition_AddInterfaceToTypeProperty_DefaultsToTrue()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var addInterfaceToType = doc.RootElement.GetProperty("properties").GetProperty("addInterfaceToType");

        Assert.Equal("boolean", addInterfaceToType.GetProperty("type").GetString());
        Assert.True(addInterfaceToType.GetProperty("default").GetBoolean());
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
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Test.cs"",
            ""typeName"": ""MyClass""
        }").RootElement;

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
        Assert.DoesNotContain("Arguments required", GetResultText(result), StringComparison.Ordinal);
    }

    #endregion

    #region Helper Methods

    private static List<string> ReadStrings(JsonElement array)
    {
        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
            values.Add(item.GetString()!);
        return values;
    }

    private static string GetResultText(ToolResult result)
    {
        return result.Content.FirstOrDefault()?.Text ?? string.Empty;
    }

    #endregion
}
