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
    public string Description => "Generate interface member implementations for a type.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "typeName", "interfaceName" },
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
                description = "Absolute path to the source file containing the type"
            },
            typeName = new
            {
                type = "string",
                description = "Name of the type to implement interface on"
            },
            interfaceName = new
            {
                type = "string",
                description = "Name of the interface to implement (simple or fully qualified)"
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
                description = "Names of specific members to implement. If not specified, implements all missing members."
            },
            throwNotImplemented = new
            {
                type = "boolean",
                description = "Throw NotImplementedException in method bodies",
                @default = true
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
                TypeName = args.TypeName,
                InterfaceName = args.InterfaceName,
                ExplicitImplementation = args.ExplicitImplementation ?? false,
                Members = args.Members,
                ThrowNotImplemented = args.ThrowNotImplemented ?? true,
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
        public string SourceFile { get; init; } = "";
        public string TypeName { get; init; } = "";
        public string InterfaceName { get; init; } = "";
        public bool? ExplicitImplementation { get; init; }
        public List<string>? Members { get; init; }
        public bool? ThrowNotImplemented { get; init; }
        public bool? Preview { get; init; }
    }
}
