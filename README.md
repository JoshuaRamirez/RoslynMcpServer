# Roslyn MCP Server

[![Build and Test](https://github.com/JoshuaRamirez/RoslynMcpServer/actions/workflows/build.yml/badge.svg)](https://github.com/JoshuaRamirez/RoslynMcpServer/actions/workflows/build.yml)
[![Code Quality](https://github.com/JoshuaRamirez/RoslynMcpServer/actions/workflows/quality.yml/badge.svg)](https://github.com/JoshuaRamirez/RoslynMcpServer/actions/workflows/quality.yml)
[![NuGet](https://img.shields.io/nuget/v/RoslynMcp.Server.svg)](https://www.nuget.org/packages/RoslynMcp.Server)
[![NuGet Downloads](https://img.shields.io/nuget/dt/RoslynMcp.Server.svg)](https://www.nuget.org/packages/RoslynMcp.Server)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Let AI assistants like Claude safely refactor your C# codebase using the same Roslyn compiler platform that powers Visual Studio.

Roslyn MCP Server is a [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server that exposes **66 Roslyn-powered tools** to AI assistants and other MCP clients. It combines 37 refactoring operations, 5 code navigation tools, 6 analysis and metrics tools, 5 code generation tools, and 13 code conversion tools -- giving your AI deep code intelligence, comprehensive refactoring, and modern C# syntax transformations with full solution-wide reference tracking and preview support.

---

## Table of Contents

- [Why RoslynMcpServer?](#why-roslynmcpserver)
- [Prerequisites](#prerequisites)
- [Quick Start](#quick-start)
- [Standalone CLI](#standalone-cli)
- [Configuration](#configuration)
- [Available Tools](#available-tools)
- [Preview Mode](#preview-mode)
- [Troubleshooting](#troubleshooting)
- [NuGet Libraries](#nuget-libraries)
- [Contributing](#contributing)
- [License](#license)

---

## Why RoslynMcpServer?

- **66 tools** -- refactoring, navigation, analysis, generation, and conversion tools, the most comprehensive Roslyn MCP server available
- **Preview mode on every operation** -- see exactly what will change before applying
- **Atomic file writes with rollback** -- if any file write fails, all changes are reverted
- **Solution-wide reference updates** -- renames and moves propagate across your entire solution
- **Single command install** -- `dotnet tool install -g RoslynMcp.Server`, no repo cloning needed
- **Cross-platform** -- works on Windows, Linux, and macOS

---

## Prerequisites

Before installing, make sure you have:

1. **.NET 9.0 SDK or later** -- [Download here](https://dotnet.microsoft.com/download/dotnet/9.0)
2. **A C# solution (`.sln` or `.slnx`) or project (`.csproj`) to work with**

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

## Standalone CLI

All 66 tools are also available as a standalone CLI for use in scripts, CI/CD pipelines, and terminals without an AI assistant.

### Install

```bash
dotnet tool install -g RoslynMcp.Cli
```

### Usage

```bash
roslyn-cli <solution-path> <tool-name> [--option value ...]
roslyn-cli <tool-name> --help
roslyn-cli --help
```

### Examples

```bash
# Check environment health
roslyn-cli C:/path/to/MySolution.sln diagnose --format text

# Rename a symbol across the entire solution
roslyn-cli C:/path/to/MySolution.sln rename-symbol --source-file C:/path/to/Foo.cs --symbol-name Bar --new-name Baz

# Get compiler diagnostics (errors only), pipe to jq
roslyn-cli C:/path/to/MySolution.sln get-diagnostics --severity-filter Error | jq '.data'

# Preview a refactoring without applying
roslyn-cli C:/path/to/MySolution.sln extract-method --source-file Foo.cs --start-line 10 --end-line 20 --method-name DoWork --preview
```

Output is JSON by default (pipeable to `jq`). Use `--format text` for human-readable output. Exit codes: 0=success, 1=tool error, 2=CLI error, 3=environment error.

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

All tools accept a `solutionPath` parameter (absolute path to a `.sln`, `.slnx`, or `.csproj` file). Refactoring tools also accept a `preview` parameter (set to `true` to see changes without applying them).

### Move and Rename

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `move_type_to_file` | Move a C# type declaration to a different file. Updates all references automatically. | `sourceFile`, `symbolName`, `targetFile`, `createTargetFile` |
| `move_type_to_namespace` | Change the namespace of a C# type. Updates all using directives and qualified references. | `sourceFile`, `symbolName`, `targetNamespace`, `updateFileLocation` |
| `rename_symbol` | Rename any C# symbol (type, method, property, field, variable, etc.) with automatic reference updates across the solution. | `sourceFile`, `symbolName`, `newName`, `line`, `column`, `renameOverloads`, `renameFile` |
| `rename_file_to_match_type` | Rename a source file so its name matches the primary type declared in it, without renaming the type or its references. | `sourceFile`, `typeName`, `line`, `column` |
| `rename_namespace` | Rename a C# namespace across the solution, updating declarations, using directives, and qualified name references. Does not move folders by default. | `sourceFile`, `namespaceName`, `newName`, `line`, `column`, `updateFolders` |

### Extract

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `extract_method` | Extract selected code into a new method. Automatically detects parameters and return values. | `sourceFile`, `startLine`, `startColumn`, `endLine`, `endColumn`, `methodName`, `visibility` |
| `extract_variable` | Extract an expression to a local variable. | `sourceFile`, `startLine`, `startColumn`, `endLine`, `endColumn`, `variableName`, `useVar`, `replaceAll` |
| `extract_constant` | Extract a literal value to a named constant. | `sourceFile`, `startLine`, `startColumn`, `endLine`, `endColumn`, `constantName`, `visibility`, `replaceAll` |
| `extract_interface` | Extract an interface from a class's public members (`line` picks the type whose identifier or declaration span covers that 1-based line when several types share the name — identifier preferred, then smallest containing type, including nested types; omitted keeps today's typeName `FirstOrDefault` pick so two same-named types still succeed; a continuation-line identifier is eligible — do not require the declaration to start on `line`; `column` picks the type whose identifier or declaration span covers that 1-based column when set with `line` — identifier preferred, then smallest containing type; omitted keeps today's typeName + optional line pick so indented types still extract; `column` without `line` keeps today's first-match after the typeName filter rather than substituting each candidate's own start line; indexers emit `this[...]` declarations via `CreateInterfaceIndexer`; ordinary properties stay on `CreateInterfaceProperty`; `members` matches `Item` / `this[]` / `this[int i]`). | `sourceFile`, `typeName`, `line`, `column`, `interfaceName`, `members`, `targetFile`, `separateFile` |
| `extract_base_class` | Extract members (methods, properties, fields, events, and indexers) to a new base class (`line` picks the type whose identifier or declaration span covers that 1-based line when several types share the name — identifier preferred, then smallest containing type, including nested types; omitted keeps today's typeName `FirstOrDefault` pick on `ClassDeclarationSyntax` so two same-named classes still succeed; a continuation-line identifier is eligible — do not require the declaration to start on `line`; `column` picks the type whose identifier or declaration span covers that 1-based column when set with `line` — identifier preferred, then smallest containing type; omitted keeps today's typeName + optional line pick so indented types still extract; `column` without `line` keeps today's first-match after the typeName filter rather than substituting each candidate's own start line). Field-like and accessor-style events move by name; a multi-variable event field moves only the selected declarator. Indexers move as legal `this[...]` declarations; `members` matches `Item` / `this[]` / `this[int i]`. Private events and indexers become protected on the new base. `makeAbstract` makes extracted methods, properties, events, and indexers abstract on the new base and keeps override implementations on the derived type; fields still move as concrete. | `sourceFile`, `typeName`, `line`, `column`, `baseClassName`, `members`, `targetFile`, `separateFile`, `makeAbstract` |
| `pull_members_up` | Move selected members (methods, properties, fields, events, and indexers) from a derived type onto an existing base class or interface (`line` picks the type whose identifier or declaration span covers that 1-based line when several types share the name — identifier preferred, then smallest containing type, including nested types; omitted keeps today's typeName `FirstOrDefault` pick on `TypeDeclarationSyntax` so two same-named types still succeed; a continuation-line identifier is eligible — do not require the declaration to start on `line`; `column` picks the type whose identifier or declaration span covers that 1-based column when set with `line` — identifier preferred, then smallest containing type; omitted keeps today's typeName + optional line pick so indented types still extract; `column` without `line` keeps today's first-match after the typeName filter rather than substituting each candidate's own start line). Field-like and accessor-style events move by name; a multi-variable event field moves only the selected declarator. Indexers move as legal `this[...]` declarations; `members` matches `Item` / `this[]` / `this[int i]`. Private events and indexers become protected on the base. `makeAbstract` on a class target makes the event or indexer abstract and keeps an override on the derived type; interface targets keep the implementation on the derived type. Cross-assembly `makeAbstract` reduces `protected internal` method, property, event, and indexer overrides (and a more-restricted accessor) to `protected` (CS0507); same-assembly keeps `protected internal`. | `sourceFile`, `typeName`, `line`, `column`, `members`, `targetBaseType`, `makeAbstract` |
| `push_members_down` | Move selected members (methods, properties, fields, events, and indexers) from a base type down onto derived types (`line` picks the type whose identifier or declaration span covers that 1-based line when several types share the name — identifier preferred, then smallest containing type, including nested types; omitted keeps today's typeName `FirstOrDefault` pick on `TypeDeclarationSyntax` so two same-named types still succeed; a continuation-line identifier is eligible — do not require the declaration to start on `line`; `column` picks the type whose identifier or declaration span covers that 1-based column when set with `line` — identifier preferred, then smallest containing type; omitted keeps today's typeName + optional line pick so indented types still extract; `column` without `line` keeps today's first-match after the typeName filter rather than substituting each candidate's own start line). Field-like and accessor-style events move by name; a multi-variable event field moves only the selected declarator. Indexers move as legal `this[...]` declarations; `members` matches `Item` / `this[]` / `this[int i]`. `leaveAbstract` on a class source makes the event or indexer abstract on the base and adds an override on each derived type. Cross-assembly `leaveAbstract` reduces `protected internal` method, property, event, and indexer overrides (and a more-restricted accessor) to `protected` (CS0507); same-assembly keeps `protected internal`. Push to an interface emits a legal `this[...]` signature (only publicly accessible accessors). | `sourceFile`, `typeName`, `line`, `column`, `members`, `targetDerivedTypes`, `leaveAbstract` |
| `use_base_type` | Replace derived-type references with a compatible base type or interface (`line` picks the type whose identifier or declaration span covers that 1-based line when several types share the name — identifier preferred, then smallest containing type, including nested types; omitted keeps today's typeName first-match on `TypeDeclarationSyntax` — `matches[0]` when the name has no `.` or there is a single match, else semantic `TypeNameMatches` for FQN — so two same-named types still succeed; a continuation-line identifier is eligible — do not require the declaration to start on `line`; `column` picks the type whose identifier or declaration span covers that 1-based column when set with `line` — identifier preferred, then smallest containing type; omitted keeps today's typeName + optional line pick so indented types still rewrite; `column` without `line` keeps today's omitted-line `PickOmittedLineMatch` after the typeName filter rather than substituting each candidate's own start line). | `sourceFile`, `typeName`, `line`, `column`, `targetBaseType` |
| `introduce_field` | Turn a selected local variable or expression into a class field, optionally initializing it in a constructor. | `sourceFile`, `startLine`, `startColumn`, `endLine`, `endColumn`, `fieldName`, `isReadonly`, `isStatic`, `initializeInConstructor`, `replaceAll` |
| `safe_delete` | Delete a selected symbol only when it has no remaining references. If usages exist, reject with their locations. | `sourceFile`, `startLine`, `startColumn`, `endLine`, `endColumn`, `symbolName` |
| `make_static` | Make a selected instance method static when it does not use instance state. Adds the static modifier and updates call sites and method-group conversions to the containing type name. | `sourceFile`, `startLine`, `startColumn`, `endLine`, `endColumn`, `symbolName` |
| `make_non_static` | Make a selected static method an instance method when a valid instance receiver exists. Removes the static modifier and updates type-name call sites and method-group conversions to an instance receiver (or `this` in the same type). | `sourceFile`, `startLine`, `startColumn`, `endLine`, `endColumn`, `symbolName` |
| `invert_if` | Flip an if-statement condition and swap the if/else branches, preserving semantics. Comparison operators are inverted (`>` ↔ `<=`, `==` ↔ `!=`, …); `&&` / `||` use De Morgan. An if without else gets an empty if body and the original body as else. Conditions that introduce a pattern or out variable are rejected. | `sourceFile`, `line`, `column` |
| `add_braces` | Add braces to control statements (`if`, `else`, `for`, `foreach`, `while`, `using`) that have a single-statement body, preserving semantics. `scope` is `statement` (default; wrap the body at `line` / optional `column`), `file` (every braceless control body in the file), or `type` (bodies inside `typeName`). `else if` stays a single construct in file/type scope. | `sourceFile`, `line`, `column`, `scope`, `typeName` |
| `remove_braces` | Remove braces from control statements (`if`, `else`, `for`, `foreach`, `while`, `using`) whose body is a single-statement block, preserving semantics. `scope` is `statement` (default; unwrap the body at `line` / optional `column`), `file` (every eligible single-statement braced control body in the file), or `type` (bodies inside `typeName`). `else if` stays a single construct in file/type scope. | `sourceFile`, `line`, `column`, `scope`, `typeName` |
| `simplify_name` | Remove redundant namespace qualifications from type (and similar) references when a using directive or the current namespace already makes the short name bind to the same symbol. `scope` is `file` (default; every eligible qualified name) or `location` (the name at `line` / optional `column`). Names that would become ambiguous or bind differently are skipped and reported. | `sourceFile`, `line`, `column`, `scope` |

### Inline

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `inline_method` | Inline a method by replacing call sites with the method body. Optionally remove the method. | `sourceFile`, `methodName`, `line`, `column`, `callSiteLocation`, `removeMethod` |
| `inline_variable` | Inline a local variable by replacing all usages with its initializer value (`column` picks the declaration whose identifier or declaration span covers that column; omitted keeps today's variableName + optional line start-line pick so a continuation-line identifier still needs `column` when `line` is the type line of a split declaration). | `sourceFile`, `variableName`, `line`, `column`, `preview` |
| `inline_constant` | Inline a const field by replacing references with a formatted literal. Optionally remove the constant. | `sourceFile`, `constantName`, `typeName`, `removeConstant` |

### Signature and Encapsulation

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `change_signature` | Add, remove, or reorder method parameters and update all call sites (`column` picks the smallest method whose identifier or declaration span covers that column; omitted keeps today's methodName and/or line start-line pick so a continuation-line identifier still needs `column` when `line` is the identifier line of a split signature). | `sourceFile`, `methodName`, `parameters` (array of changes), `line`, `column` |
| `add_parameter` | Add a named parameter to a method and update call sites, overrides, and interface implementations. | `sourceFile`, `methodName`, `parameterName`, `parameterType`, `defaultValue`, `position`, `line`, `column`, `updateOverrides`, `updateImplementations` |
| `remove_parameter` | Remove a named parameter from a method and drop matching call-site arguments, updating overrides and interface implementations. | `sourceFile`, `methodName`, `parameterName`, `line`, `column`, `force`, `updateOverrides`, `updateImplementations` |
| `reorder_parameters` | Reorder a method's parameters by a 0-based permutation and update positional call-site arguments, updating overrides and interface implementations. | `sourceFile`, `methodName`, `newOrder`, `line`, `column`, `updateOverrides`, `updateImplementations` |
| `change_return_type` | Change a method's return type and update return statements when the conversion is safe, updating overrides and interface implementations. | `sourceFile`, `methodName`, `newReturnType`, `line`, `column`, `updateOverrides`, `updateImplementations`, `convertReturnStatements` |
| `encapsulate_field` | Convert a field to a property with backing field (`line` picks the field whose identifier or declaration span covers that 1-based line when several fields share the name — identifier preferred, then smallest covering declarator/field, including nested types; omitted keeps today's fieldName first-match so two same-named fields still succeed; a continuation-line identifier is eligible — do not require the declaration to start on `line`; `column` picks the field whose identifier or declaration span covers that 1-based column when set with `line` — identifier preferred, then smallest covering declarator/field; omitted keeps today's fieldName + optional line pick so indented fields still encapsulate; `column` without `line` keeps today's FirstOrDefault after the fieldName filter rather than substituting each candidate's own start line; `updateReferences`: default true rewrites external references to the new property; false still encapsulates — private field + property — and does not redirect callers to the property; if the backing field is renamed for a case-only collision (`name` → `_name`), external identifiers follow the new field name; same-class references stay on the field; `preview` describes whether references will be updated and writes nothing). | `sourceFile`, `fieldName`, `line`, `column`, `propertyName`, `readOnly`, `updateReferences`, `preview` |

### Generate

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `generate_constructor` | Generate a constructor that initializes fields and/or properties of a type (`line` picks the type whose identifier or declaration span covers that 1-based line when several types share the name — identifier preferred, then smallest containing type, including nested types; omitted keeps today's typeName `FirstOrDefault` pick so two same-named types still succeed; a continuation-line identifier is eligible — do not require the declaration to start on `line`; `column` picks the type whose identifier or declaration span covers that 1-based column when set with `line` — identifier preferred, then smallest containing type; omitted keeps today's typeName + optional line pick so indented types still generate; `column` without `line` keeps today's first-match after the typeName filter rather than substituting each candidate's own start line; `includeProperties`: default true includes settable properties, false uses instance fields only unless `members` names a property; `includeInheritedMembers`: default false keeps this-type-only collection, true also collects accessible instance fields and, when `includeProperties` is true, settable properties declared on base types; `replaceExisting`: replace an existing constructor with the exact same signature instead of failing — optional-parameter / required-parameter ambiguity still fails; `visibility`: default public, also `private` / `protected` / `internal` / `protected internal` / `private protected`; structs/record structs reject the three protected forms; unsealed-record copy constructors must be public or protected (CS8878); `copyConstructor`: default false keeps today's one-parameter-per-member constructor, true generates a single same-type copy constructor that assigns each selected member from that parameter — derived records whose base is also a record include `: base(other)`; `classBaseCopy`: default false keeps today's class copy-constructor shape, true (requires `copyConstructor`) emits `: base((Base)other)` on an ordinary class when the immediate base has an accessible `Base(Base)` copy constructor and does not reassign inherited members; `callBase`: default false keeps today's non-copy shape (no `: base(...)`), true emits `: base(...)` on an ordinary class or record class when an accessible immediate-base constructor's parameter types are a prefix of the generated constructor — conflicts with `copyConstructor`; record structs / structs ignore `callBase`). | `sourceFile`, `typeName`, `line`, `column`, `members`, `includeProperties`, `includeInheritedMembers`, `addNullChecks`, `replaceExisting`, `visibility`, `copyConstructor`, `classBaseCopy`, `callBase` |
| `generate_property` | Generate a property on a type: auto-property `{ get; set; }`, init-only `{ get; init; }`, or a backing-field form when a field is the target (`line` picks the type whose identifier or declaration span covers that 1-based line when several types share the name — identifier preferred, then smallest containing type, including nested types; omitted keeps today's typeName `FirstOrDefault` pick so two same-named types still succeed; a continuation-line identifier is eligible — do not require the declaration to start on `line`; `column` picks the type whose identifier or declaration span covers that 1-based column when set with `line` — identifier preferred, then smallest containing type; omitted keeps today's typeName + optional line pick so indented types still generate; `column` without `line` keeps today's first-match after the typeName filter rather than substituting each candidate's own start line; `replaceExisting`: default false keeps today's fail-on-clash; true removes the existing property of that name — including across partials — and inserts a freshly generated one; fields and methods of the same name are never removed; two same-named properties with no single target fail with `NameCollision`). | `sourceFile`, `typeName`, `line`, `column`, `propertyName`, `propertyType`, `fieldName`, `visibility`, `initOnly`, `replaceExisting` |
| `generate_method_stub` | Generate a method from an undefined call site, inferring the signature from usage (`throwNotImplemented`: default true emits `throw new NotImplementedException();`; false uses default-return / empty void bodies via `SyntaxGenerationHelper.CreateDefaultReturnBody`; async `Task` / `Task<T>` unwrap so the body compiles; `ref` / `ref readonly` returns still throw; `replaceExisting`: default false keeps today's fail-on-clash; true removes a compatible ordinary method — same name, type-parameter arity, parameter count, parameter types in order, and `RefKind` — including across partials, then inserts a freshly generated stub; constructors, operators, local functions, explicit interface implementations, and accessors are never replaced; two compatible ordinary methods with no single target fail with `NameCollision`). | `sourceFile`, `line`, `column`, `methodName`, `returnType`, `visibility`, `generateAsync`, `throwNotImplemented`, `replaceExisting` |
| `generate_overrides` | Generate override methods, properties/indexers, and events for base class virtual/abstract members (`line` picks the type whose identifier or declaration span covers that 1-based line when several types share the name — identifier preferred, then smallest containing type, including nested types; omitted keeps today's typeName `FirstOrDefault` pick so two same-named types still succeed; a continuation-line identifier is eligible — do not require the declaration to start on `line`; `column` picks the type whose identifier or declaration span covers that 1-based column when set with `line` — identifier preferred, then smallest containing type; omitted keeps today's typeName + optional line pick so indented types still generate; `column` without `line` keeps today's first-match after the typeName filter rather than substituting each candidate's own start line; `callBase`: default true emits `base.M(...)` for non-abstract virtual methods and `return base.Prop;` / `base.Prop = value;` for non-abstract virtual properties and indexers (`return base[i];` / `base[i] = value;`); abstract members still throw; events always use empty add/remove regardless of `callBase`; a cross-assembly `protected internal` method, property, indexer, or event is emitted as `protected` (CS0507), including a more-restricted accessor; `internal` / `private protected` members from another assembly are not generated; false uses default-return / empty bodies; `replaceExisting`: default false skips members this type already overrides; true also replaces those existing overrides — match methods by name + parameter types + `RefKind`, properties and events by name; two same-name existing overrides with no exact signature match fail with `OverrideExists`; `new` hiders / explicit interface implementations / non-override ordinary members are never replaced). | `sourceFile`, `typeName`, `line`, `column`, `members`, `callBase`, `replaceExisting` |
| `implement_interface` | Generate interface member implementations for a type (`line` picks the type whose identifier or declaration span covers that 1-based line when several types share the name — identifier preferred, then smallest containing type, including nested types; omitted keeps today's typeName `FirstOrDefault` pick so two same-named types still succeed; a continuation-line identifier is eligible — do not require the declaration to start on `line`; `column` picks the type whose identifier or declaration span covers that 1-based column when set with `line` — identifier preferred, then smallest containing type; omitted keeps today's typeName + optional line pick so indented types still generate; `column` without `line` keeps today's first-match after the typeName filter rather than substituting each candidate's own start line; indexers emit `this[...]` indexer declarations via `CreateIndexerStub`; ordinary properties stay on `CreatePropertyStub`; `throwNotImplemented`: default true emits `throw new NotImplementedException();`; false uses default-return / empty setter bodies; `ref` / `ref readonly` getters still throw; `replaceExisting`: default false keeps today's only-unimplemented members and `MemberAlreadyImplemented` when nothing is missing; true also replaces already-implemented interface members — methods by name + parameter types + `RefKind`, properties and events by name, indexers by `Item` / `this[]` / display forms; two same-name existing implementations with no exact signature match fail with `NameCollision` before any write; `explicitImplementation` still selects explicit vs ordinary public stubs; `preview` describes generate vs replace and writes nothing). | `sourceFile`, `typeName`, `line`, `column`, `interfaceName`, `explicitImplementation`, `members`, `throwNotImplemented`, `replaceExisting`, `preview` |
| `implement_abstract` | Generate implementation stubs for unimplemented abstract members inherited by a selected class — methods, properties, indexers, and events (`line` picks the type whose identifier or declaration span covers that 1-based line when several types share the name — identifier preferred, then smallest containing type, including nested types; omitted keeps today's typeName `FirstOrDefault` pick so two same-named types still succeed; a continuation-line identifier is eligible — do not require the declaration to start on `line`; `column` picks the type whose identifier or declaration span covers that 1-based column when set with `line` — identifier preferred, then smallest containing type; omitted keeps today's typeName + optional line pick so indented types still generate; `column` without `line` keeps today's first-match after the typeName filter rather than substituting each candidate's own start line; `throwNotImplemented`: default true emits `throw new NotImplementedException();`; false uses default-return / empty setter bodies; `ref` / `ref readonly` methods and getters still throw; events always use empty add/remove; a cross-assembly `protected internal` event is emitted as `protected` (CS0507); `internal` / `private protected` events from another assembly are not selected; `replaceExisting`: default false keeps today's only-unimplemented members and `NoUnimplementedAbstractMembers` when nothing is missing; true also replaces already-implemented abstract members — methods by name + parameter types + `RefKind`, properties and events by name, indexers by `Item` / `this[]` / display forms; two same-name existing implementations with no exact signature match fail with `NameCollision` before any write; `new` hiders / explicit interface implementations / non-override ordinary members are never replaced; `preview` describes generate vs replace and writes nothing). | `sourceFile`, `typeName`, `line`, `column`, `members`, `throwNotImplemented`, `replaceExisting`, `preview` |

### Convert

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `convert_to_async` | Convert a synchronous method to async/await pattern (`column` picks the method whose identifier or declaration span covers that column; omitted keeps today's MethodName + Line pick so a continuation-line identifier still needs `column`; `renameToAsync`: default true rewrites call-site identifiers to the `Async` name; `updateCallers`: default false keeps today's unaugmented callers — no `await` wrap and callers are not made async; true wraps already-async callers in `await` and skips synchronous callers that cannot legally await, reporting them; `preview` describes caller updates or that none will happen and writes nothing). | `sourceFile`, `methodName`, `line`, `column`, `renameToAsync`, `updateCallers`, `preview` |
| `convert_expression_body` | Toggle between expression body (`=> expr;`) and block body (`{ return expr; }`) (`direction`: `ToExpressionBody` / `ToBlockBody`; `column` picks the member whose identifier or declaration span covers that column on the given line; omitted keeps today's first-match-on-the-line pick; `preview` describes the rewrite and writes nothing). | `sourceFile`, `direction`, `memberName`, `line`, `column`, `preview` |
| `convert_to_block_body` | Convert a selected expression-bodied member (`=> expr`) to a block body. Methods become `{ return expr; }` or `{ expr; }` as appropriate; properties and accessors that are expression-bodied are converted too (`column` picks the member whose identifier or declaration span covers that column on the given line; omitted keeps today's memberName and/or line pick — smallest containing node; `preview` describes the rewrite and writes nothing). | `sourceFile`, `memberName`, `line`, `column`, `preview` |
| `convert_property` | Convert between auto-property and full property with backing field (`column` picks the smallest property whose identifier or declaration span covers that column on the given line; omitted keeps today's propertyName and/or line start-line pick so indented properties still convert; `preview` describes the rewrite and writes nothing). | `sourceFile`, `direction`, `propertyName`, `line`, `column`, `preview` |
| `convert_foreach_linq` | Convert foreach loops with Add patterns to LINQ (`preferQuerySyntax`: default false keeps today's method syntax `.Where().Select().ToList()`; true emits query syntax (`from … where … select`) for filter / project / filter+project / ToList-after-query, preserving an explicitly typed foreach element (`from string item in …`) while `var` stays untyped; Any / All / FirstOrDefault / Count-only keep method syntax rather than inventing invalid query syntax; `column` picks the foreach whose keyword covers that column on the given line; `preview` describes the rewrite and writes nothing). | `sourceFile`, `line`, `column`, `preferQuerySyntax`, `preview` |
| `convert_anonymous_to_class` | Convert an anonymous type (`new { ... }`) to a named class or record, replacing same-shape anonymous creations in the solution. | `sourceFile`, `line`, `newTypeName`, `column`, `asRecord` |
| `convert_tuple_to_struct` | Convert a tuple (`(int X, int Y)` / `(1, 2)` / `ValueTuple`) to a named struct, replacing same-shape tuple creations in the solution. | `sourceFile`, `line`, `newTypeName`, `column` |
| `convert_to_pattern_matching` | Convert if/is chains and switch statements to switch expressions (`column` picks the smallest switch or if whose span covers that column on the given line; omitted keeps today's first start-line switch-then-if pick so indented statements still convert; `preview` describes the rewrite and writes nothing). | `sourceFile`, `line`, `column`, `preview` |
| `convert_to_interpolated_string` | Convert string.Format() calls and concatenation to interpolated strings (`column` picks the Format invocation or concatenation whose span covers that column on the given line; omitted keeps today's first-match-on-the-line pick so indented expressions still convert; `preview` describes the rewrite and writes nothing). | `sourceFile`, `line`, `column`, `preview` |
| `introduce_parameter` | Promote a local variable to a method parameter, updating all call sites. | `sourceFile`, `variableName`, `line` |

### Using Directives

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `add_missing_usings` | Add missing using directives required to resolve unbound type references. Process a single file or all files in the solution. | `sourceFile`, `allFiles` |
| `remove_unused_usings` | Remove unused using directives. Process a single file or all files in the solution. | `sourceFile`, `allFiles` |
| `sort_usings` | Sort using directives alphabetically in a C# file. | `sourceFile`, `systemFirst` |

### Diagnostics

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `diagnose` | Check the health of the Roslyn MCP server environment and workspace status. | `solutionPath` (optional), `verbose` |

### Code Navigation

These read-only tools let you explore and understand your codebase without making changes. Use them to discover symbols, trace references, and inspect type information before refactoring.

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `find_references` | Find all references to a symbol across the entire solution. Returns file locations, context snippets, and write/definition indicators. | `sourceFile`, `symbolName`, `line`, `column`, `maxResults` |
| `go_to_definition` | Navigate to the source definition of a symbol. Supports partial classes with multiple definition locations. | `sourceFile`, `symbolName`, `line`, `column` |
| `get_symbol_info` | Get detailed metadata for any symbol: kind, accessibility, modifiers, base types, interfaces, members, parameters, return type, and XML documentation. | `sourceFile`, `symbolName`, `line`, `column` |
| `find_implementations` | Find all implementations of an interface or overrides of an abstract/virtual member. | `sourceFile`, `symbolName`, `line`, `column`, `maxResults` |
| `search_symbols` | Search for symbols by name pattern across the entire workspace. Filter by kind (class, method, property, etc.). | `query`, `kindFilter`, `maxResults` |

### Analysis & Metrics

These tools analyze your code without making changes. Use them to understand code quality, data flow, and control flow.

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `get_diagnostics` | Retrieve compiler diagnostics (errors, warnings, info) filtered by severity and optionally by file. | `sourceFile`, `severityFilter` |
| `get_code_metrics` | Calculate code metrics: cyclomatic complexity, lines of code, maintainability index, class coupling, depth of inheritance. | `sourceFile`, `symbolName`, `line` |
| `analyze_control_flow` | Analyze control flow for a code region: start/end point reachability, return statements, and exit points. | `sourceFile`, `startLine`, `endLine` |
| `analyze_data_flow` | Analyze data flow for a code region: variables read/written inside, data flowing in/out, captured variables. | `sourceFile`, `startLine`, `endLine` |
| `find_callers` | Find all callers of a symbol across the entire solution. | `sourceFile`, `symbolName`, `line`, `column`, `maxResults` |
| `get_type_hierarchy` | Retrieve the type hierarchy (base types and/or derived types) for a given type. | `sourceFile`, `symbolName`, `line`, `column`, `direction` |
| `get_document_outline` | Get a hierarchical outline of all symbols in a file (namespaces, types, members). | `sourceFile` |

### Code Generation

These tools generate new code members for existing types.

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `generate_equals_hashcode` | Generate Equals() and GetHashCode() overrides for a type based on its fields/properties (`line` picks the type whose identifier or declaration span covers that 1-based line when several types share the name — identifier preferred, then smallest containing type, including nested types; omitted keeps today's typeName `FirstOrDefault` pick so two same-named types still succeed; a continuation-line identifier is eligible — do not require the declaration to start on `line`; `column` picks the type whose identifier or declaration span covers that 1-based column when set with `line` — identifier preferred, then smallest containing type; omitted keeps today's typeName + optional line pick so indented types still generate; `column` without `line` keeps today's first-match after the typeName filter rather than substituting each candidate's own start line; `implementIEquatable`: also add IEquatable&lt;T&gt; and typed Equals; `generateOperators`: also add `==` / `!=`; `replaceExisting`: replace existing equality members instead of failing; `useHashCodeCombine`: default true uses HashCode.Combine / builder, false uses unchecked prime-multiply; `includeProperties`: default true includes readable properties, false uses instance fields only unless `fields` names a property; `callSuper`: default false keeps member-only Equals/GetHashCode, true folds the immediate base type's equality into both methods; `includeInheritedMembers`: default false keeps this-type-only collection, true also collects accessible instance fields/properties declared on base types). | `sourceFile`, `typeName`, `line`, `column`, `fields`, `includeProperties`, `implementIEquatable`, `generateOperators`, `replaceExisting`, `useHashCodeCombine`, `callSuper`, `includeInheritedMembers` |
| `generate_tostring` | Generate a ToString() override for a type (`line` picks the type whose identifier or declaration span covers that 1-based line when several types share the name — identifier preferred, then smallest containing type, including nested types; omitted keeps today's typeName `FirstOrDefault` pick so two same-named types still succeed; a continuation-line identifier is eligible — do not require the declaration to start on `line`; `column` picks the type whose identifier or declaration span covers that 1-based column when set with `line` — identifier preferred, then smallest containing type; omitted keeps today's typeName + optional line pick so indented types still generate; `column` without `line` keeps today's first-match after the typeName filter rather than substituting each candidate's own start line; `format`: interpolated or stringbuilder; `includeProperties`: default true includes readable properties, false uses instance fields only unless `fields` names a property; `includeInheritedMembers`: default false keeps this-type-only collection, true also collects accessible instance fields/properties declared on base types; `replaceExisting`: replace an existing parameterless ToString instead of failing; `callSuper`: default false keeps member-only ToString, true folds the immediate base type's ToString into the generated override). | `sourceFile`, `typeName`, `line`, `column`, `fields`, `includeProperties`, `format`, `includeInheritedMembers`, `replaceExisting`, `callSuper` |
| `format_document` | Format a C# file using Roslyn's built-in formatter. | `sourceFile` |
| `add_null_checks` | Add null-check statements (ArgumentNullException.ThrowIfNull or guard clauses) for method or constructor parameters (`column` picks the smallest method or constructor whose identifier or declaration span covers that column; omitted keeps today's methodName + optional line start-line pick so a continuation-line identifier still needs `column` when `line` is the identifier line of a split signature). | `sourceFile`, `methodName`, `line`, `column`, `style` |

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
3. Check that `solutionPath` is an absolute path to a valid `.sln`, `.slnx`, or `.csproj` file

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
dotnet test tests/RoslynMcp.Cli.Tests
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
