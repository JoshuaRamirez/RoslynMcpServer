using RoslynMcp.Cli;
using Xunit;

namespace RoslynMcp.Cli.Tests;

public class HelpGeneratorTests
{
    [Fact]
    public void GenerateGlobalHelp_ContainsUsage()
    {
        var registry = ToolRegistry.BuildDefault();
        var help = HelpGenerator.GenerateGlobalHelp(registry);
        Assert.Contains("USAGE:", help);
        Assert.Contains("roslyn-cli", help);
    }

    [Fact]
    public void GenerateGlobalHelp_Lists41Tools()
    {
        var registry = ToolRegistry.BuildDefault();
        var help = HelpGenerator.GenerateGlobalHelp(registry);
        Assert.Contains("Total: 41 tools", help);
    }

    [Fact]
    public void GenerateGlobalHelp_ContainsCategories()
    {
        var registry = ToolRegistry.BuildDefault();
        var help = HelpGenerator.GenerateGlobalHelp(registry);
        Assert.Contains("REFACTORING", help);
        Assert.Contains("QUERY", help);
        Assert.Contains("DIAGNOSTIC", help);
    }

    [Fact]
    public void GenerateToolHelp_ContainsToolName()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("rename-symbol")!;
        var help = HelpGenerator.GenerateToolHelp(tool);
        Assert.Contains("rename-symbol", help);
        Assert.Contains("Rename any C# symbol", help);
    }

    [Fact]
    public void GenerateToolHelp_ContainsUsage()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("find-references")!;
        var help = HelpGenerator.GenerateToolHelp(tool);
        Assert.Contains("USAGE:", help);
        Assert.Contains("find-references", help);
    }

    [Theory]
    [InlineData("SourceFile", "source-file")]
    [InlineData("SymbolName", "symbol-name")]
    [InlineData("Line", "line")]
    [InlineData("NewName", "new-name")]
    [InlineData("SeverityFilter", "severity-filter")]
    public void PascalToKebab_ConvertsCorrectly(string pascal, string expected)
    {
        Assert.Equal(expected, HelpGenerator.PascalToKebab(pascal));
    }

    [Fact]
    public void PascalToKebab_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", HelpGenerator.PascalToKebab(""));
    }

    [Theory]
    [InlineData("XMLPath", "xml-path")]
    [InlineData("IP", "ip")]
    [InlineData("SQLQuery", "sql-query")]
    [InlineData("HTMLParser", "html-parser")]
    [InlineData("IOStream", "io-stream")]
    [InlineData("XMLFile", "xml-file")]
    public void PascalToKebab_ConsecutiveCapitals_HandlesAcronyms(string pascal, string expected)
    {
        Assert.Equal(expected, HelpGenerator.PascalToKebab(pascal));
    }

    [Fact]
    public void GenerateToolHelp_ShowsParamsAsKebabCase()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("rename-symbol")!;
        var help = HelpGenerator.GenerateToolHelp(tool);
        // RenameSymbolParams should have source-file and new-name
        Assert.Contains("--source-file", help);
        Assert.Contains("--new-name", help);
    }

    [Fact]
    public void GenerateToolHelp_RequiredKeywordParams_ShownAsRequired()
    {
        // RenameSymbolParams has: required string SourceFile, required string SymbolName,
        // required string NewName — these should appear under REQUIRED, not OPTIONAL.
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("rename-symbol")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        // The help should have a REQUIRED section containing source-file, symbol-name, new-name
        Assert.Contains("REQUIRED:", help);

        // Split at REQUIRED: and OPTIONAL: to verify placement
        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--symbol-name", requiredSection);
        Assert.Contains("--new-name", requiredSection);
    }
}
