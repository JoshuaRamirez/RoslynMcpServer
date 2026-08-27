using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for generate_equals_hashcode operation.
/// </summary>
public sealed class GenerateEqualsHashCodeTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new generate equals/hashcode tool.
    /// </summary>
    public GenerateEqualsHashCodeTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "generate_equals_hashcode";

    /// <inheritdoc />
    public string Description => "Generate Equals() and GetHashCode() overrides for a C# type based on its fields and properties.";

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
                description = "Name of the type to generate Equals/GetHashCode for"
            },
            fields = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Specific field/property names to include. If not specified, uses instance fields and (when includeProperties is true) readable properties."
            },
            includeProperties = new
            {
                type = "boolean",
                description = "Include readable instance properties as equality members. When false, only instance fields are used unless fields names a property",
                @default = true
            },
            implementIEquatable = new
            {
                type = "boolean",
                description = "Also implement IEquatable<T> with a typed Equals(T) and have Equals(object) delegate to it",
                @default = false
            },
            generateOperators = new
            {
                type = "boolean",
                description = "Also generate operator == and operator != that agree with the generated Equals",
                @default = false
            },
            replaceExisting = new
            {
                type = "boolean",
                description = "Replace existing Equals/GetHashCode (and IEquatable / operators when those flags are set) instead of failing",
                @default = false
            },
            useHashCodeCombine = new
            {
                type = "boolean",
                description = "Use HashCode.Combine() (or a HashCode builder for more than 8 members) instead of a classic unchecked prime-multiply GetHashCode",
                @default = true
            },
            callSuper = new
            {
                type = "boolean",
                description = "Fold the immediate base type's Equals/GetHashCode into the generated methods. Rejected when the immediate base is System.Object or System.ValueType, or when the base Equals(object) or GetHashCode is abstract",
                @default = false
            },
            includeInheritedMembers = new
            {
                type = "boolean",
                description = "Also collect accessible instance fields (and readable properties when includeProperties is true) declared on base types. Distinct from callSuper, which only folds base.Equals / base.GetHashCode",
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

            var args = JsonSerializer.Deserialize<GenerateEqualsHashCodeArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            // Create workspace context
            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            // Execute operation
            var operation = new GenerateEqualsHashCodeOperation(context);
            var @params = new GenerateEqualsHashCodeParams
            {
                SourceFile = args.SourceFile,
                TypeName = args.TypeName,
                Fields = args.Fields,
                IncludeProperties = args.IncludeProperties ?? true,
                ImplementIEquatable = args.ImplementIEquatable ?? false,
                GenerateOperators = args.GenerateOperators ?? false,
                ReplaceExisting = args.ReplaceExisting ?? false,
                UseHashCodeCombine = args.UseHashCodeCombine ?? true,
                CallSuper = args.CallSuper ?? false,
                IncludeInheritedMembers = args.IncludeInheritedMembers ?? false,
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

    private sealed class GenerateEqualsHashCodeArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string TypeName { get; init; } = "";
        public List<string>? Fields { get; init; }
        public bool? IncludeProperties { get; init; }
        public bool? ImplementIEquatable { get; init; }
        public bool? GenerateOperators { get; init; }
        public bool? ReplaceExisting { get; init; }
        public bool? UseHashCodeCombine { get; init; }
        public bool? CallSuper { get; init; }
        public bool? IncludeInheritedMembers { get; init; }
        public bool? Preview { get; init; }
    }
}
