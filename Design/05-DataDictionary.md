# Data Dictionary - Roslyn MCP Move Server

## 1. Input Parameters

### 1.1 move_type_to_file Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sourceFile | string | Yes | - | Path to file containing the type |
| symbolName | string | Yes | - | Name of type to move |
| line | integer | No | - | Line number for disambiguation |
| targetFile | string | Yes | - | Destination file path |
| createTargetFile | boolean | No | true | Create file if not exists |
| preview | boolean | No | false | Compute without applying |

### 1.2 move_type_to_namespace Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sourceFile | string | Yes | - | Path to file containing the type |
| symbolName | string | Yes | - | Name of type to move |
| line | integer | No | - | Line number for disambiguation |
| targetNamespace | string | Yes | - | New namespace |
| updateFileLocation | boolean | No | false | Move file to match namespace |
| preview | boolean | No | false | Compute without applying |

### 1.3 diagnose Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| solutionPath | string | No | - | Solution to test load |
| verbose | boolean | No | false | Include detailed info |
---

## 2. Response Fields

### 2.1 Success Response Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| success | boolean | No | Always true for success |
| operationId | string (GUID) | No | Unique operation identifier |
| changes.filesModified | string[] | No | Paths of modified files |
| changes.filesCreated | string[] | No | Paths of created files |
| changes.filesDeleted | string[] | No | Paths of deleted files |
| symbol.name | string | No | Simple name of moved type |
| symbol.fullyQualifiedName | string | No | Full name including namespace |
| symbol.kind | string | No | Type kind (Class, Struct, etc.) |
| referencesUpdated | integer | No | Count of references updated |
| executionTimeMs | integer | No | Operation duration |

### 2.2 Error Response Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| success | boolean | No | Always false for error |
| error.code | string | No | Machine-readable error code |
| error.message | string | No | Human-readable description |
| error.details | object | Yes | Context-specific info |
| error.suggestions | string[] | Yes | Possible fixes |

---

## 3. Enumerations

### 3.1 SymbolKind Values (Moveable)

| Value | Description |
|-------|-------------|
| Class | Class declaration |
| Struct | Struct declaration |
| Interface | Interface declaration |
| Enum | Enum declaration |
| Record | Record declaration |
| Delegate | Delegate declaration |

### 3.2 ChangeType Values

| Value | Description |
|-------|-------------|
| Create | New file created |
| Modify | Existing file modified |
| Delete | File deleted (was emptied) |

### 3.3 WorkspaceState Values

| Value | Description |
|-------|-------------|
| Unloaded | No solution loaded |
| Loading | Solution load in progress |
| Ready | Available for operations |
| Operating | Operation in progress |
| Error | In error state |
| Disposed | Shutting down |

---

## 4. Error Codes Reference

### 4.1 Input Validation Errors (1xxx)

| Code | Constant | Message |
|------|----------|---------|
| 1001 | INVALID_SOURCE_PATH | Source file path is invalid |
| 1002 | INVALID_TARGET_PATH | Target file path is invalid |
| 1003 | INVALID_SYMBOL_NAME | Symbol name is invalid |
| 1004 | INVALID_NAMESPACE | Namespace format is invalid |
| 1005 | MISSING_REQUIRED_PARAM | Required parameter missing |
| 1006 | INVALID_LINE_NUMBER | Line number must be positive |

### 4.2 Resource Errors (2xxx)

| Code | Constant | Message |
|------|----------|---------|
| 2001 | SOURCE_FILE_NOT_FOUND | Source file does not exist |
| 2002 | SOURCE_NOT_IN_WORKSPACE | Source file not in solution |
| 2003 | SYMBOL_NOT_FOUND | No symbol found |
| 2004 | SYMBOL_AMBIGUOUS | Multiple symbols match |
| 2005 | WORKSPACE_NOT_LOADED | No solution loaded |

### 4.3 Semantic Errors (3xxx)

| Code | Constant | Message |
|------|----------|---------|
| 3001 | SYMBOL_NOT_MOVEABLE | Cannot move this symbol type |
| 3002 | SYMBOL_IS_NESTED | Nested type cannot move alone |
| 3003 | NAME_COLLISION | Type exists in target |
| 3004 | SAME_LOCATION | Source equals target |
| 3005 | CIRCULAR_REFERENCE | Would create circular ref |
| 3006 | BREAKS_ACCESSIBILITY | Would break accessibility |

### 4.4 System Errors (4xxx)

| Code | Constant | Message |
|------|----------|---------|
| 4001 | WORKSPACE_BUSY | Operation in progress |
| 4002 | FILESYSTEM_ERROR | Filesystem error |
| 4003 | ROSLYN_ERROR | Roslyn error |
| 4004 | COMPILATION_ERROR | Would break compilation |
| 4005 | TIMEOUT | Operation timed out |

### 4.5 Environment Errors (5xxx)

| Code | Constant | Message |
|------|----------|---------|
| 5001 | MSBUILD_NOT_FOUND | MSBuild not located |
| 5002 | ROSLYN_NOT_AVAILABLE | Roslyn not loadable |
| 5003 | SDK_NOT_FOUND | .NET SDK not found |
| 5004 | SOLUTION_LOAD_FAILED | Failed to load solution |

---

## 5. Type Mappings

### 5.1 JSON to C# Type Mapping

| JSON Type | C# Type | Notes |
|-----------|---------|-------|
| string | string | UTF-8 |
| integer | int | 32-bit signed |
| boolean | bool | true/false |
| string[] | List<string> | Array |
| object | class/record | Structured |
| null | null/default | Nullable |

### 5.2 Roslyn to Response Mapping

| Roslyn Type | Response Field | Transform |
|-------------|----------------|-----------|
| INamedTypeSymbol.Name | symbol.name | Direct |
| ISymbol.ToDisplayString() | symbol.fullyQualifiedName | Format |
| TypeKind | symbol.kind | Enum to string |
| Location.GetLineSpan() | line/column | 0-based to 1-based |
| TextChange | diff | Unified diff format |
