---
description: Install the Roslyn MCP Server .NET global tool
allowed-tools: Bash(dotnet:*), Bash(roslyn-mcp:*), Bash(which:*)
---

# Roslyn MCP Server Setup

Install and verify the Roslyn MCP Server .NET global tool.

## Step 1: Check Prerequisites

Verify .NET SDK is installed (9.0+ required):

```
dotnet --version
```

If not installed, direct the user to https://dotnet.microsoft.com/download/dotnet/9.0

## Step 2: Check Existing Installation

Check if roslyn-mcp is already installed:

```
dotnet tool list -g | grep -i roslyn
```

## Step 3: Install the Tool

If not installed:

```
dotnet tool install -g RoslynMcp.Server
```

If already installed and needs updating:

```
dotnet tool update -g RoslynMcp.Server
```

## Step 4: Verify Installation

Confirm the tool is available on PATH:

```
which roslyn-mcp
```

Then verify it runs:

```
roslyn-mcp --help
```

## Troubleshooting

If `roslyn-mcp` is not found after install:
1. Ensure `~/.dotnet/tools` is on your PATH
2. For zsh: `export PATH="$HOME/.dotnet/tools:$PATH"` in `~/.zshrc`
3. For bash: `export PATH="$HOME/.dotnet/tools:$PATH"` in `~/.bashrc`
4. Restart your terminal after PATH changes
