using Xunit;
using RoslynMcp.Cli;

namespace RoslynMcp.Cli.Tests;

public class ToolRegistryTests
{
    [Fact]
    public void BuildDefault_Registers41Tools()
    {
        var registry = ToolRegistry.BuildDefault();
        var tools = registry.GetAllTools();
        Assert.Equal(41, tools.Count);
    }

    [Fact]
    public void GetTool_KnownTool_ReturnsEntry()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("rename-symbol");
        Assert.NotNull(tool);
        Assert.Equal("rename-symbol", tool.Name);
        Assert.Equal("Refactoring", tool.Category);
    }

    [Fact]
    public void GetTool_UnknownTool_ReturnsNull()
    {
        var registry = ToolRegistry.BuildDefault();
        Assert.Null(registry.GetTool("nonexistent-tool"));
    }

    [Fact]
    public void GetTool_CaseInsensitive()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("Rename-Symbol");
        Assert.NotNull(tool);
    }

    [Fact]
    public void Diagnose_DoesNotRequireWorkspace()
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool("diagnose");
        Assert.NotNull(tool);
        Assert.False(tool.RequiresWorkspace);
    }

    [Fact]
    public void AllTools_HaveDescriptions()
    {
        var registry = ToolRegistry.BuildDefault();
        foreach (var tool in registry.GetAllTools())
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description),
                $"Tool '{tool.Name}' has no description");
        }
    }

    [Fact]
    public void AllTools_HaveParamsTypes()
    {
        var registry = ToolRegistry.BuildDefault();
        foreach (var tool in registry.GetAllTools())
        {
            Assert.NotNull(tool.ParamsType);
        }
    }

    [Fact]
    public void GetAllTools_SortedByCategoryThenName()
    {
        var registry = ToolRegistry.BuildDefault();
        var tools = registry.GetAllTools();
        var sorted = tools.OrderBy(t => t.Category).ThenBy(t => t.Name).ToList();

        for (int i = 0; i < tools.Count; i++)
        {
            Assert.Equal(sorted[i].Name, tools[i].Name);
        }
    }

    [Theory]
    [InlineData("extract-method", "Refactoring")]
    [InlineData("find-references", "Query")]
    [InlineData("get-diagnostics", "Query")]
    [InlineData("diagnose", "Diagnostic")]
    [InlineData("move-type-to-file", "Refactoring")]
    [InlineData("convert-to-async", "Refactoring")]
    [InlineData("generate-constructor", "Refactoring")]
    [InlineData("add-missing-usings", "Refactoring")]
    [InlineData("format-document", "Refactoring")]
    [InlineData("get-symbol-info", "Query")]
    public void ToolCategories_CorrectlyAssigned(string name, string expectedCategory)
    {
        var registry = ToolRegistry.BuildDefault();
        var tool = registry.GetTool(name);
        Assert.NotNull(tool);
        Assert.Equal(expectedCategory, tool.Category);
    }

    [Fact]
    public void RefactoringToolCount_Is28()
    {
        var registry = ToolRegistry.BuildDefault();
        var count = registry.GetAllTools().Count(t => t.Category == "Refactoring");
        Assert.Equal(28, count);
    }

    [Fact]
    public void QueryToolCount_Is12()
    {
        var registry = ToolRegistry.BuildDefault();
        var count = registry.GetAllTools().Count(t => t.Category == "Query");
        Assert.Equal(12, count);
    }

    [Fact]
    public void DiagnosticToolCount_Is1()
    {
        var registry = ToolRegistry.BuildDefault();
        var count = registry.GetAllTools().Count(t => t.Category == "Diagnostic");
        Assert.Equal(1, count);
    }
}
