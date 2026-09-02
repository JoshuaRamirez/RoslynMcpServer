using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Encapsulate;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for encapsulate_field operation.
/// </summary>
public sealed class EncapsulateFieldTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new encapsulate field tool.
    /// </summary>
    public EncapsulateFieldTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "encapsulate_field";

    /// <inheritdoc />
    public string Description =>
        "Convert a field to a property with backing field. line (optional) picks the field whose identifier or declaration span covers that line when several fields share the name; omitted keeps today's fieldName FirstOrDefault pick. column (optional) picks the field whose identifier or declaration span covers that 1-based column when set with line (identifier preferred, then smallest covering declarator/field); omitted keeps today's fieldName + optional line pick; column without line keeps today's first-match after the fieldName filter. updateReferences (default true) rewrites external references to the new property; false still encapsulates (private field + property) but leaves external callers on the field. Same-class references stay on the field. Preview describes whether references will be updated and writes nothing. allFiles: true walks every C# file and encapsulates every eligible field (sourceFile optional when true; cannot be combined with fieldName, line, column, or propertyName).";

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
                description = "Absolute path to the source file containing the field. Required when allFiles is false."
            },
            allFiles = new
            {
                type = "boolean",
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with fieldName, line, column, or propertyName.",
                @default = false
            },
            fieldName = new
            {
                type = "string",
                description = "Name of the field to encapsulate. Single-site only; cannot be combined with allFiles."
            },
            line = new
            {
                type = "integer",
                description = "1-based line number for disambiguation when several fields share the name. When set, selects the field whose identifier or declaration span covers that line (identifier preferred, then smallest covering declarator/field). Omitted keeps today's fieldName FirstOrDefault pick. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column for disambiguation. When set with line, selects the field whose identifier or declaration span covers that column (identifier preferred, then smallest covering declarator/field). Omitted keeps today's fieldName + optional line pick. Column without line keeps today's first-match after the fieldName filter. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            propertyName = new
            {
                type = "string",
                description = "Name for the property. If not specified, derives from field name (e.g., _name -> Name). Single-site only; cannot be combined with allFiles."
            },
            readOnly = new
            {
                type = "boolean",
                description = "Create read-only property (getter only)",
                @default = false
            },
            updateReferences = new
            {
                type = "boolean",
                description = "Update external references to use the new property. Default true keeps today's rewrite. False still encapsulates (private field + property) but leaves external callers on the field.",
                @default = true
            },
            preview = new
            {
                type = "boolean",
                description = "Return computed changes without applying. Describes whether references will be updated and writes nothing.",
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

            var args = JsonSerializer.Deserialize<EncapsulateFieldArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new EncapsulateFieldOperation(context);
            var @params = new EncapsulateFieldParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                FieldName = args.FieldName,
                Line = args.Line,
                Column = args.Column,
                PropertyName = args.PropertyName,
                ReadOnly = args.ReadOnly ?? false,
                UpdateReferences = args.UpdateReferences ?? true,
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

    private sealed class EncapsulateFieldArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public string? FieldName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public string? PropertyName { get; init; }
        public bool? ReadOnly { get; init; }
        public bool? UpdateReferences { get; init; }
        public bool? Preview { get; init; }
    }
}
