# Roslyn MCP Server

[![Build and Test](https://github.com/JoshuaRamirez/RoslynMcpServer/actions/workflows/build.yml/badge.svg)](https://github.com/JoshuaRamirez/RoslynMcpServer/actions/workflows/build.yml)
[![Code Quality](https://github.com/JoshuaRamirez/RoslynMcpServer/actions/workflows/quality.yml/badge.svg)](https://github.com/JoshuaRamirez/RoslynMcpServer/actions/workflows/quality.yml)
[![NuGet](https://img.shields.io/nuget/v/RoslynMcp.Server.svg)](https://www.nuget.org/packages/RoslynMcp.Server)
[![NuGet Downloads](https://img.shields.io/nuget/dt/RoslynMcp.Server.svg)](https://www.nuget.org/packages/RoslynMcp.Server)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Let AI assistants like Claude safely refactor your C# codebase using the same Roslyn compiler platform that powers Visual Studio.

Roslyn MCP Server is a [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server that exposes 19 Roslyn-powered refactoring operations to AI assistants and other MCP clients. It gives your AI tools the ability to rename symbols, move types, extract methods, generate constructors, and much more -- with full solution-wide reference tracking and preview support on every operation.

---

## Table of Contents

- [Why RoslynMcpServer?](#why-roslynmcpserver)
- [Prerequisites](#prerequisites)
- [Quick Start](#quick-start)
- [Configuration](#configuration)
- [Available Tools](#available-tools)
- [Preview Mode](#preview-mode)
- [Troubleshooting](#troubleshooting)
- [NuGet Libraries](#nuget-libraries)
- [Contributing](#contributing)
- [License](#license)

---

## Why RoslynMcpServer?

- **19 refactoring operations** -- the most comprehensive Roslyn MCP server available
- **Preview mode on every operation** -- see exactly what will change before applying
- **Atomic file writes with rollback** -- if any file write fails, all changes are reverted
- **Solution-wide reference updates** -- renames and moves propagate across your entire solution
- **Single command install** -- `dotnet tool install -g RoslynMcp.Server`, no repo cloning needed
- **Cross-platform** -- works on Windows, Linux, and macOS

---

## Prerequisites

Before installing, make sure you have:

1. **.NET 9.0 SDK or later** -- [Download here](https://dotnet.microsoft.com/download/dotnet/9.0)
2. **A C# solution (.sln) or project (.csproj) to work with**

Verify your .NET SDK version:

```bash
dotnet --version
```

The output should be `9.0.x` or higher.

---

## Quick Start

### 1. Install

```bash
dotnet tool install -g RoslynMcp.Server
```

### 2. Configure

Create a `.mcp.json` file in your project root (for Claude Code):

```json
{
  "mcpServers": {
    "roslyn-refactor": {
      "type": "stdio",
      "command": "roslyn-mcp",
      "args": []
    }
  }
}
```

Then restart Claude Code or run `/mcp` to connect.

### 3. Verify

Ask Claude:

> "Run the roslyn diagnose tool for my solution at C:/path/to/MySolution.sln"

You should see a health report with Roslyn version, MSBuild status, and workspace details.

### 4. Try It

Ask Claude:

> "Rename the class UserService to AccountService in C:/path/to/MySolution.sln"

Claude will use the `rename_symbol` tool to rename the class and update every reference across your entire solution.

---

## Configuration

### Claude Code

Create `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "roslyn-refactor": {
      "type": "stdio",
      "command": "roslyn-mcp",
      "args": []
    }
  }
}
```

### Claude Desktop

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "roslyn-refactor": {
      "command": "roslyn-mcp",
      "args": []
    }
  }
}
```

Config file locations:

| OS      | Path                                                        |
|---------|-------------------------------------------------------------|
| Windows | `%APPDATA%\Claude\claude_desktop_config.json`               |
| macOS   | `~/Library/Application Support/Claude/claude_desktop_config.json` |

---

## Available Tools

All 19 tools accept a `solutionPath` parameter (absolute path to a `.sln` or `.csproj` file) and a `preview` parameter (set to `true` to see changes without applying them).

### Move and Rename

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `move_type_to_file` | Move a C# type declaration to a different file. Updates all references automatically. | `sourceFile`, `symbolName`, `targetFile`, `createTargetFile` |
| `move_type_to_namespace` | Change the namespace of a C# type. Updates all using directives and qualified references. | `sourceFile`, `symbolName`, `targetNamespace`, `updateFileLocation` |
| `rename_symbol` | Rename any C# symbol (type, method, property, field, variable, etc.) with automatic reference updates across the solution. | `sourceFile`, `symbolName`, `newName`, `line`, `column`, `renameOverloads`, `renameFile` |

### Extract

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `extract_method` | Extract selected code into a new method. Automatically detects parameters and return values. | `sourceFile`, `startLine`, `startColumn`, `endLine`, `endColumn`, `methodName`, `visibility` |
| `extract_variable` | Extract an expression to a local variable. | `sourceFile`, `startLine`, `startColumn`, `endLine`, `endColumn`, `variableName`, `useVar` |
| `extract_constant` | Extract a literal value to a named constant. | `sourceFile`, `startLine`, `startColumn`, `endLine`, `endColumn`, `constantName`, `visibility`, `replaceAll` |
| `extract_interface` | Extract an interface from a class's public members. | `sourceFile`, `typeName`, `interfaceName`, `members`, `targetFile` |
| `extract_base_class` | Extract members to a new base class. | `sourceFile`, `typeName`, `baseClassName`, `members`, `targetFile`, `makeAbstract` |

### Inline

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `inline_variable` | Inline a local variable by replacing all usages with its initializer value. | `sourceFile`, `variableName`, `line` |

### Signature and Encapsulation

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `change_signature` | Add, remove, or reorder method parameters and update all call sites. | `sourceFile`, `methodName`, `parameters` (array of changes), `line` |
| `encapsulate_field` | Convert a field to a property with backing field. | `sourceFile`, `fieldName`, `propertyName`, `readOnly` |

### Generate

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `generate_constructor` | Generate a constructor that initializes fields and/or properties of a type. | `sourceFile`, `typeName`, `members`, `addNullChecks` |
| `generate_overrides` | Generate override methods for base class virtual/abstract members. | `sourceFile`, `typeName`, `members`, `callBase` |
| `implement_interface` | Generate interface member implementations for a type. | `sourceFile`, `typeName`, `interfaceName`, `explicitImplementation`, `members` |

### Convert

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `convert_to_async` | Convert a synchronous method to async/await pattern. | `sourceFile`, `methodName`, `line`, `renameToAsync` |

### Using Directives

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `add_missing_usings` | Add missing using directives required to resolve unbound type references. Process a single file or all files in the solution. | `sourceFile`, `allFiles` |
| `remove_unused_usings` | Remove unused using directives. Process a single file or all files in the solution. | `sourceFile`, `allFiles` |
| `sort_usings` | Sort using directives alphabetically in a C# file. | `sourceFile` |

### Diagnostics

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `diagnose` | Check the health of the Roslyn MCP server environment and workspace status. | `solutionPath` (optional), `verbose` |

---

## Preview Mode

Every refactoring tool supports a `preview` parameter. When set to `true`, the tool computes and returns the changes that would be made without writing anything to disk. This lets you review diffs before committing to a refactoring.

Example (as a natural language prompt to Claude):

> "Preview what would happen if I renamed OrderProcessor to OrderHandler in C:/path/to/MySolution.sln"

Claude will call `rename_symbol` with `preview: true` and show you the affected files and diffs.

---

## Troubleshooting

### .NET 9 SDK not found

If you see errors about the SDK not being found:

1. Verify the SDK is installed: `dotnet --list-sdks`
2. Make sure .NET 9.0 or later appears in the list
3. If not, install it from [https://dotnet.microsoft.com/download/dotnet/9.0](https://dotnet.microsoft.com/download/dotnet/9.0)

### MSBuild or solution loading issues

If MSBuild cannot be located or your solution fails to load:

1. Make sure you can build the solution from the command line first: `dotnet build /path/to/MySolution.sln`
2. On Windows, ensure Visual Studio Build Tools or a Visual Studio installation is available
3. Check that `solutionPath` is an absolute path to a valid `.sln` or `.csproj` file

### Using the diagnose tool

The `diagnose` tool is the first thing to try when something is not working. It reports:

- Whether Roslyn is loaded and its version
- Whether MSBuild was found and its version
- Whether the .NET SDK is available and its version
- Whether a given solution can be loaded, including project and document counts

Run it through Claude:

> "Run the roslyn diagnose tool with verbose output for C:/path/to/MySolution.sln"

Or without a solution path to check just the environment:

> "Run the roslyn diagnose tool"

---

## NuGet Libraries

In addition to the global tool, the project publishes libraries for building custom integrations:

```bash
# Core library -- refactoring operations and workspace management
dotnet add package RoslynMcp.Core

# Contracts library -- shared models and interfaces
dotnet add package RoslynMcp.Contracts
```

---

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](./CONTRIBUTING.md) for guidelines.

### Build from Source

```bash
git clone https://github.com/JoshuaRamirez/RoslynMcpServer.git
cd RoslynMcpServer
dotnet build -c Release
```

### Run Tests

```bash
# All tests
dotnet test

# Specific test projects
dotnet test tests/RoslynMcp.Core.Tests
dotnet test tests/RoslynMcp.Server.Tests
```

---

## License

This project is licensed under the MIT License. See the [LICENSE](./LICENSE) file for details.

---

## Acknowledgments

- Built with [Roslyn](https://github.com/dotnet/roslyn) -- the .NET Compiler Platform
- Implements the [Model Context Protocol](https://modelcontextprotocol.io)

## Support

- **Issues**: [GitHub Issues](https://github.com/JoshuaRamirez/RoslynMcpServer/issues)
- **Discussions**: [GitHub Discussions](https://github.com/JoshuaRamirez/RoslynMcpServer/discussions)
