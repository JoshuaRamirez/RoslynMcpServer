using System.Text.Json;
using RoslynMcp.Cli;
using RoslynMcp.Contracts.Models;
using Xunit;

namespace RoslynMcp.Cli.Tests;

/// <summary>
/// Regression tests for #3073: string-list options such as --members were split on every comma,
/// so a multi-parameter indexer identity (<c>this[int row, int col]</c>) could never be selected.
/// </summary>
public class CliMembersIndexerCommaTests
{
    [Theory]
    [InlineData("this[int row, int col]", new[] { "this[int row, int col]" })]
    [InlineData("this[int,int]", new[] { "this[int,int]" })]
    [InlineData("this[int row, int col], Clear", new[] { "this[int row, int col]", "Clear" })]
    [InlineData("this[Dictionary<string, int> map],Clear", new[] { "this[Dictionary<string, int> map]", "Clear" })]
    [InlineData("Run(int, string), Stop", new[] { "Run(int, string)", "Stop" })]
    [InlineData("operator <, Clear", new[] { "operator <", "Clear" })]
    [InlineData("Foo, Bar", new[] { "Foo", "Bar" })]
    [InlineData("Foo,,Bar", new[] { "Foo", "", "Bar" })]
    public void MembersOption_SplitsOnlyTopLevelCommas(string value, string[] expected)
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string> { ["members"] = value }, typeof(ExtractInterfaceParams));

        var p = JsonSerializer.Deserialize<ExtractInterfaceParams>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(expected, p.Members);
    }

    [Fact]
    public void UnbalancedBracket_KeepsTheRestAsOneEntry()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string> { ["members"] = "this[int, Clear" }, typeof(ExtractInterfaceParams));

        var p = JsonSerializer.Deserialize<ExtractInterfaceParams>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(new[] { "this[int, Clear" }, p.Members);
    }

    [Fact]
    public void JsonArrayValue_IsStillUsedAsIs()
    {
        var json = ArgsToJsonConverter.Convert(
            new Dictionary<string, string> { ["members"] = "[\"this[int row, int col]\",\"Clear\"]" },
            typeof(ExtractInterfaceParams));

        var p = JsonSerializer.Deserialize<ExtractInterfaceParams>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(new[] { "this[int row, int col]", "Clear" }, p.Members);
    }
}
