using System.Text;
using System.Text.Json;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Transport;

/// <summary>
/// Regression tests for #3077: a JSON-RPC response must always carry <c>id</c>,
/// and it must be <c>null</c> when the request id could not be detected.
/// </summary>
public class McpResponseNullIdTests
{
    private static async Task<JsonElement> WriteAsync(McpResponse response)
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        using (var transport = new StdioTransport(input, output))
        {
            await transport.WriteMessageAsync(response);
        }

        return JsonDocument.Parse(Encoding.UTF8.GetString(output.ToArray())).RootElement.Clone();
    }

    [Fact]
    public async Task WriteMessageAsync_ParseErrorWithUnknownId_WritesExplicitNullId()
    {
        var json = await WriteAsync(McpResponse.Failure(null, -32700, "Parse error"));

        Assert.True(json.TryGetProperty("id", out var id), json.GetRawText());
        Assert.Equal(JsonValueKind.Null, id.ValueKind);
        Assert.Equal(-32700, json.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(json.TryGetProperty("result", out _), json.GetRawText());
    }

    [Fact]
    public async Task WriteMessageAsync_SuccessResponse_KeepsIdAndOmitsError()
    {
        var json = await WriteAsync(McpResponse.Success(7, new { }));

        Assert.Equal(7, json.GetProperty("id").GetInt32());
        Assert.False(json.TryGetProperty("error", out _), json.GetRawText());
    }
}
