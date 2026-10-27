using System.Text.Json;
using System.Text.Json.Serialization;
using RoslynMcp.Cli;
using RoslynMcp.Contracts.Models;
using Xunit;

namespace RoslynMcp.Cli.Tests;

public class ArgsToJsonConverterTests
{
    [Fact]
    public void EmptyDictionary_ReturnsEmptyJsonObject()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>());
        Assert.Equal("{}", json);
    }

    [Fact]
    public void StringValue_SerializedAsJsonString()
    {
        var dict = new Dictionary<string, string> { ["source-file"] = "Foo.cs" };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Foo.cs", doc.RootElement.GetProperty("sourceFile").GetString());
    }

    [Fact]
    public void NumericValue_SerializedAsJsonNumber()
    {
        var dict = new Dictionary<string, string> { ["line"] = "42" };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(42, doc.RootElement.GetProperty("line").GetInt32());
    }

    [Fact]
    public void BooleanTrue_SerializedAsJsonBool()
    {
        var dict = new Dictionary<string, string> { ["preview"] = "true" };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("preview").GetBoolean());
    }

    [Fact]
    public void BooleanFalse_SerializedAsJsonBool()
    {
        var dict = new Dictionary<string, string> { ["preview"] = "false" };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("preview").GetBoolean());
    }

    [Fact]
    public void MultipleOptions_AllConverted()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "C:/Code/Foo.cs",
            ["symbol-name"] = "Bar",
            ["line"] = "10",
            ["preview"] = "true"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("C:/Code/Foo.cs", doc.RootElement.GetProperty("sourceFile").GetString());
        Assert.Equal("Bar", doc.RootElement.GetProperty("symbolName").GetString());
        Assert.Equal(10, doc.RootElement.GetProperty("line").GetInt32());
        Assert.True(doc.RootElement.GetProperty("preview").GetBoolean());
    }

    [Theory]
    [InlineData("source-file", "sourceFile")]
    [InlineData("line", "line")]
    [InlineData("symbol-name", "symbolName")]
    [InlineData("severity-filter", "severityFilter")]
    [InlineData("new-name", "newName")]
    [InlineData("all-files", "allFiles")]
    [InlineData("a-b-c-d", "aBCD")]
    public void KebabToCamel_ConvertsCorrectly(string kebab, string expected)
    {
        Assert.Equal(expected, ArgsToJsonConverter.KebabToCamel(kebab));
    }

    [Fact]
    public void KebabToCamel_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", ArgsToJsonConverter.KebabToCamel(""));
    }

    [Fact]
    public void PathWithBackslash_PreservedAsString()
    {
        var dict = new Dictionary<string, string> { ["source-file"] = @"C:\Code\Foo.cs" };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(@"C:\Code\Foo.cs", doc.RootElement.GetProperty("sourceFile").GetString());
    }

    [Fact]
    public void LargeNumericValue_SerializedAsJsonNumber()
    {
        // 3_000_000_000 exceeds int.MaxValue (2_147_483_647) but fits in long
        var dict = new Dictionary<string, string> { ["big-value"] = "3000000000" };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(3_000_000_000L, doc.RootElement.GetProperty("bigValue").GetInt64());
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("TRUE", true)]
    public void SearchSymbols_CaseSensitive_RoundTripsToParams(string value, bool expected)
    {
        var dict = new Dictionary<string, string>
        {
            ["query"] = "ID",
            ["case-sensitive"] = value
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, doc.RootElement.GetProperty("caseSensitive").GetBoolean());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.SearchSymbolsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("ID", p.Query);
        Assert.Equal(expected, p.CaseSensitive);
    }

    [Fact]
    public void SearchSymbols_CaseSensitiveOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string> { ["query"] = "ID" });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.SearchSymbolsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.CaseSensitive);
    }

    [Fact]
    public void FindCallers_CallerFileOption_BindsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run",
            ["caller-file"] = "/src/CallerA.cs"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindCallersParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;

        Assert.Equal("/src/Target.cs", p.SourceFile);
        Assert.Equal("Run", p.SymbolName);
        Assert.Equal("/src/CallerA.cs", p.CallerFile);
    }

    [Fact]
    public void FindCallers_NamespaceFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run",
            ["namespace-filter"] = "App.Services",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("App.Services", doc.RootElement.GetProperty("namespaceFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindCallersParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("App.Services", p.NamespaceFilter);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void FindCallers_NamespaceFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindCallersParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.NamespaceFilter);
    }

    [Fact]
    public void FindCallers_AccessibilityFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run",
            ["accessibility-filter"] = "private protected",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("private protected", doc.RootElement.GetProperty("accessibilityFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindCallersParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("private protected", p.AccessibilityFilter);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void FindCallers_AccessibilityFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindCallersParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.AccessibilityFilter);
    }

    [Fact]
    public void FindCallers_NameFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run",
            ["name-filter"] = "Handle",
            ["accessibility-filter"] = "public",
            ["max-results"] = "2"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Handle", doc.RootElement.GetProperty("nameFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindCallersParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Handle", p.NameFilter);
        Assert.Equal("public", p.AccessibilityFilter);
        Assert.Equal(2, p.MaxResults);
    }

    [Fact]
    public void FindCallers_NameFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindCallersParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.NameFilter);
    }

    [Fact]
    public void FindCallers_KindFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run",
            ["kind-filter"] = "Property",
            ["name-filter"] = "Total",
            ["max-results"] = "2"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Property", doc.RootElement.GetProperty("kindFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindCallersParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Property", p.KindFilter);
        Assert.Equal("Total", p.NameFilter);
        Assert.Equal(2, p.MaxResults);
    }

    [Fact]
    public void FindCallers_KindFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindCallersParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.KindFilter);
    }

    [Fact]
    public void FindReferences_ReferenceFileOption_BindsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run",
            ["reference-file"] = "/src/UserA.cs"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindReferencesParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;

        Assert.Equal("/src/Target.cs", p.SourceFile);
        Assert.Equal("Run", p.SymbolName);
        Assert.Equal("/src/UserA.cs", p.ReferenceFile);
    }

    [Fact]
    public void FindImplementations_ImplementationFileOption_BindsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/IRunner.cs",
            ["symbol-name"] = "IRunner",
            ["implementation-file"] = "/src/ImplA.cs"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindImplementationsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;

        Assert.Equal("/src/IRunner.cs", p.SourceFile);
        Assert.Equal("IRunner", p.SymbolName);
        Assert.Equal("/src/ImplA.cs", p.ImplementationFile);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void FindImplementations_TransitiveOption_RoundTripsToParams(string value, bool expected)
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/IRunner.cs",
            ["symbol-name"] = "IRunner",
            ["transitive"] = value
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, doc.RootElement.GetProperty("transitive").GetBoolean());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindImplementationsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("IRunner", p.SymbolName);
        Assert.Equal(expected, p.Transitive);
    }

    [Fact]
    public void FindImplementations_TransitiveOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/IRunner.cs",
            ["symbol-name"] = "IRunner"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindImplementationsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.Transitive);
    }

    [Fact]
    public void FindReferences_NamespaceFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run",
            ["namespace-filter"] = "App.Services",
            ["include-declaration"] = "false",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("App.Services", doc.RootElement.GetProperty("namespaceFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindReferencesParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("App.Services", p.NamespaceFilter);
        Assert.False(p.IncludeDeclaration);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void FindReferences_NamespaceFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindReferencesParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.NamespaceFilter);
    }

    [Fact]
    public void FindReferences_AccessibilityFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run",
            ["accessibility-filter"] = "private protected",
            ["include-declaration"] = "false",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("private protected", doc.RootElement.GetProperty("accessibilityFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindReferencesParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("private protected", p.AccessibilityFilter);
        Assert.False(p.IncludeDeclaration);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void FindReferences_AccessibilityFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindReferencesParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.AccessibilityFilter);
    }

    [Fact]
    public void FindReferences_NameFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run",
            ["name-filter"] = "Handle",
            ["accessibility-filter"] = "public",
            ["max-results"] = "2"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Handle", doc.RootElement.GetProperty("nameFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindReferencesParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Handle", p.NameFilter);
        Assert.Equal("public", p.AccessibilityFilter);
        Assert.Equal(2, p.MaxResults);
    }

    [Fact]
    public void FindReferences_NameFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindReferencesParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.NameFilter);
    }

    [Fact]
    public void FindReferences_KindFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run",
            ["kind-filter"] = "Method",
            ["name-filter"] = "Handle",
            ["max-results"] = "2"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Method", doc.RootElement.GetProperty("kindFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindReferencesParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Method", p.KindFilter);
        Assert.Equal("Handle", p.NameFilter);
        Assert.Equal(2, p.MaxResults);
    }

    [Fact]
    public void FindReferences_KindFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Target.cs",
            ["symbol-name"] = "Run"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindReferencesParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.KindFilter);
    }

    [Fact]
    public void FindImplementations_NamespaceFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/IRunner.cs",
            ["symbol-name"] = "IRunner",
            ["namespace-filter"] = "App.Services",
            ["transitive"] = "false",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("App.Services", doc.RootElement.GetProperty("namespaceFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindImplementationsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("App.Services", p.NamespaceFilter);
        Assert.False(p.Transitive);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void FindImplementations_NamespaceFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/IRunner.cs",
            ["symbol-name"] = "IRunner"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindImplementationsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.NamespaceFilter);
    }

    [Fact]
    public void FindImplementations_AccessibilityFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/IRunner.cs",
            ["symbol-name"] = "IRunner",
            ["accessibility-filter"] = "private protected",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("private protected", doc.RootElement.GetProperty("accessibilityFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindImplementationsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("private protected", p.AccessibilityFilter);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void FindImplementations_AccessibilityFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/IRunner.cs",
            ["symbol-name"] = "IRunner"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindImplementationsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.AccessibilityFilter);
    }

    [Fact]
    public void FindImplementations_KindFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/IRunner.cs",
            ["symbol-name"] = "IRunner",
            ["kind-filter"] = "Struct",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Struct", doc.RootElement.GetProperty("kindFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindImplementationsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Struct", p.KindFilter);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void FindImplementations_KindFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/IRunner.cs",
            ["symbol-name"] = "IRunner"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindImplementationsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.KindFilter);
    }

    [Fact]
    public void FindImplementations_NameFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/IRepository.cs",
            ["symbol-name"] = "IRepository",
            ["name-filter"] = "Sql",
            ["kind-filter"] = "Class",
            ["max-results"] = "2"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Sql", doc.RootElement.GetProperty("nameFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindImplementationsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Sql", p.NameFilter);
        Assert.Equal("Class", p.KindFilter);
        Assert.Equal(2, p.MaxResults);
    }

    [Fact]
    public void FindImplementations_NameFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/IRepository.cs",
            ["symbol-name"] = "IRepository"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.FindImplementationsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.NameFilter);
    }

    [Fact]
    public void GetTypeHierarchy_DerivedFileOption_BindsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Shape.cs",
            ["symbol-name"] = "Shape",
            ["derived-file"] = "/src/Circles.cs"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetTypeHierarchyParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;

        Assert.Equal("/src/Shape.cs", p.SourceFile);
        Assert.Equal("Shape", p.SymbolName);
        Assert.Equal("/src/Circles.cs", p.DerivedFile);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void GetTypeHierarchy_TransitiveOption_RoundTripsToParams(string value, bool expected)
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Shape.cs",
            ["symbol-name"] = "Shape",
            ["transitive"] = value
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, doc.RootElement.GetProperty("transitive").GetBoolean());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetTypeHierarchyParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Shape", p.SymbolName);
        Assert.Equal(expected, p.Transitive);
    }

    [Fact]
    public void GetTypeHierarchy_TransitiveOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Shape.cs",
            ["symbol-name"] = "Shape"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetTypeHierarchyParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.Transitive);
    }

    [Fact]
    public void GetTypeHierarchy_NamespaceFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Shape.cs",
            ["symbol-name"] = "Shape",
            ["namespace-filter"] = "App.Services",
            ["transitive"] = "false",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("App.Services", doc.RootElement.GetProperty("namespaceFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetTypeHierarchyParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("App.Services", p.NamespaceFilter);
        Assert.False(p.Transitive);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void GetTypeHierarchy_NamespaceFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Shape.cs",
            ["symbol-name"] = "Shape"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetTypeHierarchyParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.NamespaceFilter);
    }

    [Fact]
    public void GetTypeHierarchy_AccessibilityFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Shape.cs",
            ["symbol-name"] = "Shape",
            ["accessibility-filter"] = "private protected",
            ["namespace-filter"] = "App.Services",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("private protected", doc.RootElement.GetProperty("accessibilityFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetTypeHierarchyParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("private protected", p.AccessibilityFilter);
        Assert.Equal("App.Services", p.NamespaceFilter);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void GetTypeHierarchy_AccessibilityFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Shape.cs",
            ["symbol-name"] = "Shape"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetTypeHierarchyParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.AccessibilityFilter);
    }

    [Fact]
    public void GetTypeHierarchy_KindFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Entity.cs",
            ["symbol-name"] = "Entity",
            ["kind-filter"] = "Record",
            ["accessibility-filter"] = "internal",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Record", doc.RootElement.GetProperty("kindFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetTypeHierarchyParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Record", p.KindFilter);
        Assert.Equal("internal", p.AccessibilityFilter);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void GetTypeHierarchy_KindFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Shape.cs",
            ["symbol-name"] = "Shape"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetTypeHierarchyParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.KindFilter);
    }

    [Fact]
    public void GetTypeHierarchy_NameFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Controller.cs",
            ["symbol-name"] = "Controller",
            ["name-filter"] = "Admin",
            ["kind-filter"] = "Class",
            ["max-results"] = "2"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Admin", doc.RootElement.GetProperty("nameFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetTypeHierarchyParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Admin", p.NameFilter);
        Assert.Equal("Class", p.KindFilter);
        Assert.Equal(2, p.MaxResults);
    }

    [Fact]
    public void GetTypeHierarchy_NameFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Controller.cs",
            ["symbol-name"] = "Controller"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetTypeHierarchyParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.NameFilter);
    }

    [Fact]
    public void GoToDefinition_DefinitionFileOption_BindsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Customer.cs",
            ["symbol-name"] = "Customer",
            ["definition-file"] = "/src/Customer.Generated.cs"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GoToDefinitionParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;

        Assert.Equal("/src/Customer.cs", p.SourceFile);
        Assert.Equal("Customer", p.SymbolName);
        Assert.Equal("/src/Customer.Generated.cs", p.DefinitionFile);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("TRUE", true)]
    public void SearchSymbols_ExactMatch_RoundTripsToParams(string value, bool expected)
    {
        var dict = new Dictionary<string, string>
        {
            ["query"] = "Add",
            ["exact-match"] = value
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, doc.RootElement.GetProperty("exactMatch").GetBoolean());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.SearchSymbolsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Add", p.Query);
        Assert.Equal(expected, p.ExactMatch);
    }

    [Fact]
    public void SearchSymbols_ExactMatchOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string> { ["query"] = "Add" });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.SearchSymbolsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.ExactMatch);
    }

    [Fact]
    public void SearchSymbols_NamespaceFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["query"] = "Widget",
            ["namespace-filter"] = "App.Services"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("App.Services", doc.RootElement.GetProperty("namespaceFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.SearchSymbolsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Widget", p.Query);
        Assert.Equal("App.Services", p.NamespaceFilter);
    }

    [Fact]
    public void SearchSymbols_NamespaceFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string> { ["query"] = "Widget" });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.SearchSymbolsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.NamespaceFilter);
    }

    [Fact]
    public void GetDocumentOutline_KindFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Foo.cs",
            ["kind-filter"] = "Method"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Method", doc.RootElement.GetProperty("kindFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetDocumentOutlineParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("/src/Foo.cs", p.SourceFile);
        Assert.Equal("Method", p.KindFilter);
    }

    [Fact]
    public void GetDocumentOutline_KindFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string> { ["source-file"] = "/src/Foo.cs" });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetDocumentOutlineParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.KindFilter);
    }

    [Fact]
    public void GetSymbolInfo_KindFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Foo.cs",
            ["symbol-name"] = "Widget",
            ["kind-filter"] = "Method",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Method", doc.RootElement.GetProperty("kindFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetSymbolInfoParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("/src/Foo.cs", p.SourceFile);
        Assert.Equal("Widget", p.SymbolName);
        Assert.Equal("Method", p.KindFilter);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void GetSymbolInfo_AccessibilityFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Foo.cs",
            ["symbol-name"] = "Widget",
            ["accessibility-filter"] = "protected internal",
            ["kind-filter"] = "Method",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("protected internal", doc.RootElement.GetProperty("accessibilityFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetSymbolInfoParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("protected internal", p.AccessibilityFilter);
        Assert.Equal("Method", p.KindFilter);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void GetSymbolInfo_AccessibilityFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Foo.cs",
            ["symbol-name"] = "Widget"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetSymbolInfoParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.AccessibilityFilter);
    }

    [Fact]
    public void GetSymbolInfo_NameFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Foo.cs",
            ["symbol-name"] = "Widget",
            ["name-filter"] = "Handle",
            ["kind-filter"] = "Method",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Handle", doc.RootElement.GetProperty("nameFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetSymbolInfoParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Handle", p.NameFilter);
        Assert.Equal("Method", p.KindFilter);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void GetSymbolInfo_NameFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Foo.cs",
            ["symbol-name"] = "Widget"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetSymbolInfoParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.NameFilter);
    }


    [Fact]
    public void GetSymbolInfo_NamespaceFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Foo.cs",
            ["symbol-name"] = "Widget",
            ["namespace-filter"] = "App.Services",
            ["name-filter"] = "Handle",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("App.Services", doc.RootElement.GetProperty("namespaceFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetSymbolInfoParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("App.Services", p.NamespaceFilter);
        Assert.Equal("Handle", p.NameFilter);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void GetSymbolInfo_NamespaceFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Foo.cs",
            ["symbol-name"] = "Widget"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetSymbolInfoParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.NamespaceFilter);
    }

    [Fact]
    public void GetSymbolInfo_KindFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["source-file"] = "/src/Foo.cs",
            ["symbol-name"] = "Widget"
        });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetSymbolInfoParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.KindFilter);
    }

    [Fact]
    public void GetDocumentOutline_NameFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Foo.cs",
            ["name-filter"] = "Handle",
            ["kind-filter"] = "Method"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Handle", doc.RootElement.GetProperty("nameFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetDocumentOutlineParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("/src/Foo.cs", p.SourceFile);
        Assert.Equal("Handle", p.NameFilter);
        Assert.Equal("Method", p.KindFilter);
    }

    [Fact]
    public void GetDocumentOutline_NameFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string> { ["source-file"] = "/src/Foo.cs" });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.GetDocumentOutlineParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.NameFilter);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("123")]
    [InlineData("-1")]
    public void GetDocumentOutline_TypeAware_BooleanOrNumericLookingNameFilter_StaysString(string value)
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Foo.cs",
            ["name-filter"] = value,
            ["max-depth"] = "2",
            ["max-results"] = "10"
        };
        var json = ArgsToJsonConverter.Convert(dict, typeof(GetDocumentOutlineParams));
        var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("nameFilter").ValueKind);
        Assert.Equal(value, doc.RootElement.GetProperty("nameFilter").GetString());

        // Non-string parameters keep numeric inference.
        Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("maxDepth").ValueKind);

        var p = JsonSerializer.Deserialize<GetDocumentOutlineParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal(value, p.NameFilter);
        Assert.Equal(2, p.MaxDepth);
        Assert.Equal(10, p.MaxResults);
    }

    [Fact]
    public void SearchSymbols_TypeAware_NumericQuery_StaysStringAndBooleanOptionStaysBoolean()
    {
        var dict = new Dictionary<string, string>
        {
            ["query"] = "123",
            ["case-sensitive"] = "true"
        };
        var json = ArgsToJsonConverter.Convert(dict, typeof(SearchSymbolsParams));

        var p = JsonSerializer.Deserialize<SearchSymbolsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("123", p.Query);
        Assert.True(p.CaseSensitive);
    }

    [Fact]
    public void StringListOption_CommaSeparated_SerializedAsJsonArray()
    {
        var dict = new Dictionary<string, string> { ["exclude-diagnostic-ids"] = "CS1591, CS8019 ,CS0168" };
        var json = ArgsToJsonConverter.Convert(dict, typeof(GetDiagnosticsParams));
        var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.GetProperty("excludeDiagnosticIds");
        Assert.Equal(JsonValueKind.Array, ids.ValueKind);
        Assert.Equal(new[] { "CS1591", "CS8019", "CS0168" }, ids.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void StringListOption_SingleValue_SerializedAsSingleElementArray()
    {
        var dict = new Dictionary<string, string> { ["diagnostic-ids"] = "CS0168" };
        var json = ArgsToJsonConverter.Convert(dict, typeof(GetDiagnosticsParams));
        var doc = JsonDocument.Parse(json);
        Assert.Equal(new[] { "CS0168" }, doc.RootElement.GetProperty("diagnosticIds").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void StringListOption_JsonArrayLiteral_UsedAsIs()
    {
        var dict = new Dictionary<string, string> { ["exclude-diagnostic-ids"] = "[\"CS1591\", \"CS8019\"]" };
        var json = ArgsToJsonConverter.Convert(dict, typeof(GetDiagnosticsParams));
        var doc = JsonDocument.Parse(json);
        Assert.Equal(new[] { "CS1591", "CS8019" }, doc.RootElement.GetProperty("excludeDiagnosticIds").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void StringListOption_BlankValue_SerializedAsEmptyArray()
    {
        var dict = new Dictionary<string, string> { ["exclude-diagnostic-ids"] = "  " };
        var json = ArgsToJsonConverter.Convert(dict, typeof(GetDiagnosticsParams));
        var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("excludeDiagnosticIds").GetArrayLength());
    }

    [Fact]
    public void StringListOption_WithoutParamsType_KeepsStringBehavior()
    {
        var dict = new Dictionary<string, string> { ["exclude-diagnostic-ids"] = "CS1591,CS8019" };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("CS1591,CS8019", doc.RootElement.GetProperty("excludeDiagnosticIds").GetString());
    }

    [Fact]
    public void ParamsType_NonListOptions_KeepScalarRules()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "C:/Code/Foo.cs",
            ["max-results"] = "5",
            ["severity-filter"] = "Error"
        };
        var json = ArgsToJsonConverter.Convert(dict, typeof(GetDiagnosticsParams));
        var doc = JsonDocument.Parse(json);
        Assert.Equal("C:/Code/Foo.cs", doc.RootElement.GetProperty("sourceFile").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("maxResults").GetInt32());
        Assert.Equal("Error", doc.RootElement.GetProperty("severityFilter").GetString());
    }

    [Fact]
    public void GetDiagnostics_NamespaceFilter_RoundTripsToParams()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("get-diagnostics")!;
        var dict = new Dictionary<string, string>
        {
            ["namespace-filter"] = "App.Services",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict, tool.ParamsType);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("App.Services", doc.RootElement.GetProperty("namespaceFilter").GetString());

        var p = JsonSerializer.Deserialize<GetDiagnosticsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("App.Services", p.NamespaceFilter);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void GetDiagnostics_MessageFilter_RoundTripsToParams()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("get-diagnostics")!;
        var dict = new Dictionary<string, string>
        {
            ["message-filter"] = "never used",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict, tool.ParamsType);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("never used", doc.RootElement.GetProperty("messageFilter").GetString());

        var p = JsonSerializer.Deserialize<GetDiagnosticsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("never used", p.MessageFilter);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void GetDiagnostics_MessageFilterNumericLooking_StaysString()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("get-diagnostics")!;
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["message-filter"] = "1030"
        }, tool.ParamsType);

        var p = JsonSerializer.Deserialize<GetDiagnosticsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("1030", p.MessageFilter);
    }


    [Fact]
    public void GetDiagnostics_KindFilter_RoundTripsToParams()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("get-diagnostics")!;
        var dict = new Dictionary<string, string>
        {
            ["kind-filter"] = "Method",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict, tool.ParamsType);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Method", doc.RootElement.GetProperty("kindFilter").GetString());

        var p = JsonSerializer.Deserialize<GetDiagnosticsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Method", p.KindFilter);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void GetDiagnostics_NameFilter_RoundTripsToParams()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("get-diagnostics")!;
        var dict = new Dictionary<string, string>
        {
            ["name-filter"] = "Handle",
            ["kind-filter"] = "Method",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict, tool.ParamsType);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Handle", doc.RootElement.GetProperty("nameFilter").GetString());

        var p = JsonSerializer.Deserialize<GetDiagnosticsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Handle", p.NameFilter);
        Assert.Equal("Method", p.KindFilter);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void GetDiagnostics_NameFilterOmitted_DeserializesAsNull()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("get-diagnostics")!;
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["severity-filter"] = "Error"
        }, tool.ParamsType);

        var p = JsonSerializer.Deserialize<GetDiagnosticsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.NameFilter);
    }

    [Fact]
    public void GetDiagnostics_KindFilterOmitted_DeserializesAsNull()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("get-diagnostics")!;
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["severity-filter"] = "Error"
        }, tool.ParamsType);

        var p = JsonSerializer.Deserialize<GetDiagnosticsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.KindFilter);
    }


    [Fact]
    public void GetDiagnostics_NamespaceFilterOmitted_DeserializesAsNull()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("get-diagnostics")!;
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["severity-filter"] = "Error"
        }, tool.ParamsType);

        var p = JsonSerializer.Deserialize<GetDiagnosticsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.NamespaceFilter);
        Assert.Null(p.MessageFilter);
        Assert.Null(p.KindFilter);
    }


    [Fact]
    public void GetDiagnostics_AccessibilityFilter_RoundTripsToParams()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("get-diagnostics")!;
        var dict = new Dictionary<string, string>
        {
            ["accessibility-filter"] = "private protected",
            ["namespace-filter"] = "App.Services",
            ["max-results"] = "3"
        };
        var json = ArgsToJsonConverter.Convert(dict, tool.ParamsType);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("private protected", doc.RootElement.GetProperty("accessibilityFilter").GetString());

        var p = JsonSerializer.Deserialize<GetDiagnosticsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("private protected", p.AccessibilityFilter);
        Assert.Equal("App.Services", p.NamespaceFilter);
        Assert.Equal(3, p.MaxResults);
    }

    [Fact]
    public void GetDiagnostics_AccessibilityFilterOmitted_DeserializesAsNull()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("get-diagnostics")!;
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["severity-filter"] = "Error"
        }, tool.ParamsType);

        var p = JsonSerializer.Deserialize<GetDiagnosticsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.AccessibilityFilter);
    }

    [Fact]
    public void GetDiagnosticsParams_ListOptions_RoundTripThroughDeserialize()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("get-diagnostics")!;
        var dict = new Dictionary<string, string>
        {
            ["diagnostic-ids"] = "CS1030,CS0168",
            ["exclude-diagnostic-ids"] = "cs0168",
            ["max-results"] = "10"
        };

        var json = ArgsToJsonConverter.Convert(dict, tool.ParamsType);
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var p = JsonSerializer.Deserialize<GetDiagnosticsParams>(json, opts)!;

        Assert.Equal(new[] { "CS1030", "CS0168" }, p.DiagnosticIds);
        Assert.Equal(new[] { "cs0168" }, p.ExcludeDiagnosticIds);
        Assert.Equal(10, p.MaxResults);
    }

    [Fact]
    public void PullMembersUpParams_MembersOption_RoundTripThroughDeserialize()
    {
        var dict = new Dictionary<string, string> { ["members"] = "Foo, Bar" };
        var json = ArgsToJsonConverter.Convert(dict, typeof(PullMembersUpParams));
        var p = JsonSerializer.Deserialize<PullMembersUpParams>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(new[] { "Foo", "Bar" }, p.Members);
    }

    [Fact]
    public void SearchSymbols_AccessibilityFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["query"] = "Widget",
            ["accessibility-filter"] = "protected internal"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("protected internal", doc.RootElement.GetProperty("accessibilityFilter").GetString());

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.SearchSymbolsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Widget", p.Query);
        Assert.Equal("protected internal", p.AccessibilityFilter);
    }

    [Fact]
    public void SearchSymbols_AccessibilityFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string> { ["query"] = "Widget" });

        var p = JsonSerializer.Deserialize<RoslynMcp.Contracts.Models.SearchSymbolsParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.AccessibilityFilter);
    }

    [Fact]
    public void GetDocumentOutline_AccessibilityFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Foo.cs",
            ["accessibility-filter"] = "private protected",
            ["kind-filter"] = "Method"
        };
        var json = ArgsToJsonConverter.Convert(dict, typeof(GetDocumentOutlineParams));
        var doc = JsonDocument.Parse(json);
        Assert.Equal("private protected", doc.RootElement.GetProperty("accessibilityFilter").GetString());

        var p = JsonSerializer.Deserialize<GetDocumentOutlineParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("/src/Foo.cs", p.SourceFile);
        Assert.Equal("private protected", p.AccessibilityFilter);
        Assert.Equal("Method", p.KindFilter);
    }

    [Fact]
    public void GetDocumentOutline_AccessibilityFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string> { ["source-file"] = "/src/Foo.cs" });

        var p = JsonSerializer.Deserialize<GetDocumentOutlineParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.AccessibilityFilter);
    }

    [Fact]
    public void GetDocumentOutline_NamespaceFilter_RoundTripsToParams()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "/src/Foo.cs",
            ["namespace-filter"] = "App.Services",
            ["kind-filter"] = "Method",
            ["max-depth"] = "2"
        };
        var json = ArgsToJsonConverter.Convert(dict, typeof(GetDocumentOutlineParams));
        var doc = JsonDocument.Parse(json);
        Assert.Equal("App.Services", doc.RootElement.GetProperty("namespaceFilter").GetString());

        var p = JsonSerializer.Deserialize<GetDocumentOutlineParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Equal("/src/Foo.cs", p.SourceFile);
        Assert.Equal("App.Services", p.NamespaceFilter);
        Assert.Equal("Method", p.KindFilter);
        Assert.Equal(2, p.MaxDepth);
    }

    [Fact]
    public void GetDocumentOutline_NamespaceFilterOmitted_DeserializesAsNull()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string> { ["source-file"] = "/src/Foo.cs" });

        var p = JsonSerializer.Deserialize<GetDocumentOutlineParams>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true })!;
        Assert.Null(p.NamespaceFilter);
    }
}
