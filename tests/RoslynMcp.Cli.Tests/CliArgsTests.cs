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
}
