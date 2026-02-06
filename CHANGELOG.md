# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [0.3.0] - 2026-02-06

### Added
- 5 new code navigation / query tools (24 total):
  - `find_references` -- find all references to a symbol across the solution
  - `go_to_definition` -- navigate to the source definition of a symbol
  - `get_symbol_info` -- retrieve detailed metadata for any symbol (type, accessibility, modifiers, members, docs)
  - `find_implementations` -- find all implementations of an interface or abstract member
  - `search_symbols` -- search for symbols by name pattern across the workspace
- `QueryOperationBase<TParams, TResult>` -- new base class for read-only query operations (parallel to `RefactoringOperationBase`)
- `SymbolResolver` -- general-purpose symbol resolver supporting position-based (line/column) and name-based resolution
- `SymbolKindMapper` -- maps Roslyn `ISymbol` kinds to contract enum values
- New contract models: `QueryResult<T>`, `ReferenceLocationInfo`, `DefinitionLocation`, `DetailedSymbolInfo`, `ImplementationInfo`, `SymbolSearchEntry`
- New error codes: `SYMBOL_NOT_FOUND`, `SYMBOL_AMBIGUOUS`, `NO_IMPLEMENTATIONS_FOUND`, `INVALID_SYMBOL_KIND`

## [0.2.1] - 2026-02-06

### Added
- `sort_usings` tool -- sort using directives alphabetically in a C# file (19th tool)
- `allFiles` parameter for `add_missing_usings` and `remove_unused_usings` -- process every C# file in the solution with a single call

### Changed
- Server now reports its actual assembly version instead of a hardcoded value
- README updated to document all 19 tools

## [0.2.0] - 2026-02-06

### Added
- 10 new refactoring operations (18 total): extract variable, extract constant, extract interface, extract base class, inline variable, change signature, encapsulate field, generate overrides, implement interface, convert to async
- File-based logging for troubleshooting (`%TEMP%/roslyn-mcp/` or `/tmp/roslyn-mcp/`)
- JSON-RPC error responses and structured logging
- Unit tests for StdioTransport
- Pinned .NET SDK version via `global.json`

### Fixed
- Blocking async calls that could cause deadlocks in MoveTypeToFile and MoveTypeToNamespace
- MSBuildWorkspaceProvider reliability with better error handling
- CI workflow branch triggers (now correctly target `master`)
- NuGet push wildcard handling on Windows in CI

### Removed
- Broken integration tests with MSBuild assembly conflicts

## [0.1.0] - 2026-01-30

### Added
- Initial public release
- 8 Roslyn-powered C# refactoring operations
- Cross-platform .NET global tool (`roslyn-mcp`)
- MCP protocol support for Claude Code and Claude Desktop

[0.3.0]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/JoshuaRamirez/RoslynMcpServer/releases/tag/v0.1.0
