using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for change_signature operation.
/// </summary>
public sealed class ChangeSignatureTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new change signature tool.
    /// </summary>
    public ChangeSignatureTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "change_signature";

    /// <inheritdoc />
    public string Description => "Add, remove, or reorder method parameters and update all call sites.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "methodName", "parameters" },
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
                description = "Absolute path to the source file containing the method"
            },
            methodName = new
            {
                type = "string",
                description = "Name of the method to modify"
            },
            line = new
            {
                type = "integer",
                description = "Line number for disambiguation if multiple methods have the same name (1-based)"
            },
            parameters = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        originalName = new
                        {
                            type = "string",
                            description = "Original parameter name (null for new parameters)"
                        },
                        name = new
                        {
                            type = "string",
                            description = "New parameter name"
                        },
                        type = new
                        {
                            type = "string",
                            description = "Parameter type (required for new parameters)"
                        },
                        defaultValue = new
                        {
                            type = "string",
                            description = "Default value for the parameter"
                        },
                        newPosition = new
                        {
                            type = "integer",
                            description = "New position in parameter list (0-based)"
                        },
                        remove = new
                        {
                            type = "boolean",
                            description = "If true, removes this parameter",
                            @default = false
                        }
                    },
                    required = new[] { "name" }
                },
                description = "Parameter changes to apply"
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

            var args = JsonSerializer.Deserialize<ChangeSignatureArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new ChangeSignatureOperation(context);
            var @params = new ChangeSignatureParams
            {
                SourceFile = args.SourceFile,
                MethodName = args.MethodName,
                Line = args.Line,
                Parameters = args.Parameters?.Select(p => new ParameterChange
                {
                    OriginalName = p.OriginalName,
                    Name = p.Name ?? "",
                    Type = p.Type,
                    DefaultValue = p.DefaultValue,
                    NewPosition = p.NewPosition,
                    Remove = p.Remove ?? false
                }).ToList() ?? new List<ParameterChange>(),
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

    private sealed class ChangeSignatureArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string MethodName { get; init; } = "";
        public int? Line { get; init; }
        public List<ParameterChangeArg>? Parameters { get; init; }
        public bool? Preview { get; init; }
    }

    private sealed class ParameterChangeArg
    {
        public string? OriginalName { get; init; }
        public string? Name { get; init; }
        public string? Type { get; init; }
        public string? DefaultValue { get; init; }
        public int? NewPosition { get; init; }
        public bool? Remove { get; init; }
    }
}
