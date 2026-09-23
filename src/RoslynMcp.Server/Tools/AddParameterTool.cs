using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for add_parameter operation.
/// </summary>
public sealed class AddParameterTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new add parameter tool.
    /// </summary>
    public AddParameterTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "add_parameter";

    /// <inheritdoc />
    public string Description =>
        "Add a named parameter to a method and update call sites, overrides, and interface implementations. column (optional) picks the smallest method whose identifier or declaration span covers that column; omitted keeps today's methodName and/or line start-line pick. sourceFile and methodName are required when allFiles is omitted or false. allFiles: true walks every C# file and adds the same parameterName/parameterType (and optional defaultValue/position) to every eligible method under today's single-site validation (sourceFile optional when true; cannot be combined with methodName, line, or column; parameterName and parameterType remain required). updateOverrides / updateImplementations / preview remain valid with allFiles.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "parameterName", "parameterType" },
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
                description = "Absolute path to the source file containing the method. Required when allFiles is false. When allFiles is true, optional and limits the walk to that one file."
            },
            allFiles = new
            {
                type = "boolean",
                description = "When true, walk every C# document (or the optional single sourceFile) and add the same parameter to every eligible method. Cannot be combined with methodName, line, or column. Default false.",
                @default = false
            },
            methodName = new
            {
                type = "string",
                description = "Name of the method to modify. Single-site only; cannot be combined with allFiles."
            },
            parameterName = new
            {
                type = "string",
                description = "Name for the new parameter. Required for both single-site and allFiles."
            },
            parameterType = new
            {
                type = "string",
                description = "C# type of the new parameter. Required for both single-site and allFiles."
            },
            defaultValue = new
            {
                type = "string",
                description = "Default value for existing call sites. Valid with allFiles."
            },
            position = new
            {
                type = "integer",
                description = "0-based insertion position; -1 inserts at the end of required parameters. Valid with allFiles.",
                @default = -1
            },
            line = new
            {
                type = "integer",
                description = "Line number for disambiguation if multiple methods have the same name (1-based). Single-site only; cannot be combined with allFiles."
            },
            column = new
            {
                type = "integer",
                description = "1-based column for disambiguation. When set, selects the smallest method whose identifier or declaration span covers that column. Omitted keeps today's MethodName and/or Line start-line pick. Single-site only; cannot be combined with allFiles."
            },
            updateOverrides = new
            {
                type = "boolean",
                description = "Update the virtual/override chain together. Valid with allFiles.",
                @default = true
            },
            updateImplementations = new
            {
                type = "boolean",
                description = "Update interface declarations and implementations together. Valid with allFiles.",
                @default = true
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
                required = new[] { "solutionPath", "sourceFile", "methodName", "parameterName", "parameterType" }
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
                required = new[] { "solutionPath", "allFiles", "parameterName", "parameterType" },
                not = new
                {
                    anyOf = new object[]
                    {
                        new { required = new[] { "methodName" } },
                        new { required = new[] { "line" } },
                        new { required = new[] { "column" } }
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

            var args = JsonSerializer.Deserialize<AddParameterArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new AddParameterOperation(context);
            var @params = new AddParameterParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                MethodName = args.MethodName,
                ParameterName = args.ParameterName,
                ParameterType = args.ParameterType,
                DefaultValue = args.DefaultValue,
                Position = args.Position ?? -1,
                Line = args.Line,
                Column = args.Column,
                UpdateOverrides = args.UpdateOverrides ?? true,
                UpdateImplementations = args.UpdateImplementations ?? true,
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

    private sealed class AddParameterArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public string? MethodName { get; init; }
        public string ParameterName { get; init; } = "";
        public string ParameterType { get; init; } = "";
        public string? DefaultValue { get; init; }
        public int? Position { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public bool? UpdateOverrides { get; init; }
        public bool? UpdateImplementations { get; init; }
        public bool? Preview { get; init; }
    }
}
