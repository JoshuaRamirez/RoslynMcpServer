using System.Text;
using System.Text.Json;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Transport;

public class StdioTransportTests
{
    [Fact]
    public async Task ReadMessageAsync_ParsesValidJsonRequest()
    {
        // Arrange
        var json = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json + "\n"));
        using var output = new MemoryStream();
        using var transport = new StdioTransport(input, output);

        // Act
        var request = await transport.ReadMessageAsync();

        // Assert
        Assert.NotNull(request);
        Assert.Equal("2.0", request.JsonRpc);
        Assert.Equal(1, ((JsonElement)request.Id!).GetInt32());
        Assert.Equal("initialize", request.Method);
    }

    [Fact]
    public async Task ReadMessageAsync_ReturnsNullOnStreamEnd()
    {
        // Arrange
        using var input = new MemoryStream(); // Empty stream
        using var output = new MemoryStream();
        using var transport = new StdioTransport(input, output);

        // Act
        var request = await transport.ReadMessageAsync();

        // Assert
        Assert.Null(request);
    }

    [Fact]
    public async Task ReadMessageAsync_ReturnsNullOnEmptyLine()
    {
        // Arrange
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("\n"));
        using var output = new MemoryStream();
        using var transport = new StdioTransport(input, output);

        // Act
        var request = await transport.ReadMessageAsync();

        // Assert
        Assert.Null(request);
    }

    [Fact]
    public async Task ReadMessageAsync_ThrowsOnInvalidJson()
    {
        // Arrange
        var invalidJson = "not valid json{";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(invalidJson + "\n"));
        using var output = new MemoryStream();
        using var transport = new StdioTransport(input, output);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.ReadMessageAsync());
        Assert.StartsWith("Failed to parse MCP message", ex.Message);
    }

    [Fact]
    public async Task WriteMessageAsync_WritesValidJsonResponse()
    {
        // Arrange
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        using var transport = new StdioTransport(input, output);
        var response = McpResponse.Success(1, new { test = "value" });

        // Act
        await transport.WriteMessageAsync(response);
        output.Position = 0;
        var written = await new StreamReader(output).ReadToEndAsync();

        // Assert
        Assert.Contains("\"jsonrpc\":\"2.0\"", written);
        Assert.Contains("\"id\":1", written);
        Assert.Contains("\"test\":\"value\"", written);
    }

    [Fact]
    public async Task WriteMessageAsync_WritesErrorResponse()
    {
        // Arrange
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        using var transport = new StdioTransport(input, output);
        var response = McpResponse.Failure(null, -32700, "Parse error");

        // Act
        await transport.WriteMessageAsync(response);
        output.Position = 0;
        var written = await new StreamReader(output).ReadToEndAsync();

        // Assert
        Assert.Contains("\"jsonrpc\":\"2.0\"", written);
        Assert.Contains("\"error\"", written);
        Assert.Contains("-32700", written);
        Assert.Contains("Parse error", written);
    }
}
