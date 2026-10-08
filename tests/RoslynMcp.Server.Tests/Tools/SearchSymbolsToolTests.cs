using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for SearchSymbolsTool.
/// Tests tool definition and argument validation.
/// </summary>
public class SearchSymbolsToolTests
{
    private readonly SearchSymbolsTool _tool;

    public SearchSymbolsToolTests()
    {
        // Fails loudly if workspace creation is attempted; argument validation is exercised via ExecuteAsync error paths.
        _tool = new SearchSymbolsTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        // Assert
        Assert.Equal("search_symbols", _tool.Name);
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
    public void GetDefinition_HasRequiredFields_SolutionPath_Query()
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
        Assert.Contains("query", requiredFields);
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
        Assert.True(properties.TryGetProperty("query", out _));

        // Assert - Optional properties
        Assert.True(properties.TryGetProperty("kindFilter", out _));
        Assert.True(properties.TryGetProperty("maxResults", out _));
        Assert.True(properties.TryGetProperty("sourceFile", out _));
        Assert.True(properties.TryGetProperty("caseSensitive", out _));
        Assert.True(properties.TryGetProperty("exactMatch", out _));
        Assert.True(properties.TryGetProperty("namespaceFilter", out _));
        Assert.True(properties.TryGetProperty("accessibilityFilter", out _));
        Assert.True(properties.TryGetProperty("projectPath", out _));
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
    public void GetDefinition_AccessibilityFilter_IsOptionalString()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var accessibilityFilter = doc.RootElement.GetProperty("properties").GetProperty("accessibilityFilter");

        Assert.Equal("string", accessibilityFilter.GetProperty("type").GetString());
        var description = accessibilityFilter.GetProperty("description").GetString();
        Assert.False(string.IsNullOrWhiteSpace(description));
        Assert.Contains("protected internal", description);
        Assert.Contains("private protected", description);

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("accessibilityFilter", required);
        Assert.Contains("accessibilityFilter", _tool.Description);
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void GetDefinition_NamespaceFilter_IsOptionalString()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var namespaceFilter = doc.RootElement.GetProperty("properties").GetProperty("namespaceFilter");

        Assert.Equal("string", namespaceFilter.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(namespaceFilter.GetProperty("description").GetString()));

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("namespaceFilter", required);
        Assert.Contains("namespaceFilter", _tool.Description);
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void GetDefinition_ExactMatch_IsOptionalBoolean()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var exactMatch = doc.RootElement.GetProperty("properties").GetProperty("exactMatch");

        Assert.Equal("boolean", exactMatch.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(exactMatch.GetProperty("description").GetString()));

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("exactMatch", required);
        Assert.Contains("exactMatch", _tool.Description);
    }

    [Fact]
    public void GetDefinition_CaseSensitive_IsOptionalBoolean()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var caseSensitive = doc.RootElement.GetProperty("properties").GetProperty("caseSensitive");

        Assert.Equal("boolean", caseSensitive.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(caseSensitive.GetProperty("description").GetString()));

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("caseSensitive", required);
        Assert.Contains("caseSensitive", _tool.Description);
    }

    [Fact]
    public void GetDefinition_DisallowsAdditionalProperties()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
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
    public async Task ExecuteAsync_InvalidJson_ReturnsError()
    {
        // Arrange - Valid JSON but missing required fields, will fail on workspace access
        var args = JsonDocument.Parse("{\"invalidField\": \"value\"}").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_MissingQuery_ReturnsError()
    {
        // Arrange - Has solutionPath but missing query
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln""
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_CaseSensitiveBoolean_ParsesAndReachesWorkspace()
    {
        // Arrange - well-formed caseSensitive parses; the throwing provider proves parsing succeeded
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""query"": ""ID"",
            ""caseSensitive"": true
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Fact]
    public async Task ExecuteAsync_CaseSensitiveNonBoolean_ReturnsParseError()
    {
        // Arrange - a string is not a boolean; deserialization fails before workspace creation
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""query"": ""ID"",
            ""caseSensitive"": ""yes""
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Fact]
    public async Task ExecuteAsync_ExactMatchBoolean_ParsesAndReachesWorkspace()
    {
        // Arrange - well-formed exactMatch parses; the throwing provider proves parsing succeeded
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""query"": ""Add"",
            ""exactMatch"": true
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Fact]
    public async Task ExecuteAsync_ExactMatchNonBoolean_ReturnsParseError()
    {
        // Arrange - a string is not a boolean; deserialization fails before workspace creation
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""query"": ""Add"",
            ""exactMatch"": ""yes""
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Fact]
    public async Task ExecuteAsync_AccessibilityFilterString_ParsesAndReachesWorkspace()
    {
        // Arrange - well-formed accessibilityFilter parses; the throwing provider proves parsing succeeded
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""query"": ""Widget"",
            ""accessibilityFilter"": ""protected internal""
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Fact]
    public async Task ExecuteAsync_AccessibilityFilterNonString_ReturnsParseError()
    {
        // Arrange - a boolean is not a string; deserialization fails before workspace creation
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""query"": ""Widget"",
            ""accessibilityFilter"": true
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Fact]
    public async Task ExecuteAsync_NamespaceFilterString_ParsesAndReachesWorkspace()
    {
        // Arrange - well-formed namespaceFilter parses; the throwing provider proves parsing succeeded
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""query"": ""Widget"",
            ""namespaceFilter"": ""App.Services""
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Fact]
    public async Task ExecuteAsync_NamespaceFilterNonString_ReturnsParseError()
    {
        // Arrange - a number is not a string; deserialization fails before workspace creation
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""query"": ""Widget"",
            ""namespaceFilter"": 42
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Fact]
    public async Task ExecuteAsync_ProjectPathString_ParsesAndReachesWorkspace()
    {
        // Arrange - well-formed projectPath parses; the throwing provider proves parsing succeeded
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""query"": ""Widget"",
            ""projectPath"": ""C:/test/Lib/Lib.csproj""
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Fact]
    public async Task ExecuteAsync_ProjectPathNonString_ReturnsParseError()
    {
        // Arrange - a number is not a string; deserialization fails before workspace creation
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""query"": ""Widget"",
            ""projectPath"": 42
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", GetResultText(result));
    }

    #endregion

    #region Helper Methods

    private static string GetResultText(ToolResult result)
    {
        return result.Content.FirstOrDefault()?.Text ?? string.Empty;
    }

    #endregion
}
