using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for generate_method_stub operation.
/// </summary>
public sealed class GenerateMethodStubTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new generate method stub tool.
    /// </summary>
    public GenerateMethodStubTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "generate_method_stub";

    /// <inheritdoc />
    public string Description =>
        "Generate a method from an undefined call site, inferring the signature from usage. Placeholder body is throw new NotImplementedException() when throwNotImplemented is true (the default); otherwise default-return / empty void bodies. replaceExisting (default false) replaces a compatible ordinary method instead of failing. allFiles: true walks every C# file and generates stubs for distinct eligible undefined call sites (sourceFile optional when true; cannot be combined with line, column, or methodName).";

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
                description = "Absolute path to the file containing the call site. Required when allFiles is false. When allFiles is true, optional and limits the walk to that one file."
            },
            allFiles = new
            {
                type = "boolean",
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with line, column, or methodName.",
                @default = false
            },
            line = new
            {
                type = "integer",
                description = "1-based line number of the call site. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column number within the method name. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            methodName = new
            {
                type = "string",
                description = "Method name override when not inferable from the location. Single-site only; cannot be combined with allFiles."
            },
            returnType = new
            {
                type = "string",
                description = "Explicit return type override. Valid with allFiles."
            },
            visibility = new
            {
                type = "string",
                description = "Access modifier for the generated method",
                @default = "private"
            },
            generateAsync = new
            {
                type = "boolean",
                description = "Force async method generation. Valid with allFiles.",
                @default = false
            },
            throwNotImplemented = new
            {
                type = "boolean",
                description = "Throw NotImplementedException in the generated stub body. Valid with allFiles.",
                @default = true
            },
            replaceExisting = new
            {
                type = "boolean",
                description = "Replace a compatible existing ordinary method instead of failing. Constructors, operators, local functions, explicit interface implementations, and accessors are left alone. Default false. Valid with allFiles.",
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

            var args = JsonSerializer.Deserialize<GenerateMethodStubArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new GenerateMethodStubOperation(context);
            var @params = new GenerateMethodStubParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                Line = args.Line,
                Column = args.Column,
                MethodName = args.MethodName,
                ReturnType = args.ReturnType,
                Visibility = args.Visibility,
                GenerateAsync = args.GenerateAsync ?? false,
                ThrowNotImplemented = args.ThrowNotImplemented ?? true,
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

    private sealed class GenerateMethodStubArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public string? MethodName { get; init; }
        public string? ReturnType { get; init; }
        public string? Visibility { get; init; }
        public bool? GenerateAsync { get; init; }
        public bool? ThrowNotImplemented { get; init; }
        public bool? ReplaceExisting { get; init; }
        public bool? Preview { get; init; }
    }
}
