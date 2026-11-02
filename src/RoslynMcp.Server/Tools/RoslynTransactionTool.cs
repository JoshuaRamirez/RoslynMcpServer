using System.Text.Json;
using System.Text.Json.Serialization;
using RoslynMcp.Contracts.Transactions;
using RoslynMcp.Core.Transactions;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for <c>roslyn_transaction</c> — executes an atomic multi-step <see cref="SolutionTransactionRequest"/>
/// pipeline over a warm in-memory <see cref="WarmSolutionSession"/> with unified diff preview and automatic compilation-error rollback.
/// </summary>
public sealed class RoslynTransactionTool : IToolHandler
{
    private readonly SolutionSessionManager _sessionManager;
    private readonly bool _persistToDisk;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new <see cref="RoslynTransactionTool"/> backed by a <see cref="SolutionSessionManager"/>.
    /// </summary>
    /// <param name="sessionManager">Warm solution session manager.</param>
    /// <param name="persistToDisk">Whether committed transactions write modified files to disk.</param>
    public RoslynTransactionTool(SolutionSessionManager sessionManager, bool persistToDisk = true)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        _sessionManager = sessionManager;
        _persistToDisk = persistToDisk;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    /// <summary>
    /// Initializes a new <see cref="RoslynTransactionTool"/> backed by an <see cref="IWorkspaceProvider"/>.
    /// </summary>
    /// <param name="workspaceProvider">Workspace provider wrapped by a <see cref="SolutionSessionManager"/>.</param>
    public RoslynTransactionTool(IWorkspaceProvider workspaceProvider)
        : this(new SolutionSessionManager(workspaceProvider), persistToDisk: true)
    {
    }

    /// <inheritdoc />
    public string Name => "roslyn_transaction";

    /// <inheritdoc />
    public string Description =>
        "Execute an atomic multi-step refactoring, code generation, or semantic rewrite transaction pipeline across a warm Roslyn Solution session. " +
        "Supports TargetCoordinate resolution, SyntaxGenerator declarations, CodeActionHost fixes, IOperation semantic pattern rewrites, " +
        "previewDiffOnly unified diffs, and rollbackOnCompilationErrors atomic rollback.";

    /// <inheritdoc />
    public object InputSchema => new Dictionary<string, object>
    {
        ["type"] = "object",
        ["required"] = new[] { "solutionPath", "operations" },
        ["properties"] = new Dictionary<string, object>
        {
            ["solutionPath"] = new
            {
                type = "string",
                description = "Absolute path to the .sln, .slnx, or .csproj file."
            },
            ["operations"] = new
            {
                type = "array",
                description = "Ordered list of SolutionOperation steps to fold sequentially over the immutable Solution snapshot.",
                items = new Dictionary<string, object> { ["$ref"] = "#/$defs/SolutionOperation" }
            },
            ["previewDiffOnly"] = new
            {
                type = "boolean",
                description = "When true, computes and returns unified diffs without committing changes to the warm session or disk.",
                @default = false
            },
            ["rollbackOnCompilationErrors"] = new
            {
                type = "boolean",
                description = "When true (default), automatically rolls back the entire transaction if any post-transaction project compilation introduces C# compiler errors.",
                @default = true
            }
        },
        ["$defs"] = CompactSchemaDefs.BuildActionAlgebraDefs(),
        ["additionalProperties"] = false
    };

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(JsonElement? arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (arguments is null)
            {
                return ToolResult.Error("Arguments required");
            }

            var request = JsonSerializer.Deserialize<SolutionTransactionRequest>(
                arguments.Value.GetRawText(),
                _jsonOptions);

            if (request is null || string.IsNullOrWhiteSpace(request.SolutionPath))
            {
                return ToolResult.Error("solutionPath and operations are required");
            }

            var session = await _sessionManager
                .GetOrLoadSessionAsync(request.SolutionPath, cancellationToken)
                .ConfigureAwait(false);

            var result = await SolutionTransactionEngine
                .ExecuteAsync(session, request, _persistToDisk, cancellationToken)
                .ConfigureAwait(false);

            var json = JsonSerializer.Serialize(result, _jsonOptions);
            return result.Success ? ToolResult.Success(json) : ToolResult.Error(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = new SolutionTransactionResult
            {
                Success = false,
                RolledBack = true,
                Preview = false,
                OperationsExecuted = 0,
                FileDiffs = [],
                CompilationErrors = [],
                ExecutionTimeMs = 0,
                Error = ex.Message
            };
            return ToolResult.Error(JsonSerializer.Serialize(failure, _jsonOptions));
        }
    }
}
