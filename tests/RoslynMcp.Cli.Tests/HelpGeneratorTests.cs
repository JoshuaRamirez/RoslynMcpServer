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
    public void GenerateGlobalHelp_Lists66Tools()
    {
        var registry = ToolRegistry.BuildDefault();
        var help = HelpGenerator.GenerateGlobalHelp(registry);
        Assert.Contains("Total: 66 tools", help);
        Assert.Contains("pull-members-up", help);
        Assert.Contains("push-members-down", help);
        Assert.Contains("use-base-type", help);
        Assert.Contains("introduce-field", help);
        Assert.Contains("safe-delete", help);
        Assert.Contains("make-static", help);
        Assert.Contains("make-non-static", help);
        Assert.Contains("convert-to-block-body", help);
        Assert.Contains("invert-if", help);
        Assert.Contains("add-braces", help);
        Assert.Contains("remove-braces", help);
        Assert.Contains("simplify-name", help);
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
        Assert.Contains("--call-super", help);
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
        Assert.DoesNotContain("--call-super", requiredSection);

        Assert.Contains("--include-properties", optionalSection);
        Assert.Contains("--call-super", optionalSection);
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
        Assert.Contains("record class", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateToolHelp_GenerateProperty_ReplaceExistingIsOptional()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("generate-property")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--type-name", requiredSection);
        Assert.DoesNotContain("--replace-existing", requiredSection);
        Assert.DoesNotContain("--init-only", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--replace-existing", optionalSection);
        Assert.Contains("--init-only", optionalSection);
        Assert.Contains("--preview", optionalSection);
        Assert.Contains("replaceExisting", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateToolHelp_GenerateOverrides_ReplaceExistingIsOptional()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("generate-overrides")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--type-name", requiredSection);
        Assert.DoesNotContain("--replace-existing", requiredSection);
        Assert.DoesNotContain("--call-base", requiredSection);
        Assert.DoesNotContain("--members", requiredSection);

        Assert.Contains("--replace-existing", optionalSection);
        Assert.Contains("--call-base", optionalSection);
        Assert.Contains("--members", optionalSection);
        Assert.Contains("replaceExisting", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("callBase", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("base.Prop", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateToolHelp_GenerateMethodStub_ThrowNotImplementedIsOptional()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("generate-method-stub")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--line", requiredSection);
        Assert.Contains("--column", requiredSection);
        Assert.DoesNotContain("--throw-not-implemented", requiredSection);
        Assert.DoesNotContain("--generate-async", requiredSection);
        Assert.DoesNotContain("--visibility", requiredSection);
        Assert.DoesNotContain("--replace-existing", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--throw-not-implemented", optionalSection);
        Assert.Contains("--generate-async", optionalSection);
        Assert.Contains("--visibility", optionalSection);
        Assert.Contains("--replace-existing", optionalSection);
        Assert.Contains("--preview", optionalSection);
        Assert.Contains("throwNotImplemented", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("replaceExisting", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateToolHelp_ImplementAbstract_ThrowNotImplementedIsOptional()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("implement-abstract")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--type-name", requiredSection);
        Assert.DoesNotContain("--throw-not-implemented", requiredSection);
        Assert.DoesNotContain("--members", requiredSection);
        Assert.DoesNotContain("--replace-existing", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--throw-not-implemented", optionalSection);
        Assert.Contains("--members", optionalSection);
        Assert.Contains("--replace-existing", optionalSection);
        Assert.Contains("--preview", optionalSection);
        Assert.Contains("throwNotImplemented", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("replaceExisting", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateToolHelp_RemoveBraces_ShowsScopeLineAndPreview()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("remove-braces")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("remove-braces", help);
        Assert.Contains("single-statement", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--scope", requiredSection);
        Assert.DoesNotContain("--type-name", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--scope", optionalSection);
        Assert.Contains("--type-name", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_ConvertToAsync_ShowsUpdateCallersAndRenameToAsync()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("convert-to-async")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("convert-to-async", help);
        Assert.Contains("updateCallers", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--method-name", requiredSection);
        Assert.DoesNotContain("--update-callers", requiredSection);
        Assert.DoesNotContain("--rename-to-async", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--line", optionalSection);
        Assert.Contains("--rename-to-async", optionalSection);
        Assert.Contains("--update-callers", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_ConvertExpressionBody_ShowsColumnAndDirection()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("convert-expression-body")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("convert-expression-body", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--direction", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.DoesNotContain("--member-name", requiredSection);
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--column", optionalSection);
        Assert.Contains("--member-name", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_ConvertForeachLinq_ShowsPreferQuerySyntaxAndColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("convert-foreach-linq")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("convert-foreach-linq", help);
        Assert.Contains("preferQuerySyntax", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--line", requiredSection);
        Assert.DoesNotContain("--prefer-query-syntax", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--prefer-query-syntax", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_ConvertToInterpolatedString_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("convert-to-interpolated-string")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("convert-to-interpolated-string", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--line", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--column", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_SimplifyName_ShowsScopeLineAndPreview()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("simplify-name")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("simplify-name", help);
        Assert.Contains("namespace", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--scope", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--scope", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }
}
