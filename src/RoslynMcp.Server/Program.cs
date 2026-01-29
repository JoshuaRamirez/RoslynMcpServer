using RoslynMcp.Core.Workspace;
using RoslynMcp.Server;

// Create cancellation token for graceful shutdown
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Create workspace provider
var workspaceProvider = new MSBuildWorkspaceProvider();

// Create and run server
await using var server = new McpServerHost(workspaceProvider);
await server.RunAsync(cts.Token);
