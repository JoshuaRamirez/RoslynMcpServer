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
    public string Description =>
        "Rename any C# symbol (type, method, property, field, variable, etc.) with automatic reference updates across the solution. sourceFile, symbolName, and newName are required when allFiles is omitted or false. allFiles: true walks every C# file and renames every eligible declaration whose simple name equals symbolName to newName under today's single-site validation (sourceFile optional when true; cannot be combined with line or column; symbolName / newName remain required). renameOverloads / renameImplementations / renameFile / preview remain valid with allFiles.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "symbolName", "newName" },
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
                description = "Absolute path to the source file containing the symbol. Required when allFiles is false. When allFiles is true, optional and limits the walk to that one file."
            },
            allFiles = new
            {
                type = "boolean",
                description = "When true, walk every C# document (or the optional single sourceFile) and rename every eligible declaration whose simple name equals symbolName to newName. Cannot be combined with line or column. Default false.",
                @default = false
            },
            symbolName = new
            {
                type = "string",
                description = "Current name of the symbol to rename. Required for both single-site and allFiles."
            },
            newName = new
            {
                type = "string",
                description = "New name for the symbol. Required for both single-site and allFiles."
            },
            line = new
            {
                type = "integer",
                description = "1-based line number for disambiguation if multiple symbols match. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column number for disambiguation. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            renameOverloads = new
            {
                type = "boolean",
                description = "Rename all overloads of a method. Valid with allFiles.",
                @default = false
            },
            renameImplementations = new
            {
                type = "boolean",
                description = "Rename interface implementations. Valid with allFiles.",
                @default = true
            },
            renameFile = new
            {
                type = "boolean",
                description = "Rename the file if renaming a type that matches the filename. Valid with allFiles; colliding destinations are skipped.",
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
                required = new[] { "solutionPath", "sourceFile", "symbolName", "newName" }
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
                required = new[] { "solutionPath", "allFiles", "symbolName", "newName" },
                not = new
                {
                    anyOf = new object[]
                    {
                        new { required = new[] { "line" } },
                        new { required = new[] { "column" } }
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

            var args = JsonSerializer.Deserialize<RenameSymbolArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new RenameSymbolOperation(context);
            var @params = new RenameSymbolParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
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
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
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
