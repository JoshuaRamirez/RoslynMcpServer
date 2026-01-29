# Roslyn MCP Server Expansion - Overview

## 1. Executive Summary

This document specifies requirements for expanding the Roslyn MCP Server from 3 operations (move_type_to_file, move_type_to_namespace, diagnose) to a comprehensive refactoring platform supporting 30+ operations across 9 categories.

---

## 2. Expansion Scope

### 2.1 Current State (MVP)
| Operation | Status | Priority |
|-----------|--------|----------|
| move_type_to_file | Implemented | MVP |
| move_type_to_namespace | Implemented | MVP |
| diagnose | Implemented | MVP |

### 2.2 Proposed Expansion Categories

| Category | Operation Count | Complexity | Phase |
|----------|-----------------|------------|-------|
| 1. Rename Operations | 8 | Medium | 1 |
| 2. Extract Operations | 5 | High | 1 |
| 3. Inline Operations | 3 | High | 2 |
| 4. Generate Operations | 5 | Medium | 1 |
| 5. Organize Imports | 3 | Low | 1 |
| 6. Change Signature | 4 | High | 2 |
| 7. Encapsulation | 1 | Low | 2 |
| 8. Convert Operations | 4 | Medium | 2 |
| 9. Pull/Push Members | 2 | High | 2 |

---

## 3. Priority Ranking by Developer Value

### 3.1 Tier 1 - Critical (High Impact, Frequent Use)

| Rank | Operation | Value Rationale |
|------|-----------|-----------------|
| 1 | rename_symbol | Most frequent refactoring; enables codebase evolution |
| 2 | extract_method | Core refactoring for code quality improvement |
| 3 | extract_interface | Enables interface-based design and testability |
| 4 | add_missing_usings | Essential for AI code generation workflows |
| 5 | generate_constructor | Accelerates class implementation |

### 3.2 Tier 2 - Important (Medium Impact, Regular Use)

| Rank | Operation | Value Rationale |
|------|-----------|-----------------|
| 6 | extract_variable | Improves readability and debuggability |
| 7 | inline_variable | Simplifies code when variable is trivial |
| 8 | remove_unused_usings | Code cleanliness |
| 9 | implement_interface | Accelerates interface implementation |
| 10 | generate_overrides | Accelerates inheritance implementation |

### 3.3 Tier 3 - Valuable (Lower Frequency)

| Rank | Operation | Value Rationale |
|------|-----------|-----------------|
| 11 | change_signature | Complex but high value for API evolution |
| 12 | extract_base_class | Architectural refactoring |
| 13 | convert_to_async | Modernization of legacy code |
| 14 | encapsulate_field | Improves encapsulation |
| 15 | pull_member_up | Inheritance hierarchy management |

### 3.4 Tier 4 - Specialized (Niche Use Cases)

| Rank | Operation | Value Rationale |
|------|-----------|-----------------|
| 16+ | inline_method | Rare but valuable |
| | rename_file_to_match_type | Consistency |
| | convert_foreach_to_linq | Style preference |
| | convert_anonymous_to_class | Edge case |
| | push_member_down | Rare refactoring |

---

## 4. Implementation Phases

### Phase 1: Foundation (Weeks 1-4)
- Rename Operations (all 8)
- Organize Imports (all 3)
- Generate: constructor, implement_interface

### Phase 2: Extraction (Weeks 5-8)
- Extract: method, interface, variable, constant
- Generate: overrides, method_stub

### Phase 3: Advanced (Weeks 9-12)
- Change Signature (all 4)
- Inline Operations (all 3)
- Extract: base_class

### Phase 4: Conversion & Hierarchy (Weeks 13-16)
- Convert Operations (all 4)
- Pull/Push Members (both)
- Encapsulate Field

---

## 5. Architectural Considerations

### 5.1 Shared Infrastructure

All new operations leverage existing infrastructure:
- MSBuildWorkspaceProvider for solution loading
- TypeSymbolResolver extended for additional symbol kinds
- ReferenceTracker for cross-solution reference updates
- AtomicFileWriter for transactional file operations

### 5.2 New Infrastructure Required

| Component | Purpose | Operations Using |
|-----------|---------|------------------|
| MethodSymbolResolver | Resolve method symbols | rename, extract_method, inline_method |
| MemberSymbolResolver | Resolve any member | rename, encapsulate, change_signature |
| SignatureAnalyzer | Parse method signatures | change_signature, extract_method |
| InheritanceAnalyzer | Analyze type hierarchy | extract_base, extract_interface, pull/push |
| ExpressionAnalyzer | Analyze expressions | extract_variable, inline_variable |
| StatementAnalyzer | Analyze statement blocks | extract_method |
| AsyncAnalyzer | Detect async patterns | convert_to_async |

### 5.3 Domain Model Extensions

Current RefactoringKind enum expands from 2 values to 30+:

```
RefactoringKind
├── Move
│   ├── MoveTypeToFile
│   └── MoveTypeToNamespace
├── Rename
│   ├── RenameSymbol
│   ├── RenameFile
│   └── RenameNamespace
├── Extract
│   ├── ExtractMethod
│   ├── ExtractInterface
│   ├── ExtractBaseClass
│   ├── ExtractVariable
│   └── ExtractConstant
├── Inline
│   ├── InlineMethod
│   ├── InlineVariable
│   └── InlineConstant
├── Generate
│   ├── GenerateConstructor
│   ├── GenerateMethodStub
│   ├── GenerateOverrides
│   └── ImplementInterface
├── Organize
│   ├── SortUsings
│   ├── RemoveUnusedUsings
│   └── AddMissingUsings
├── ChangeSignature
│   ├── AddParameter
│   ├── RemoveParameter
│   ├── ReorderParameters
│   └── ChangeReturnType
├── Encapsulate
│   └── EncapsulateField
├── Convert
│   ├── ConvertToAsync
│   ├── ConvertForeachToLinq
│   ├── ConvertAnonymousToClass
│   └── ConvertTupleToStruct
└── Hierarchy
    ├── PullMemberUp
    └── PushMemberDown
```

---

## 6. Error Code Expansion

Current error codes (1xxx-5xxx) require expansion. See dedicated document: 05-ErrorCodeExpansion.md

Summary of new ranges:
- 1xxx: Input Validation (expanded for new parameters)
- 2xxx: Resource (expanded for new symbol types)
- 3xxx: Semantic (major expansion for operation-specific errors)
- 4xxx: System (unchanged)
- 5xxx: Environment (unchanged)

---

## 7. Cross-Cutting Concerns

### 7.1 Preview Mode
All operations support preview: true parameter returning computed changes without applying.

### 7.2 Undo Support
Future consideration: All operations produce reversible DocumentChange sets enabling potential undo capability.

### 7.3 Telemetry
Each operation emits:
- Operation start/complete/fail events
- Duration metrics
- Reference count metrics
- File change metrics

### 7.4 Cancellation
All operations support CancellationToken for timeout and user-initiated cancellation.

---

## 8. Related Documents

| Document | Content |
|----------|---------|
| 02-UseCaseSpecifications-Rename.md | Rename operation use cases |
| 03-UseCaseSpecifications-Extract.md | Extract operation use cases |
| 04-UseCaseSpecifications-Other.md | Remaining operation use cases |
| 05-ErrorCodeExpansion.md | Extended error taxonomy |
| 06-InterfaceContracts-Expansion.md | MCP tool schemas for new operations |
| 07-ValidationRules-Expansion.md | Validation rules for new operations |
| 08-TestScenarioMatrix-Expansion.md | Test scenarios for new operations |
