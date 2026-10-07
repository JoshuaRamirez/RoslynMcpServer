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
}
