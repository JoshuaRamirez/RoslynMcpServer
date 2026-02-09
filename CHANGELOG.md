# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [0.3.0] - 2026-02-09

### Added
- 22 new tools (41 total), organized across five categories:

  **Code Navigation (5 tools)**
  - `find_references` — find all references to a symbol across the solution
  - `go_to_definition` — navigate to the source definition of a symbol
  - `get_symbol_info` — retrieve detailed metadata for any symbol (type, accessibility, modifiers, members, docs)
  - `find_implementations` — find all implementations of an interface or abstract member
  - `search_symbols` — search for symbols by name pattern across the workspace

  **Analysis & Metrics (6 tools)**
  - `get_diagnostics` — retrieve compiler diagnostics filtered by severity and file
  - `get_code_metrics` — calculate cyclomatic complexity, lines of code, maintainability index, class coupling, and depth of inheritance
  - `analyze_control_flow` — analyze control flow for a code region (reachability, return/exit points)
  - `analyze_data_flow` — analyze data flow for a code region (reads, writes, captured variables)
  - `get_document_outline` — get a hierarchical outline of all symbols in a file
  - `get_type_hierarchy` — retrieve base types and derived classes for a type

  **Code Generation & Formatting (4 tools)**
  - `generate_equals_hashcode` — generate Equals() and GetHashCode() overrides for a type
  - `generate_tostring` — generate ToString() override for a type
  - `format_document` — format a C# file using Roslyn's built-in formatter
  - `add_null_checks` — add null-check statements for method parameters

  **Code Conversions (7 tools)**
  - `convert_expression_body` — toggle between expression body and block body for methods/properties
  - `convert_property` — convert between auto-property and full property with backing field
  - `introduce_parameter` — promote a local variable to a method parameter, updating call sites
  - `convert_foreach_linq` — convert foreach loops with Add patterns to LINQ expressions
  - `convert_to_pattern_matching` — convert if/is chains and switch statements to switch expressions
  - `convert_to_interpolated_string` — convert string.Format() and concatenation to interpolated strings
  - `find_callers` — find all callers of a symbol across the solution

- `roslyn-cli` standalone CLI tool (`RoslynMcp.Cli` package)
  - All 41 Roslyn tools accessible from the command line without an AI assistant
  - JSON output by default (pipeable to `jq`), with `--format text` for human-readable output
  - Per-tool help via `roslyn-cli <tool-name> --help`
  - Exit codes: 0=success, 1=tool error, 2=CLI error, 3=environment error

- `QueryOperationBase<TParams, TResult>` — new base class for read-only query operations
- `SymbolResolver` — general-purpose symbol resolver (position-based and name-based)
- New contract models, error codes, and enums for all 22 new tools
- New shared utilities: `MetricsCalculator`, `EqualityMemberCollector`, `NullCheckGenerator`

### Fixed
- Null-ref in `GetCodeMetrics` when metrics are unavailable
- Null-unsafe `GetHashCode` generation for types with >8 members
- Redundant allocation in `GetDiagnostics`
- `PascalToKebab` producing `x-m-l-path` instead of `xml-path` for acronyms
- `IsRequired` not detecting required value-type properties
- `IsHelpFlag` case sensitivity inconsistent with other CLI flag parsing
- `IsEnvironmentError` using fragile message-based detection instead of exception types

### Changed
- 557 total tests (269 Core + 206 Server + 82 CLI)

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
