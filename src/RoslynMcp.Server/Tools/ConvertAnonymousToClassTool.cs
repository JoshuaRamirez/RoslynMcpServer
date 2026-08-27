using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for convert_anonymous_to_class operation.
/// </summary>
public sealed class ConvertAnonymousToClassTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new convert-anonymous-to-class tool.
    /// </summary>
    public ConvertAnonymousToClassTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "convert_anonymous_to_class";

    /// <inheritdoc />
    public string Description =>
        "Convert an anonymous type (new { ... }) to a named class or record and replace same-shape anonymous creations in the solution.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "line", "newTypeName" },
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
                description = "Absolute path to the source file containing the anonymous object creation"
            },
            line = new
            {
                type = "integer",
                description = "1-based line number of the anonymous object creation"
            },
            newTypeName = new
            {
                type = "string",
                description = "Name of the class or record to create"
            },
            column = new
            {
                type = "integer",
                description = "1-based column number for disambiguation when multiple anonymous creations share a line"
            },
            asRecord = new
            {
                type = "boolean",
                description = "Create a record instead of a class",
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

            var args = JsonSerializer.Deserialize<ConvertAnonymousToClassArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new ConvertAnonymousToClassOperation(context);
            var @params = new ConvertAnonymousToClassParams
            {
                SourceFile = args.SourceFile,
                Line = args.Line ?? 0,
                NewTypeName = args.NewTypeName,
                Column = args.Column,
                AsRecord = args.AsRecord ?? false,
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

    private sealed class ConvertAnonymousToClassArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public int? Line { get; init; }
        public string NewTypeName { get; init; } = "";
        public int? Column { get; init; }
        public bool? AsRecord { get; init; }
        public bool? Preview { get; init; }
    }
}
