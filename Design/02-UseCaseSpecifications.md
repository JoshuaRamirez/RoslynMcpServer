# Use Case Specifications - Roslyn MCP Move Server

## UC-1: Move Type to File

### Overview
| Property | Value |
|----------|-------|
| ID | UC-1 |
| Name | Move Type to File |
| Actor | AI Agent (via MCP) |
| Priority | MVP - Must Have |
| Complexity | High |

### Description
Relocate a top-level type declaration from its current source file to a specified target file, updating all references to maintain compilability.

---

### Preconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-1.1 | Workspace is loaded and in Ready state | WorkspaceState == Ready |
| PRE-1.2 | Source file exists and is part of loaded solution | Document exists in workspace |
| PRE-1.3 | Symbol exists at specified location | Symbol resolvable via Roslyn |
| PRE-1.4 | Symbol is a moveable type (class/struct/interface/enum/record/delegate) | INamedTypeSymbol with correct TypeKind |
| PRE-1.5 | Symbol is a top-level type (not nested) | ContainingType == null |
| PRE-1.6 | Target file path is a valid C# file path | Ends with .cs, valid path chars |
| PRE-1.7 | No other refactoring operation in progress | No active OperationState |

---

### Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-1.1 | Type declaration exists in target file | Parse target, find type |
| POST-1.2 | Type declaration removed from source file | Parse source, type absent |
| POST-1.3 | All references updated with correct using directives | Compilation succeeds |
| POST-1.4 | Source file either deleted (if empty) or contains remaining declarations | File state matches expectation |
| POST-1.5 | Target file created if did not exist | File exists on filesystem |
| POST-1.6 | Solution still compiles | Zero compilation errors |

---

### Main Success Scenario

| Step | Actor | System | State |
|------|-------|--------|-------|
| 1 | Agent sends move_type_to_file request | - | Pending |
| 2 | - | Validate input parameters | Validating |
| 3 | - | Resolve symbol from location | Resolving |
| 4 | - | Find all references to symbol | Resolving |
| 5 | - | Compute document changes | Computing |
| 6 | - | Apply changes to workspace | Applying |
| 7 | - | Persist changes to filesystem | Committing |
| 8 | - | Return success response | Completed |

---

### Alternative Flows

#### AF-1.1: Target File Exists with Content
**Trigger**: Target file already exists
**Steps**:
1. Parse existing target file
2. Determine insertion point (after last using, before first type, or after last type)
3. Insert type declaration preserving file structure
4. Add any required using directives
5. Continue from Main Step 6

#### AF-1.2: Source File Contains Multiple Types
**Trigger**: Source file has other type declarations
**Steps**:
1. Extract only the target type declaration
2. Preserve other declarations in source file
3. Keep source file using directives that are still needed
4. Remove unused using directives
5. Continue from Main Step 6

#### AF-1.3: Type Has Associated Types (Nested)
**Trigger**: Type contains nested types
**Steps**:
1. Move the entire type including all nested types
2. Nested type references remain internal (no update needed)
3. Continue from Main Step 6

#### AF-1.4: Preview Mode Requested
**Trigger**: preview parameter is true
**Steps**:
1. Complete steps 1-5 (compute changes)
2. Return computed changes without applying
3. State remains Ready (no Committing)

---

### Exception Flows

#### EF-1.1: Symbol Not Found
**Trigger**: Cannot resolve symbol at given location
**Response**: Error SYMBOL_NOT_FOUND
**Recovery**: Return error, no state change

#### EF-1.2: Symbol Not Moveable
**Trigger**: Symbol is method, property, field, or nested type
**Response**: Error SYMBOL_NOT_MOVEABLE
**Recovery**: Return error with explanation of valid symbol types

#### EF-1.3: Invalid Target Path
**Trigger**: Target path is malformed or outside solution
**Response**: Error INVALID_TARGET_PATH
**Recovery**: Return error with path validation details

#### EF-1.4: Target Would Create Conflict
**Trigger**: Target file already contains type with same name in same namespace
**Response**: Error NAME_CONFLICT
**Recovery**: Return error suggesting rename or different target

#### EF-1.5: Filesystem Write Failure
**Trigger**: Cannot write to target file (permissions, disk full)
**Response**: Error FILESYSTEM_ERROR
**Recovery**: Rollback workspace changes, return error

#### EF-1.6: Workspace Busy
**Trigger**: Another operation in progress
**Response**: Error WORKSPACE_BUSY
**Recovery**: Return error, suggest retry

---

### Business Rules

| ID | Rule | Rationale |
|----|------|-----------|
| BR-1.1 | Type retains original namespace unless namespace move also requested | Semantic preservation |
| BR-1.2 | Using directives in target file updated to include types namespace | Compilability |
| BR-1.3 | Reference files receive using directive for new location | Compilability |
| BR-1.4 | Empty source files are deleted | Cleanup |
| BR-1.5 | File header comments are not moved | Typically file-specific |
| BR-1.6 | XML documentation on type is moved with type | Documentation preservation |
| BR-1.7 | Attributes on type are moved with type | Semantic preservation |

---
---

## UC-2: Move Type to Namespace

### Overview
| Property | Value |
|----------|-------|
| ID | UC-2 |
| Name | Move Type to Namespace |
| Actor | AI Agent (via MCP) |
| Priority | MVP - Must Have |
| Complexity | High |

### Description
Change the namespace declaration of a type, updating all references across the solution to use the new fully qualified name.

---

### Preconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-2.1 | Workspace is loaded and in Ready state | WorkspaceState == Ready |
| PRE-2.2 | Source file exists and is part of loaded solution | Document exists in workspace |
| PRE-2.3 | Symbol exists at specified location | Symbol resolvable via Roslyn |
| PRE-2.4 | Symbol is a moveable type | INamedTypeSymbol with correct TypeKind |
| PRE-2.5 | Symbol is a top-level type | ContainingType == null |
| PRE-2.6 | Target namespace is a valid C# namespace identifier | Regex: ^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$ |
| PRE-2.7 | Target namespace differs from current namespace | newNamespace \!= currentNamespace |
| PRE-2.8 | No other refactoring operation in progress | No active OperationState |

---

### Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-2.1 | Type declaration has new namespace | Parse and verify namespace |
| POST-2.2 | All using directives updated in referencing files | Using statements reflect new namespace |
| POST-2.3 | Fully qualified references updated | FQN references use new namespace |
| POST-2.4 | Solution still compiles | Zero compilation errors |
| POST-2.5 | File location optionally updated to match namespace | If updateFileLocation=true |

---

### Main Success Scenario

| Step | Actor | System | State |
|------|-------|--------|-------|
| 1 | Agent sends move_type_to_namespace request | - | Pending |
| 2 | - | Validate input parameters | Validating |
| 3 | - | Resolve symbol from location | Resolving |
| 4 | - | Find all references to symbol | Resolving |
| 5 | - | Compute namespace changes | Computing |
| 6 | - | Compute using directive changes | Computing |
| 7 | - | Apply changes to workspace | Applying |
| 8 | - | Persist changes to filesystem | Committing |
| 9 | - | Return success response | Completed |

---

### Alternative Flows

#### AF-2.1: Update File Location Requested
**Trigger**: updateFileLocation parameter is true
**Steps**:
1. Compute new file path based on namespace (e.g., MyApp.Services -> MyApp/Services/)
2. Create directory structure if needed
3. Move type to new file (combines with UC-1)
4. Continue from Main Step 7

#### AF-2.2: File-Scoped Namespace
**Trigger**: Source file uses file-scoped namespace syntax
**Steps**:
1. Update file-scoped namespace declaration
2. Maintain file-scoped style (do not convert to block)
3. Continue from Main Step 6

#### AF-2.3: Multiple Types in File Share Namespace
**Trigger**: Multiple types in file, all in same namespace
**Steps**:
1. Update only the target type namespace
2. This may require extracting type to new file first
3. Or update entire file namespace (user choice)
4. Continue from Main Step 7

#### AF-2.4: Namespace Already Exists Elsewhere
**Trigger**: Target namespace already used by other types
**Steps**:
1. Proceed normally - namespaces can contain multiple types
2. Verify no name collision in target namespace
3. Continue from Main Step 7

---

### Exception Flows

#### EF-2.1: Symbol Not Found
**Trigger**: Cannot resolve symbol at given location
**Response**: Error SYMBOL_NOT_FOUND
**Recovery**: Return error, no state change

#### EF-2.2: Invalid Namespace
**Trigger**: Target namespace contains invalid characters
**Response**: Error INVALID_NAMESPACE
**Recovery**: Return error with valid namespace format

#### EF-2.3: Name Collision
**Trigger**: Type with same name exists in target namespace
**Response**: Error NAME_COLLISION
**Recovery**: Return error suggesting rename

#### EF-2.4: Circular Reference Would Result
**Trigger**: Namespace move would create circular project reference
**Response**: Error CIRCULAR_REFERENCE
**Recovery**: Return error explaining the dependency issue

#### EF-2.5: Same Namespace
**Trigger**: Target namespace equals current namespace
**Response**: Error SAME_NAMESPACE
**Recovery**: Return error, operation is no-op

---

### Business Rules

| ID | Rule | Rationale |
|----|------|-----------|
| BR-2.1 | Namespace change affects only the target type, not file peers | Precision |
| BR-2.2 | Using directives added only where needed (not already imported) | Minimal changes |
| BR-2.3 | Fully qualified names in code updated to new namespace | Semantic correctness |
| BR-2.4 | Extern alias references handled correctly | Rare but important |
| BR-2.5 | Global using directives considered | Modern C# support |
| BR-2.6 | Implicit usings considered | SDK-style project support |

---
---

## UC-3: Diagnose Environment

### Overview
| Property | Value |
|----------|-------|
| ID | UC-3 |
| Name | Diagnose Environment |
| Actor | AI Agent (via MCP) |
| Priority | MVP - Must Have |
| Complexity | Low |

### Description
Verify the MCP server environment is correctly configured with required Roslyn dependencies and can successfully load and analyze .NET solutions.

---

### Preconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-3.1 | MCP server is running | Server process active |
| PRE-3.2 | Request is valid MCP format | JSON schema validates |

---

### Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-3.1 | Diagnostic report returned | Response contains all diagnostic fields |
| POST-3.2 | No side effects | No workspace state changes |

---

### Main Success Scenario

| Step | Actor | System | State |
|------|-------|--------|-------|
| 1 | Agent sends diagnose request | - | - |
| 2 | - | Check Roslyn assembly availability | - |
| 3 | - | Check MSBuild locator status | - |
| 4 | - | Check .NET SDK availability | - |
| 5 | - | Report current workspace state | - |
| 6 | - | Report loaded solution info (if any) | - |
| 7 | - | Return diagnostic report | - |

---

### Alternative Flows

#### AF-3.1: Solution Path Provided
**Trigger**: solutionPath parameter included
**Steps**:
1. Attempt to load specified solution
2. Report load success/failure
3. Report project count, document count
4. Report any load diagnostics (warnings/errors)
5. Return enhanced diagnostic report

#### AF-3.2: Verbose Mode
**Trigger**: verbose parameter is true
**Steps**:
1. Include assembly version information
2. Include full MSBuild path
3. Include environment variables
4. Include memory usage statistics
5. Return verbose diagnostic report

---

### Exception Flows

#### EF-3.1: Roslyn Not Available
**Trigger**: Required Roslyn assemblies not loadable
**Response**: Partial diagnostic with error flag
**Recovery**: Report which assemblies missing

#### EF-3.2: MSBuild Not Found
**Trigger**: MSBuild locator cannot find MSBuild
**Response**: Partial diagnostic with error flag
**Recovery**: Report MSBuild search paths tried

#### EF-3.3: Solution Load Failure
**Trigger**: Provided solution path cannot be loaded
**Response**: Diagnostic with solution load errors
**Recovery**: Report specific load failures (missing projects, etc.)

---

### Business Rules

| ID | Rule | Rationale |
|----|------|-----------|
| BR-3.1 | Diagnose never modifies state | Read-only operation |
| BR-3.2 | Diagnose always returns (never throws to caller) | Reliability for debugging |
| BR-3.3 | Sensitive paths may be redacted in response | Security |
| BR-3.4 | Solution load for diagnosis is temporary | No persistent workspace change |

---
---

## Use Case Interaction Matrix

| Use Case | Depends On | Triggers |
|----------|------------|----------|
| UC-1 Move to File | UC-3 (workspace loaded) | None |
| UC-2 Move to Namespace | UC-3 (workspace loaded) | Optionally UC-1 |
| UC-3 Diagnose | None | None |

---

## State Transitions by Use Case

### UC-1 and UC-2 (Refactoring Operations)
```
Ready
  |
  v (request received)
Validating
  |
  +---> Failed (validation error)
  |
  v (validation passed)
Resolving
  |
  +---> Failed (symbol not found)
  |
  v (symbol resolved)
Computing
  |
  +---> Failed (computation error)
  |
  v (changes computed)
Applying
  |
  +---> Failed (apply error)
  |
  v (changes applied)
Committing
  |
  +---> Failed (filesystem error)
  |
  v (persisted)
Completed --> Ready
```

### UC-3 (Diagnose)
```
Any State
  |
  v (request received)
Diagnostic Execution (no state change)
  |
  v
Response Returned (state unchanged)
```
