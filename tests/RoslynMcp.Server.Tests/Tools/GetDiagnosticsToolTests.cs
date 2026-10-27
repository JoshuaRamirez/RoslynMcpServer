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
        Assert.True(properties.TryGetProperty("namespaceFilter", out _));
        Assert.True(properties.TryGetProperty("accessibilityFilter", out _));
        Assert.True(properties.TryGetProperty("messageFilter", out _));
        Assert.True(properties.TryGetProperty("kindFilter", out _));
        Assert.True(properties.TryGetProperty("nameFilter", out _));
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
    public void GetDefinition_NamespaceFilter_IsOptionalString()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var namespaceFilter = doc.RootElement.GetProperty("properties").GetProperty("namespaceFilter");

        Assert.Equal("string", namespaceFilter.GetProperty("type").GetString());
        Assert.Contains("nested inside it", namespaceFilter.GetProperty("description").GetString());
        Assert.Contains("diagnostic site", namespaceFilter.GetProperty("description").GetString());
        Assert.Contains("find_references", namespaceFilter.GetProperty("description").GetString());
        Assert.Contains("before maxResults", namespaceFilter.GetProperty("description").GetString());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("namespaceFilter", required);
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("namespaceFilter", _tool.Description);
    }

    [Fact]
    public async Task ExecuteAsync_NamespaceFilterString_ParsesAndReachesWorkspace()
    {
        // Arrange - well-formed namespaceFilter parses; the throwing provider proves parsing succeeded
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
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
            ""namespaceFilter"": 42
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", GetResultText(result));
    }


    [Fact]
    public void GetDefinition_MessageFilter_IsOptionalString()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var messageFilter = doc.RootElement.GetProperty("properties").GetProperty("messageFilter");

        Assert.Equal("string", messageFilter.GetProperty("type").GetString());
        var description = messageFilter.GetProperty("description").GetString();
        Assert.Contains("case-insensitive substring", description);
        Assert.Contains("nameFilter", description);
        Assert.Contains("before maxResults", description);

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("messageFilter", required);
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("messageFilter", _tool.Description);
    }

    [Fact]
    public async Task ExecuteAsync_MessageFilterString_ParsesAndReachesWorkspace()
    {
        // Arrange - well-formed messageFilter parses; the throwing provider proves parsing succeeded
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""messageFilter"": ""never used""
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Fact]
    public async Task ExecuteAsync_MessageFilterNonString_ReturnsParseError()
    {
        // Arrange - a number is not a string; deserialization fails before workspace creation
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""messageFilter"": 42
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", GetResultText(result));
    }


    [Fact]
    public void GetDefinition_NameFilter_IsOptionalString()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var nameFilter = doc.RootElement.GetProperty("properties").GetProperty("nameFilter");

        Assert.Equal("string", nameFilter.GetProperty("type").GetString());
        var description = nameFilter.GetProperty("description").GetString();
        Assert.Contains("diagnostic site", description);
        Assert.Contains("case-insensitive", description);
        Assert.Contains("ISymbol.Name", description);
        Assert.Contains("find_references", description);
        Assert.Contains("before maxResults", description);

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("nameFilter", required);
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("nameFilter", _tool.Description);
    }

    [Fact]
    public async Task ExecuteAsync_NameFilterString_ParsesAndReachesWorkspace()
    {
        // Arrange - well-formed nameFilter parses; the throwing provider proves parsing succeeded
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""nameFilter"": ""Handle""
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Fact]
    public async Task ExecuteAsync_NameFilterNonString_ReturnsParseError()
    {
        // Arrange - a number is not a string; deserialization fails before workspace creation
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""nameFilter"": 42
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Fact]
    public void GetDefinition_KindFilter_IsOptionalString()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var kindFilter = doc.RootElement.GetProperty("properties").GetProperty("kindFilter");

        Assert.Equal("string", kindFilter.GetProperty("type").GetString());
        var description = kindFilter.GetProperty("description").GetString();
        Assert.Contains("SymbolKind", description);
        Assert.Contains("diagnostic site", description);
        Assert.Contains("find_references", description);
        Assert.Contains("before maxResults", description);
        Assert.Contains("InvalidSymbolKind", description);

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("kindFilter", required);
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("kindFilter", _tool.Description);
    }

    [Fact]
    public async Task ExecuteAsync_KindFilterString_ParsesAndReachesWorkspace()
    {
        // Arrange - well-formed kindFilter parses; the throwing provider proves parsing succeeded
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""kindFilter"": ""Method""
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Fact]
    public async Task ExecuteAsync_KindFilterNonString_ReturnsParseError()
    {
        // Arrange - a number is not a string; deserialization fails before workspace creation
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""kindFilter"": 42
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Fact]
    public void GetDefinition_AccessibilityFilter_IsOptionalString()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var accessibilityFilter = doc.RootElement.GetProperty("properties").GetProperty("accessibilityFilter");

        Assert.Equal("string", accessibilityFilter.GetProperty("type").GetString());
        Assert.Contains("DeclaredAccessibility", accessibilityFilter.GetProperty("description").GetString());
        Assert.Contains("diagnostic site", accessibilityFilter.GetProperty("description").GetString());
        Assert.Contains("find_references", accessibilityFilter.GetProperty("description").GetString());
        Assert.Contains("before maxResults", accessibilityFilter.GetProperty("description").GetString());
        Assert.Contains("InvalidVisibility", accessibilityFilter.GetProperty("description").GetString());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("accessibilityFilter", required);
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("accessibilityFilter", _tool.Description);
    }

    [Fact]
    public async Task ExecuteAsync_AccessibilityFilterString_ParsesAndReachesWorkspace()
    {
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""accessibilityFilter"": ""private protected""
        }").RootElement;

        var result = await _tool.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Fact]
    public async Task ExecuteAsync_AccessibilityFilterNonString_ReturnsParseError()
    {
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""accessibilityFilter"": 42
        }").RootElement;

        var result = await _tool.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.DoesNotContain("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Fact]
    public void GetDefinition_StaticFilter_IsOptionalBoolean()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var staticFilter = doc.RootElement.GetProperty("properties").GetProperty("staticFilter");

        Assert.Equal("boolean", staticFilter.GetProperty("type").GetString());
        var description = staticFilter.GetProperty("description").GetString();
        Assert.Contains("diagnostic site", description);
        Assert.Contains("IsStatic", description);
        Assert.Contains("find_references", description);
        Assert.Contains("find_callers", description);
        Assert.Contains("find_implementations", description);
        Assert.Contains("before maxResults", description);
        Assert.Contains("INTERNAL_ERROR", description);
        Assert.Contains("never match either value", description);

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("staticFilter", required);
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("staticFilter", _tool.Description);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    public async Task ExecuteAsync_StaticFilterBoolean_ParsesAndReachesWorkspace(string value)
    {
        // Arrange - a JSON boolean (or null) parses; the throwing provider proves parsing succeeded
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""staticFilter"": " + value + @"
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", GetResultText(result));
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    public async Task ExecuteAsync_StaticFilterNonBoolean_ReturnsInternalError(string value)
    {
        // Arrange - a non-boolean fails deserialization before workspace creation
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln"",
            ""staticFilter"": " + value + @"
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
        var text = GetResultText(result);
        Assert.Contains("INTERNAL_ERROR", text);
        Assert.DoesNotContain("Workspace creation should not have been attempted", text);
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
