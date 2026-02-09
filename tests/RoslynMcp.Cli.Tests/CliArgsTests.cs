using Xunit;
using RoslynMcp.Cli;

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

    [Fact]
    public void SolutionAndTool_ParsesCorrectly()
    {
        var result = CliArgs.Parse(["MySolution.sln", "rename-symbol", "--source-file", "Foo.cs", "--symbol-name", "Bar"]);

        Assert.False(result.ShowHelp);
        Assert.False(result.ShowToolHelp);
        Assert.Equal("MySolution.sln", result.SolutionPath);
        Assert.Equal("rename-symbol", result.ToolName);
        Assert.Equal("Foo.cs", result.Options["source-file"]);
        Assert.Equal("Bar", result.Options["symbol-name"]);
    }

    [Fact]
    public void BooleanFlag_ParsesAsTrue()
    {
        var result = CliArgs.Parse(["My.sln", "extract-method", "--preview"]);
        Assert.Equal("true", result.Options["preview"]);
    }

    [Fact]
    public void FormatFlag_StrippedFromToolOptions()
    {
        var result = CliArgs.Parse(["My.sln", "get-diagnostics", "--severity-filter", "Error", "--format", "text"]);
        Assert.Equal("text", result.Format);
        Assert.False(result.Options.ContainsKey("format"));
        Assert.Equal("Error", result.Options["severity-filter"]);
    }

    [Fact]
    public void VerboseFlag_StrippedFromToolOptions()
    {
        var result = CliArgs.Parse(["My.sln", "diagnose", "--verbose"]);
        Assert.True(result.Verbose);
        Assert.False(result.Options.ContainsKey("verbose"));
    }

    [Fact]
    public void DefaultFormat_IsJson()
    {
        var result = CliArgs.Parse(["My.sln", "diagnose"]);
        Assert.Equal("json", result.Format);
    }

    [Fact]
    public void HelpInOptions_ShowsToolHelp()
    {
        var result = CliArgs.Parse(["My.sln", "rename-symbol", "--help"]);
        Assert.True(result.ShowToolHelp);
        Assert.Equal("rename-symbol", result.ToolName);
    }

    [Fact]
    public void SolutionPathLookingLikeFile_NotTreatedAsToolName()
    {
        var result = CliArgs.Parse(["MySolution.sln", "--help"]);
        Assert.True(result.ShowHelp);
    }

    [Fact]
    public void MultipleOptions_AllParsed()
    {
        var result = CliArgs.Parse([
            "My.sln", "change-signature",
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
}
