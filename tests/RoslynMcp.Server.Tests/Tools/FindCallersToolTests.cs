using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for FindCallersTool.
/// </summary>
public class FindCallersToolTests
{
    private readonly FindCallersTool _tool;

    public FindCallersToolTests()
    {
        _tool = new FindCallersTool(new ThrowingWorkspaceProvider());
    }

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        Assert.Equal("find_callers", _tool.Name);
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
    public void GetDefinition_HasExpectedProperties()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var properties = doc.RootElement.GetProperty("properties");

        Assert.True(properties.TryGetProperty("solutionPath", out _));
        Assert.True(properties.TryGetProperty("sourceFile", out _));
        Assert.True(properties.TryGetProperty("symbolName", out _));
        Assert.True(properties.TryGetProperty("line", out _));
        Assert.True(properties.TryGetProperty("column", out _));
        Assert.True(properties.TryGetProperty("maxResults", out _));
        Assert.True(properties.TryGetProperty("callerFile", out _));
        Assert.True(properties.TryGetProperty("projectPath", out _));
        Assert.True(properties.TryGetProperty("namespaceFilter", out _));
        Assert.True(properties.TryGetProperty("accessibilityFilter", out _));
        Assert.True(properties.TryGetProperty("nameFilter", out _));
        Assert.True(properties.TryGetProperty("kindFilter", out _));
    }

    [Fact]
    public void GetDefinition_CallerFile_IsOptionalString()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var callerFile = doc.RootElement.GetProperty("properties").GetProperty("callerFile");

        Assert.Equal("string", callerFile.GetProperty("type").GetString());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("callerFile", required);
        Assert.Contains("callerFile", _tool.Description);
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
            ""sourceFile"": ""C:/test/Lib/Target.cs"",
            ""symbolName"": ""Run"",
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
            ""sourceFile"": ""C:/test/Lib/Target.cs"",
            ""symbolName"": ""Run"",
            ""projectPath"": 42
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", result.Content.FirstOrDefault()?.Text ?? "");
    }

    [Fact]
    public void GetDefinition_NamespaceFilter_IsOptionalString()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var namespaceFilter = doc.RootElement.GetProperty("properties").GetProperty("namespaceFilter");

        Assert.Equal("string", namespaceFilter.GetProperty("type").GetString());
        Assert.Contains("nested inside it", namespaceFilter.GetProperty("description").GetString());
        Assert.Contains("call site", namespaceFilter.GetProperty("description").GetString());
        Assert.Contains("search_symbols", namespaceFilter.GetProperty("description").GetString());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("namespaceFilter", required);
        Assert.Contains("namespaceFilter", _tool.Description);
    }

    [Fact]
    public async Task ExecuteAsync_NamespaceFilterString_ParsesAndReachesWorkspace()
    {
        // Arrange - well-formed namespaceFilter parses; the throwing provider proves parsing succeeded
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Lib/Target.cs"",
            ""symbolName"": ""Run"",
            ""namespaceFilter"": ""App.Services""
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", result.Content.FirstOrDefault()?.Text ?? "");
    }

    [Fact]
    public async Task ExecuteAsync_NamespaceFilterNonString_ReturnsParseError()
    {
        // Arrange - a number is not a string; deserialization fails before workspace creation
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Lib/Target.cs"",
            ""symbolName"": ""Run"",
            ""namespaceFilter"": 42
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", result.Content.FirstOrDefault()?.Text ?? "");
    }

    [Fact]
    public void GetDefinition_AccessibilityFilter_IsOptionalString()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var accessibilityFilter = doc.RootElement.GetProperty("properties").GetProperty("accessibilityFilter");

        Assert.Equal("string", accessibilityFilter.GetProperty("type").GetString());
        Assert.Contains("DeclaredAccessibility", accessibilityFilter.GetProperty("description").GetString());
        Assert.Contains("calling symbol", accessibilityFilter.GetProperty("description").GetString());
        Assert.Contains("search_symbols", accessibilityFilter.GetProperty("description").GetString());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("accessibilityFilter", required);
        Assert.Contains("accessibilityFilter", _tool.Description);
    }

    [Fact]
    public async Task ExecuteAsync_AccessibilityFilterString_ParsesAndReachesWorkspace()
    {
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Lib/Target.cs"",
            ""symbolName"": ""Run"",
            ""accessibilityFilter"": ""public""
        }").RootElement;

        var result = await _tool.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", result.Content.FirstOrDefault()?.Text ?? "");
    }

    [Fact]
    public async Task ExecuteAsync_AccessibilityFilterNonString_ReturnsParseError()
    {
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Lib/Target.cs"",
            ""symbolName"": ""Run"",
            ""accessibilityFilter"": 42
        }").RootElement;

        var result = await _tool.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", result.Content.FirstOrDefault()?.Text ?? "");
    }

    [Fact]
    public void GetDefinition_NameFilter_IsOptionalString()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var nameFilter = doc.RootElement.GetProperty("properties").GetProperty("nameFilter");

        Assert.Equal("string", nameFilter.GetProperty("type").GetString());
        var description = nameFilter.GetProperty("description").GetString();
        Assert.Contains("calling symbol", description);
        Assert.Contains("case-insensitive", description);
        Assert.Contains("find_references", description);
        Assert.Contains("before maxResults", description);

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("nameFilter", required);
        Assert.Contains("nameFilter", _tool.Description);
        Assert.Contains("nameFilter", doc.RootElement.GetProperty("properties").GetProperty("accessibilityFilter").GetProperty("description").GetString());
        Assert.Contains("nameFilter", doc.RootElement.GetProperty("properties").GetProperty("namespaceFilter").GetProperty("description").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_NameFilterString_ParsesAndReachesWorkspace()
    {
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Lib/Target.cs"",
            ""symbolName"": ""Run"",
            ""nameFilter"": ""Handle""
        }").RootElement;

        var result = await _tool.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", result.Content.FirstOrDefault()?.Text ?? "");
    }

    [Fact]
    public async Task ExecuteAsync_NameFilterNonString_ReturnsParseError()
    {
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Lib/Target.cs"",
            ""symbolName"": ""Run"",
            ""nameFilter"": 42
        }").RootElement;

        var result = await _tool.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", result.Content.FirstOrDefault()?.Text ?? "");
    }

    [Fact]
    public void GetDefinition_KindFilter_IsOptionalString()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var properties = doc.RootElement.GetProperty("properties");
        var kindFilter = properties.GetProperty("kindFilter");

        Assert.Equal("string", kindFilter.GetProperty("type").GetString());
        var description = kindFilter.GetProperty("description").GetString();
        Assert.Contains("calling symbol", description);
        Assert.Contains("case-insensitive", description);
        Assert.Contains("InvalidSymbolKind", description);
        Assert.Contains("find_implementations", description);
        Assert.Contains("before maxResults", description);

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("kindFilter", required);
        Assert.Contains("kindFilter", _tool.Description);
        Assert.Contains("kindFilter", properties.GetProperty("accessibilityFilter").GetProperty("description").GetString());
        Assert.Contains("kindFilter", properties.GetProperty("namespaceFilter").GetProperty("description").GetString());
        Assert.Contains("kindFilter", properties.GetProperty("nameFilter").GetProperty("description").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_KindFilterString_ParsesAndReachesWorkspace()
    {
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Lib/Target.cs"",
            ""symbolName"": ""Run"",
            ""kindFilter"": ""Property""
        }").RootElement;

        var result = await _tool.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", result.Content.FirstOrDefault()?.Text ?? "");
    }

    [Fact]
    public async Task ExecuteAsync_KindFilterNonString_ReturnsParseError()
    {
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""sourceFile"": ""C:/test/Lib/Target.cs"",
            ""symbolName"": ""Run"",
            ""kindFilter"": 7
        }").RootElement;

        var result = await _tool.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", result.Content.FirstOrDefault()?.Text ?? "");
    }
}
