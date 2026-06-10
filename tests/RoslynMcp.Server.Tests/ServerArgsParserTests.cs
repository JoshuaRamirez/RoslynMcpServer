using RoslynMcp.Server;
using Xunit;

namespace RoslynMcp.Server.Tests;

public class ServerArgsParserTests
{
    [Fact]
    public void Parse_SetsSkipUnrecognizedProjects_WhenSwitchIsPresent()
    {
        var result = ServerArgsParser.Parse(["--skip-unrecognized-projects"]);
        Assert.True(result.WorkspaceLoadOptions.SkipUnrecognizedProjects);
    }

    [Fact]
    public void Parse_ThrowsForUnknownSwitch()
    {
        var ex = Assert.Throws<ArgumentException>(() => ServerArgsParser.Parse(["--unknown"]));
        Assert.Contains("--unknown", ex.Message);
    }
}
