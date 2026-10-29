using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for GetSymbolInfoTool.
/// Tests tool definition and argument validation.
/// </summary>
public class GetSymbolInfoToolTests
{
    private readonly GetSymbolInfoTool _tool;

    public GetSymbolInfoToolTests()
    {
        // Fails loudly if workspace creation is attempted; argument validation is exercised via ExecuteAsync error paths.
        _tool = new GetSymbolInfoTool(new ThrowingWorkspaceProvider());
    }

    #region GetDefinition Tests

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        // Assert
        Assert.Equal("get_symbol_info", _tool.Name);
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
    public void GetDefinition_HasRequiredFields_SolutionPath_SourceFile()
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
        Assert.Contains("sourceFile", requiredFields);
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

        // Assert - Optional properties
        Assert.True(properties.TryGetProperty("symbolName", out _));
        Assert.True(properties.TryGetProperty("line", out _));
        Assert.True(properties.TryGetProperty("column", out _));
        Assert.True(properties.TryGetProperty("maxResults", out _));
        Assert.True(properties.TryGetProperty("kindFilter", out _));
        Assert.True(properties.TryGetProperty("accessibilityFilter", out _));
        Assert.True(properties.TryGetProperty("nameFilter", out _));
    }

    [Fact]
    public void GetDefinition_MaxResults_IsOptionalIntegerWithMinimumOne()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var maxResults = doc.RootElement.GetProperty("properties").GetProperty("maxResults");

        Assert.Equal("integer", maxResults.GetProperty("type").GetString());
        Assert.Equal(1, maxResults.GetProperty("minimum").GetInt32());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("maxResults", required);
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void GetDefinition_DescriptionMentionsMaxResults()
    {
        Assert.Contains("maxResults", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Members", _tool.Description, StringComparison.Ordinal);
    }


    [Fact]
    public void GetDefinition_KindFilter_IsOptionalString()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var kindFilter = doc.RootElement.GetProperty("properties").GetProperty("kindFilter");

        Assert.Equal("string", kindFilter.GetProperty("type").GetString());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("kindFilter", required);
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void GetDefinition_AccessibilityFilter_IsOptionalString()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var accessibilityFilter = doc.RootElement.GetProperty("properties").GetProperty("accessibilityFilter");

        Assert.Equal("string", accessibilityFilter.GetProperty("type").GetString());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("accessibilityFilter", required);
    }

    [Fact]
    public void GetDefinition_DescriptionMentionsAccessibilityFilter()
    {
        Assert.Contains("accessibilityFilter", _tool.Description, StringComparison.Ordinal);
        Assert.Contains("applied before maxResults", _tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDefinition_NameFilter_IsOptionalString()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var nameFilter = doc.RootElement.GetProperty("properties").GetProperty("nameFilter");

        Assert.Equal("string", nameFilter.GetProperty("type").GetString());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("nameFilter", required);
    }

    [Fact]
    public void GetDefinition_DescriptionMentionsNameFilter()
    {
        Assert.Contains("nameFilter", _tool.Description, StringComparison.Ordinal);
        Assert.Contains("applied before maxResults", _tool.Description, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void GetDefinition_NamespaceFilter_IsOptionalString()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var namespaceFilter = doc.RootElement.GetProperty("properties").GetProperty("namespaceFilter");

        Assert.Equal("string", namespaceFilter.GetProperty("type").GetString());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("namespaceFilter", required);
    }

    [Fact]
    public void GetDefinition_DescriptionMentionsNamespaceFilter()
    {
        Assert.Contains("namespaceFilter", _tool.Description, StringComparison.Ordinal);
        Assert.Contains("applied before maxResults", _tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDefinition_DescriptionMentionsKindFilter()
    {
        Assert.Contains("kindFilter", _tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("before maxResults", _tool.Description, StringComparison.OrdinalIgnoreCase);
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
    public async Task ExecuteAsync_MissingSourceFile_ReturnsError()
    {
        // Arrange - Has solutionPath but missing sourceFile
        var args = JsonDocument.Parse(@"{
            ""solutionPath"": ""C:/test/test.sln""
        }").RootElement;

        // Act
        var result = await _tool.ExecuteAsync(args);

        // Assert
        Assert.True(result.IsError);
    }

    #endregion

    #region Helper Methods

    private static string GetResultText(ToolResult result)
    {
        return result.Content.FirstOrDefault()?.Text ?? string.Empty;
    }

    #endregion

    [Fact]
    public void GetDefinition_StaticFilter_IsOptionalBoolean()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var staticFilter = doc.RootElement.GetProperty("properties").GetProperty("staticFilter");

        Assert.Equal("boolean", staticFilter.GetProperty("type").GetString());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("staticFilter", required);
    }

    [Fact]
    public void GetDefinition_DescriptionMentionsStaticFilter()
    {
        Assert.Contains("staticFilter", _tool.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    public async Task ExecuteAsync_StaticFilterNonBoolean_ReturnsInternalError(string rawValue)
    {
        // A non-boolean staticFilter fails deserialization before workspace creation.
        var args = JsonDocument.Parse($$"""
            {
                "solutionPath": "C:/test/test.sln",
                "sourceFile": "C:/test/Foo.cs",
                "symbolName": "Foo",
                "staticFilter": {{rawValue}}
            }
            """).RootElement;

        var result = await _tool.ExecuteAsync(args);

        Assert.True(result.IsError);
        var text = GetResultText(result);
        Assert.Contains("INTERNAL_ERROR", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Workspace creation should not have been attempted", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_StaticFilterBoolean_ParsesAndReachesWorkspace()
    {
        var args = JsonDocument.Parse("""
            {
                "solutionPath": "C:/test/test.sln",
                "sourceFile": "C:/test/Foo.cs",
                "symbolName": "Foo",
                "staticFilter": false
            }
            """).RootElement;

        var result = await _tool.ExecuteAsync(args);

        Assert.True(result.IsError);
        var text = GetResultText(result);
        Assert.Contains("Workspace creation should not have been attempted", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GetDefinition_AbstractFilter_IsOptionalBoolean()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var abstractFilter = doc.RootElement.GetProperty("properties").GetProperty("abstractFilter");

        Assert.Equal("boolean", abstractFilter.GetProperty("type").GetString());

        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("abstractFilter", required);
        Assert.Contains("abstractFilter", _tool.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void GetDefinition_AbstractFilter_DocumentsEdgeSemanticsAndCliRules()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var doc = JsonDocument.Parse(json);
        var schemaText = doc.RootElement.GetProperty("properties").GetProperty("abstractFilter")
            .GetProperty("description").GetString();
        Assert.NotNull(schemaText);
        var maxResultsText = doc.RootElement.GetProperty("properties").GetProperty("maxResults")
            .GetProperty("description").GetString();
        Assert.Contains("abstractFilter", maxResultsText, StringComparison.Ordinal);

        foreach (var text in new[] { schemaText, _tool.Description })
        {
            Assert.Contains("interface members without a default body including static abstract members", text, StringComparison.Ordinal);
            Assert.Contains("a static class is static but not abstract", text, StringComparison.Ordinal);
            Assert.Contains("staticFilter true + abstractFilter true keeps static abstract interface members", text, StringComparison.Ordinal);
            Assert.Contains("CLI: bare --abstract-filter = true", text, StringComparison.Ordinal);
            Assert.Contains("a value other than true / false (case-insensitive) is rejected", text, StringComparison.Ordinal);
            Assert.Contains("the target type itself is always returned whatever its own IsAbstract", text, StringComparison.Ordinal);
            Assert.Contains("a nested abstract class with no abstract members still matches true", text, StringComparison.Ordinal);
            Assert.Contains("An abstract event matches true and a field-like event false", text, StringComparison.Ordinal);
            Assert.Contains("System.IO.Stream", text, StringComparison.Ordinal);
            Assert.Contains("search_symbols abstractFilter judges every name-matched symbol, is source-only, and drops namespaces", text, StringComparison.Ordinal);
            Assert.Contains("get_type_hierarchy abstractFilter judges only derived types (including metadata) and returns empty for an interface root", text, StringComparison.Ordinal);
            Assert.Contains("find_implementations abstractFilter judges only implementers, counts metadata implementers in totalCount, and returns nothing for a class target", text, StringComparison.Ordinal);
            Assert.DoesNotContain("same ISymbol.IsAbstract test", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    public async Task ExecuteAsync_AbstractFilterNonBoolean_ReturnsInternalError(string rawValue)
    {
        // A non-boolean abstractFilter fails deserialization before workspace creation.
        var args = JsonDocument.Parse($$"""
            {
                "solutionPath": "C:/test/test.sln",
                "sourceFile": "C:/test/Foo.cs",
                "symbolName": "Foo",
                "abstractFilter": {{rawValue}}
            }
            """).RootElement;

        var result = await _tool.ExecuteAsync(args);

        Assert.True(result.IsError);
        var text = GetResultText(result);
        Assert.Contains("INTERNAL_ERROR", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Workspace creation should not have been attempted", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task ExecuteAsync_AbstractFilterBoolean_ParsesAndReachesWorkspace(string rawValue)
    {
        var args = JsonDocument.Parse($$"""
            {
                "solutionPath": "C:/test/test.sln",
                "sourceFile": "C:/test/Foo.cs",
                "symbolName": "Foo",
                "abstractFilter": {{rawValue}}
            }
            """).RootElement;

        var result = await _tool.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("Workspace creation should not have been attempted", GetResultText(result), StringComparison.Ordinal);
    }
}
