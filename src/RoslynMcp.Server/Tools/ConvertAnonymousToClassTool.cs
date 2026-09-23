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
        "Convert an anonymous type (new { ... }) to a named class or record and replace same-shape anonymous creations in the solution. sourceFile, line, and newTypeName are required when allFiles is omitted or false. column (optional) picks the anonymous creation whose span covers that column when set with line (exclusive-end; unique covering match, else CannotConvert / SymbolAmbiguous); omitted keeps today's line pick. allFiles: true walks every C# file and converts every distinct eligible anonymous-type shape (type named from sanitized member names; sourceFile optional when true; cannot be combined with line, column, or newTypeName). asRecord / preview remain valid with allFiles.";

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
                description = "Absolute path to the source file containing the anonymous object creation. Required when allFiles is false. When allFiles is true, optional and limits the walk to that one file."
            },
            allFiles = new
            {
                type = "boolean",
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with line, column, or newTypeName.",
                @default = false
            },
            line = new
            {
                type = "integer",
                description = "1-based line number of the anonymous object creation. Required when allFiles is false. When column is omitted, matching stays today's line pick (single covering candidate returns; several on the line stay SymbolAmbiguous). Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            newTypeName = new
            {
                type = "string",
                description = "Name of the class or record to create. Required when allFiles is false. When allFiles is true, each type is named from sanitized member names. Single-site only; cannot be combined with allFiles."
            },
            column = new
            {
                type = "integer",
                description = "1-based column on the anonymous object creation. When set with line, selects the creation whose span covers that column (exclusive-end; today's unique covering match, else CannotConvert / SymbolAmbiguous). Omitted keeps today's line pick. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            asRecord = new
            {
                type = "boolean",
                description = "Create a record instead of a class. Valid with allFiles.",
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
                required = new[] { "solutionPath", "sourceFile", "line", "newTypeName" }
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
                        new { required = new[] { "line" } },
                        new { required = new[] { "column" } },
                        new { required = new[] { "newTypeName" } }
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
                AllFiles = args.AllFiles ?? false,
                Line = args.Line,
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
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public int? Line { get; init; }
        public string? NewTypeName { get; init; }
        public int? Column { get; init; }
        public bool? AsRecord { get; init; }
        public bool? Preview { get; init; }
    }
}
