using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for RenameNamespaceTool.
/// Tests tool definition and argument validation.
/// </summary>
public class RenameNamespaceToolTests
{
    private readonly RenameNamespaceTool _tool;

    public RenameNamespaceToolTests()
    {
        // Fails loudly if workspace creation is attempted; argument validation is exercised via ExecuteAsync error paths.
        _tool = new RenameNamespaceTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        Assert.Equal("rename_namespace", _tool.Name);
    }

    [Fact]
    public void GetDefinition_ReturnsNonEmptyDescription()
    {
        Assert.NotNull(_tool.Description);
        Assert.NotEmpty(_tool.Description);
        Assert.Contains("column", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("smallest namespace", _tool.Description, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("newName", requiredFields);
        Assert.DoesNotContain("sourceFile", requiredFields);
        Assert.DoesNotContain("namespaceName", requiredFields);

        var branches = root.GetProperty("oneOf");
        Assert.Equal(2, branches.GetArrayLength());

        var singleSiteRequired = ReadStrings(branches[0].GetProperty("required"));
        Assert.Contains("solutionPath", singleSiteRequired);
        Assert.Contains("sourceFile", singleSiteRequired);
        Assert.Contains("namespaceName", singleSiteRequired);
        Assert.Contains("newName", singleSiteRequired);

        var allFilesRequired = ReadStrings(branches[1].GetProperty("required"));
        Assert.Contains("solutionPath", allFilesRequired);
        Assert.Contains("allFiles", allFilesRequired);
        Assert.Contains("newName", allFilesRequired);
        Assert.DoesNotContain("sourceFile", allFilesRequired);
        Assert.DoesNotContain("namespaceName", allFilesRequired);
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
        Assert.Contains("eligible", description!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile", description!, StringComparison.OrdinalIgnoreCase);
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
        Assert.True(properties.TryGetProperty("namespaceName", out _));
        Assert.True(properties.TryGetProperty("newName", out _));
        Assert.True(properties.TryGetProperty("line", out _));
        Assert.True(properties.TryGetProperty("column", out _));
        Assert.True(properties.TryGetProperty("updateFolders", out _));
        Assert.True(properties.TryGetProperty("preview", out _));
        Assert.False(RequiredFieldsContains(doc, "column"));

        var column = properties.GetProperty("column");
        Assert.Equal("integer", column.GetProperty("type").GetString());
        Assert.Contains("smallest namespace", column.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("covers that column", column.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("optional line pick", column.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("omitted-line path", column.GetProperty("description").GetString(), StringComparison.Ordinal);
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

    private static List<string> ReadStrings(JsonElement array)
    {
        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
            values.Add(item.GetString()!);
        return values;
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
                "sourceFile": "C:/test/Foo.cs"
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
                "allFiles": true,
                "newName": "NewNs"
            }
            """).RootElement;

        var result = await _tool.ExecuteAsync(args);

        // ThrowingWorkspaceProvider rejects workspace creation; args including allFiles parsed.
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
