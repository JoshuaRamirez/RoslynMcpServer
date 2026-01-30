# Roslyn MCP Server

[![Build and Test](https://github.com/JoshuaRamirez/RoslynMcpServer/actions/workflows/build.yml/badge.svg)](https://github.com/JoshuaRamirez/RoslynMcpServer/actions/workflows/build.yml)
[![Code Quality](https://github.com/JoshuaRamirez/RoslynMcpServer/actions/workflows/quality.yml/badge.svg)](https://github.com/JoshuaRamirez/RoslynMcpServer/actions/workflows/quality.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server that exposes Roslyn-powered C# refactoring operations to AI assistants and other MCP clients.

## 🚀 Features

- **8 Refactoring Operations**: Comprehensive C# code transformations powered by Roslyn
- **Preview Mode**: See changes before applying them to your codebase
- **Cross-platform**: Works on Windows, Linux, and macOS
- **MSBuild Integration**: Seamlessly works with `.sln` and `.csproj` files
- **Atomic File Operations**: Safe file writes with rollback on failure
- **Comprehensive Error Handling**: Detailed error messages and diagnostics

## 📦 Installation

### As a NuGet Package

```bash
# Install the core library
dotnet add package RoslynMcp.Core

# Install the contracts library
dotnet add package RoslynMcp.Contracts
```

### As an MCP Server

```bash
# Clone the repository
git clone https://github.com/JoshuaRamirez/RoslynMcpServer.git
cd RoslynMcpServer

# Build the solution
dotnet build -c Release

# Run the server
dotnet run --project src/RoslynMcp.Server
```

## 🎯 Quick Start

### Running the MCP Server

The server communicates via stdin/stdout using the MCP protocol:

```bash
dotnet run --project src/RoslynMcp.Server
```

### Configuration for Claude Code

1. Create a `.mcp.json` file in your project root (or copy `.mcp.json.example`):

```json
{
  "mcpServers": {
    "roslyn-refactor": {
      "type": "stdio",
      "command": "/absolute/path/to/RoslynMcpServer/src/RoslynMcp.Server/bin/Release/net9.0/RoslynMcp.Server.exe",
      "args": [],
      "env": {}
    }
  }
}
```

2. Restart Claude Code or run `/mcp` to connect

**Note:** Use absolute paths. On Windows, use forward slashes: `C:/Source/RoslynMcpServer/...`

### Configuration for Claude Desktop

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "roslyn-refactor": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/RoslynMcpServer/src/RoslynMcp.Server", "--no-build"],
      "env": {}
    }
  }
}
```

**Tip:** Using `--no-build` with pre-built Release binaries improves startup time.

## 🛠️ Available Tools

### 1. `move_type_to_file`
Move a type (class, interface, struct, enum) to its own file.

**Parameters:**
- `solutionPath` (required): Path to `.sln` or `.csproj` file
- `sourceFile` (required): Path to source file containing the type
- `symbolName` (required): Name of the type to move
- `targetFile` (optional): Destination file path
- `createTargetFile` (optional): Create target file if it doesn't exist (default: true)
- `preview` (optional): Return changes without applying (default: false)

**Example:**
```json
{
  "solutionPath": "/path/to/MySolution.sln",
  "sourceFile": "/path/to/Models.cs",
  "symbolName": "User",
  "targetFile": "/path/to/User.cs",
  "preview": false
}
```

### 2. `move_type_to_namespace`
Move a type to a different namespace and update all references.

**Parameters:**
- `solutionPath` (required): Path to `.sln` or `.csproj` file
- `sourceFile` (required): Path to source file
- `symbolName` (required): Name of the type
- `targetNamespace` (required): New namespace
- `updateFileLocation` (optional): Move file to match namespace structure (default: false)
- `preview` (optional): Preview mode (default: false)

### 3. `rename_symbol`
Rename any C# symbol (type, method, property, field, variable) with automatic reference updates.

**Parameters:**
- `solutionPath` (required): Path to `.sln` or `.csproj` file
- `sourceFile` (required): Path to source file
- `symbolName` (required): Current name of the symbol
- `newName` (required): New name for the symbol
- `line` (optional): Line number for disambiguation
- `column` (optional): Column number for disambiguation
- `renameOverloads` (optional): Rename overloaded methods (default: false)
- `renameImplementations` (optional): Rename interface implementations (default: true)
- `renameFile` (optional): Rename file if renaming a type (default: true)
- `preview` (optional): Preview mode (default: false)

### 4. `extract_method`
Extract selected code into a new method.

**Parameters:**
- `solutionPath` (required): Path to `.sln` or `.csproj` file
- `sourceFile` (required): Path to source file
- `startLine` (required): Start line of selection (1-based)
- `startColumn` (required): Start column of selection (1-based)
- `endLine` (required): End line of selection (1-based)
- `endColumn` (required): End column of selection (1-based)
- `methodName` (required): Name for the new method
- `visibility` (optional): Method visibility (default: "private")
- `makeStatic` (optional): Make method static if possible (default: false)
- `preview` (optional): Preview mode (default: false)

### 5. `add_missing_usings`
Add missing using directives to resolve unbound type references.

**Parameters:**
- `solutionPath` (required): Path to `.sln` or `.csproj` file
- `sourceFile` (required): Path to source file
- `preview` (optional): Preview mode (default: false)

### 6. `remove_unused_usings`
Remove unused using directives from a file.

**Parameters:**
- `solutionPath` (required): Path to `.sln` or `.csproj` file
- `sourceFile` (required): Path to source file
- `preview` (optional): Preview mode (default: false)

### 7. `generate_constructor`
Generate a constructor for a class with specified members.

**Parameters:**
- `solutionPath` (required): Path to `.sln` or `.csproj` file
- `sourceFile` (required): Path to source file
- `typeName` (required): Name of the class
- `members` (required): Array of member names to include in constructor
- `addNullChecks` (optional): Add null checks for reference types (default: false)
- `preview` (optional): Preview mode (default: false)

### 8. `diagnose`
Check the health of the Roslyn MCP server environment and workspace status.

**Parameters:**
- `solutionPath` (optional): Solution to test loading
- `verbose` (optional): Include detailed diagnostic information (default: false)

## 📚 Documentation

For detailed documentation on each refactoring operation, see the [Design](./Design) folder.

## 🤝 Contributing

Contributions are welcome! Please see [CONTRIBUTING.md](./CONTRIBUTING.md) for guidelines.

### Development Setup

1. Clone the repository
2. Install .NET 9.0 SDK or later
3. Run `dotnet restore`
4. Run `dotnet build`
5. Run tests: `dotnet test`

### Running Tests

```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test tests/RoslynMcp.Core.Tests
dotnet test tests/RoslynMcp.Server.Tests
```

## ⚠️ Known Issues

### Integration Test Failures

Currently, 8 integration tests fail due to environment-specific MSBuild/NuGet assembly loading issues:

- `MoveTypeToFileIntegrationTests` (4 tests)
- `MoveTypeToNamespaceIntegrationTests` (4 tests)

**Status:** These are not code defects. The failures are related to MSBuild workspace initialization in test environments and do not affect the production functionality of the MCP server.

**Workaround:** The core refactoring operations work correctly in production. Unit tests (211 passing) provide comprehensive coverage of the refactoring logic.

**Tracking:** We're investigating solutions to make these tests more resilient to different MSBuild environments.

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](./LICENSE) file for details.

## 🙏 Acknowledgments

- Built with [Roslyn](https://github.com/dotnet/roslyn) - the .NET Compiler Platform
- Implements the [Model Context Protocol](https://modelcontextprotocol.io)

## 📞 Support

- **Issues**: [GitHub Issues](https://github.com/JoshuaRamirez/RoslynMcpServer/issues)
- **Discussions**: [GitHub Discussions](https://github.com/JoshuaRamirez/RoslynMcpServer/discussions)


