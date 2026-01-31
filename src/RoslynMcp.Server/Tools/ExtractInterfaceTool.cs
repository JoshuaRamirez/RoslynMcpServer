using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for extract_interface operation.
/// </summary>
public sealed class ExtractInterfaceTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new extract interface tool.
    /// </summary>
    public ExtractInterfaceTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "extract_interface";

    /// <inheritdoc />
    public string Description => "Extract an interface from a class's public members.";

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
                description = "Name of the type to extract interface from"
            },
            interfaceName = new
            {
                type = "string",
                description = "Name for the new interface"
            },
            members = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Names of members to include. If not specified, includes all public instance members."
            },
            targetFile = new
            {
                type = "string",
                description = "Absolute path for the interface file. If not specified, creates in same file."
            },
            addInterfaceToType = new
            {
                type = "boolean",
                description = "Add the interface to the type's base list",
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

            var args = JsonSerializer.Deserialize<ExtractInterfaceArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new ExtractInterfaceOperation(context);
            var @params = new ExtractInterfaceParams
            {
                SourceFile = args.SourceFile,
                TypeName = args.TypeName,
                InterfaceName = args.InterfaceName,
                Members = args.Members,
                TargetFile = args.TargetFile,
                AddInterfaceToType = args.AddInterfaceToType ?? true,
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

    private sealed class ExtractInterfaceArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string TypeName { get; init; } = "";
        public string InterfaceName { get; init; } = "";
        public List<string>? Members { get; init; }
        public string? TargetFile { get; init; }
        public bool? AddInterfaceToType { get; init; }
        public bool? Preview { get; init; }
    }
}
