# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed
- `generate_tostring` now honors `includeProperties`: omitted / true keeps today's field+property collection (instance fields plus readable non-indexer properties; still skip static/const/implicit/indexers); `false` with no `fields` list collects instance fields only. A non-empty `fields` list is authoritative and still resolves listed names against fields and properties (an explicit property name is still included in ToString). Auto-property backing fields stay excluded (`IsImplicitlyDeclared`). Members named `ToString` are still dropped after collection so the generated override cannot hide `this.ToString`. Additive with `fields`, `format`, `includeInheritedMembers`, and `replaceExisting`. Preview describes the chosen members / format and writes nothing. `generate_equals_hashcode` is unchanged (already honors the flag). Collection always uses the four-parameter `EqualityMemberCollector.CollectMembers` overload so the flag is honored on both this-type-only and inherited paths (the two-parameter always-true forward is no longer used).
- `generate_tostring` now honors `replaceExisting`: omitted / false keeps today's fail-on-existing behavior (`AlreadyHasOverride` 3056, "Type already has a ToString override.") when a non-implicit non-generic parameterless `ToString` already exists (instance or static); `true` removes those parameterless `ToString()` members on the target type across every partial that holds one (instance and static, so a leftover `static ToString()` cannot collide with the generated override / CS0111), then generates a fresh instance override for the requested `fields` / `format` / `includeInheritedMembers`. Generic `ToString<T>()` (arity ≠ 0) and parameterized overloads (`ToString(string)`, `ToString(IFormatProvider)`, etc.) are left alone and do not trip 3056. Implicit `Object` / `ValueType` `ToString` does not count as existing (already today's rule). Additive with `fields`, `format`, and `includeInheritedMembers`. Preview describes the replacement / generated members and writes nothing. No new error codes for the happy replace path.
- `generate_tostring` now honors `includeInheritedMembers`: omitted / false keeps today's this-type-only collection (two-parameter `EqualityMemberCollector.CollectMembers`); `true` also collects accessible inherited instance fields and readable properties via the four-parameter overload (`includeProperties: true`, `includeInheritedMembers: true`). Accessibility / Object-ValueType stop / static-const-implicit-indexer filters / hide-override skip / order come from the existing collector. Hide now treats any closer non-implicit member with the same name as a hider (methods and nested types included, not only fields/properties/events) so generated `this.Name` cannot bind to a method group (CS0119). Members named `ToString` are dropped so the generated override cannot hide `this.ToString`. A non-empty `fields` list stays authoritative and, when the flag is true, resolves listed names against this type and accessible inherited members. Additive with `fields` and `format`. Preview describes the chosen members / format and writes nothing. `generate_equals_hashcode` is unchanged (already honors the flag). The existing two- and three-parameter `CollectMembers` overloads stay this-type-only.
- `generate_equals_hashcode` now honors `includeInheritedMembers`: omitted / false keeps today's this-type-only collection (`typeSymbol.GetMembers()`); `true` also walks `BaseType` until `System.Object` / `System.ValueType` and appends accessible instance fields (and, when `includeProperties` is true, readable properties) declared on those bases — public / protected / protected-internal, plus internal when the same assembly; private and otherwise-inaccessible members are skipped. This-type members keep today's relative order (immediate base first, then further bases). A non-empty `fields` list stays authoritative and, when the flag is true, resolves listed names against this type and accessible inherited members. Distinct from `callSuper` (member collection vs base-method fold-in); combining both is allowed. Additive with `fields`, `includeProperties`, `implementIEquatable`, `generateOperators`, `replaceExisting`, `useHashCodeCombine`, and `callSuper`. Preview describes inherited inclusion and writes nothing. `generate_tostring` is unchanged (still this-type collection). The existing two- and three-parameter `EqualityMemberCollector.CollectMembers` overloads are preserved and forward `includeInheritedMembers: false` so NuGet callers compiled against the previous package do not hit `MissingMethodException`.
- `generate_equals_hashcode` now honors `callSuper`: omitted / false keeps today's member-only Equals/GetHashCode (no `base.Equals` / `base.GetHashCode`); `true` folds the immediate base type's equality into both methods — `base.Equals(other)` before member comparisons (object Equals after `obj is T other`; typed Equals when `implementIEquatable` binds the best applicable base Equals), and `base.GetHashCode()` as the first `HashCode.Combine` / builder `Add` argument or as the prime-multiply seed instead of `17`. Rejected when the immediate base is `System.Object` or `System.ValueType` (`CallSuperOnObjectBase` 3146), or when the immediate base's `Equals(object)` or `GetHashCode` is abstract (`CallSuperOnAbstractBase` 3147). Zero collected members is allowed when `callSuper` is true (base-only methods; skips `NoMembersToGenerate` 3055). Additive with `fields`, `includeProperties`, `implementIEquatable`, `generateOperators`, `replaceExisting`, and `useHashCodeCombine`. Preview describes base inclusion and writes nothing.
- `generate_equals_hashcode` now honors `includeProperties`: omitted / true keeps today's field+property collection (instance fields plus readable non-indexer properties; still skip static/const/implicit/indexers); `false` with no `fields` list collects instance fields only. A non-empty `fields` list is authoritative and still resolves listed names against fields and properties (an explicit property name is still hashed/equaled). Auto-property backing fields stay excluded (`IsImplicitlyDeclared`). Additive with `fields`, `implementIEquatable`, `generateOperators`, `replaceExisting`, and `useHashCodeCombine`. Preview describes the chosen members and writes nothing. `generate_tostring` is unchanged (still includes properties by default). The existing two-parameter `EqualityMemberCollector.CollectMembers` overload is preserved and forwards with `includeProperties: true` so NuGet callers compiled against the previous package do not hit `MissingMethodException`.
- `generate_equals_hashcode` now honors `useHashCodeCombine`: omitted / true keeps today's `HashCode.Combine` (≤8 members) or `HashCode` builder (&gt;8 members), qualifying `global::System.HashCode` so a type-local `HashCode` cannot steal the call; `false` emits a classic unchecked prime-multiply `GetHashCode` (seed 17, `hash = (hash * 31) + memberHash`) with `this.Member.GetHashCode()` for non-nullable members and `(this.Member?.GetHashCode() ?? 0)` for nullable reference / nullable value members. Combine / builder / prime-multiply member accesses are qualified with `this.` so a field named `hash` is not shadowed by the accumulator local. Additive with `fields`, `implementIEquatable`, `generateOperators`, and `replaceExisting`. Preview describes the chosen GetHashCode style and writes nothing.
- `generate_equals_hashcode` now honors `replaceExisting`: omitted / false keeps today's fail-on-existing behavior (`AlreadyHasOverride` 3056, `AlreadyImplementsIEquatable` 3144, `AlreadyHasEqualityOperators` 3145); `true` removes existing `Equals(object)` and `GetHashCode()` before generating, and also removes typed `Equals(T)` / `System.IEquatable<T>` when `implementIEquatable` is true, and `operator ==` / `operator !=` when `generateOperators` is true. Existing operators are left alone when `generateOperators` is false. Preview writes nothing.
- `generate_equals_hashcode` now honors `generateOperators`: omitted / false keeps today's no-operator behavior; `true` adds `public static bool operator ==` and `operator !=` that agree with the generated Equals (`global::System.Object.Equals(left, right)` / `!(left == right)`). Class parameters are nullable (`Person?`); struct parameters are not (`Point`). Generic types use the constructed self type (`Box<T>`). Additive with `implementIEquatable`. Types that already declare `==` or `!=` fail with AlreadyHasEqualityOperators (3145). Preview writes nothing.
- `generate_equals_hashcode` now honors `implementIEquatable`: omitted / false keeps today's Equals(object)+GetHashCode-only behavior; `true` adds `global::System.IEquatable<{TypeName}>`, a typed `Equals(T)` (nullable for class, not for struct) comparing the same members, and has `Equals(object)` delegate to it. Types that already implement IEquatable&lt;T&gt; or already have a compatible typed Equals fail with AlreadyImplementsIEquatable (3144). Preview writes nothing.
- `generate_tostring` now honors `format`: omitted / null / empty / `"interpolated"` (case-insensitive) keeps today's interpolated-string `ToString`; `"stringbuilder"` (case-insensitive) builds the same `TypeName { Field = value, ... }` text via `global::System.Text.StringBuilder`, qualifying member accesses with `this.` so a field named `sb` is not shadowed by the local builder. Unknown values fail with InvalidToStringFormat (3143). Preview writes nothing.
- `extract_base_class` now honors `separateFile`: default `false` keeps today's same-file extract unless `targetFile` is set; `true` with no `targetFile` writes `{BaseClassName}.cs` next to the source; explicit `targetFile` always wins; rejects an existing sibling with TargetFileExists (3019). Preview writes nothing. When default compile items are disabled, the new sibling is added as an explicit `Compile Include`. Nested types reject a separate-file extract (3142).
- `extract_interface` now honors `separateFile`: default `false` keeps today's same-file extract unless `targetFile` is set; `true` with no `targetFile` writes `{InterfaceName}.cs` next to the source; explicit `targetFile` always wins; rejects an existing sibling with TargetFileExists (3019). Preview writes nothing.
- `sort_usings` now honors `systemFirst`: default `true` keeps today's System / System.* first grouping within regular and static usings; `false` keeps group order (regular, static, alias) but sorts namespaces alphabetically with no System priority. Alias usings stay alphabetical by alias. Global usings stay ahead of non-global usings in both modes (CS8915). Preview writes nothing.
- `move_type_to_namespace` now honors `updateFileLocation`: when true, moves the source file to a folder matching the target namespace (e.g. `MyApp.Services` → `MyApp/Services/`), creating missing directories and updating project document paths / explicit compile items; preview writes nothing (including folders); rejects destination-exists, file-name collision, uneditable project, and missing file. Default `false` keeps today's namespace-only rewrite.

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
- `change_return_type` — change a method's declared return type and convert return statements when safe (implicit conversions left alone; void↔non-void rewritten), with override/interface updates, preview mode, per-document type qualification, and rejects for same type, invalid type syntax, incompatible returns or invocation result contexts, uneditable override/interface contracts, async/Task/ValueTask, iterators, overload collisions, method-group references that would break, missing methods, and uneditable documents
- `convert_anonymous_to_class` — convert an anonymous type (`new { ... }`) to a named class or record with matching public properties, replace same-shape anonymous creations in the solution, with preview mode and rejects for non-anonymous locations, invalid type names, name conflicts, missing files, and uneditable documents
- `convert_tuple_to_struct` — convert a C# tuple / `ValueTuple` creation to a named struct with matching public members, replace same-shape tuple creations in the solution, with preview mode and rejects for non-tuple locations, invalid type names, name conflicts, missing files, uneditable documents, less-accessible member types, and method type-parameters
- `rename_file_to_match_type` — rename a source file so its name matches the primary type declared in it (preserve directory), without renaming the type, constructors, or references, with preview mode and rejects for already-matching names, no type, multiple types without disambiguation, destination-exists, missing files, and uneditable documents
- `rename_namespace` — rename a namespace across the solution, updating declarations, using directives, and qualified name references; `updateFolders` moves a folder whose path matches the old namespace and updates document paths / explicit compile items (including wildcard patterns), with preview mode and rejects for same name, invalid namespace name, missing namespace, name conflicts, missing files, uneditable documents, destination-folder-exists, nested destination, folder that contains a project file, unsupported compile items, file-name collision, and folder path that does not correspond to the namespace

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
