using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Hierarchy;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for pull_members_up operation.
/// </summary>
public sealed class PullMembersUpTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new pull members up tool.
    /// </summary>
    public PullMembersUpTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "pull_members_up";

    /// <inheritdoc />
    public string Description => "Move selected members from a derived type onto an existing base class or interface.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "typeName", "members" },
        properties = new
        {
            solutionPath = new
            {
                type = "string",
                description = "Absolute path to the .sln or .csproj file"
            },
            sourceFile = new
            {
                type = "string",
                description = "Absolute path to the source file containing the derived type"
            },
            typeName = new
            {
                type = "string",
                description = "Name of the derived class or struct"
            },
            members = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Names of members to move to the base class or interface. Indexers match Item, this[], and this[int i]."
            },
            targetBaseType = new
            {
                type = "string",
                description = "Target base class or interface name. Uses the nearest base if omitted."
            },
            makeAbstract = new
            {
                type = "boolean",
                description = "Declare abstract members on the base class and keep overrides on the derived type",
                @default = false
            },
            preview = new
            {
                type = "boolean",
                description = "Return computed changes without applying",
                @default = false
            }
        },
        additionalProperties = false
    };

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(JsonElement? arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (arguments == null)
            {
                return ToolResult.Error("Arguments required");
            }

            var args = JsonSerializer.Deserialize<PullMembersUpArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new PullMembersUpOperation(context);
            var @params = new PullMembersUpParams
            {
                SourceFile = args.SourceFile,
                TypeName = args.TypeName,
                Members = args.Members ?? new List<string>(),
                TargetBaseType = args.TargetBaseType,
                MakeAbstract = args.MakeAbstract ?? false,
                Preview = args.Preview ?? false
            };

            var result = await operation.ExecuteAsync(@params, cancellationToken);

            var json = JsonSerializer.Serialize(result, _jsonOptions);
            return result.Success ? ToolResult.Success(json) : ToolResult.Error(json);
        }
        catch (RefactoringException ex)
        {
            var error = ex.ToError();
            var json = JsonSerializer.Serialize(new { success = false, error }, _jsonOptions);
            return ToolResult.Error(json);
        }
        catch (Exception ex)
        {
            var json = JsonSerializer.Serialize(new
            {
                success = false,
                error = new { code = "INTERNAL_ERROR", message = ex.Message }
            }, _jsonOptions);
            return ToolResult.Error(json);
        }
    }

    private sealed class PullMembersUpArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string TypeName { get; init; } = "";
        public List<string>? Members { get; init; }
        public string? TargetBaseType { get; init; }
        public bool? MakeAbstract { get; init; }
        public bool? Preview { get; init; }
    }
}
