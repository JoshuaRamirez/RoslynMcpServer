using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for implement_interface operation.
/// </summary>
public sealed class ImplementInterfaceTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new implement interface tool.
    /// </summary>
    public ImplementInterfaceTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "implement_interface";

    /// <inheritdoc />
    public string Description =>
        "Generate interface member implementations for a type. line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick. column (optional) picks the type whose identifier or declaration span covers that 1-based column when set with line (identifier preferred, then smallest containing type); omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter. throwNotImplemented (default true) throws NotImplementedException in stub bodies; replaceExisting (default false) replaces already-implemented interface members instead of failing; preview returns computed changes without applying. allFiles: true walks every C# file and implements missing members of already-declared interfaces for every eligible type (sourceFile optional when true; cannot be combined with typeName, interfaceName, members, line, or column).";

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
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with typeName, interfaceName, members, line, or column.",
                @default = false
            },
            typeName = new
            {
                type = "string",
                description = "Name of the type to implement interface on. Single-site only; cannot be combined with allFiles."
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
            interfaceName = new
            {
                type = "string",
                description = "Name of the interface to implement (simple or fully qualified). Single-site only; cannot be combined with allFiles."
            },
            explicitImplementation = new
            {
                type = "boolean",
                description = "Use explicit interface implementation",
                @default = false
            },
            members = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Names of specific members to implement. If not specified, implements all missing members (and, when replaceExisting is true, replaces existing implementable members). Single-site only; cannot be combined with allFiles."
            },
            throwNotImplemented = new
            {
                type = "boolean",
                description = "Throw NotImplementedException in method bodies",
                @default = true
            },
            replaceExisting = new
            {
                type = "boolean",
                description = "Replace already-implemented interface members with freshly generated stubs instead of failing. Default false.",
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

            var args = JsonSerializer.Deserialize<ImplementInterfaceArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new ImplementInterfaceOperation(context);
            var @params = new ImplementInterfaceParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                TypeName = args.TypeName,
                Line = args.Line,
                Column = args.Column,
                InterfaceName = args.InterfaceName,
                ExplicitImplementation = args.ExplicitImplementation ?? false,
                Members = args.Members,
                ThrowNotImplemented = args.ThrowNotImplemented ?? true,
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

    private sealed class ImplementInterfaceArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public string? TypeName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public string? InterfaceName { get; init; }
        public bool? ExplicitImplementation { get; init; }
        public List<string>? Members { get; init; }
        public bool? ThrowNotImplemented { get; init; }
        public bool? ReplaceExisting { get; init; }
        public bool? Preview { get; init; }
    }
}
