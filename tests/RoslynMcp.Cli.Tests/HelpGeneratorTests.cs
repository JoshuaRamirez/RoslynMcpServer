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
    public void GenerateToolHelp_FormatDocument_ShowsPreview()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("format-document")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("format-document", help);
        Assert.Contains("preview", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("without applying", tool.Description, StringComparison.OrdinalIgnoreCase);

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_FormatDocument_ShowsAllFiles()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("format-document")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("format-document", help);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_AnalyzeControlFlow_ShowsStartAndEndColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("analyze-control-flow")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("analyze-control-flow", help);
        Assert.Contains("--start-line", help);
        Assert.Contains("--end-line", help);
        Assert.Contains("--start-column", help);
        Assert.Contains("--end-column", help);
        Assert.Contains("startColumn", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("endColumn", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("whole-line", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];
        Assert.Contains("--start-line", requiredSection);
        Assert.Contains("--end-line", requiredSection);
        Assert.DoesNotContain("--start-column", requiredSection);
        Assert.DoesNotContain("--end-column", requiredSection);
        Assert.Contains("--start-column", optionalSection);
        Assert.Contains("--end-column", optionalSection);
        Assert.Contains("int?", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_AnalyzeDataFlow_ShowsStartAndEndColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("analyze-data-flow")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("analyze-data-flow", help);
        Assert.Contains("--start-line", help);
        Assert.Contains("--end-line", help);
        Assert.Contains("--start-column", help);
        Assert.Contains("--end-column", help);
        Assert.Contains("startColumn", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("endColumn", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("whole-line", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];
        Assert.Contains("--start-line", requiredSection);
        Assert.Contains("--end-line", requiredSection);
        Assert.DoesNotContain("--start-column", requiredSection);
        Assert.DoesNotContain("--end-column", requiredSection);
        Assert.Contains("--start-column", optionalSection);
        Assert.Contains("--end-column", optionalSection);
        Assert.Contains("int?", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_IntroduceParameter_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("introduce-parameter")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("introduce-parameter", help);
        Assert.Contains("--line", help);
        Assert.Contains("--column", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];
        Assert.Contains("--line", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.Contains("--column", optionalSection);
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
        Assert.Contains("--line", help);
        Assert.Contains("--column", help);
        Assert.Contains("line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_ExtractBaseClass_ShowsSeparateFile()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("extract-base-class")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("--separate-file", help);
        Assert.Contains("--line", help);
        Assert.Contains("--column", help);
        Assert.Contains("line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_PullMembersUp_ShowsLine()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("pull-members-up")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("pull-members-up", help);
        Assert.Contains("--line", help);
        Assert.Contains("--column", help);
        Assert.Contains("line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--target-base-type", optionalSection);
        Assert.Contains("--make-abstract", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_PushMembersDown_ShowsLine()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("push-members-down")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("push-members-down", help);
        Assert.Contains("--line", help);
        Assert.Contains("--column", help);
        Assert.Contains("line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--target-derived-types", optionalSection);
        Assert.Contains("--leave-abstract", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_UseBaseType_ShowsLine()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("use-base-type")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("use-base-type", help);
        Assert.Contains("--line", help);
        Assert.Contains("--column", help);
        Assert.Contains("line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile and typeName are required", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--type-name", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--target-base-type", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_MoveTypeToFile_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("move-type-to-file")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("move-type-to-file", help);
        Assert.Contains("--line", help);
        Assert.Contains("--column", help);
        Assert.Contains("line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("omitted-line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile, symbolName, and targetFile are required", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--symbol-name", optionalSection);
        Assert.Contains("--target-file", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--create-target-file", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_MoveTypeToNamespace_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("move-type-to-namespace")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("move-type-to-namespace", help);
        Assert.Contains("--line", help);
        Assert.Contains("--column", help);
        Assert.Contains("line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("omitted-line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile and symbolName are required", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--target-namespace", requiredSection);
        Assert.DoesNotContain("--source-file", requiredSection);
        Assert.DoesNotContain("--symbol-name", requiredSection);
        Assert.DoesNotContain("--all-files", requiredSection);
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.DoesNotContain("--update-file-location", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--symbol-name", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--update-file-location", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_GetCodeMetrics_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("get-code-metrics")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("get-code-metrics", help);
        Assert.Contains("--line", help);
        Assert.Contains("--column", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("omitted-line", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");

        var optionalSection = optionalIdx >= 0 ? help[optionalIdx..] : help;
        var requiredSection = requiredIdx >= 0 && optionalIdx > requiredIdx
            ? help[requiredIdx..optionalIdx]
            : string.Empty;

        Assert.DoesNotContain("--column", requiredSection);
        Assert.DoesNotContain("--line", requiredSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--line", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_GenerateEqualsHashCode_ShowsImplementIEquatable()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("generate-equals-hashcode")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("generate-equals-hashcode", help);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--type-name", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--fields", optionalSection);
        Assert.Contains("--implement-i-equatable", help);
        Assert.Contains("--generate-operators", help);
        Assert.Contains("--replace-existing", help);
        Assert.Contains("--use-hash-code-combine", help);
        Assert.Contains("--include-properties", help);
        Assert.Contains("--call-super", help);
        Assert.Contains("--include-inherited-members", help);
        Assert.Contains("line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateToolHelp_GenerateToString_ShowsIncludeInheritedMembers()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("generate-tostring")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("generate-tostring", help);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--include-inherited-members", help);
        Assert.Contains("--include-properties", help);
        Assert.Contains("--format", help);
        Assert.Contains("--replace-existing", help);
        Assert.Contains("--call-super", help);
        Assert.Contains("--line", help);
        Assert.Contains("--column", help);
        Assert.Contains("line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", tool.Description, StringComparison.OrdinalIgnoreCase);
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

        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--type-name", optionalSection);
        Assert.DoesNotContain("--include-properties", help[..optionalIdx]);
        Assert.DoesNotContain("--call-super", help[..optionalIdx]);
        Assert.DoesNotContain("--line", help[..optionalIdx]);
        Assert.DoesNotContain("--column", help[..optionalIdx]);

        Assert.Contains("--include-properties", optionalSection);
        Assert.Contains("--call-super", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateToolHelp_GenerateConstructor_IncludePropertiesIsOptional_RequiredStringsStayRequired()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("generate-constructor")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("generate-constructor", help);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--type-name", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--include-properties", optionalSection);
        Assert.Contains("--include-inherited-members", optionalSection);
        Assert.Contains("--replace-existing", optionalSection);
        Assert.Contains("--visibility", optionalSection);
        Assert.Contains("--copy-constructor", optionalSection);
        Assert.Contains("--class-base-copy", optionalSection);
        Assert.Contains("--call-base", optionalSection);
        Assert.Contains("record class", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", tool.Description, StringComparison.OrdinalIgnoreCase);
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
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.DoesNotContain("--replace-existing", requiredSection);
        Assert.DoesNotContain("--init-only", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--replace-existing", optionalSection);
        Assert.Contains("--init-only", optionalSection);
        Assert.Contains("--preview", optionalSection);
        Assert.Contains("replaceExisting", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateToolHelp_GenerateOverrides_ReplaceExistingIsOptional()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("generate-overrides")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--type-name", optionalSection);
        Assert.Contains("--replace-existing", optionalSection);
        Assert.Contains("--call-base", optionalSection);
        Assert.Contains("--members", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("replaceExisting", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("callBase", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("base.Prop", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateToolHelp_ImplementInterface_LineIsOptional()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("implement-interface")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--type-name", optionalSection);
        Assert.Contains("--interface-name", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--replace-existing", optionalSection);
        Assert.Contains("--throw-not-implemented", optionalSection);
        Assert.Contains("--preview", optionalSection);
        Assert.Contains("line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", tool.Description, StringComparison.OrdinalIgnoreCase);
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

        Assert.Contains("implement-abstract", help);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--type-name", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--throw-not-implemented", optionalSection);
        Assert.Contains("--members", optionalSection);
        Assert.Contains("--replace-existing", optionalSection);
        Assert.Contains("--preview", optionalSection);
        Assert.Contains("throwNotImplemented", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("replaceExisting", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateToolHelp_RemoveBraces_ShowsAllFiles()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("remove-braces")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("remove-braces", help);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--scope", optionalSection);
        Assert.Contains("--type-name", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_EncapsulateField_ShowsUpdateReferences()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("encapsulate-field")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("encapsulate-field", help);
        Assert.Contains("--line", help);
        Assert.Contains("--column", help);
        Assert.Contains("line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FirstOrDefault", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("updateReferences", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--field-name", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--update-references", optionalSection);
        Assert.Contains("--property-name", optionalSection);
        Assert.Contains("--read-only", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_AddNullChecks_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("add-null-checks")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("add-null-checks", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--method-name", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--style", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_ChangeSignature_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("change-signature")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("change-signature", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--method-name", requiredSection);
        Assert.Contains("--parameters", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_AddParameter_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("add-parameter")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("add-parameter", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("smallest method", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start-line", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--method-name", requiredSection);
        Assert.Contains("--parameter-name", requiredSection);
        Assert.Contains("--parameter-type", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_RemoveParameter_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("remove-parameter")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("remove-parameter", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("smallest method", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start-line", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--method-name", requiredSection);
        Assert.Contains("--parameter-name", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);
        Assert.DoesNotContain("--force", requiredSection);

        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--force", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_ReorderParameters_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("reorder-parameters")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("reorder-parameters", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("smallest method", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start-line", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--method-name", requiredSection);
        Assert.Contains("--new-order", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_InlineMethod_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("inline-method")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("inline-method", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("smallest method", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("identifier start-line", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--method-name", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--preview", optionalSection);
        Assert.Contains("--call-site-location", optionalSection);
        Assert.Contains("--remove-method", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_RenameNamespace_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("rename-namespace")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("rename-namespace", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("smallest namespace", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("optional line pick", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("omitted-line path", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--namespace-name", requiredSection);
        Assert.Contains("--new-name", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--preview", optionalSection);
        Assert.Contains("--update-folders", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_ChangeReturnType_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("change-return-type")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("change-return-type", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("smallest method", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start-line", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--method-name", requiredSection);
        Assert.Contains("--new-return-type", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--preview", optionalSection);
        Assert.Contains("--convert-return-statements", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_ConvertToAsync_ShowsUpdateCallersAndRenameToAsync()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("convert-to-async")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("convert-to-async", help);
        Assert.Contains("updateCallers", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--method-name", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--rename-to-async", optionalSection);
        Assert.Contains("--update-callers", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_MakeStatic_ShowsAllFiles()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("make-static")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("make-static", help);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--start-line", optionalSection);
        Assert.Contains("--start-column", optionalSection);
        Assert.Contains("--end-line", optionalSection);
        Assert.Contains("--end-column", optionalSection);
        Assert.Contains("--symbol-name", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_MakeNonStatic_ShowsAllFiles()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("make-non-static")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("make-non-static", help);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--start-line", optionalSection);
        Assert.Contains("--start-column", optionalSection);
        Assert.Contains("--end-line", optionalSection);
        Assert.Contains("--end-column", optionalSection);
        Assert.Contains("--symbol-name", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_ConvertToBlockBody_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("convert-to-block-body")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("convert-to-block-body", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
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
    public void GenerateToolHelp_ConvertExpressionBody_ShowsColumnAndDirection()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("convert-expression-body")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("convert-expression-body", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.DoesNotContain("--source-file", requiredSection);
        Assert.Contains("--direction", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.DoesNotContain("--member-name", requiredSection);
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);
        Assert.DoesNotContain("--all-files", requiredSection);

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
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
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--line", optionalSection);
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
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_ConvertProperty_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("convert-property")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("convert-property", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.DoesNotContain("--source-file", requiredSection);
        Assert.Contains("--direction", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.DoesNotContain("--property-name", requiredSection);
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);
        Assert.DoesNotContain("--all-files", requiredSection);

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--property-name", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_InvertIf_ShowsAllFiles()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("invert-if")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("invert-if", help);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("covers that column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exclusive-end", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_ConvertToPatternMatching_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("convert-to-pattern-matching")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("convert-to-pattern-matching", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_InlineVariable_ShowsColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("inline-variable")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("inline-variable", help);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);

        var requiredIdx = help.IndexOf("REQUIRED:");
        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(requiredIdx >= 0, "REQUIRED section should exist");
        Assert.True(optionalIdx > requiredIdx, "OPTIONAL section should follow REQUIRED");

        var requiredSection = help[requiredIdx..optionalIdx];
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", requiredSection);
        Assert.Contains("--variable-name", requiredSection);
        Assert.DoesNotContain("--column", requiredSection);
        Assert.DoesNotContain("--line", requiredSection);
        Assert.DoesNotContain("--preview", requiredSection);

        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_InlineConstant_ShowsLineAndColumn()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("inline-constant")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("inline-constant", help);
        Assert.Contains("line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SymbolAmbiguous", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("omitted-line", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--constant-name", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--type-name", optionalSection);
        Assert.Contains("--remove-constant", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_AddBraces_ShowsAllFiles()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("add-braces")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("add-braces", help);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--scope", optionalSection);
        Assert.Contains("--type-name", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_SimplifyName_ShowsAllFiles()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("simplify-name")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("simplify-name", help);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("covers that column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exclusive-end", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--scope", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_SortUsings_ShowsAllFiles()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("sort-usings")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("sort-usings", help);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--system-first", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }

    [Fact]
    public void GenerateToolHelp_RenameFileToMatchType_ShowsAllFiles()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("rename-file-to-match-type")!;
        var help = HelpGenerator.GenerateToolHelp(tool);

        Assert.Contains("rename-file-to-match-type", help);
        Assert.Contains("allFiles", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sourceFile optional", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("smallest type", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("optional line pick", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("omitted-line path", tool.Description, StringComparison.OrdinalIgnoreCase);

        Assert.True(help.IndexOf("REQUIRED:") < 0, "sourceFile is optional when allFiles is true; no required params");

        var optionalIdx = help.IndexOf("OPTIONAL:");
        Assert.True(optionalIdx >= 0, "OPTIONAL section should exist");
        var optionalSection = help[optionalIdx..];

        Assert.Contains("--source-file", optionalSection);
        Assert.Contains("--all-files", optionalSection);
        Assert.Contains("--type-name", optionalSection);
        Assert.Contains("--line", optionalSection);
        Assert.Contains("--column", optionalSection);
        Assert.Contains("--preview", optionalSection);
    }
}
