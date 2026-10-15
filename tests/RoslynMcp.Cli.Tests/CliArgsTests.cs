using RoslynMcp.Cli;
using Xunit;

namespace RoslynMcp.Cli.Tests;

public class CliArgsTests
{
    [Fact]
    public void EmptyArgs_ShowsGlobalHelp()
    {
        var result = CliArgs.Parse([]);
        Assert.True(result.ShowHelp);
        Assert.Null(result.ToolName);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("help")]
    [InlineData("--Help")]
    [InlineData("--HELP")]
    [InlineData("HELP")]
    [InlineData("-H")]
    public void SingleHelpFlag_ShowsGlobalHelp(string flag)
    {
        var result = CliArgs.Parse([flag]);
        Assert.True(result.ShowHelp);
    }

    [Fact]
    public void ToolNameWithHelp_ShowsToolHelp()
    {
        var result = CliArgs.Parse(["rename-symbol", "--help"]);
        Assert.True(result.ShowToolHelp);
        Assert.Equal("rename-symbol", result.ToolName);
        Assert.False(result.ShowHelp);
    }

    [Theory]
    [InlineData("MySolution.sln")]
    [InlineData("MySolution.slnx")]
    public void SolutionAndTool_ParsesCorrectly(string solutionFile)
    {
        var result = CliArgs.Parse([solutionFile, "rename-symbol", "--source-file", "Foo.cs", "--symbol-name", "Bar"]);

        Assert.False(result.ShowHelp);
        Assert.False(result.ShowToolHelp);
        Assert.Equal(solutionFile, result.SolutionPath);
        Assert.Equal("rename-symbol", result.ToolName);
        Assert.Equal("Foo.cs", result.Options["source-file"]);
        Assert.Equal("Bar", result.Options["symbol-name"]);
    }

    [Theory]
    [InlineData("My.sln")]
    [InlineData("My.slnx")]
    public void BooleanFlag_ParsesAsTrue(string solutionFile)
    {
        var result = CliArgs.Parse([solutionFile, "extract-method", "--preview"]);
        Assert.Equal("true", result.Options["preview"]);
    }

    [Theory]
    [InlineData("My.sln")]
    [InlineData("My.slnx")]
    public void FormatFlag_StrippedFromToolOptions(string solutionFile)
    {
        var result = CliArgs.Parse([solutionFile, "get-diagnostics", "--severity-filter", "Error", "--format", "text"]);
        Assert.Equal("text", result.Format);
        Assert.False(result.Options.ContainsKey("format"));
        Assert.Equal("Error", result.Options["severity-filter"]);
    }

    [Theory]
    [InlineData("My.sln")]
    [InlineData("My.slnx")]
    public void VerboseFlag_StrippedFromToolOptions(string solutionFile)
    {
        var result = CliArgs.Parse([solutionFile, "diagnose", "--verbose"]);
        Assert.True(result.Verbose);
        Assert.False(result.Options.ContainsKey("verbose"));
    }

    [Theory]
    [InlineData("My.sln")]
    [InlineData("My.slnx")]
    public void DefaultFormat_IsJson(string solutionFile)
    {
        var result = CliArgs.Parse([solutionFile, "diagnose"]);
        Assert.Equal("json", result.Format);
    }

    [Theory]
    [InlineData("My.sln")]
    [InlineData("My.slnx")]
    public void HelpInOptions_ShowsToolHelp(string solutionFile)
    {
        var result = CliArgs.Parse([solutionFile, "rename-symbol", "--help"]);
        Assert.True(result.ShowToolHelp);
        Assert.Equal("rename-symbol", result.ToolName);
    }

    [Theory]
    [InlineData("MySolution.sln")]
    [InlineData("MySolution.slnx")]
    public void SolutionPathLookingLikeFile_NotTreatedAsToolName(string solutionFile)
    {
        var result = CliArgs.Parse([solutionFile, "--help"]);
        Assert.True(result.ShowHelp);
    }

    [Theory]
    [InlineData("My.sln")]
    [InlineData("My.slnx")]
    public void MultipleOptions_AllParsed(string solutionFile)
    {
        var result = CliArgs.Parse([
            solutionFile, "change-signature",
            "--source-file", "Foo.cs",
            "--line", "42",
            "--new-name", "DoStuff",
            "--preview"
        ]);

        Assert.Equal(4, result.Options.Count);
        Assert.Equal("Foo.cs", result.Options["source-file"]);
        Assert.Equal("42", result.Options["line"]);
        Assert.Equal("DoStuff", result.Options["new-name"]);
        Assert.Equal("true", result.Options["preview"]);
    }

    [Fact]
    public void SearchSymbols_CaseSensitiveFlag_ParsesAsTrue()
    {
        var result = CliArgs.Parse(["My.sln", "search-symbols", "--query", "ID", "--case-sensitive"]);
        Assert.Equal("search-symbols", result.ToolName);
        Assert.Equal("ID", result.Options["query"]);
        Assert.Equal("true", result.Options["case-sensitive"]);
    }

    [Fact]
    public void SearchSymbols_CaseSensitiveExplicitFalse_ParsesAsFalse()
    {
        var result = CliArgs.Parse(["My.sln", "search-symbols", "--query", "ID", "--case-sensitive", "false"]);
        Assert.Equal("false", result.Options["case-sensitive"]);
    }

    [Fact]
    public void SearchSymbols_ExactMatchFlag_ParsesAsTrue()
    {
        var result = CliArgs.Parse(["My.sln", "search-symbols", "--query", "Add", "--exact-match"]);
        Assert.Equal("search-symbols", result.ToolName);
        Assert.Equal("Add", result.Options["query"]);
        Assert.Equal("true", result.Options["exact-match"]);
    }

    [Fact]
    public void SearchSymbols_ExactMatchExplicitFalse_ParsesAsFalse()
    {
        var result = CliArgs.Parse(["My.sln", "search-symbols", "--query", "Add", "--exact-match", "false"]);
        Assert.Equal("false", result.Options["exact-match"]);
    }

    [Fact]
    public void SearchSymbols_NamespaceFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "search-symbols", "--query", "Widget", "--namespace-filter", "App.Services"]);
        Assert.Equal("search-symbols", result.ToolName);
        Assert.Equal("Widget", result.Options["query"]);
        Assert.Equal("App.Services", result.Options["namespace-filter"]);
    }

    [Fact]
    public void SearchSymbols_AccessibilityFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "search-symbols", "--query", "Widget", "--accessibility-filter", "private protected"]);
        Assert.Equal("search-symbols", result.ToolName);
        Assert.Equal("Widget", result.Options["query"]);
        Assert.Equal("private protected", result.Options["accessibility-filter"]);
    }

    [Fact]
    public void GetDocumentOutline_AccessibilityFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "get-document-outline", "--source-file", "/src/Widget.cs", "--accessibility-filter", "protected internal"]);
        Assert.Equal("get-document-outline", result.ToolName);
        Assert.Equal("/src/Widget.cs", result.Options["source-file"]);
        Assert.Equal("protected internal", result.Options["accessibility-filter"]);
    }

    [Fact]
    public void GetDocumentOutline_NamespaceFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "get-document-outline", "--source-file", "/src/Widget.cs", "--namespace-filter", "App.Services"]);
        Assert.Equal("get-document-outline", result.ToolName);
        Assert.Equal("/src/Widget.cs", result.Options["source-file"]);
        Assert.Equal("App.Services", result.Options["namespace-filter"]);
    }

    [Fact]
    public void GetSymbolInfo_NameFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "get-symbol-info", "--source-file", "/src/Widget.cs", "--symbol-name", "Widget", "--name-filter", "Handle"]);
        Assert.Equal("get-symbol-info", result.ToolName);
        Assert.Equal("/src/Widget.cs", result.Options["source-file"]);
        Assert.Equal("Handle", result.Options["name-filter"]);
    }

    [Fact]
    public void FindImplementations_NamespaceFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "find-implementations", "--source-file", "/src/IRunner.cs", "--symbol-name", "IRunner", "--namespace-filter", "App.Services"]);
        Assert.Equal("find-implementations", result.ToolName);
        Assert.Equal("/src/IRunner.cs", result.Options["source-file"]);
        Assert.Equal("App.Services", result.Options["namespace-filter"]);
    }

    [Fact]
    public void FindImplementations_AccessibilityFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "find-implementations", "--source-file", "/src/IRunner.cs", "--symbol-name", "IRunner", "--accessibility-filter", "protected internal"]);
        Assert.Equal("find-implementations", result.ToolName);
        Assert.Equal("/src/IRunner.cs", result.Options["source-file"]);
        Assert.Equal("protected internal", result.Options["accessibility-filter"]);
    }

    [Fact]
    public void FindImplementations_KindFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "find-implementations", "--source-file", "/src/IRunner.cs", "--symbol-name", "IRunner", "--kind-filter", "Record"]);
        Assert.Equal("find-implementations", result.ToolName);
        Assert.Equal("/src/IRunner.cs", result.Options["source-file"]);
        Assert.Equal("Record", result.Options["kind-filter"]);
    }

    [Fact]
    public void FindImplementations_NameFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "find-implementations", "--source-file", "/src/IRepository.cs", "--symbol-name", "IRepository", "--name-filter", "Sql"]);
        Assert.Equal("find-implementations", result.ToolName);
        Assert.Equal("/src/IRepository.cs", result.Options["source-file"]);
        Assert.Equal("Sql", result.Options["name-filter"]);
    }

    [Fact]
    public void FindReferences_NamespaceFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "find-references", "--source-file", "/src/Target.cs", "--symbol-name", "Run", "--namespace-filter", "App.Services"]);
        Assert.Equal("find-references", result.ToolName);
        Assert.Equal("/src/Target.cs", result.Options["source-file"]);
        Assert.Equal("App.Services", result.Options["namespace-filter"]);
    }

    [Fact]
    public void FindReferences_AccessibilityFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "find-references", "--source-file", "/src/Target.cs", "--symbol-name", "Run", "--accessibility-filter", "protected internal"]);
        Assert.Equal("find-references", result.ToolName);
        Assert.Equal("/src/Target.cs", result.Options["source-file"]);
        Assert.Equal("protected internal", result.Options["accessibility-filter"]);
    }

    [Fact]
    public void FindReferences_NameFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "find-references", "--source-file", "/src/Target.cs", "--symbol-name", "Run", "--name-filter", "Handle"]);
        Assert.Equal("find-references", result.ToolName);
        Assert.Equal("/src/Target.cs", result.Options["source-file"]);
        Assert.Equal("Handle", result.Options["name-filter"]);
    }

    [Fact]
    public void FindReferences_KindFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "find-references", "--source-file", "/src/Target.cs", "--symbol-name", "Run", "--kind-filter", "Method"]);
        Assert.Equal("find-references", result.ToolName);
        Assert.Equal("/src/Target.cs", result.Options["source-file"]);
        Assert.Equal("Method", result.Options["kind-filter"]);
    }

    [Fact]
    public void FindCallers_NamespaceFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "find-callers", "--source-file", "/src/Target.cs", "--symbol-name", "Run", "--namespace-filter", "App.Services"]);
        Assert.Equal("find-callers", result.ToolName);
        Assert.Equal("/src/Target.cs", result.Options["source-file"]);
        Assert.Equal("App.Services", result.Options["namespace-filter"]);
    }

    [Fact]
    public void FindCallers_AccessibilityFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "find-callers", "--source-file", "/src/Target.cs", "--symbol-name", "Run", "--accessibility-filter", "protected internal"]);
        Assert.Equal("find-callers", result.ToolName);
        Assert.Equal("/src/Target.cs", result.Options["source-file"]);
        Assert.Equal("protected internal", result.Options["accessibility-filter"]);
    }

    [Fact]
    public void FindCallers_NameFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "find-callers", "--source-file", "/src/Target.cs", "--symbol-name", "Run", "--name-filter", "Handle"]);
        Assert.Equal("find-callers", result.ToolName);
        Assert.Equal("/src/Target.cs", result.Options["source-file"]);
        Assert.Equal("Handle", result.Options["name-filter"]);
    }

    [Fact]
    public void FindCallers_KindFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "find-callers", "--source-file", "/src/Target.cs", "--symbol-name", "Run", "--kind-filter", "Property"]);
        Assert.Equal("find-callers", result.ToolName);
        Assert.Equal("/src/Target.cs", result.Options["source-file"]);
        Assert.Equal("Property", result.Options["kind-filter"]);
    }

    [Fact]
    public void GetTypeHierarchy_NamespaceFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "get-type-hierarchy", "--source-file", "/src/Shape.cs", "--symbol-name", "Shape", "--namespace-filter", "App.Services"]);
        Assert.Equal("get-type-hierarchy", result.ToolName);
        Assert.Equal("/src/Shape.cs", result.Options["source-file"]);
        Assert.Equal("App.Services", result.Options["namespace-filter"]);
    }

    [Fact]
    public void GetTypeHierarchy_AccessibilityFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "get-type-hierarchy", "--source-file", "/src/Shape.cs", "--symbol-name", "Shape", "--accessibility-filter", "protected internal"]);
        Assert.Equal("get-type-hierarchy", result.ToolName);
        Assert.Equal("/src/Shape.cs", result.Options["source-file"]);
        Assert.Equal("protected internal", result.Options["accessibility-filter"]);
    }

    [Fact]
    public void GetTypeHierarchy_KindFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "get-type-hierarchy", "--source-file", "/src/Entity.cs", "--symbol-name", "Entity", "--kind-filter", "Record"]);
        Assert.Equal("get-type-hierarchy", result.ToolName);
        Assert.Equal("/src/Entity.cs", result.Options["source-file"]);
        Assert.Equal("Record", result.Options["kind-filter"]);
    }

    [Fact]
    public void GetTypeHierarchy_NameFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "get-type-hierarchy", "--source-file", "/src/Controller.cs", "--symbol-name", "Controller", "--name-filter", "Admin"]);
        Assert.Equal("get-type-hierarchy", result.ToolName);
        Assert.Equal("/src/Controller.cs", result.Options["source-file"]);
        Assert.Equal("Admin", result.Options["name-filter"]);
    }

    [Fact]
    public void GetDiagnostics_NamespaceFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "get-diagnostics", "--severity-filter", "Error", "--namespace-filter", "App.Services"]);
        Assert.Equal("get-diagnostics", result.ToolName);
        Assert.Equal("Error", result.Options["severity-filter"]);
        Assert.Equal("App.Services", result.Options["namespace-filter"]);
    }

    [Fact]
    public void GetDiagnostics_AccessibilityFilterOption_ParsesValue()
    {
        var result = CliArgs.Parse(["My.sln", "get-diagnostics", "--severity-filter", "Error", "--accessibility-filter", "protected internal"]);
        Assert.Equal("get-diagnostics", result.ToolName);
        Assert.Equal("Error", result.Options["severity-filter"]);
        Assert.Equal("protected internal", result.Options["accessibility-filter"]);
    }
}

