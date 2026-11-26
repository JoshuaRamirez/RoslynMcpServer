using System.Text;
using System.Text.Json;
using RoslynMcp.Server.Tests.TestHelpers;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Host;

/// <summary>
/// Regression tests for #3076: the MCP base-protocol <c>ping</c> request must get an empty result.
/// </summary>
public class McpServerHostPingTests
{
    private static async Task<List<JsonElement>> RunAsync(params string[] lines)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        using var output = new MemoryStream();
        var transport = new StdioTransport(input, output);
        await using (var host = new McpServerHost(new ThrowingWorkspaceProvider(), transport))
        {
            await host.RunAsync();
        }

        var text = Encoding.UTF8.GetString(output.ToArray());
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Where(e => e.TryGetProperty("id", out _))
            .ToList();
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":2,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":2,"method":"ping","params":{}}""")]
    public async Task Ping_ReturnsEmptyResult(string ping)
    {
        var responses = await RunAsync(ping);

        var response = Assert.Single(responses);
        Assert.Equal(2, response.GetProperty("id").GetInt32());
        Assert.False(response.TryGetProperty("error", out _), response.GetRawText());
        var result = response.GetProperty("result");
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Empty(result.EnumerateObject());
    }

    [Fact]
    public async Task UnknownMethod_StillReturnsMethodNotFound()
    {
        var responses = await RunAsync("""{"jsonrpc":"2.0","id":3,"method":"pong"}""");

        var response = Assert.Single(responses);
        Assert.Equal(-32601, response.GetProperty("error").GetProperty("code").GetInt32());
    }
}
