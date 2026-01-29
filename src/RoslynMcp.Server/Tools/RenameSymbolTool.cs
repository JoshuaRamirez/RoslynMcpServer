using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for rename_symbol operation.
/// </summary>
public sealed class RenameSymbolTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new rename symbol tool.
    /// </summary>
    public RenameSymbolTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "rename_symbol";

    /// <inheritdoc />
    public string Description => "Rename any C# symbol (type, method, property, field, variable, etc.) with automatic reference updates across the solution.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "symbolName", "newName" },
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
                description = "Absolute path to the source file containing the symbol"
            },
            symbolName = new
            {
                type = "string",
                description = "Current name of the symbol to rename"
            },
            newName = new
            {
                type = "string",
                description = "New name for the symbol"
            },
            line = new
            {
                type = "integer",
                description = "1-based line number for disambiguation if multiple symbols match",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column number for disambiguation",
                minimum = 1
            },
            renameOverloads = new
            {
                type = "boolean",
                description = "Rename all overloads of a method",
                @default = false
            },
            renameImplementations = new
            {
                type = "boolean",
                description = "Rename interface implementations",
                @default = true
            },
            renameFile = new
            {
                type = "boolean",
                description = "Rename the file if renaming a type that matches the filename",
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

            var args = JsonSerializer.Deserialize<RenameSymbolArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            // Create workspace context
            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            // Execute operation
            var operation = new RenameSymbolOperation(context);
            var @params = new RenameSymbolParams
            {
                SourceFile = args.SourceFile,
                SymbolName = args.SymbolName,
                NewName = args.NewName,
                Line = args.Line,
                Column = args.Column,
                RenameOverloads = args.RenameOverloads ?? false,
                RenameImplementations = args.RenameImplementations ?? true,
                RenameFile = args.RenameFile ?? true,
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

    private sealed class RenameSymbolArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string SymbolName { get; init; } = "";
        public string NewName { get; init; } = "";
        public int? Line { get; init; }
        public int? Column { get; init; }
        public bool? RenameOverloads { get; init; }
        public bool? RenameImplementations { get; init; }
        public bool? RenameFile { get; init; }
        public bool? Preview { get; init; }
    }
}
