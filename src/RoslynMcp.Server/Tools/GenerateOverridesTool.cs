using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for generate_overrides operation.
/// </summary>
public sealed class GenerateOverridesTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new generate overrides tool.
    /// </summary>
    public GenerateOverridesTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "generate_overrides";

    /// <inheritdoc />
    public string Description => "Generate override methods, properties/indexers, and events for base class virtual/abstract members. line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick. column (optional) picks the type whose identifier or declaration span covers that 1-based column when set with line (identifier preferred, then smallest containing type); omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter. callBase (default true) emits base.Method() / base.Prop / base[i] for non-abstract virtuals (events always use empty add/remove); replaceExisting (default false) replaces already-overridden members instead of skipping them. allFiles: true walks every C# file and generates missing overrides for every eligible type (sourceFile optional when true; cannot be combined with typeName, members, line, or column).";

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
                description = "Absolute path to the source file containing the type. Required when allFiles is false. When allFiles is true, optional and limits the walk to that one file."
            },
            allFiles = new
            {
                type = "boolean",
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with typeName, members, line, or column.",
                @default = false
            },
            typeName = new
            {
                type = "string",
                description = "Name of the type to generate overrides for. Single-site only; cannot be combined with allFiles."
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
                description = "Names of specific members to override (e.g., 'ToString', 'Equals'). If not specified, generates all available overrides. Single-site only; cannot be combined with allFiles."
            },
            callBase = new
            {
                type = "boolean",
                description = "Include base.Method() / base.Prop / base[i] calls in generated overrides. Abstract members still throw. Default true.",
                @default = true
            },
            replaceExisting = new
            {
                type = "boolean",
                description = "Replace already-overridden members with a freshly generated override instead of skipping them. Default false.",
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

            var args = JsonSerializer.Deserialize<GenerateOverridesArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new GenerateOverridesOperation(context);
            var @params = new GenerateOverridesParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                TypeName = args.TypeName,
                Line = args.Line,
                Column = args.Column,
                Members = args.Members,
                CallBase = args.CallBase ?? true,
                ReplaceExisting = args.ReplaceExisting ?? false,
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

    private sealed class GenerateOverridesArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public string? TypeName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public List<string>? Members { get; init; }
        public bool? CallBase { get; init; }
        public bool? ReplaceExisting { get; init; }
        public bool? Preview { get; init; }
    }
}
