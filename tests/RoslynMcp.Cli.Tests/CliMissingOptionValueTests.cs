using RoslynMcp.Cli;
using RoslynMcp.Contracts.Models;
using Xunit;

namespace RoslynMcp.Cli.Tests;

/// <summary>
/// Regression tests for #3288: an option given with no value (last token, or followed by another
/// <c>--option</c>) was bound as the literal string "true" even when the option is not boolean.
/// </summary>
public class CliMissingOptionValueTests
{
    [Fact]
    public void StringOptionFollowedByOption_IsReportedAsMissingValue()
    {
        var parsed = CliArgs.Parse(["/abs/App.csproj", "search-symbols", "--query", "--kind-filter", "Class"]);

        Assert.Contains("query", parsed.ValuelessOptions);
        Assert.DoesNotContain("kind-filter", parsed.ValuelessOptions);
        Assert.Equal("query", ArgsToJsonConverter.FindOptionMissingValue(parsed.ValuelessOptions, typeof(SearchSymbolsParams)));
    }

    [Theory]
    [InlineData("--target-namespace")]
    [InlineData("--target-namespace", "--preview")]
    public void StringOptionAsLastTokenOrBeforeFlag_IsReportedAsMissingValue(params string[] tail)
    {
        string[] args = ["/abs/App.csproj", "move-type-to-namespace", "--source-file", "/abs/Bar.cs", "--symbol-name", "Bar", .. tail];
        var parsed = CliArgs.Parse(args);

        Assert.Equal("target-namespace", ArgsToJsonConverter.FindOptionMissingValue(parsed.ValuelessOptions, typeof(MoveTypeToNamespaceParams)));
    }

    [Fact]
    public void IntOptionFollowedByOption_IsReportedAsMissingValue()
    {
        var parsed = CliArgs.Parse(["/abs/App.csproj", "go-to-definition", "--source-file", "/abs/Bar.cs", "--line", "--column", "30"]);

        Assert.Equal("line", ArgsToJsonConverter.FindOptionMissingValue(parsed.ValuelessOptions, typeof(GoToDefinitionParams)));
    }

    [Fact]
    public void BareBooleanFlags_AreNotMissingValues()
    {
        var parsed = CliArgs.Parse(["/abs/App.csproj", "move-type-to-namespace", "--source-file", "/abs/Bar.cs", "--symbol-name", "Bar", "--target-namespace", "Receiving", "--preview", "--verbose"]);

        Assert.Equal("true", parsed.Options["preview"]);
        Assert.True(parsed.Verbose);
        Assert.Null(ArgsToJsonConverter.FindOptionMissingValue(parsed.ValuelessOptions, typeof(MoveTypeToNamespaceParams)));
    }

    [Fact]
    public void OptionsWithValues_AreNotMissingValues()
    {
        var parsed = CliArgs.Parse(["/abs/App.csproj", "search-symbols", "--query", "Foo", "--kind-filter", "Class"]);

        Assert.Empty(parsed.ValuelessOptions);
        Assert.Null(ArgsToJsonConverter.FindOptionMissingValue(parsed.ValuelessOptions, typeof(SearchSymbolsParams)));
    }
}
