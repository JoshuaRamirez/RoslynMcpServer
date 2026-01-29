# Domain Model - Roslyn MCP Move Server

## 1. Domain Overview

The Roslyn MCP Move Server operates in the **Code Refactoring** domain, specifically handling **symbol relocation operations** within .NET solutions. The domain bridges MCP protocol semantics with Roslyn workspace model.

---

## 2. Core Entities

### 2.1 Workspace (Aggregate Root)

**Definition**: The loaded representation of a .NET solution or project, providing semantic analysis capabilities.

| Attribute | Type | Description |
|-----------|------|-------------|
| WorkspaceId | Guid | Unique identifier for this workspace instance |
| SolutionPath | AbsolutePath | Path to .sln or .csproj file |
| State | WorkspaceState | Current lifecycle state |
| Projects | List of Project | Contained projects |
| LoadedAt | DateTimeOffset | When workspace was loaded |
| LastOperationAt | DateTimeOffset? | Last refactoring operation timestamp |

**Invariants**:
- SolutionPath must exist on filesystem
- State must be Ready before any refactoring operation
- Projects collection is immutable once loaded (Roslyn immutability)

**Roslyn Mapping**: Microsoft.CodeAnalysis.Workspace -> AdhocWorkspace or MSBuildWorkspace

---

### 2.2 Project

**Definition**: A compilation unit within a workspace, representing a .csproj.

| Attribute | Type | Description |
|-----------|------|-------------|
| ProjectId | ProjectId | Roslyn project identifier |
| Name | string | Project name |
| FilePath | AbsolutePath | Path to .csproj |
| Documents | List of Document | Source files in project |
| References | List of ProjectReference | Inter-project references |
| TargetFramework | string | e.g., net8.0 |

**Roslyn Mapping**: Microsoft.CodeAnalysis.Project

---

### 2.3 Document

**Definition**: A source code file within a project.

| Attribute | Type | Description |
|-----------|------|-------------|
| DocumentId | DocumentId | Roslyn document identifier |
| Name | string | File name (e.g., Foo.cs) |
| FilePath | AbsolutePath | Full path to file |
| SyntaxTree | SyntaxTree | Parsed syntax (lazy) |
| SemanticModel | SemanticModel | Type/symbol info (lazy) |

**Roslyn Mapping**: Microsoft.CodeAnalysis.Document

---

### 2.4 Symbol

**Definition**: A named program element (type, method, property, etc.) that can be referenced and potentially moved.

| Attribute | Type | Description |
|-----------|------|-------------|
| SymbolId | SymbolKey | Stable identifier across compilations |
| Name | string | Simple name |
| FullyQualifiedName | string | Namespace + name |
| Kind | SymbolKind | Type, Method, Property, etc. |
| ContainingNamespace | Namespace | Current namespace |
| ContainingDocument | Document | Current source file |
| Accessibility | Accessibility | public, internal, etc. |

**Roslyn Mapping**: Microsoft.CodeAnalysis.ISymbol and subtypes (INamedTypeSymbol, etc.)

---

### 2.5 Reference

**Definition**: A usage of a symbol from another location in code.

| Attribute | Type | Description |
|-----------|------|-------------|
| ReferenceLocation | Location | Where the reference occurs |
| ReferencingDocument | Document | Document containing reference |
| ReferencedSymbol | Symbol | Symbol being referenced |
| IsImplicit | bool | Compiler-generated reference |

**Roslyn Mapping**: Microsoft.CodeAnalysis.FindSymbols.ReferencedSymbol

---

### 2.6 RefactoringOperation (Aggregate Root)

**Definition**: A single atomic refactoring request with its execution context.

| Attribute | Type | Description |
|-----------|------|-------------|
| OperationId | Guid | Unique operation identifier |
| Kind | RefactoringKind | MoveToFile, MoveToNamespace |
| TargetSymbol | Symbol | Symbol being moved |
| Destination | Destination | Where symbol moves to |
| State | OperationState | Current execution state |
| Changes | List of DocumentChange | Computed changes |
| ValidationResult | ValidationResult | Pre-execution validation |
| ExecutionResult | ExecutionResult? | Post-execution result |

**Invariants**:
- TargetSymbol must be moveable (top-level type for MVP)
- Destination must be valid for the operation kind
- Changes are computed only after successful validation

---

### 2.7 DocumentChange

**Definition**: A modification to be applied to a single document.

| Attribute | Type | Description |
|-----------|------|-------------|
| DocumentId | DocumentId | Target document |
| ChangeKind | ChangeKind | Create, Modify, Delete |
| OldText | SourceText? | Original content (for Modify) |
| NewText | SourceText | New content |
| TextChanges | List of TextChange | Granular edits |

**Roslyn Mapping**: Microsoft.CodeAnalysis.Text.TextChange

---

### 2.8 Destination (Value Object)

**Definition**: The target location for a move operation.

**Variants**:
- FileDestination: FilePath (AbsolutePath), CreateIfNotExists (bool)
- NamespaceDestination: TargetNamespace (string), UpdateFileLocation (bool)

---

## 3. Value Objects

### 3.1 AbsolutePath
| Property | Type | Constraint |
|----------|------|------------|
| Value | string | Must be rooted, normalized |

**Behavior**: Immutable, case-insensitive comparison on Windows.

### 3.2 SymbolLocation
| Property | Type | Description |
|----------|------|-------------|
| FilePath | AbsolutePath | Source file |
| Line | int | 1-based line number |
| Column | int | 1-based column |
| Span | TextSpan | Character span |

### 3.3 ValidationResult
| Property | Type | Description |
|----------|------|-------------|
| IsValid | bool | Overall validity |
| Errors | List of ValidationError | Blocking issues |
| Warnings | List of ValidationWarning | Non-blocking issues |

### 3.4 ExecutionResult
| Property | Type | Description |
|----------|------|-------------|
| Success | bool | Operation succeeded |
| FilesModified | List of AbsolutePath | Changed files |
| FilesCreated | List of AbsolutePath | New files |
| Error | ErrorInfo? | Failure details |

---

## 4. Enumerations

### 4.1 WorkspaceState
- Unloaded: Initial state, no solution loaded
- Loading: Solution load in progress
- Ready: Loaded, available for operations
- Operating: Refactoring in progress (locked)
- Error: Load or operation failed
- Disposed: Workspace released

### 4.2 OperationState
- Pending: Created, not yet started
- Validating: Input/semantic validation in progress
- Resolving: Finding references
- Computing: Calculating changes
- Previewing: Changes computed, awaiting confirmation
- Applying: Writing changes to workspace
- Committing: Persisting to filesystem
- Completed: Successfully finished
- Failed: Terminated with error
- Cancelled: User-cancelled

### 4.3 RefactoringKind
- MoveTypeToFile
- MoveTypeToNamespace

### 4.4 SymbolKind (Moveable subset for MVP)
- Class
- Struct
- Interface
- Enum
- Record
- Delegate

### 4.5 ChangeKind
- Create: New file
- Modify: Existing file changed
- Delete: File removed (empty after extraction)

---

## 5. Entity Relationships

```
WORKSPACE AGGREGATE
===================

Solution (1) -------- (*) Project
                            |
                            | (1)
                            v (*)
                        Document
                            |
                            | contains
                            v (*)
                         Symbol
                            |
                            | referenced by
                            v (*)
                        Reference

REFACTORING OPERATION AGGREGATE
================================

RefactoringOp (1) ---- targets ---- (1) Symbol
      |
      | (1)
      v (*)
DocumentChange
```

---

## 6. Domain-to-Roslyn Type Mapping

| Domain Concept | Roslyn Type | Notes |
|----------------|-------------|-------|
| Workspace | MSBuildWorkspace | For .sln/.csproj loading |
| Project | Project | Immutable snapshot |
| Document | Document | Immutable snapshot |
| Symbol | INamedTypeSymbol | For types being moved |
| Reference | ReferencedSymbol | From SymbolFinder |
| Change | DocumentEditor | For mutations |
| SyntaxNode | TypeDeclarationSyntax | Class/struct/etc. |

---

## 7. Bounded Context Integration

```
MCP PROTOCOL CONTEXT
--------------------
MCP Request --> MCP Tool Handler --> MCP Response
                      |
                      | translates to/from
                      v
REFACTORING DOMAIN CONTEXT
--------------------------
RefactoringOperation <-- RefactoringOrchestrator
         |
         | orchestrates
         v
ROSLYN WORKSPACE CONTEXT
------------------------
MSBuildWorkspace, Project, SemanticModel, SymbolFinder
```

---

## 8. Aggregate Boundaries

### Workspace Aggregate
- **Root**: Workspace
- **Contains**: Projects, Documents (via Roslyn immutable model)
- **Consistency**: Roslyn handles internally via immutable snapshots

### RefactoringOperation Aggregate
- **Root**: RefactoringOperation
- **Contains**: DocumentChanges, ValidationResult, ExecutionResult
- **Consistency**: Single operation is atomic; all changes apply or none

---

## 9. Domain Events (For Future Extensibility)

| Event | Trigger | Payload |
|-------|---------|---------|
| WorkspaceLoaded | Solution load completes | WorkspaceId, ProjectCount |
| RefactoringStarted | Operation begins | OperationId, Kind, TargetSymbol |
| RefactoringCompleted | Operation succeeds | OperationId, FilesChanged |
| RefactoringFailed | Operation fails | OperationId, ErrorCode, Message |
| WorkspaceDisposed | Cleanup | WorkspaceId |

---

## 10. Domain Invariants (Enforced)

1. **Single Active Operation**: Only one RefactoringOperation may be in Operating state per Workspace.
2. **Moveable Symbols Only**: Only top-level type symbols (class, struct, interface, enum, record, delegate) can be move targets.
3. **Valid Destinations**: File destinations must be valid C# file paths; namespace destinations must be valid C# identifiers.
4. **Atomic Changes**: DocumentChanges are all-or-nothing; partial application is forbidden.
5. **No Self-Move**: A symbol cannot be moved to its current location.
6. **Reference Integrity**: All references must be updated to maintain compilability.
