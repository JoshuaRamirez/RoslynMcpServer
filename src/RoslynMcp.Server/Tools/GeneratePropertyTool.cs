using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for generate_property operation.
/// </summary>
public sealed class GeneratePropertyTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new generate property tool.
    /// </summary>
    public GeneratePropertyTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "generate_property";

    /// <inheritdoc />
    public string Description =>
        "Generate a property on a C# type. Creates an auto-property { get; set; }, an init-only property { get; init; }, or a backing-field property { get => field; set => field = value; } when a field is the target. line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick. column (optional) picks the type whose identifier or declaration span covers that 1-based column when set with line (identifier preferred, then smallest containing type); omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter. replaceExisting (default false) replaces an existing property of the same name instead of failing.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "typeName" },
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
                description = "Name of the type to add the property to"
            },
            line = new
            {
                type = "integer",
                description = "1-based line number for disambiguation when several types share the name. When set, selects the type whose identifier or declaration span covers that line (identifier preferred, then smallest containing type). Omitted keeps today's typeName FirstOrDefault pick.",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column for disambiguation. When set with line, selects the type whose identifier or declaration span covers that column (identifier preferred, then smallest containing type). Omitted keeps today's typeName + optional line pick. Column without line keeps today's first-match after the typeName filter.",
                minimum = 1
            },
            propertyName = new
            {
                type = "string",
                description = "Name of the property to generate. Derived from fieldName when omitted."
            },
            propertyType = new
            {
                type = "string",
                description = "C# type of the property. Required for auto-properties; inferred from the field when fieldName is set."
            },
            fieldName = new
            {
                type = "string",
                description = "Optional field to wrap with a backing-field property"
            },
            visibility = new
            {
                type = "string",
                description = "Accessibility of the generated property",
                @default = "public"
            },
            initOnly = new
            {
                type = "boolean",
                description = "Generate an init-only setter ({ get; init; })",
                @default = false
            },
            replaceExisting = new
            {
                type = "boolean",
                description = "Replace an existing property of the same name instead of failing. Fields and methods of the same name are left alone. Default false.",
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

            var args = JsonSerializer.Deserialize<GeneratePropertyArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new GeneratePropertyOperation(context);
            var @params = new GeneratePropertyParams
            {
                SourceFile = args.SourceFile,
                TypeName = args.TypeName,
                Line = args.Line,
                Column = args.Column,
                PropertyName = args.PropertyName,
                PropertyType = args.PropertyType,
                FieldName = args.FieldName,
                Visibility = args.Visibility,
                InitOnly = args.InitOnly ?? false,
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

    private sealed class GeneratePropertyArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string TypeName { get; init; } = "";
        public int? Line { get; init; }
        public int? Column { get; init; }
        public string? PropertyName { get; init; }
        public string? PropertyType { get; init; }
        public string? FieldName { get; init; }
        public string? Visibility { get; init; }
        public bool? InitOnly { get; init; }
        public bool? ReplaceExisting { get; init; }
        public bool? Preview { get; init; }
    }
}
