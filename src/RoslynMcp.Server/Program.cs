using RoslynMcp.Core.Workspace;
using RoslynMcp.Server;
using RoslynMcp.Server.Logging;

// Log startup
FileLogger.Log($"RoslynMcp Server starting. Log file: {FileLogger.LogFilePath}");
FileLogger.Log($"Process ID: {Environment.ProcessId}");
FileLogger.Log($"Working directory: {Environment.CurrentDirectory}");
FileLogger.Log($".NET Version: {Environment.Version}");

// Wire up logging callbacks for MSBuildWorkspaceProvider
MSBuildWorkspaceProvider.LogCallback = FileLogger.Log;
MSBuildWorkspaceProvider.LogErrorCallback = FileLogger.LogError;

// Create cancellation token for graceful shutdown
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    FileLogger.Log("Received cancel signal, initiating graceful shutdown...");
    cts.Cancel();
};

// Create workspace provider
FileLogger.Log("Creating MSBuildWorkspaceProvider...");
var workspaceProvider = new MSBuildWorkspaceProvider();
FileLogger.Log("MSBuildWorkspaceProvider created successfully.");

// Create and run server
FileLogger.Log("Creating McpServerHost...");
await using var server = new McpServerHost(workspaceProvider);
FileLogger.Log("McpServerHost created, starting message loop...");

try
{
    await server.RunAsync(cts.Token);
    FileLogger.Log("Server message loop ended normally.");
}
catch (Exception ex)
{
    FileLogger.LogError("Server message loop failed with exception", ex);
    throw;
}
finally
{
    FileLogger.Log("RoslynMcp Server shutting down.");
}
