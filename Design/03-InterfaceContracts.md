# Interface Contracts - Roslyn MCP Move Server

## 1. MCP Tool Definitions

### 1.1 Tool: move_type_to_file

**Purpose**: Relocate a type declaration to a specified file.

#### Input Schema (JSON Schema)

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["sourceFile", "symbolName", "targetFile"],
  "properties": {
    "sourceFile": {
      "type": "string",
      "description": "Absolute path to the source file containing the type",
      "pattern": "^[A-Za-z]:\\\\.*\\.cs$|^/.*\\.cs$"
    },
    "symbolName": {
      "type": "string",
      "description": "Name of the type to move (simple name or fully qualified)",
      "minLength": 1,
      "pattern": "^[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)*$"
    },
    "line": {
      "type": "integer",
      "description": "1-based line number where symbol is declared (for disambiguation)",
      "minimum": 1
    },
    "targetFile": {
      "type": "string",
      "description": "Absolute path to the target file",
      "pattern": "^[A-Za-z]:\\\\.*\\.cs$|^/.*\\.cs$"
    },
    "createTargetFile": {
      "type": "boolean",
      "description": "Create target file if it does not exist",
      "default": true
    },
    "preview": {
      "type": "boolean",
      "description": "Return computed changes without applying",
      "default": false
    }
  },
  "additionalProperties": false
}
```

#### Success Response

```json
{
  "success": true,
  "operationId": "guid-string",
  "changes": {
    "filesModified": ["C:\\path\\to\\Source.cs", "C:\\path\\to\\Consumer.cs"],
    "filesCreated": ["C:\\path\\to\\Target.cs"],
    "filesDeleted": []
  },
  "symbol": {
    "name": "MyClass",
    "fullyQualifiedName": "MyNamespace.MyClass",
    "kind": "Class",
    "previousLocation": {
      "file": "C:\\path\\to\\Source.cs",
      "line": 10,
      "column": 5
    },
    "newLocation": {
      "file": "C:\\path\\to\\Target.cs",
      "line": 8,
      "column": 5
    }
  },
  "referencesUpdated": 15,
  "executionTimeMs": 1250
}
```

#### Preview Response

```json
{
  "success": true,
  "preview": true,
  "operationId": "guid-string",
  "pendingChanges": [
    {
      "file": "C:\\path\\to\\Source.cs",
      "changeType": "Modify",
      "description": "Remove MyClass declaration",
      "diff": "--- a/Source.cs\n+++ b/Source.cs\n@@ -10,20 +10,0 @@\n-public class MyClass..."
    },
    {
      "file": "C:\\path\\to\\Target.cs",
      "changeType": "Create",
      "description": "Add MyClass declaration",
      "content": "namespace MyNamespace;\n\npublic class MyClass..."
    }
  ]
}
```

---

### 1.2 Tool: move_type_to_namespace

**Purpose**: Change the namespace of a type declaration.

#### Input Schema (JSON Schema)

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["sourceFile", "symbolName", "targetNamespace"],
  "properties": {
    "sourceFile": {
      "type": "string",
      "description": "Absolute path to the source file containing the type",
      "pattern": "^[A-Za-z]:\\\\.*\\.cs$|^/.*\\.cs$"
    },
    "symbolName": {
      "type": "string",
      "description": "Name of the type to move",
      "minLength": 1,
      "pattern": "^[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)*$"
    },
    "line": {
      "type": "integer",
      "description": "1-based line number where symbol is declared",
      "minimum": 1
    },
    "targetNamespace": {
      "type": "string",
      "description": "Target namespace (e.g., MyApp.Services)",
      "pattern": "^[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)*$"
    },
    "updateFileLocation": {
      "type": "boolean",
      "description": "Also move file to match namespace folder structure",
      "default": false
    },
    "preview": {
      "type": "boolean",
      "description": "Return computed changes without applying",
      "default": false
    }
  },
  "additionalProperties": false
}
```

#### Success Response

```json
{
  "success": true,
  "operationId": "guid-string",
  "changes": {
    "filesModified": ["C:\\path\\Source.cs", "C:\\path\\Consumer1.cs", "C:\\path\\Consumer2.cs"],
    "filesCreated": [],
    "filesDeleted": []
  },
  "symbol": {
    "name": "MyClass",
    "previousNamespace": "OldNamespace",
    "newNamespace": "NewNamespace",
    "fullyQualifiedName": "NewNamespace.MyClass"
  },
  "referencesUpdated": 23,
  "usingDirectivesAdded": 5,
  "usingDirectivesRemoved": 0,
  "executionTimeMs": 980
}
```

---

### 1.3 Tool: diagnose

**Purpose**: Check environment health and workspace status.

#### Input Schema (JSON Schema)

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "properties": {
    "solutionPath": {
      "type": "string",
      "description": "Optional: solution to test loading"
    },
    "verbose": {
      "type": "boolean",
      "description": "Include detailed diagnostic information",
      "default": false
    }
  },
  "additionalProperties": false
}
```

#### Response

```json
{
  "healthy": true,
  "components": {
    "roslynAvailable": true,
    "roslynVersion": "4.8.0",
    "msbuildFound": true,
    "msbuildVersion": "17.8.0",
    "dotnetSdkAvailable": true,
    "dotnetSdkVersion": "8.0.100"
  },
  "workspace": {
    "state": "Ready",
    "solutionLoaded": true,
    "solutionPath": "C:\\path\\to\\Solution.sln",
    "projectCount": 5,
    "documentCount": 127
  },
  "capabilities": [
    "move_type_to_file",
    "move_type_to_namespace",
    "diagnose"
  ],
  "errors": [],
  "warnings": []
}
```

#### Unhealthy Response Example

```json
{
  "healthy": false,
  "components": {
    "roslynAvailable": true,
    "roslynVersion": "4.8.0",
    "msbuildFound": false,
    "msbuildVersion": null,
    "dotnetSdkAvailable": true,
    "dotnetSdkVersion": "8.0.100"
  },
  "workspace": {
    "state": "Error",
    "solutionLoaded": false
  },
  "capabilities": [],
  "errors": [
    {
      "code": "MSBUILD_NOT_FOUND",
      "message": "MSBuild could not be located. Install Visual Studio or Build Tools.",
      "searchPaths": [
        "C:\\Program Files\\Microsoft Visual Studio\\2022",
        "C:\\Program Files (x86)\\Microsoft Visual Studio\\2022"
      ]
    }
  ],
  "warnings": []
}
```

---

## 2. Error Response Taxonomy

### 2.1 Error Response Schema

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["success", "error"],
  "properties": {
    "success": {
      "type": "boolean",
      "const": false
    },
    "error": {
      "type": "object",
      "required": ["code", "message"],
      "properties": {
        "code": {
          "type": "string",
          "description": "Machine-readable error code"
        },
        "message": {
          "type": "string",
          "description": "Human-readable error description"
        },
        "details": {
          "type": "object",
          "description": "Additional context-specific information"
        },
        "suggestions": {
          "type": "array",
          "items": { "type": "string" },
          "description": "Possible remediation actions"
        }
      }
    }
  }
}
```

### 2.2 Error Code Catalog

#### Input Validation Errors (1xxx)

| Code | Name | Description | HTTP Analog |
|------|------|-------------|-------------|
| 1001 | INVALID_SOURCE_PATH | Source file path is malformed | 400 |
| 1002 | INVALID_TARGET_PATH | Target file path is malformed | 400 |
| 1003 | INVALID_SYMBOL_NAME | Symbol name contains invalid characters | 400 |
| 1004 | INVALID_NAMESPACE | Namespace format is invalid | 400 |
| 1005 | MISSING_REQUIRED_PARAM | Required parameter not provided | 400 |
| 1006 | INVALID_LINE_NUMBER | Line number out of valid range | 400 |

#### Resource Errors (2xxx)

| Code | Name | Description | HTTP Analog |
|------|------|-------------|-------------|
| 2001 | SOURCE_FILE_NOT_FOUND | Source file does not exist | 404 |
| 2002 | SOURCE_NOT_IN_WORKSPACE | Source file not part of loaded solution | 404 |
| 2003 | SYMBOL_NOT_FOUND | No symbol found at specified location | 404 |
| 2004 | SYMBOL_AMBIGUOUS | Multiple symbols match; provide line number | 409 |
| 2005 | WORKSPACE_NOT_LOADED | No solution currently loaded | 412 |

#### Semantic Errors (3xxx)

| Code | Name | Description | HTTP Analog |
|------|------|-------------|-------------|
| 3001 | SYMBOL_NOT_MOVEABLE | Symbol type cannot be moved (method, field, etc.) | 422 |
| 3002 | SYMBOL_IS_NESTED | Nested types cannot be moved independently | 422 |
| 3003 | NAME_COLLISION | Target location already has type with same name | 409 |
| 3004 | SAME_LOCATION | Source and target are the same | 422 |
| 3005 | CIRCULAR_REFERENCE | Move would create circular dependency | 422 |
| 3006 | BREAKS_ACCESSIBILITY | Move would break accessibility constraints | 422 |

#### System Errors (4xxx)

| Code | Name | Description | HTTP Analog |
|------|------|-------------|-------------|
| 4001 | WORKSPACE_BUSY | Another operation in progress | 503 |
| 4002 | FILESYSTEM_ERROR | Cannot read/write files | 500 |
| 4003 | ROSLYN_ERROR | Roslyn operation failed unexpectedly | 500 |
| 4004 | COMPILATION_ERROR | Changes would break compilation | 500 |
| 4005 | TIMEOUT | Operation exceeded time limit | 504 |

#### Environment Errors (5xxx)

| Code | Name | Description | HTTP Analog |
|------|------|-------------|-------------|
| 5001 | MSBUILD_NOT_FOUND | MSBuild not available | 503 |
| 5002 | ROSLYN_NOT_AVAILABLE | Roslyn assemblies not loadable | 503 |
| 5003 | SDK_NOT_FOUND | .NET SDK not found | 503 |
| 5004 | SOLUTION_LOAD_FAILED | Could not load solution | 500 |

---

### 2.3 Error Response Examples

#### Example: Symbol Not Found

```json
{
  "success": false,
  "error": {
    "code": "SYMBOL_NOT_FOUND",
    "message": "No type symbol found at the specified location",
    "details": {
      "sourceFile": "C:\\project\\Services\\UserService.cs",
      "symbolName": "UserServce",
      "line": 15,
      "nearbySymbols": ["UserService", "UserServiceOptions"]
    },
    "suggestions": [
      "Check spelling of symbol name",
      "Verify the line number is correct",
      "Did you mean UserService?"
    ]
  }
}
```

#### Example: Symbol Not Moveable

```json
{
  "success": false,
  "error": {
    "code": "SYMBOL_NOT_MOVEABLE",
    "message": "Methods cannot be moved independently; only types are supported",
    "details": {
      "symbolName": "ProcessData",
      "symbolKind": "Method",
      "containingType": "DataProcessor",
      "supportedKinds": ["Class", "Struct", "Interface", "Enum", "Record", "Delegate"]
    },
    "suggestions": [
      "Move the containing type DataProcessor instead",
      "Use Extract Method refactoring in your IDE"
    ]
  }
}
```

#### Example: Name Collision

```json
{
  "success": false,
  "error": {
    "code": "NAME_COLLISION",
    "message": "Target namespace already contains a type named UserDto",
    "details": {
      "symbolName": "UserDto",
      "targetNamespace": "MyApp.Models",
      "existingTypeLocation": "C:\\project\\Models\\UserDto.cs"
    },
    "suggestions": [
      "Rename the type before moving",
      "Choose a different target namespace",
      "Merge the types if they represent the same concept"
    ]
  }
}
```

---

## 3. MCP Protocol Compliance

### 3.1 Tool Registration

```json
{
  "tools": [
    {
      "name": "move_type_to_file",
      "description": "Move a C# type declaration to a different file within the solution. Updates all references automatically.",
      "inputSchema": { "$ref": "#/definitions/MoveTypeToFileInput" }
    },
    {
      "name": "move_type_to_namespace",
      "description": "Change the namespace of a C# type. Updates all using directives and qualified references.",
      "inputSchema": { "$ref": "#/definitions/MoveTypeToNamespaceInput" }
    },
    {
      "name": "diagnose",
      "description": "Check the health of the Roslyn MCP server environment and workspace status.",
      "inputSchema": { "$ref": "#/definitions/DiagnoseInput" }
    }
  ]
}
```

### 3.2 MCP Message Format

#### Tool Call Request

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "move_type_to_file",
    "arguments": {
      "sourceFile": "C:\\project\\Services\\DataService.cs",
      "symbolName": "DataService",
      "targetFile": "C:\\project\\Services\\Data\\DataService.cs"
    }
  }
}
```

#### Tool Call Response (Success)

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "content": [
      {
        "type": "text",
        "text": "{ ... success response JSON ... }"
      }
    ],
    "isError": false
  }
}
```

#### Tool Call Response (Error)

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "content": [
      {
        "type": "text",
        "text": "{ ... error response JSON ... }"
      }
    ],
    "isError": true
  }
}
```

---

## 4. Behavioral Contracts

### 4.1 Idempotency

| Operation | Idempotent | Notes |
|-----------|------------|-------|
| move_type_to_file | No | Second call fails with SAME_LOCATION |
| move_type_to_namespace | No | Second call fails with SAME_NAMESPACE |
| diagnose | Yes | Read-only, always safe to call |

### 4.2 Atomicity

All refactoring operations are atomic:
- Either all changes apply successfully, or none apply
- On failure, workspace remains in previous valid state
- Filesystem changes are written only after all validations pass

### 4.3 Concurrency

- Only one refactoring operation may execute at a time per workspace
- Diagnose may run concurrently with other operations
- Concurrent refactoring requests receive WORKSPACE_BUSY error

### 4.4 Timeout Behavior

| Operation | Default Timeout | Max Timeout | Behavior on Timeout |
|-----------|-----------------|-------------|---------------------|
| move_type_to_file | 30s | 120s | Rollback, return TIMEOUT |
| move_type_to_namespace | 30s | 120s | Rollback, return TIMEOUT |
| diagnose | 10s | 30s | Return partial results |

### 4.5 Preview Mode Contract

When preview=true:
- All validation and computation steps execute
- Changes are NOT applied to workspace or filesystem
- Response includes detailed change descriptions
- No locks are held after response
- Subsequent non-preview call may yield different results if files changed
