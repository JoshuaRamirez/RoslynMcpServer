using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynMcp.Server.Transport;

/// <summary>
/// JSON-RPC 2.0 request message.
/// </summary>
public sealed class McpRequest
{
    /// <summary>
    /// JSON-RPC protocol version (always "2.0").
    /// </summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    /// <summary>
    /// Request identifier for matching responses.
    /// </summary>
    [JsonPropertyName("id")]
    public object? Id { get; init; }

    /// <summary>
    /// Method name to invoke.
    /// </summary>
    [JsonPropertyName("method")]
    public required string Method { get; init; }

    /// <summary>
    /// Method parameters as JSON element.
    /// </summary>
    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }
}

/// <summary>
/// JSON-RPC 2.0 response message.
/// </summary>
public sealed class McpResponse
{
    /// <summary>
    /// JSON-RPC protocol version (always "2.0").
    /// </summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    /// <summary>
    /// Request identifier matching the original request.
    /// </summary>
    [JsonPropertyName("id")]
    public object? Id { get; init; }

    /// <summary>
    /// Result data for successful responses.
    /// </summary>
    [JsonPropertyName("result")]
    public object? Result { get; init; }

    /// <summary>
    /// Error information for failed responses.
    /// </summary>
    [JsonPropertyName("error")]
    public McpError? Error { get; init; }

    /// <summary>
    /// Creates a successful response.
    /// </summary>
    public static McpResponse Success(object? id, object result) => new()
    {
        Id = id,
        Result = result
    };

    /// <summary>
    /// Creates an error response.
    /// </summary>
    public static McpResponse Failure(object? id, int code, string message, object? data = null) => new()
    {
        Id = id,
        Error = new McpError
        {
            Code = code,
            Message = message,
            Data = data
        }
    };
}

/// <summary>
/// JSON-RPC 2.0 error object.
/// </summary>
public sealed class McpError
{
    /// <summary>
    /// Error code indicating the type of error.
    /// </summary>
    [JsonPropertyName("code")]
    public required int Code { get; init; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// Additional error data (optional).
    /// </summary>
    [JsonPropertyName("data")]
    public object? Data { get; init; }
}

/// <summary>
/// MCP tool call parameters.
/// </summary>
public sealed class ToolCallParams
{
    /// <summary>
    /// Name of the tool to invoke.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Tool-specific arguments as JSON element.
    /// </summary>
    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; init; }
}

/// <summary>
/// MCP tool result content.
/// </summary>
public sealed class ToolResult
{
    /// <summary>
    /// List of content items in the result.
    /// </summary>
    [JsonPropertyName("content")]
    public required IReadOnlyList<ToolContent> Content { get; init; }

    /// <summary>
    /// Indicates whether this result represents an error.
    /// </summary>
    [JsonPropertyName("isError")]
    public bool IsError { get; init; }

    /// <summary>
    /// Creates a success result.
    /// </summary>
    public static ToolResult Success(string text) => new()
    {
        Content = [new ToolContent { Type = "text", Text = text }],
        IsError = false
    };

    /// <summary>
    /// Creates an error result.
    /// </summary>
    public static ToolResult Error(string text) => new()
    {
        Content = [new ToolContent { Type = "text", Text = text }],
        IsError = true
    };
}

/// <summary>
/// MCP tool content item.
/// </summary>
public sealed class ToolContent
{
    /// <summary>
    /// Content type (e.g., "text").
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Text content (for type="text").
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>
/// MCP tool definition.
/// </summary>
public sealed class ToolDefinition
{
    /// <summary>
    /// Unique tool name.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Human-readable tool description.
    /// </summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>
    /// JSON schema defining the tool's input parameters.
    /// </summary>
    [JsonPropertyName("inputSchema")]
    public required object InputSchema { get; init; }
}
