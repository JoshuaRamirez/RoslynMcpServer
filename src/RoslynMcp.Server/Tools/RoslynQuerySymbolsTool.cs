using System.Text.Json;
using System.Text.Json.Serialization;
using RoslynMcp.Contracts.Predicates;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for <c>roslyn_query_symbols</c> — evaluates a composable Layer 2 <see cref="SymbolPredicate"/> AST
/// in a single compilation pass over a warm in-memory <see cref="WarmSolutionSession"/>.
/// </summary>
public sealed class RoslynQuerySymbolsTool : IToolHandler
{
    private readonly SolutionSessionManager _sessionManager;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new <see cref="RoslynQuerySymbolsTool"/> backed by a <see cref="SolutionSessionManager"/>.
    /// </summary>
    /// <param name="sessionManager">Warm solution session manager.</param>
    public RoslynQuerySymbolsTool(SolutionSessionManager sessionManager)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        _sessionManager = sessionManager;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    /// <summary>
    /// Initializes a new <see cref="RoslynQuerySymbolsTool"/> backed by an <see cref="IWorkspaceProvider"/>.
    /// </summary>
    /// <param name="workspaceProvider">Workspace provider wrapped by a <see cref="SolutionSessionManager"/>.</param>
    public RoslynQuerySymbolsTool(IWorkspaceProvider workspaceProvider)
        : this(new SolutionSessionManager(workspaceProvider))
    {
    }

    /// <inheritdoc />
    public string Name => "roslyn_query_symbols";

    /// <inheritdoc />
    public string Description =>
        "Evaluate a composable SymbolPredicate AST (All, Any, Not, SymbolKindIn, AccessibilityIn, NameMatches, NamespaceMatches, " +
        "HasAttribute, ImplementsOrInherits, CallerCountRange, ReferencesCountRange, InDocumentScope) in a single pass across a warm Roslyn Solution session.";

    /// <inheritdoc />
    public object InputSchema => new Dictionary<string, object>
    {
        ["type"] = "object",
        ["required"] = new[] { "solutionPath", "predicate" },
        ["properties"] = new Dictionary<string, object>
        {
            ["solutionPath"] = new
            {
                type = "string",
                description = "Absolute path to the .sln, .slnx, or .csproj file."
            },
            ["predicate"] = new Dictionary<string, object>
            {
                ["$ref"] = "#/$defs/SymbolPredicate",
                ["description"] = "Composable SymbolPredicate AST to evaluate."
            },
            ["maxResults"] = new
            {
                type = "integer",
                description = "Maximum number of matched symbols to return before truncation (default 200).",
                minimum = 1,
                @default = 200
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

            var request = JsonSerializer.Deserialize<QuerySymbolsRequest>(
                arguments.Value.GetRawText(),
                _jsonOptions);

            if (request is null || string.IsNullOrWhiteSpace(request.SolutionPath) || request.Predicate is null)
            {
                return ToolResult.Error("solutionPath and predicate are required");
            }

            var session = await _sessionManager
                .GetOrLoadSessionAsync(request.SolutionPath, cancellationToken)
                .ConfigureAwait(false);

            var result = await SymbolPredicateEvaluator
                .EvaluateAsync(session.CurrentSolution, request.Predicate, request.MaxResults, cancellationToken)
                .ConfigureAwait(false);

            var json = JsonSerializer.Serialize(result, _jsonOptions);
            return result.Success ? ToolResult.Success(json) : ToolResult.Error(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = new SymbolQueryResult
            {
                Success = false,
                Symbols = [],
                TotalCount = 0,
                Truncated = false,
                ExecutionTimeMs = 0,
                Error = ex.Message
            };
            return ToolResult.Error(JsonSerializer.Serialize(failure, _jsonOptions));
        }
    }
}
