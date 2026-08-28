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
    public void GenerateGlobalHelp_Lists62Tools()
    {
        var registry = ToolRegistry.BuildDefault();
        var help = HelpGenerator.GenerateGlobalHelp(registry);
        Assert.Contains("Total: 62 tools", help);
        Assert.Contains("pull-members-up", help);
        Assert.Contains("push-members-down", help);
        Assert.Contains("use-base-type", help);
        Assert.Contains("introduce-field", help);
        Assert.Contains("safe-delete", help);
        Assert.Contains("make-static", help);
        Assert.Contains("make-non-static", help);
        Assert.Contains("convert-to-block-body", help);
        Assert.Contains("generate-property", help);
        Assert.Contains("generate-method-stub", help);
        Assert.Contains("implement-abstract", help);
        Assert.Contains("inline-constant", help);
        Assert.Contains("add-parameter", help);
        Assert.Contains("remove-parameter", help);
        Assert.Contains("reorder-parameters", help);
        Assert.Contains("change-return-type", help);
        Assert.Contains("convert-anonymous-to-class", help);
        Assert.Contains("convert-tuple-to-struct", help);
        Assert.Contains("rename-file-to-match-type", help);
        Assert.Contains("rename-namespace", help);
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
    public void GenerateToolHelp_ExtractVariable_ShowsReplaceAll()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("extract-variable")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("--replace-all", help);
    }

    [Fact]
    public void GenerateToolHelp_ExtractInterface_ShowsSeparateFile()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("extract-interface")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("--separate-file", help);
    }

    [Fact]
    public void GenerateToolHelp_ExtractBaseClass_ShowsSeparateFile()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("extract-base-class")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("--separate-file", help);
    }

    [Fact]
    public void GenerateToolHelp_GenerateEqualsHashCode_ShowsImplementIEquatable()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("generate-equals-hashcode")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("--implement-i-equatable", help);
        Assert.Contains("--generate-operators", help);
        Assert.Contains("--replace-existing", help);
        Assert.Contains("--use-hash-code-combine", help);
        Assert.Contains("--include-properties", help);
        Assert.Contains("--call-super", help);
        Assert.Contains("--include-inherited-members", help);
    }

    [Fact]
    public void GenerateToolHelp_GenerateToString_ShowsIncludeInheritedMembers()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("generate-tostring")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("--include-inherited-members", help);
        Assert.Contains("--include-properties", help);
        Assert.Contains("--format", help);
        Assert.Contains("--replace-existing", help);
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

    [Fact]
    public void GenerateToolHelp_GenerateToString_IncludePropertiesIsOptional_RequiredStringsStayRequired()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("generate-tostring")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--type-name", requiredSection);
        Assert.DoesNotContain("--include-properties", requiredSection);

        Assert.Contains("--include-properties", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_GenerateConstructor_IncludePropertiesIsOptional_RequiredStringsStayRequired()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("generate-constructor")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--type-name", requiredSection);
        Assert.DoesNotContain("--include-properties", requiredSection);
        Assert.DoesNotContain("--include-inherited-members", requiredSection);
        Assert.DoesNotContain("--replace-existing", requiredSection);
        Assert.DoesNotContain("--visibility", requiredSection);
        Assert.DoesNotContain("--copy-constructor", requiredSection);
        Assert.DoesNotContain("--class-base-copy", requiredSection);
        Assert.DoesNotContain("--call-base", requiredSection);

        Assert.Contains("--include-properties", optionalSection);
        Assert.Contains("--include-inherited-members", optionalSection);
        Assert.Contains("--replace-existing", optionalSection);
        Assert.Contains("--visibility", optionalSection);
        Assert.Contains("--copy-constructor", optionalSection);
        Assert.Contains("--class-base-copy", optionalSection);
        Assert.Contains("--call-base", optionalSection);
    }
}
