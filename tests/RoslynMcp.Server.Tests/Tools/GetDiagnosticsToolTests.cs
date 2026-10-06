using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for GetDiagnosticsTool.
/// </summary>
public class GetDiagnosticsToolTests
{
    private readonly GetDiagnosticsTool _tool;

    public GetDiagnosticsToolTests()
    {
        _tool = new GetDiagnosticsTool(new ThrowingWorkspaceProvider());
    }

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        Assert.Equal("get_diagnostics", _tool.Name);
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
    public void GetDefinition_HasRequiredField_SolutionPath()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var required = doc.RootElement.GetProperty("required");

        var requiredFields = new List<string>();
        foreach (var item in required.EnumerateArray())
            requiredFields.Add(item.GetString()!);

        Assert.Contains("solutionPath", requiredFields);
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
        Assert.True(properties.TryGetProperty("severityFilter", out _));
        Assert.True(properties.TryGetProperty("maxResults", out _));
        Assert.True(properties.TryGetProperty("diagnosticIds", out _));
        Assert.True(properties.TryGetProperty("excludeDiagnosticIds", out _));
        Assert.True(properties.TryGetProperty("projectPath", out _));
    }

    [Fact]
    public void GetDefinition_DiagnosticIds_IsOptionalStringArray()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var diagnosticIds = doc.RootElement.GetProperty("properties").GetProperty("diagnosticIds");

        Assert.Equal("array", diagnosticIds.GetProperty("type").GetString());
        Assert.Equal("string", diagnosticIds.GetProperty("items").GetProperty("type").GetString());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("diagnosticIds", required);
        Assert.Contains("diagnosticIds", _tool.Description);
    }

    [Fact]
    public void GetDefinition_ExcludeDiagnosticIds_IsOptionalStringArray()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var excludeDiagnosticIds = doc.RootElement.GetProperty("properties").GetProperty("excludeDiagnosticIds");

        Assert.Equal("array", excludeDiagnosticIds.GetProperty("type").GetString());
        Assert.Equal("string", excludeDiagnosticIds.GetProperty("items").GetProperty("type").GetString());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("excludeDiagnosticIds", required);
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("excludeDiagnosticIds", _tool.Description);
    }

    [Fact]
    public void GetDefinition_ProjectPath_IsOptionalString()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var projectPath = doc.RootElement.GetProperty("properties").GetProperty("projectPath");

        Assert.Equal("string", projectPath.GetProperty("type").GetString());
        Assert.Contains(".csproj", projectPath.GetProperty("description").GetString());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("projectPath", required);
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("projectPath", _tool.Description);
    }

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

    private static string GetResultText(ToolResult result)
    {
        return result.Content.FirstOrDefault()?.Text ?? string.Empty;
    }
}
