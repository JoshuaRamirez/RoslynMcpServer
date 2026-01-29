using System.Text;
using System.Text.Json;

namespace RoslynMcp.Server.Transport;

/// <summary>
/// Handles MCP communication over stdin/stdout.
/// </summary>
public sealed class StdioTransport : IDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    /// <summary>
    /// Creates a new stdio transport using Console streams.
    /// </summary>
    public StdioTransport() : this(Console.OpenStandardInput(), Console.OpenStandardOutput())
    {
    }

    /// <summary>
    /// Creates a new stdio transport with custom streams (for testing).
    /// </summary>
    public StdioTransport(Stream input, Stream output)
    {
        _reader = new StreamReader(input, Encoding.UTF8);
        _writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true };

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Reads the next message from stdin.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed request, or null if stream ended.</returns>
    public async Task<McpRequest?> ReadMessageAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var line = await _reader.ReadLineAsync(cancellationToken);
        if (line == null) return null;

        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            return JsonSerializer.Deserialize<McpRequest>(line, _jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse MCP message: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Writes a response to stdout.
    /// </summary>
    /// <param name="response">Response to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task WriteMessageAsync(McpResponse response, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var json = JsonSerializer.Serialize(response, _jsonOptions);
        await _writer.WriteLineAsync(json.AsMemory(), cancellationToken);
    }

    /// <summary>
    /// Writes a notification to stdout (no id, no response expected).
    /// </summary>
    /// <param name="method">Notification method.</param>
    /// <param name="params">Notification parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task WriteNotificationAsync(string method, object? @params = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var notification = new
        {
            jsonrpc = "2.0",
            method,
            @params
        };

        var json = JsonSerializer.Serialize(notification, _jsonOptions);
        await _writer.WriteLineAsync(json.AsMemory(), cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reader.Dispose();
        _writer.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
