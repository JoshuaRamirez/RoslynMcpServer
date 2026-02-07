# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [0.4.0] - 2026-02-07

### Added
- 17 new tools (41 total), organized across four categories:

  **Analysis & Metrics (3 tools)**
  - `get_diagnostics` — retrieve compiler diagnostics filtered by severity and file
  - `get_code_metrics` — calculate cyclomatic complexity, lines of code, maintainability index, class coupling, and depth of inheritance
  - `analyze_control_flow` — analyze control flow for a code region (reachability, return/exit points)

  **Navigation & Hierarchy (3 tools)**
  - `find_callers` — find all callers of a symbol across the solution
  - `get_type_hierarchy` — retrieve base types and derived classes for a type
  - `get_document_outline` — get a hierarchical outline of all symbols in a file

  **Code Generation & Formatting (4 tools)**
  - `generate_equals_hashcode` — generate Equals() and GetHashCode() overrides for a type
  - `generate_tostring` — generate ToString() override for a type
  - `format_document` — format a C# file using Roslyn's built-in formatter
  - `add_null_checks` — add null-check statements for method parameters

  **Data Flow & Conversions (7 tools)**
  - `analyze_data_flow` — analyze data flow for a code region (reads, writes, captured variables)
  - `convert_expression_body` — toggle between expression body and block body for methods/properties
  - `convert_property` — convert between auto-property and full property with backing field
  - `introduce_parameter` — promote a local variable to a method parameter, updating call sites
  - `convert_foreach_linq` — convert foreach loops with Add patterns to LINQ expressions
  - `convert_to_pattern_matching` — convert if/is chains and switch statements to switch expressions
  - `convert_to_interpolated_string` — convert string.Format() and concatenation to interpolated strings

- New query base class extensions for analysis operations
- New contract models for all 17 tools (params DTOs, result DTOs)
- New error codes: `InvalidRegion`, `NoMembersToGenerate`, `AlreadyHasOverride`, `CannotConvert`
- New enums: `DiagnosticSeverityFilter`, `ConversionDirection`, `HierarchyDirection`
- New shared utilities: `MetricsCalculator`, `HierarchyBuilder`, `EqualityMemberCollector`, `NullCheckGenerator`
- 475 total tests (269 Core + 206 Server)

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

[0.4.0]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/JoshuaRamirez/RoslynMcpServer/releases/tag/v0.1.0
