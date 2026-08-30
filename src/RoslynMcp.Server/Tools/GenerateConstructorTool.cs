using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for generate_constructor operation.
/// </summary>
public sealed class GenerateConstructorTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new generate constructor tool.
    /// </summary>
    public GenerateConstructorTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "generate_constructor";

    /// <inheritdoc />
    public string Description => "Generate a constructor that initializes fields and/or properties of a type. line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick. column (optional) picks the type whose identifier or declaration span covers that 1-based column when set with line (identifier preferred, then smallest containing type); omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter.";

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
                description = "Name of the type to add constructor to"
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
            members = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Names of fields/properties to initialize. If not specified, uses instance fields and (when includeProperties is true) settable properties. When includeInheritedMembers is true, listed names also resolve against accessible inherited members. Listed names still resolve against fields and settable properties even if includeProperties is false."
            },
            includeProperties = new
            {
                type = "boolean",
                description = "Include settable instance properties as constructor parameters. When false, only instance fields are used unless members names a property",
                @default = true
            },
            includeInheritedMembers = new
            {
                type = "boolean",
                description = "Also collect accessible instance fields (and settable properties when includeProperties is true) declared on base types",
                @default = false
            },
            addNullChecks = new
            {
                type = "boolean",
                description = "Add null checks for reference type parameters",
                @default = false
            },
            replaceExisting = new
            {
                type = "boolean",
                description = "Replace an existing constructor with the exact same signature instead of failing. Optional-parameter / required-parameter ambiguity still fails",
                @default = false
            },
            visibility = new
            {
                type = "string",
                description = "Accessibility of the generated constructor. Valid values: public, private, protected, internal, protected internal, private protected. Structs and record structs reject the three protected forms",
                @default = "public"
            },
            copyConstructor = new
            {
                type = "boolean",
                description = "Generate a single-parameter copy constructor of the target type that assigns each selected member from that parameter, instead of one parameter per member",
                @default = false
            },
            classBaseCopy = new
            {
                type = "boolean",
                description = "When copyConstructor is true, emit : base((Base)<copyParameter>) on an ordinary class whose immediate base has an accessible copy constructor of the base type. Requires copyConstructor. Records, structs, and record structs ignore this flag",
                @default = false
            },
            callBase = new
            {
                type = "boolean",
                description = "When copyConstructor is false, emit : base(...) on an ordinary class or record class by matching an accessible immediate-base constructor whose parameter types are a prefix of the generated constructor. Conflicts with copyConstructor. Record structs and structs ignore this flag",
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

            var args = JsonSerializer.Deserialize<GenerateConstructorArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            // Create workspace context
            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            // Execute operation
            var operation = new GenerateConstructorOperation(context);
            var @params = new GenerateConstructorParams
            {
                SourceFile = args.SourceFile,
                TypeName = args.TypeName,
                Line = args.Line,
                Column = args.Column,
                Members = args.Members,
                IncludeProperties = args.IncludeProperties ?? true,
                IncludeInheritedMembers = args.IncludeInheritedMembers ?? false,
                AddNullChecks = args.AddNullChecks ?? false,
                ReplaceExisting = args.ReplaceExisting ?? false,
                Visibility = args.Visibility,
                CopyConstructor = args.CopyConstructor ?? false,
                ClassBaseCopy = args.ClassBaseCopy ?? false,
                CallBase = args.CallBase ?? false,
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

    private sealed class GenerateConstructorArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string TypeName { get; init; } = "";
        public int? Line { get; init; }
        public int? Column { get; init; }
        public List<string>? Members { get; init; }
        public bool? IncludeProperties { get; init; }
        public bool? IncludeInheritedMembers { get; init; }
        public bool? AddNullChecks { get; init; }
        public bool? ReplaceExisting { get; init; }
        public string? Visibility { get; init; }
        public bool? CopyConstructor { get; init; }
        public bool? ClassBaseCopy { get; init; }
        public bool? CallBase { get; init; }
        public bool? Preview { get; init; }
    }
}
