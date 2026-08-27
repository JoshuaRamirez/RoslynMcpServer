# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- `pull_members_up` — move selected members from a derived type onto an existing base class or interface, with preview mode and rejects for missing bases, interface-incompatible members, and name/signature conflicts
- `push_members_down` — move selected members from a base type down onto derived types, with optional named targets, preview mode, and rejects for missing derived types, conflicts, interface-incompatible members, and uneditable targets
- `use_base_type` — replace derived-type references with a compatible base type or interface, rewriting only usages whose members exist on that base, with preview mode and rejects for missing bases, uneditable documents, and no eligible references
- `introduce_field` — turn a selected local variable or expression into a class field, optionally initializing it in a constructor, with preview mode and rejects for missing expressions, uneditable documents, and invalid target types
- `safe_delete` — delete a selected symbol only when it has no remaining references, with preview mode and rejects that include usage locations, missing symbols, and uneditable documents
- `make_static` — add the static modifier to a selected instance method that does not use instance state, update call sites and method-group conversions to the containing type name, with preview mode and rejects for instance-member access, already-static methods, and uneditable documents
- `make_non_static` — remove the static modifier from a selected static method, rewrite type-name call sites and method-group conversions to an instance receiver (or `this` in the same type), with preview mode and rejects for already-instance methods, missing receivers, extern methods, and uneditable documents
- `convert_to_block_body` — convert a selected expression-bodied member (`=> expr`) to a block body (`{ return expr; }` or `{ expr; }` as appropriate), including properties and accessors, with preview mode and rejects for missing symbols, already-block bodies, unsupported members, and uneditable documents
- `generate_property` — generate an auto-property `{ get; set; }`, init-only property `{ get; init; }`, or backing-field property `{ get => field; set => field = value; }` on a selected type, with preview mode and rejects for missing symbols, uneditable documents, unsupported targets, and name clashes
- `generate_method_stub` — generate a method from an undefined call site, inferring target type, parameters, and return type from usage, with a `throw new NotImplementedException();` body, preview mode, and rejects for missing call sites, existing methods, uneditable or external targets, and uninferable return types
- `implement_abstract` — generate implementation stubs for unimplemented abstract methods and properties inherited by a selected class, with a `throw new NotImplementedException();` body, preview mode, and rejects for missing symbols, no unimplemented abstract members, uneditable documents, and unsupported targets
- `inline_constant` — replace references to a `const` field with a formatted literal (typed null casts), optionally remove the declaration, with preview mode and rejects for non-constants (including static readonly), public API constants, attribute usages, missing symbols, and uneditable documents
- `add_parameter` — add a named parameter to a method and update call sites, overrides, and interface implementations, with preview mode and rejects for duplicate names, invalid types, out-of-range positions, required-after-optional, params-not-last, missing methods, and uneditable documents
- `remove_parameter` — remove a named parameter from a method and drop matching call-site arguments, with override/interface updates, preview mode, force-gated body handling that must stay compiling, and rejects for a missing parameter, used-in-body without force, method-group references, missing methods, and uneditable documents
- `reorder_parameters` — reorder a method's parameters by a 0-based permutation and update positional call-site arguments, with override/interface updates, preview mode, and rejects for invalid permutations, params-not-last, optional-before-required, method-group references, missing methods, and uneditable documents

## [0.4.0] - 2026-02-23

### Added
- `.slnx` (Visual Studio 2022+ XML solution format) support in workspace loading and CLI
- Codex CLI compatibility via JSON-RPC 2.0 notification handling (`notifications/initialized`)
- `TestSolution.slnx` test fixture

### Changed
- Roslyn upgraded from 4.8.0 to 5.0.0 (`Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`)
- Workspace failure handler migrated to `RegisterWorkspaceFailedHandler` API (Roslyn 5.0)
- xunit upgraded from 2.6.0 to 2.9.3 across all test projects
- Microsoft.NET.Test.Sdk upgraded from 17.8.0 to 18.0.1 across all test projects
- coverlet.collector upgraded from 6.0.0 to 6.0.4 across all test projects
- GitHub Actions: checkout v4 to v6, setup-dotnet v4 to v5, upload-artifact v4 to v6
- 566 total tests (270 Core + 206 Server + 90 Cli)

### Fixed
- RS1039 analyzer warning from Roslyn 5.0 upgrade (suppressed in SymbolKindMapperTests)
- Import ordering in Cli.Tests to pass format check

## [0.3.1] - 2026-02-09

### Fixed
- Republish all packages — NuGet 0.3.0 was immutable from a prior incomplete release

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

[0.4.0]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.3.1...v0.4.0
[0.3.1]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/JoshuaRamirez/RoslynMcpServer/releases/tag/v0.1.0
