using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for GetTypeHierarchyTool.
/// </summary>
public class GetTypeHierarchyToolTests
{
    private readonly GetTypeHierarchyTool _tool;

    public GetTypeHierarchyToolTests()
    {
        _tool = new GetTypeHierarchyTool(new ThrowingWorkspaceProvider());
    }

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        Assert.Equal("get_type_hierarchy", _tool.Name);
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
            requiredFields.Add(item.GetString()!);

        Assert.Contains("solutionPath", requiredFields);
        Assert.Contains("sourceFile", requiredFields);
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
        Assert.True(properties.TryGetProperty("maxResults", out _));
        Assert.True(properties.TryGetProperty("derivedFile", out _));
        Assert.True(properties.TryGetProperty("projectPath", out _));
        Assert.True(properties.TryGetProperty("transitive", out _));
    }

    [Fact]
    public void GetDefinition_DerivedFile_IsOptionalString()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var derivedFile = doc.RootElement.GetProperty("properties").GetProperty("derivedFile");

        Assert.Equal("string", derivedFile.GetProperty("type").GetString());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("derivedFile", required);
        Assert.Contains("derivedFile", _tool.Description);
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
    public void GetDefinition_DescriptionMentionsMaxResults()
    {
        Assert.Contains("maxResults", _tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_NullArguments_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(null);
        Assert.True(result.IsError);
        Assert.Contains("Arguments required", result.Content.FirstOrDefault()?.Text ?? "");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyArguments_ReturnsError()
    {
        var args = JsonDocument.Parse("{}").RootElement;
        var result = await _tool.ExecuteAsync(args);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_ProjectPathString_ParsesAndReachesWorkspace()
    {
        // Arrange - well-formed projectPath parses; the throwing provider proves parsing succeeded
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Lib/Shape.cs"",
            ""symbolName"": ""Shape"",
            ""projectPath"": ""C:/test/App/App.csproj""
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", result.Content.FirstOrDefault()?.Text ?? "");
    }

    [Fact]
    public async Task ExecuteAsync_ProjectPathNonString_ReturnsParseError()
    {
        // Arrange - a number is not a string; deserialization fails before workspace creation
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Lib/Shape.cs"",
            ""symbolName"": ""Shape"",
            ""projectPath"": 42
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", result.Content.FirstOrDefault()?.Text ?? "");
    }

    [Fact]
    public void GetDefinition_Transitive_IsOptionalBoolean()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var transitive = doc.RootElement.GetProperty("properties").GetProperty("transitive");

        Assert.Equal("boolean", transitive.GetProperty("type").GetString());
        Assert.Contains("direct subclasses", transitive.GetProperty("description").GetString());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("transitive", required);
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("transitive", _tool.Description);
    }

    [Fact]
    public async Task ExecuteAsync_TransitiveBoolean_ParsesAndReachesWorkspace()
    {
        // Arrange - well-formed transitive parses; the throwing provider proves parsing succeeded
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Lib/Shape.cs"",
            ""symbolName"": ""Shape"",
            ""transitive"": false
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", result.Content.FirstOrDefault()?.Text ?? "");
    }

    [Fact]
    public async Task ExecuteAsync_TransitiveNonBoolean_ReturnsParseError()
    {
        // Arrange - a string is not a boolean; deserialization fails before workspace creation
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Lib/Shape.cs"",
            ""symbolName"": ""Shape"",
            ""transitive"": ""no""
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", result.Content.FirstOrDefault()?.Text ?? "");
    }
}
