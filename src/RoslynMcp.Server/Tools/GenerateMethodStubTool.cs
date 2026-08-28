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
        "Generate a method from an undefined call site, inferring the signature from usage. Placeholder body is throw new NotImplementedException() when throwNotImplemented is true (the default); otherwise default-return / empty void bodies.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "line", "column" },
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
                description = "Absolute path to the file containing the call site"
            },
            line = new
            {
                type = "integer",
                description = "1-based line number of the call site",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column number within the method name",
                minimum = 1
            },
            methodName = new
            {
                type = "string",
                description = "Method name override when not inferable from the location"
            },
            returnType = new
            {
                type = "string",
                description = "Explicit return type override"
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
                description = "Force async method generation",
                @default = false
            },
            throwNotImplemented = new
            {
                type = "boolean",
                description = "Throw NotImplementedException in the generated stub body",
                @default = true
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
                Line = args.Line,
                Column = args.Column,
                MethodName = args.MethodName,
                ReturnType = args.ReturnType,
                Visibility = args.Visibility,
                GenerateAsync = args.GenerateAsync ?? false,
                ThrowNotImplemented = args.ThrowNotImplemented ?? true,
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
        public string SourceFile { get; init; } = "";
        public int Line { get; init; }
        public int Column { get; init; }
        public string? MethodName { get; init; }
        public string? ReturnType { get; init; }
        public string? Visibility { get; init; }
        public bool? GenerateAsync { get; init; }
        public bool? ThrowNotImplemented { get; init; }
        public bool? Preview { get; init; }
    }
}
