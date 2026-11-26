using System.Text;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Transport;

/// <summary>
/// Regression tests for #3075: blank / whitespace-only / <c>null</c> lines are empty frames,
/// not end of stream, so the next request on the still-open stream must be read.
/// </summary>
public class StdioTransportBlankLineTests
{
    private const string Request = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n\r\n")]
    [InlineData("   \t \n")]
    [InlineData("null\n")]
    [InlineData(" null \n\n")]
    public async Task ReadMessageAsync_SkipsEmptyFramesBeforeNextRequest(string prefix)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(prefix + Request + "\n"));
        using var output = new MemoryStream();
        using var transport = new StdioTransport(input, output);

        var request = await transport.ReadMessageAsync();

        Assert.NotNull(request);
        Assert.Equal("tools/list", request.Method);
    }

    [Fact]
    public async Task ReadMessageAsync_ReturnsNullWhenOnlyEmptyFramesBeforeEndOfStream()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("\n  \nnull\n"));
        using var output = new MemoryStream();
        using var transport = new StdioTransport(input, output);

        Assert.Null(await transport.ReadMessageAsync());
    }
}
