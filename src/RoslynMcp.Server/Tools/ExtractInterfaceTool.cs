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
    public string Description =>
        "Extract an interface from a class's public members, including indexers as this[...] declarations. allFiles: true walks every C# file and extracts I{TypeName} for every eligible non-static type with extractable public members into a sibling I{TypeName}.cs (sourceFile optional when true; cannot be combined with typeName, line, column, interfaceName, members, or targetFile). line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick. column (optional) picks the type whose identifier or declaration span covers that 1-based column when set with line (identifier preferred, then smallest containing type); omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter. When separateFile is true and targetFile is omitted (single-site), the interface is written to {InterfaceName}.cs next to the source file. addInterfaceToType / preview remain valid with allFiles.";

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
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with typeName, line, column, interfaceName, members, or targetFile. Each eligible type gets I{TypeName} written to a sibling I{TypeName}.cs.",
                @default = false
            },
            typeName = new
            {
                type = "string",
                description = "Name of the type to extract interface from. Required when allFiles is false. Single-site only; cannot be combined with allFiles."
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
                description = "Name for the new interface. Required when allFiles is false. Single-site only; cannot be combined with allFiles. When allFiles is true, each interface is named I{TypeName}."
            },
            members = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Names of members to include. If not specified, includes all public instance members. Indexers match Item, this[], and this[int i]. Single-site only; cannot be combined with allFiles."
            },
            targetFile = new
            {
                type = "string",
                description = "Absolute path for the interface file. If set, wins over separateFile. If neither is set, creates in the same file. Single-site only; cannot be combined with allFiles."
            },
            separateFile = new
            {
                type = "boolean",
                description = "When true and targetFile is omitted (single-site), write the interface to {InterfaceName}.cs next to the source file. allFiles always writes sibling I{TypeName}.cs files.",
                @default = false
            },
            addInterfaceToType = new
            {
                type = "boolean",
                description = "Add the interface to the type's base list. Valid with allFiles.",
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
                required = new[] { "solutionPath", "sourceFile", "typeName", "interfaceName" }
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
                        new { required = new[] { "interfaceName" } },
                        new { required = new[] { "members" } },
                        new { required = new[] { "targetFile" } }
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
                AllFiles = args.AllFiles ?? false,
                TypeName = args.TypeName,
                Line = args.Line,
                Column = args.Column,
                InterfaceName = args.InterfaceName,
                Members = args.Members,
                TargetFile = args.TargetFile,
                SeparateFile = args.SeparateFile ?? false,
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
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public string? TypeName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public string? InterfaceName { get; init; }
        public List<string>? Members { get; init; }
        public string? TargetFile { get; init; }
        public bool? SeparateFile { get; init; }
        public bool? AddInterfaceToType { get; init; }
        public bool? Preview { get; init; }
    }
}
