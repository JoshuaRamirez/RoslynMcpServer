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
    public string Description =>
        "Move selected members from a derived type onto an existing base class or interface. allFiles: true walks every C# file and pulls every pullable member from each eligible derived type onto its default target (nearest class base, or the single implemented interface; sourceFile optional when true; cannot be combined with typeName, line, column, members, or targetBaseType). line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick. column (optional) picks the type whose identifier or declaration span covers that 1-based column when set with line (identifier preferred, then smallest containing type); omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter. makeAbstract / preview remain valid with allFiles. sourceFile, typeName, and members are required when allFiles is false.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath" },
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
                description = "Absolute path to the source file containing the derived type. Required when allFiles is false. When allFiles is true, optional and limits the walk to that one file."
            },
            allFiles = new
            {
                type = "boolean",
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with typeName, line, column, members, or targetBaseType. Each eligible derived type pulls every pullable member onto its default target.",
                @default = false
            },
            typeName = new
            {
                type = "string",
                description = "Name of the derived class or struct. Required when allFiles is false. Single-site only; cannot be combined with allFiles."
            },
            line = new
            {
                type = "integer",
                description = "1-based line number for disambiguation when several types share the name. When set, selects the type whose identifier or declaration span covers that line (identifier preferred, then smallest containing type). Omitted keeps today's typeName FirstOrDefault pick. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column for disambiguation. When set with line, selects the type whose identifier or declaration span covers that column (identifier preferred, then smallest containing type). Omitted keeps today's typeName + optional line pick. Column without line keeps today's first-match after the typeName filter. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            members = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Names of members to move to the base class or interface. Indexers match Item, this[], and this[int i]. Required when allFiles is false. Single-site only; cannot be combined with allFiles."
            },
            targetBaseType = new
            {
                type = "string",
                description = "Target base class or interface name. Uses the nearest base if omitted. Single-site only; cannot be combined with allFiles."
            },
            makeAbstract = new
            {
                type = "boolean",
                description = "Declare abstract members on the base class and keep overrides on the derived type. Valid with allFiles.",
                @default = false
            },
            preview = new
            {
                type = "boolean",
                description = "Return computed changes without applying. Valid with allFiles.",
                @default = false
            }
        },
        oneOf = new object[]
        {
            new
            {
                properties = new
                {
                    allFiles = new
                    {
                        @enum = new[] { false }
                    }
                },
                required = new[] { "solutionPath", "sourceFile", "typeName", "members" }
            },
            new
            {
                properties = new
                {
                    allFiles = new
                    {
                        @const = true
                    }
                },
                required = new[] { "solutionPath", "allFiles" },
                not = new
                {
                    anyOf = new object[]
                    {
                        new { required = new[] { "typeName" } },
                        new { required = new[] { "line" } },
                        new { required = new[] { "column" } },
                        new { required = new[] { "members" } },
                        new { required = new[] { "targetBaseType" } }
                    }
                }
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
                AllFiles = args.AllFiles ?? false,
                TypeName = args.TypeName,
                Line = args.Line,
                Column = args.Column,
                Members = args.Members,
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
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public string? TypeName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public List<string>? Members { get; init; }
        public string? TargetBaseType { get; init; }
        public bool? MakeAbstract { get; init; }
        public bool? Preview { get; init; }
    }
}
