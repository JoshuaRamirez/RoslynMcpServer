using System.Text.Json;
using RoslynMcp.Cli;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Workspace;

// Exit codes
const int ExitSuccess = 0;
const int ExitToolError = 1;
const int ExitCliError = 2;
const int ExitEnvironmentError = 3;

// Wire MSBuild logging to stderr so it doesn't pollute stdout
MSBuildWorkspaceProvider.LogCallback = msg => Console.Error.WriteLine($"[info] {msg}");
MSBuildWorkspaceProvider.LogErrorCallback = (msg, ex) =>
    Console.Error.WriteLine($"[error] {msg}{(ex is not null ? $": {ex.Message}" : "")}");

// Parse args
var parsed = CliArgs.Parse(args);

// Build registry
var registry = ToolRegistry.BuildDefault();

// ── Global help ──────────────────────────────────────────────────
if (parsed.ShowHelp)
{
    Console.Write(HelpGenerator.GenerateGlobalHelp(registry));
    return ExitSuccess;
}

// ── Tool help ────────────────────────────────────────────────────
if (parsed.ShowToolHelp && parsed.ToolName is not null)
{
    var toolForHelp = registry.GetTool(parsed.ToolName);
    if (toolForHelp is null)
    {
        Console.Error.WriteLine($"Unknown tool: {parsed.ToolName}");
        Console.Error.WriteLine("Run 'roslyn-cli --help' to see available tools.");
        return ExitCliError;
    }
    Console.Write(HelpGenerator.GenerateToolHelp(toolForHelp));
    return ExitSuccess;
}

// ── Tool execution ───────────────────────────────────────────────
if (parsed.ToolName is null)
{
    Console.Error.WriteLine("No tool specified. Run 'roslyn-cli --help' for usage.");
    return ExitCliError;
}

var tool = registry.GetTool(parsed.ToolName);
if (tool is null)
{
    Console.Error.WriteLine($"Unknown tool: {parsed.ToolName}");
    Console.Error.WriteLine("Run 'roslyn-cli --help' to see available tools.");
    return ExitCliError;
}

if (tool.RequiresWorkspace && string.IsNullOrEmpty(parsed.SolutionPath))
{
    Console.Error.WriteLine($"Tool '{parsed.ToolName}' requires a solution path.");
    Console.Error.WriteLine($"Usage: roslyn-cli <solution-path> {parsed.ToolName} [--option value ...]");
    return ExitCliError;
}

// Set up cancellation
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    var workspaceProvider = new MSBuildWorkspaceProvider();

    // Special handling for diagnose (needs IWorkspaceProvider, not WorkspaceContext)
    if (parsed.ToolName.Equals("diagnose", StringComparison.OrdinalIgnoreCase))
    {
        var diagnoseResult = await ExecuteDiagnoseAsync(workspaceProvider, parsed, cts.Token);
        OutputResult(diagnoseResult, parsed.Format);
        return diagnoseResult is DiagnoseResult dr && dr.Healthy ? ExitSuccess : ExitToolError;
    }

    // Convert args to JSON
    var json = ArgsToJsonConverter.Convert(parsed.Options);

    if (parsed.Verbose)
        Console.Error.WriteLine($"[verbose] Params JSON: {json}");

    // Load workspace
    if (parsed.Verbose)
        Console.Error.WriteLine($"[verbose] Loading solution: {parsed.SolutionPath}");

    using var context = await workspaceProvider.CreateContextAsync(parsed.SolutionPath!, cts.Token);

    if (parsed.Verbose)
        Console.Error.WriteLine($"[verbose] Solution loaded. Executing tool: {parsed.ToolName}");

    // Execute tool
    var result = await tool.Execute(context, json, cts.Token);

    // Output result
    OutputResult(result, parsed.Format);

    // Determine exit code from result
    return GetExitCode(result);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Operation cancelled.");
    return ExitCliError;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    if (parsed.Verbose)
        Console.Error.WriteLine(ex.ToString());
    return IsEnvironmentError(ex) ? ExitEnvironmentError : ExitToolError;
}

// ── Helper methods ───────────────────────────────────────────────

void OutputResult(object result, string format)
{
    var output = format.Equals("text", StringComparison.OrdinalIgnoreCase)
        ? OutputFormatter.FormatText(result)
        : OutputFormatter.FormatJson(result);
    Console.Write(output);
}

int GetExitCode(object result)
{
    // Check for Success property via reflection (works for both RefactoringResult and QueryResult<T>)
    var successProp = result.GetType().GetProperty("Success");
    if (successProp is not null)
    {
        var success = (bool)successProp.GetValue(result)!;
        return success ? ExitSuccess : ExitToolError;
    }
    return ExitSuccess;
}

bool IsEnvironmentError(Exception ex)
{
    // Check exception types first for reliable detection
    if (ex is FileNotFoundException or DirectoryNotFoundException)
        return true;

    // Fallback to message matching for MSBuild/SDK loading failures
    return ex.Message.Contains("MSBuild", StringComparison.OrdinalIgnoreCase) ||
           ex.Message.Contains("SDK", StringComparison.OrdinalIgnoreCase);
}

async Task<DiagnoseResult> ExecuteDiagnoseAsync(
    MSBuildWorkspaceProvider provider,
    ParsedArgs args,
    CancellationToken ct)
{
    var envDiag = provider.CheckEnvironment();
    var errors = new List<RefactoringError>();

    if (!envDiag.MsBuildFound)
    {
        errors.Add(RefactoringError.Create(
            ErrorCodes.MsBuildNotFound,
            envDiag.ErrorMessage ?? "MSBuild not found"));
    }

    var roslynVersion = typeof(Microsoft.CodeAnalysis.Compilation)
        .Assembly.GetName().Version?.ToString();

    WorkspaceStatus workspaceStatus;
    if (!string.IsNullOrEmpty(args.SolutionPath))
    {
        try
        {
            using var context = await provider.CreateContextAsync(args.SolutionPath, ct);
            workspaceStatus = new WorkspaceStatus
            {
                State = WorkspaceState.Ready,
                SolutionLoaded = true,
                SolutionPath = context.LoadedPath,
                ProjectCount = context.Solution.Projects.Count(),
                DocumentCount = context.Solution.Projects.Sum(p => p.Documents.Count())
            };
        }
        catch (Exception ex)
        {
            workspaceStatus = new WorkspaceStatus
            {
                State = WorkspaceState.Error,
                SolutionLoaded = false
            };
            errors.Add(RefactoringError.Create(
                ErrorCodes.SolutionLoadFailed, ex.Message));
        }
    }
    else
    {
        workspaceStatus = new WorkspaceStatus
        {
            State = WorkspaceState.Unloaded,
            SolutionLoaded = false
        };
    }

    return new DiagnoseResult
    {
        Healthy = envDiag.MsBuildFound && errors.Count == 0,
        Components = new ComponentStatus
        {
            RoslynAvailable = true,
            RoslynVersion = roslynVersion,
            MsBuildFound = envDiag.MsBuildFound,
            MsBuildVersion = envDiag.MsBuildVersion,
            DotnetSdkAvailable = !string.IsNullOrEmpty(envDiag.DotnetSdkVersion),
            DotnetSdkVersion = envDiag.DotnetSdkVersion
        },
        Workspace = workspaceStatus,
        Capabilities = envDiag.MsBuildFound
            ? ["move_type_to_file", "move_type_to_namespace", "diagnose"]
            : ["diagnose"],
        Errors = errors,
        Warnings = []
    };
}
