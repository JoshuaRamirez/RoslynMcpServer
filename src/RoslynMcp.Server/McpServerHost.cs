using System.Text.Json;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Logging;
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
        _toolRegistry.Register(new SortUsingsTool(workspaceProvider));
        _toolRegistry.Register(new GenerateConstructorTool(workspaceProvider));

        // Phase 2 - Expanded Operations
        _toolRegistry.Register(new ExtractInterfaceTool(workspaceProvider));
        _toolRegistry.Register(new ImplementInterfaceTool(workspaceProvider));
        _toolRegistry.Register(new GenerateOverridesTool(workspaceProvider));
        _toolRegistry.Register(new ExtractVariableTool(workspaceProvider));
        _toolRegistry.Register(new InlineVariableTool(workspaceProvider));
        _toolRegistry.Register(new ExtractConstantTool(workspaceProvider));
        _toolRegistry.Register(new ChangeSignatureTool(workspaceProvider));
        _toolRegistry.Register(new EncapsulateFieldTool(workspaceProvider));
        _toolRegistry.Register(new ConvertToAsyncTool(workspaceProvider));
        _toolRegistry.Register(new ExtractBaseClassTool(workspaceProvider));

        // Code Navigation / Query Tools
        _toolRegistry.Register(new FindReferencesTool(workspaceProvider));
        _toolRegistry.Register(new GoToDefinitionTool(workspaceProvider));
        _toolRegistry.Register(new GetSymbolInfoTool(workspaceProvider));
        _toolRegistry.Register(new FindImplementationsTool(workspaceProvider));
        _toolRegistry.Register(new SearchSymbolsTool(workspaceProvider));

        // Analysis & Metrics Tools
        _toolRegistry.Register(new GetDiagnosticsTool(workspaceProvider));
        _toolRegistry.Register(new GetCodeMetricsTool(workspaceProvider));
        _toolRegistry.Register(new AnalyzeControlFlowTool(workspaceProvider));

        // Navigation & Hierarchy Tools
        _toolRegistry.Register(new FindCallersTool(workspaceProvider));
        _toolRegistry.Register(new GetTypeHierarchyTool(workspaceProvider));
        _toolRegistry.Register(new GetDocumentOutlineTool(workspaceProvider));

        // Code Generation & Formatting Tools
        _toolRegistry.Register(new GenerateEqualsHashCodeTool(workspaceProvider));
        _toolRegistry.Register(new GenerateToStringTool(workspaceProvider));
        _toolRegistry.Register(new FormatDocumentTool(workspaceProvider));
        _toolRegistry.Register(new AddNullChecksTool(workspaceProvider));

        // Data Flow & Conversion Tools
        _toolRegistry.Register(new AnalyzeDataFlowTool(workspaceProvider));
        _toolRegistry.Register(new ConvertExpressionBodyTool(workspaceProvider));
        _toolRegistry.Register(new ConvertPropertyTool(workspaceProvider));
        _toolRegistry.Register(new IntroduceParameterTool(workspaceProvider));

        // Syntax Conversion Tools
        _toolRegistry.Register(new ConvertForeachLinqTool(workspaceProvider));
        _toolRegistry.Register(new ConvertToPatternMatchingTool(workspaceProvider));
        _toolRegistry.Register(new ConvertToInterpolatedStringTool(workspaceProvider));

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
        FileLogger.Log("McpServerHost.RunAsync starting message loop...");
        await LogAsync("Roslyn MCP Server starting...");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var request = await _transport.ReadMessageAsync(cancellationToken);
                if (request == null)
                {
                    // Stream closed
                    FileLogger.Log("Transport stream closed, exiting message loop.");
                    break;
                }

                FileLogger.Log($"Received request: method={request.Method}, id={request.Id}");
                var response = await HandleRequestAsync(request, cancellationToken);

                // JSON-RPC notifications do not include an id and must not receive a response.
                if (request.Id != null)
                {
                    await _transport.WriteMessageAsync(response, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                FileLogger.Log("Operation cancelled, exiting message loop.");
                break;
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Failed to parse MCP message"))
            {
                // JSON parse error - send proper JSON-RPC error response
                // Per JSON-RPC spec, use error code -32700 for parse errors
                // ID is null because we couldn't parse the request to get an ID
                FileLogger.LogError("JSON parse error", ex);
                await LogAsync($"JSON parse error: {ex.Message}");
                var errorResponse = McpResponse.Failure(null, -32700, $"Parse error: {ex.InnerException?.Message ?? ex.Message}");
                await _transport.WriteMessageAsync(errorResponse, cancellationToken);
            }
            catch (Exception ex)
            {
                FileLogger.LogError("Error handling request", ex);
                await LogAsync($"Error handling request: {ex.Message}");
            }
        }

        FileLogger.Log("McpServerHost.RunAsync message loop ended.");
        await LogAsync("Roslyn MCP Server shutting down...");
    }

    private async Task<McpResponse> HandleRequestAsync(McpRequest request, CancellationToken cancellationToken)
    {
        return request.Method switch
        {
            "initialize" => HandleInitialize(request),
            "initialized" => HandleInitialized(request),
            "notifications/initialized" => HandleInitialized(request),
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
                version = typeof(McpServerHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"
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
            FileLogger.LogWarning($"Unknown tool requested: {callParams.Name}");
            return McpResponse.Failure(request.Id, -32602, $"Unknown tool: {callParams.Name}");
        }

        try
        {
            FileLogger.Log($"Executing tool: {callParams.Name}");
            var result = await handler.ExecuteAsync(callParams.Arguments, cancellationToken);
            FileLogger.Log($"Tool completed successfully: {callParams.Name}");
            return McpResponse.Success(request.Id, result);
        }
        catch (Exception ex)
        {
            FileLogger.LogError($"Tool execution failed: {callParams.Name}", ex);
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
