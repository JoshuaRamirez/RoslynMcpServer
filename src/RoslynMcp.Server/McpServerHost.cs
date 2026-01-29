using System.Text.Json;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server;

/// <summary>
/// Manages MCP server lifecycle: initialize, handle requests, shutdown.
/// </summary>
public sealed class McpServerHost : IAsyncDisposable
{
    private readonly StdioTransport _transport;
    private readonly ToolRegistry _toolRegistry;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new MCP server host.
    /// </summary>
    /// <param name="workspaceProvider">Workspace provider for refactoring operations.</param>
    public McpServerHost(IWorkspaceProvider workspaceProvider)
    {
        _transport = new StdioTransport();
        _toolRegistry = new ToolRegistry();

        // Register tools
        _toolRegistry.Register(new MoveTypeToFileTool(workspaceProvider));
        _toolRegistry.Register(new MoveTypeToNamespaceTool(workspaceProvider));
        _toolRegistry.Register(new DiagnoseTool(workspaceProvider));

        // Phase 1 - Tier 1 Operations
        _toolRegistry.Register(new RenameSymbolTool(workspaceProvider));
        _toolRegistry.Register(new ExtractMethodTool(workspaceProvider));
        _toolRegistry.Register(new AddMissingUsingsTool(workspaceProvider));
        _toolRegistry.Register(new RemoveUnusedUsingsTool(workspaceProvider));
        _toolRegistry.Register(new GenerateConstructorTool(workspaceProvider));

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <summary>
    /// Runs the MCP server message loop.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // Log startup
        await LogAsync("Roslyn MCP Server starting...");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var request = await _transport.ReadMessageAsync(cancellationToken);
                if (request == null)
                {
                    // Stream closed
                    break;
                }

                var response = await HandleRequestAsync(request, cancellationToken);
                await _transport.WriteMessageAsync(response, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await LogAsync($"Error handling request: {ex.Message}");
            }
        }

        await LogAsync("Roslyn MCP Server shutting down...");
    }

    private async Task<McpResponse> HandleRequestAsync(McpRequest request, CancellationToken cancellationToken)
    {
        return request.Method switch
        {
            "initialize" => HandleInitialize(request),
            "initialized" => HandleInitialized(request),
            "tools/list" => HandleToolsList(request),
            "tools/call" => await HandleToolCallAsync(request, cancellationToken),
            "shutdown" => HandleShutdown(request),
            _ => McpResponse.Failure(request.Id, -32601, $"Method not found: {request.Method}")
        };
    }

    private McpResponse HandleInitialize(McpRequest request)
    {
        return McpResponse.Success(request.Id, new
        {
            protocolVersion = "2024-11-05",
            capabilities = new
            {
                tools = new { }
            },
            serverInfo = new
            {
                name = "roslyn-mcp",
                version = "0.1.0"
            }
        });
    }

    private McpResponse HandleInitialized(McpRequest request)
    {
        // Notification, no response needed (but we return success for compatibility)
        return McpResponse.Success(request.Id, new { });
    }

    private McpResponse HandleToolsList(McpRequest request)
    {
        var tools = _toolRegistry.GetToolDefinitions();
        return McpResponse.Success(request.Id, new { tools });
    }

    private async Task<McpResponse> HandleToolCallAsync(McpRequest request, CancellationToken cancellationToken)
    {
        if (request.Params == null)
        {
            return McpResponse.Failure(request.Id, -32602, "Missing params");
        }

        ToolCallParams? callParams;
        try
        {
            callParams = JsonSerializer.Deserialize<ToolCallParams>(
                request.Params.Value.GetRawText(),
                _jsonOptions);
        }
        catch
        {
            return McpResponse.Failure(request.Id, -32602, "Invalid params");
        }

        if (callParams == null || string.IsNullOrEmpty(callParams.Name))
        {
            return McpResponse.Failure(request.Id, -32602, "Missing tool name");
        }

        var handler = _toolRegistry.GetHandler(callParams.Name);
        if (handler == null)
        {
            return McpResponse.Failure(request.Id, -32602, $"Unknown tool: {callParams.Name}");
        }

        try
        {
            var result = await handler.ExecuteAsync(callParams.Arguments, cancellationToken);
            return McpResponse.Success(request.Id, result);
        }
        catch (Exception ex)
        {
            return McpResponse.Failure(request.Id, -32603, $"Tool execution failed: {ex.Message}");
        }
    }

    private McpResponse HandleShutdown(McpRequest request)
    {
        return McpResponse.Success(request.Id, new { });
    }

    private async Task LogAsync(string message)
    {
        // Use MCP logging notification
        await _transport.WriteNotificationAsync("notifications/message", new
        {
            level = "info",
            logger = "roslyn-mcp",
            data = message
        });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _transport.Dispose();
        await Task.CompletedTask;
    }
}
