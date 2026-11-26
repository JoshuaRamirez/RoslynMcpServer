using RoslynMcp.Cli;
using RoslynMcp.Contracts.Models;
using Xunit;

namespace RoslynMcp.Cli.Tests;

/// <summary>
/// Regression tests for #3061: the CLI passed a relative solution path and relative
/// <c>--source-file</c> through untouched, so its own <c>--help</c> examples failed.
/// </summary>
public class CliRelativePathTests
{
    [Theory]
    [InlineData("Fx.csproj")]
    [InlineData("MySolution.sln")]
    [InlineData("sub/App.slnx")]
    public void RelativeSolutionPath_IsResolvedAgainstCurrentDirectory(string path)
    {
        var resolved = CliPathResolver.ResolvePath(path);

        Assert.Equal(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path)), resolved);
        Assert.True(Path.IsPathRooted(resolved));
    }

    [Fact]
    public void AbsoluteOrBlankPath_IsUnchanged()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "x", "..", "App.csproj");

        Assert.Equal(absolute, CliPathResolver.ResolvePath(absolute));
        Assert.Null(CliPathResolver.ResolvePath(null));
        Assert.Equal("", CliPathResolver.ResolvePath(""));
    }

    [Fact]
    public void PathTypedOptions_AreResolved_OthersAreNot()
    {
        var parsed = CliArgs.Parse(["Fx.csproj", "rename-symbol", "--source-file", "Foo.cs", "--symbol-name", "Bar.cs", "--new-name", "Baz"]);

        CliPathResolver.ResolvePathOptions(parsed.Options, typeof(RenameSymbolParams));

        Assert.Equal(Path.GetFullPath("Foo.cs"), parsed.Options["source-file"]);
        Assert.Equal("Bar.cs", parsed.Options["symbol-name"]);
        Assert.Equal("Baz", parsed.Options["new-name"]);
    }

    [Fact]
    public void TargetFileAndProjectPath_AreResolved()
    {
        var options = new Dictionary<string, string>
        {
            ["source-file"] = "src/Foo.cs",
            ["target-file"] = "src/Bar.cs",
            ["symbol-name"] = "Foo"
        };
        CliPathResolver.ResolvePathOptions(options, typeof(MoveTypeToFileParams));
        Assert.Equal(Path.GetFullPath("src/Foo.cs"), options["source-file"]);
        Assert.Equal(Path.GetFullPath("src/Bar.cs"), options["target-file"]);
        Assert.Equal("Foo", options["symbol-name"]);

        var search = new Dictionary<string, string> { ["project-path"] = "App.csproj", ["query"] = "x.cs" };
        CliPathResolver.ResolvePathOptions(search, typeof(SearchSymbolsParams));
        Assert.Equal(Path.GetFullPath("App.csproj"), search["project-path"]);
        Assert.Equal("x.cs", search["query"]);
    }

    [Fact]
    public void UnknownParamsType_LeavesOptionsAlone()
    {
        var options = new Dictionary<string, string> { ["source-file"] = "Foo.cs" };

        CliPathResolver.ResolvePathOptions(options, paramsType: null);

        Assert.Equal("Foo.cs", options["source-file"]);
    }
}
