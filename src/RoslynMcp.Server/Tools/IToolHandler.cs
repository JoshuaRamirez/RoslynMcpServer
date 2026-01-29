using System.Text.Json;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// Abstraction for MCP tool implementations.
/// </summary>
public interface IToolHandler
{
    /// <summary>
    /// Tool name as registered with MCP.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Human-readable description.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// JSON Schema for input parameters.
    /// </summary>
    object InputSchema { get; }

    /// <summary>
    /// Executes the tool with the given arguments.
    /// </summary>
    /// <param name="arguments">JSON element containing tool arguments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tool result.</returns>
    Task<ToolResult> ExecuteAsync(JsonElement? arguments, CancellationToken cancellationToken = default);
}
