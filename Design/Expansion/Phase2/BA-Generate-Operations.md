# Business Analysis - Generate Operations

## Document Overview

| Property | Value |
|----------|-------|
| Document ID | BA-GEN-001 |
| Version | 1.0 |
| Status | Draft |
| Operations | generate_overrides, implement_interface, generate_method_stub, generate_equals_hashcode |
| Error Code Range | 3060-3079 |

---

## 1. Operation: generate_overrides

### 1.1 Use Case Specification

| Property | Value |
|----------|-------|
| ID | UC-GEN-01 |
| Name | Generate Override Methods |
| Actor | AI Agent (via MCP) |
| Priority | Tier 2 - Important |
| Complexity | Medium |

**Description**: Generate override method implementations for virtual, abstract, or overridable members inherited from base classes.

---

#### Preconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-GO01 | Workspace is loaded and in Ready state | WorkspaceState == Ready |
| PRE-GO02 | Source file exists and is part of loaded solution | Document exists in workspace |
| PRE-GO03 | Type exists at specified location | INamedTypeSymbol resolvable |
| PRE-GO04 | Type has a base class with overridable members | BaseType != null && HasOverridableMembers |
| PRE-GO05 | Type is not static | !IsStatic |
| PRE-GO06 | Type is not sealed (for abstract members) | !IsSealed || members are virtual |
| PRE-GO07 | No other refactoring operation in progress | No active OperationState |

---

#### Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-GO01 | Override methods exist in target type | Parse type, find override members |
| POST-GO02 | Each override has correct signature | Signature matches base |
| POST-GO03 | Each override has override modifier | HasModifier(override) |
| POST-GO04 | Base calls included if callBase=true | MethodBody contains base.Method() |
| POST-GO05 | Solution still compiles | Zero compilation errors |

---

#### Main Success Scenario

| Step | Actor | System | State |
|------|-------|--------|-------|
| 1 | Agent sends generate_overrides request | - | Pending |
| 2 | - | Validate input parameters | Validating |
| 3 | - | Resolve type symbol from location | Resolving |
| 4 | - | Enumerate overridable members from base | Computing |
| 5 | - | Filter to requested members (if specified) | Computing |
| 6 | - | Generate override method bodies | Computing |
| 7 | - | Apply changes to workspace | Applying |
| 8 | - | Persist changes to filesystem | Committing |
| 9 | - | Return success response | Completed |

---

#### Alternative Flows

**AF-GO01: No Members Specified**
- Trigger: members parameter is null or empty
- Steps:
  1. Enumerate all overridable members from base type hierarchy
  2. Filter out already-overridden members
  3. Generate overrides for all remaining members
  4. Continue from Main Step 7

**AF-GO02: Abstract Base Class**
- Trigger: Base class contains abstract members
- Steps:
  1. Abstract members are prioritized
  2. Generate throwing implementations (throw new NotImplementedException())
  3. Unless callBase would be valid (virtual with default)
  4. Continue from Main Step 7

**AF-GO03: Multiple Inheritance Levels**
- Trigger: Type has grandparent with overridable members
- Steps:
  1. Traverse full inheritance chain
  2. Collect all overridable members not yet overridden
  3. Generate overrides at correct inheritance level
  4. Continue from Main Step 7

**AF-GO04: Preview Mode Requested**
- Trigger: preview parameter is true
- Steps:
  1. Complete steps 1-6 (compute changes)
  2. Return computed changes without applying
  3. State remains Ready (no Committing)

---

#### Exception Flows

| ID | Trigger | Error Code | Recovery |
|----|---------|------------|----------|
| EF-GO01 | Type not found | 2015 | Return error with nearby symbols |
| EF-GO02 | No overridable members exist | 3063 | Return error listing base type |
| EF-GO03 | Override already exists for member | 3062 | Return error with existing location |
| EF-GO04 | Type is static | 3064 | Return error, static classes cannot override |
| EF-GO05 | Specified member not found in base | 2012 | Return error with available members |
| EF-GO06 | Member is sealed in base | 3070 | Return error, member cannot be overridden |


---

### 1.2 Input Parameters (MCP Tool Schema)

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["sourceFile", "typeName"],
  "properties": {
    "sourceFile": {
      "type": "string",
      "description": "Absolute path to the source file containing the type",
      "pattern": "^[A-Za-z]:\\.*\.cs$|^/.*\.cs$"
    },
    "typeName": {
      "type": "string",
      "description": "Name of the type to add overrides to",
      "minLength": 1,
      "pattern": "^[A-Za-z_][A-Za-z0-9_]*$"
    },
    "line": {
      "type": "integer",
      "description": "1-based line number for disambiguation",
      "minimum": 1
    },
    "members": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Specific members to override (null = all overridable)"
    },
    "callBase": {
      "type": "boolean",
      "description": "Include base.Method() call in body",
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

---

### 1.3 Output Format

#### Success Response

```json
{
  "success": true,
  "operationId": "guid-string",
  "changes": {
    "filesModified": ["C:\path\to\MyClass.cs"],
    "filesCreated": [],
    "filesDeleted": []
  },
  "generatedMembers": [
    {
      "name": "ToString",
      "kind": "Method",
      "signature": "public override string ToString()",
      "baseMember": "System.Object.ToString()",
      "insertedAtLine": 25
    },
    {
      "name": "Equals",
      "kind": "Method",
      "signature": "public override bool Equals(object obj)",
      "baseMember": "System.Object.Equals(object)",
      "insertedAtLine": 31
    }
  ],
  "executionTimeMs": 450
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
      "file": "C:\path\to\MyClass.cs",
      "changeType": "Modify",
      "description": "Add 2 override methods",
      "diff": "--- a/MyClass.cs\n+++ b/MyClass.cs\n@@ -24,0 +25,12 @@\n+    public override string ToString()..."
    }
  ],
  "generatedMembers": []
}
```

---

### 1.4 Error Codes

| Code | Constant | Message |
|------|----------|---------|
| 3062 | OVERRIDE_ALREADY_EXISTS | Override for '{member}' already exists in type |
| 3063 | NO_OVERRIDABLE_MEMBERS | Base type has no overridable members |
| 3064 | TYPE_IS_STATIC | Cannot add overrides to static class |
| 3070 | MEMBER_IS_SEALED | Member '{member}' is sealed and cannot be overridden |

---

### 1.5 Validation Rules

| Rule ID | Field | Rule | Error Code | Stage |
|---------|-------|------|------------|-------|
| IV-GO01 | sourceFile | Must be valid path to .cs file | 1001 | Validating |
| IV-GO02 | typeName | Must be valid C# identifier | 1003 | Validating |
| IV-GO03 | members | Each element must be valid identifier | 1012 | Validating |
| SV-GO01 | typeName | Type must exist in file | 2015 | Resolving |
| SV-GO02 | typeName | Type must have base with overridable members | 3063 | Computing |
| SV-GO03 | members | Each member must exist in base hierarchy | 2012 | Computing |
| SV-GO04 | members | Each member must not already be overridden | 3062 | Computing |
| SV-GO05 | typeName | Type must not be static | 3064 | Computing |

---

### 1.6 Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-GO01 | Happy Path | Override virtual method from direct base | P0 |
| TC-GO02 | Happy Path | Override abstract method | P0 |
| TC-GO03 | Happy Path | Override property | P1 |
| TC-GO04 | Happy Path | Override with callBase=true | P0 |
| TC-GO05 | Happy Path | Override with callBase=false | P1 |
| TC-GO06 | Complex | Override from grandparent class | P1 |
| TC-GO07 | Complex | Override multiple members at once | P1 |
| TC-GO08 | Complex | Override generic method | P2 |
| TC-GO09 | Edge | Override Object.ToString(), Equals(), GetHashCode() | P0 |
| TC-GO10 | Edge | Override in partial class | P2 |
| TC-GO11 | Negative | No overridable members in base | P0 |
| TC-GO12 | Negative | Override already exists | P0 |
| TC-GO13 | Negative | Static class | P0 |
| TC-GO14 | Negative | Sealed member | P1 |
| TC-GO15 | Negative | Member not found in base | P0 |
| TC-GO16 | Preview | Preview returns correct diff | P0 |


---

## 2. Operation: implement_interface

### 2.1 Use Case Specification

| Property | Value |
|----------|-------|
| ID | UC-GEN-02 |
| Name | Implement Interface |
| Actor | AI Agent (via MCP) |
| Priority | Tier 2 - Important |
| Complexity | Medium |

**Description**: Generate method stubs implementing all members of a specified interface, either implicitly (public members) or explicitly (InterfaceName.Member).

---

#### Preconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-II01 | Workspace is loaded and in Ready state | WorkspaceState == Ready |
| PRE-II02 | Source file exists and is part of loaded solution | Document exists in workspace |
| PRE-II03 | Type exists at specified location | INamedTypeSymbol resolvable |
| PRE-II04 | Interface exists in workspace or referenced assemblies | Interface symbol resolvable |
| PRE-II05 | Type declares it implements the interface | Type.Interfaces contains interface |
| PRE-II06 | Type is a class, struct, or record (not interface) | TypeKind is Class/Struct/Record |
| PRE-II07 | No other refactoring operation in progress | No active OperationState |

---

#### Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-II01 | All interface members have implementations | All members implemented |
| POST-II02 | Explicit implementations use InterfaceName.Member syntax | If explicit=true |
| POST-II03 | Implicit implementations are public | If explicit=false |
| POST-II04 | Properties have get/set as required by interface | Accessors match |
| POST-II05 | Solution still compiles | Zero compilation errors |

---

#### Main Success Scenario

| Step | Actor | System | State |
|------|-------|--------|-------|
| 1 | Agent sends implement_interface request | - | Pending |
| 2 | - | Validate input parameters | Validating |
| 3 | - | Resolve type symbol from location | Resolving |
| 4 | - | Resolve interface symbol | Resolving |
| 5 | - | Enumerate unimplemented interface members | Computing |
| 6 | - | Generate implementation stubs | Computing |
| 7 | - | Apply changes to workspace | Applying |
| 8 | - | Persist changes to filesystem | Committing |
| 9 | - | Return success response | Completed |

---

#### Alternative Flows

**AF-II01: Interface Already Partially Implemented**
- Trigger: Some interface members already implemented
- Steps:
  1. Identify which members are already implemented
  2. Generate only missing implementations
  3. Report which members were skipped
  4. Continue from Main Step 7

**AF-II02: Explicit Implementation Requested**
- Trigger: explicit parameter is true
- Steps:
  1. Generate members with InterfaceName.Member syntax
  2. Members are private by default (accessible only via interface)
  3. Continue from Main Step 7

**AF-II03: Interface Inherits Other Interfaces**
- Trigger: Interface extends other interfaces
- Steps:
  1. Traverse full interface inheritance chain
  2. Collect all unimplemented members
  3. Generate implementations for entire chain
  4. Continue from Main Step 7

**AF-II04: Generic Interface**
- Trigger: Interface has type parameters
- Steps:
  1. Resolve concrete type arguments from class declaration
  2. Substitute type parameters in member signatures
  3. Generate implementations with concrete types
  4. Continue from Main Step 7

**AF-II05: Preview Mode Requested**
- Trigger: preview parameter is true
- Steps:
  1. Complete steps 1-6 (compute changes)
  2. Return computed changes without applying
  3. State remains Ready

---

#### Exception Flows

| ID | Trigger | Error Code | Recovery |
|----|---------|------------|----------|
| EF-II01 | Type not found | 2015 | Return error with nearby symbols |
| EF-II02 | Interface not found | 2009 | Return error with suggestions |
| EF-II03 | Type does not implement interface | 3071 | Return error, add interface first |
| EF-II04 | All members already implemented | 3061 | Return success with empty changes |
| EF-II05 | Explicit conflicts with existing public member | 3065 | Return error with conflict details |
| EF-II06 | Type is an interface | 3072 | Return error, interfaces cannot implement |


---

### 2.2 Input Parameters (MCP Tool Schema)

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["sourceFile", "typeName", "interfaceName"],
  "properties": {
    "sourceFile": {
      "type": "string",
      "description": "Absolute path to the source file containing the type",
      "pattern": "^[A-Za-z]:\\.*\.cs$|^/.*\.cs$"
    },
    "typeName": {
      "type": "string",
      "description": "Name of the type implementing the interface",
      "minLength": 1,
      "pattern": "^[A-Za-z_][A-Za-z0-9_]*$"
    },
    "line": {
      "type": "integer",
      "description": "1-based line number for disambiguation",
      "minimum": 1
    },
    "interfaceName": {
      "type": "string",
      "description": "Fully qualified or simple name of interface to implement",
      "minLength": 1
    },
    "explicit": {
      "type": "boolean",
      "description": "Use explicit interface implementation",
      "default": false
    },
    "throwNotImplemented": {
      "type": "boolean",
      "description": "Generate throw NotImplementedException() bodies",
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

---

### 2.3 Output Format

#### Success Response

```json
{
  "success": true,
  "operationId": "guid-string",
  "changes": {
    "filesModified": ["C:\path\to\MyClass.cs"],
    "filesCreated": [],
    "filesDeleted": []
  },
  "interface": {
    "name": "IRepository",
    "fullyQualifiedName": "MyApp.Data.IRepository",
    "totalMembers": 5,
    "alreadyImplemented": 1,
    "generated": 4
  },
  "generatedMembers": [
    {
      "name": "GetById",
      "kind": "Method",
      "signature": "public T GetById(int id)",
      "implementationType": "Implicit",
      "insertedAtLine": 30
    },
    {
      "name": "Save",
      "kind": "Method",
      "signature": "public void Save(T entity)",
      "implementationType": "Implicit",
      "insertedAtLine": 35
    }
  ],
  "skippedMembers": [
    {
      "name": "Delete",
      "reason": "Already implemented at line 45"
    }
  ],
  "executionTimeMs": 520
}
```

---

### 2.4 Error Codes

| Code | Constant | Message |
|------|----------|---------|
| 2009 | INTERFACE_NOT_FOUND | Interface '{name}' not found in workspace |
| 3061 | MEMBER_ALREADY_IMPLEMENTED | All interface members are already implemented |
| 3065 | INTERFACE_MEMBER_CONFLICT | Explicit implementation conflicts with existing member '{name}' |
| 3071 | TYPE_DOES_NOT_IMPLEMENT | Type '{type}' does not declare implementation of '{interface}' |
| 3072 | CANNOT_IMPLEMENT_ON_INTERFACE | Interfaces cannot contain implementations |

---

### 2.5 Validation Rules

| Rule ID | Field | Rule | Error Code | Stage |
|---------|-------|------|------------|-------|
| IV-II01 | sourceFile | Must be valid path to .cs file | 1001 | Validating |
| IV-II02 | typeName | Must be valid C# identifier | 1003 | Validating |
| IV-II03 | interfaceName | Must be valid C# type name | 1003 | Validating |
| SV-II01 | typeName | Type must exist in file | 2015 | Resolving |
| SV-II02 | interfaceName | Interface must exist in workspace | 2009 | Resolving |
| SV-II03 | typeName | Type must declare interface implementation | 3071 | Computing |
| SV-II04 | typeName | Type must be class/struct/record | 3072 | Computing |
| SV-II05 | explicit | Explicit impl must not conflict | 3065 | Computing |

---

### 2.6 Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-II01 | Happy Path | Implicit implementation of simple interface | P0 |
| TC-II02 | Happy Path | Explicit implementation of simple interface | P0 |
| TC-II03 | Happy Path | Interface with methods and properties | P0 |
| TC-II04 | Happy Path | Interface with events | P1 |
| TC-II05 | Complex | Generic interface IRepository<T> | P0 |
| TC-II06 | Complex | Interface inheriting other interfaces | P1 |
| TC-II07 | Complex | Multiple interfaces with same member name | P1 |
| TC-II08 | Edge | Interface with default implementation (C# 8+) | P2 |
| TC-II09 | Edge | Partial implementation already exists | P0 |
| TC-II10 | Edge | Struct implementing interface | P1 |
| TC-II11 | Negative | Interface not found | P0 |
| TC-II12 | Negative | Type does not declare interface | P0 |
| TC-II13 | Negative | All members already implemented | P0 |
| TC-II14 | Negative | Target is an interface | P0 |
| TC-II15 | Negative | Explicit conflict with existing member | P1 |
| TC-II16 | Preview | Preview returns correct diff | P0 |


---

## 3. Operation: generate_method_stub

### 3.1 Use Case Specification

| Property | Value |
|----------|-------|
| ID | UC-GEN-03 |
| Name | Generate Method Stub |
| Actor | AI Agent (via MCP) |
| Priority | Tier 3 - Valuable |
| Complexity | Medium |

**Description**: Generate a method declaration from an undefined method call site, inferring signature from usage context.

---

#### Preconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-GS01 | Workspace is loaded and in Ready state | WorkspaceState == Ready |
| PRE-GS02 | Source file exists and is part of loaded solution | Document exists in workspace |
| PRE-GS03 | Call site location is specified | Line/column provided |
| PRE-GS04 | Call site contains unresolved method invocation | Compilation error CS0103/CS1061 |
| PRE-GS05 | Target type for method is determinable | Receiver type resolvable |
| PRE-GS06 | No other refactoring operation in progress | No active OperationState |

---

#### Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-GS01 | Method exists in target type | Parse type, find method |
| POST-GS02 | Method signature matches call site usage | Parameters match arguments |
| POST-GS03 | Return type inferred from usage context | Return type appropriate |
| POST-GS04 | Method has placeholder body | Body is NotImplementedException or default |
| POST-GS05 | Solution compiles at call site | Error resolved |

---

#### Main Success Scenario

| Step | Actor | System | State |
|------|-------|--------|-------|
| 1 | Agent sends generate_method_stub request | - | Pending |
| 2 | - | Validate input parameters | Validating |
| 3 | - | Locate call site expression | Resolving |
| 4 | - | Analyze invocation arguments | Computing |
| 5 | - | Infer return type from usage | Computing |
| 6 | - | Determine target type | Computing |
| 7 | - | Generate method declaration | Computing |
| 8 | - | Apply changes to workspace | Applying |
| 9 | - | Persist changes to filesystem | Committing |
| 10 | - | Return success response | Completed |

---

#### Alternative Flows

**AF-GS01: Method on Same Class (this.Method or implicit)**
- Trigger: Call site receiver is this or implicit
- Steps:
  1. Target type is the enclosing class
  2. Generate method in same file
  3. Determine appropriate access modifier (private by default)
  4. Continue from Main Step 8

**AF-GS02: Method on Another Type**
- Trigger: Call site receiver is instance of another type
- Steps:
  1. Identify target type from receiver expression
  2. Locate target type's source file
  3. Generate method in target type
  4. Continue from Main Step 8

**AF-GS03: Static Method Call**
- Trigger: Call site uses Type.Method() syntax
- Steps:
  1. Mark method as static
  2. Generate in target type
  3. Continue from Main Step 8

**AF-GS04: Generic Method Inference**
- Trigger: Call site has type arguments or inference context
- Steps:
  1. Analyze type arguments
  2. Generate generic method with type parameters
  3. Add appropriate constraints if inferable
  4. Continue from Main Step 8

**AF-GS05: Async Method Inference**
- Trigger: Call site is awaited
- Steps:
  1. Mark method as async
  2. Wrap return type in Task<T> or Task
  3. Continue from Main Step 8

**AF-GS06: Preview Mode Requested**
- Trigger: preview parameter is true
- Steps:
  1. Complete steps 1-7 (compute changes)
  2. Return computed changes without applying
  3. State remains Ready

---

#### Exception Flows

| ID | Trigger | Error Code | Recovery |
|----|---------|------------|----------|
| EF-GS01 | No invocation at location | 2006 | Return error with what was found |
| EF-GS02 | Method already exists | 3073 | Return error with existing signature |
| EF-GS03 | Target type not editable | 3074 | Return error (external assembly) |
| EF-GS04 | Cannot infer return type | 3075 | Return error, suggest providing explicit type |
| EF-GS05 | Ambiguous target type | 2004 | Return error with candidate types |


---

### 3.2 Input Parameters (MCP Tool Schema)

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["sourceFile", "line", "column"],
  "properties": {
    "sourceFile": {
      "type": "string",
      "description": "Absolute path to the file containing the call site",
      "pattern": "^[A-Za-z]:\\.*\.cs$|^/.*\.cs$"
    },
    "line": {
      "type": "integer",
      "description": "1-based line number of the call site",
      "minimum": 1
    },
    "column": {
      "type": "integer",
      "description": "1-based column number within the method name",
      "minimum": 1
    },
    "methodName": {
      "type": "string",
      "description": "Method name override (if not inferable from location)"
    },
    "returnType": {
      "type": "string",
      "description": "Explicit return type override"
    },
    "visibility": {
      "type": "string",
      "enum": ["public", "internal", "protected", "private", "protected internal", "private protected"],
      "description": "Access modifier for generated method",
      "default": "private"
    },
    "generateAsync": {
      "type": "boolean",
      "description": "Force async method generation",
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

---

### 3.3 Output Format

#### Success Response

```json
{
  "success": true,
  "operationId": "guid-string",
  "changes": {
    "filesModified": ["C:\path\to\TargetClass.cs"],
    "filesCreated": [],
    "filesDeleted": []
  },
  "callSite": {
    "file": "C:\path\to\CallerClass.cs",
    "line": 45,
    "column": 12,
    "expression": "processor.Transform(data, options)"
  },
  "generatedMethod": {
    "name": "Transform",
    "signature": "private Result Transform(Data data, Options options)",
    "targetType": "Processor",
    "targetFile": "C:\path\to\Processor.cs",
    "insertedAtLine": 78,
    "inferredParameters": [
      { "name": "data", "type": "Data", "inferredFrom": "argument type" },
      { "name": "options", "type": "Options", "inferredFrom": "argument type" }
    ],
    "inferredReturnType": {
      "type": "Result",
      "inferredFrom": "assignment target"
    }
  },
  "executionTimeMs": 380
}
```

---

### 3.4 Error Codes

| Code | Constant | Message |
|------|----------|---------|
| 2006 | METHOD_NOT_FOUND | No method invocation found at specified location |
| 3073 | METHOD_ALREADY_EXISTS | Method '{name}' with compatible signature already exists |
| 3074 | TARGET_NOT_EDITABLE | Target type '{type}' is in external assembly |
| 3075 | CANNOT_INFER_RETURN_TYPE | Cannot infer return type; provide explicit returnType |

---

### 3.5 Validation Rules

| Rule ID | Field | Rule | Error Code | Stage |
|---------|-------|------|------------|-------|
| IV-GS01 | sourceFile | Must be valid path to .cs file | 1001 | Validating |
| IV-GS02 | line | Must be >= 1 | 1006 | Validating |
| IV-GS03 | column | Must be >= 1 | 1007 | Validating |
| IV-GS04 | methodName | If provided, must be valid identifier | 1003 | Validating |
| IV-GS05 | returnType | If provided, must be valid C# type | 1015 | Validating |
| IV-GS06 | visibility | Must be valid access modifier | 1014 | Validating |
| SV-GS01 | location | Invocation must exist at location | 2006 | Resolving |
| SV-GS02 | methodName | Method must not already exist | 3073 | Computing |
| SV-GS03 | targetType | Target type must be editable | 3074 | Computing |
| SV-GS04 | returnType | Return type must be inferable or provided | 3075 | Computing |

---

### 3.6 Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-GS01 | Happy Path | Generate void method on same class | P0 |
| TC-GS02 | Happy Path | Generate returning method from assignment | P0 |
| TC-GS03 | Happy Path | Generate method with parameters | P0 |
| TC-GS04 | Happy Path | Generate method on different class | P0 |
| TC-GS05 | Complex | Infer async from await | P1 |
| TC-GS06 | Complex | Infer generic type parameters | P2 |
| TC-GS07 | Complex | Generate static method | P1 |
| TC-GS08 | Edge | Parameter names from argument names | P1 |
| TC-GS09 | Edge | Return type from conditional expression | P2 |
| TC-GS10 | Edge | Overload detection | P1 |
| TC-GS11 | Negative | No invocation at location | P0 |
| TC-GS12 | Negative | Method already exists | P0 |
| TC-GS13 | Negative | Target type external | P0 |
| TC-GS14 | Negative | Cannot infer return type | P1 |
| TC-GS15 | Preview | Preview returns correct diff | P0 |


---

## 4. Operation: generate_equals_hashcode

### 4.1 Use Case Specification

| Property | Value |
|----------|-------|
| ID | UC-GEN-04 |
| Name | Generate Equals and GetHashCode |
| Actor | AI Agent (via MCP) |
| Priority | Tier 2 - Important |
| Complexity | Medium |

**Description**: Generate overrides of Equals(object) and GetHashCode() for a type, using specified or all fields/properties for equality comparison.

---

#### Preconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-GE01 | Workspace is loaded and in Ready state | WorkspaceState == Ready |
| PRE-GE02 | Source file exists and is part of loaded solution | Document exists in workspace |
| PRE-GE03 | Type exists at specified location | INamedTypeSymbol resolvable |
| PRE-GE04 | Type has fields or properties for comparison | Members exist |
| PRE-GE05 | Type is not static | !IsStatic |
| PRE-GE06 | No other refactoring operation in progress | No active OperationState |

---

#### Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-GE01 | Equals(object) override exists | Method present |
| POST-GE02 | GetHashCode() override exists | Method present |
| POST-GE03 | Equals compares all specified members | Body contains comparisons |
| POST-GE04 | GetHashCode uses all specified members | Body uses members |
| POST-GE05 | IEquatable<T> implemented if requested | Interface implemented |
| POST-GE06 | Solution still compiles | Zero compilation errors |

---

#### Main Success Scenario

| Step | Actor | System | State |
|------|-------|--------|-------|
| 1 | Agent sends generate_equals_hashcode request | - | Pending |
| 2 | - | Validate input parameters | Validating |
| 3 | - | Resolve type symbol from location | Resolving |
| 4 | - | Identify comparison members | Computing |
| 5 | - | Generate Equals(object) method | Computing |
| 6 | - | Generate GetHashCode() method | Computing |
| 7 | - | Optionally generate IEquatable<T> | Computing |
| 8 | - | Apply changes to workspace | Applying |
| 9 | - | Persist changes to filesystem | Committing |
| 10 | - | Return success response | Completed |

---

#### Alternative Flows

**AF-GE01: No Members Specified**
- Trigger: members parameter is null or empty
- Steps:
  1. Include all instance fields (not static)
  2. Include all auto-properties (if includeProperties=true)
  3. Exclude computed properties
  4. Continue from Main Step 5

**AF-GE02: IEquatable<T> Requested**
- Trigger: implementIEquatable parameter is true
- Steps:
  1. Add IEquatable<T> to type's base list
  2. Generate Equals(T other) method
  3. Have Equals(object) delegate to Equals(T)
  4. Continue from Main Step 6

**AF-GE03: Operators Requested**
- Trigger: generateOperators parameter is true
- Steps:
  1. Generate operator ==(T, T)
  2. Generate operator !=(T, T)
  3. Continue from Main Step 8

**AF-GE04: Struct Type**
- Trigger: Type is a struct
- Steps:
  1. Use value comparison semantics
  2. Consider boxing in Equals(object)
  3. Use HashCode.Combine for GetHashCode
  4. Continue from Main Step 8

**AF-GE05: Existing Override**
- Trigger: Equals or GetHashCode already overridden
- Steps:
  1. Offer to replace existing implementation
  2. If replace=false, skip that method
  3. Warn if only one is generated
  4. Continue from Main Step 8

**AF-GE06: Preview Mode Requested**
- Trigger: preview parameter is true
- Steps:
  1. Complete steps 1-7 (compute changes)
  2. Return computed changes without applying
  3. State remains Ready

---

#### Exception Flows

| ID | Trigger | Error Code | Recovery |
|----|---------|------------|----------|
| EF-GE01 | Type not found | 2015 | Return error with nearby symbols |
| EF-GE02 | No comparable members | 3076 | Return error, need at least one member |
| EF-GE03 | Equals already exists (and replace=false) | 3062 | Return error or partial success |
| EF-GE04 | GetHashCode already exists (and replace=false) | 3062 | Return error or partial success |
| EF-GE05 | Type is static | 3064 | Return error, static classes cannot override |
| EF-GE06 | Specified member not found | 2012 | Return error listing available members |


---

### 4.2 Input Parameters (MCP Tool Schema)

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["sourceFile", "typeName"],
  "properties": {
    "sourceFile": {
      "type": "string",
      "description": "Absolute path to the source file containing the type",
      "pattern": "^[A-Za-z]:\\.*\.cs$|^/.*\.cs$"
    },
    "typeName": {
      "type": "string",
      "description": "Name of the type to generate Equals/GetHashCode for",
      "minLength": 1,
      "pattern": "^[A-Za-z_][A-Za-z0-9_]*$"
    },
    "line": {
      "type": "integer",
      "description": "1-based line number for disambiguation",
      "minimum": 1
    },
    "members": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Specific fields/properties for equality (null = all)"
    },
    "includeProperties": {
      "type": "boolean",
      "description": "Include auto-properties in comparison",
      "default": true
    },
    "implementIEquatable": {
      "type": "boolean",
      "description": "Also implement IEquatable<T>",
      "default": false
    },
    "generateOperators": {
      "type": "boolean",
      "description": "Also generate == and != operators",
      "default": false
    },
    "useHashCodeCombine": {
      "type": "boolean",
      "description": "Use HashCode.Combine() instead of manual calculation",
      "default": true
    },
    "replaceExisting": {
      "type": "boolean",
      "description": "Replace existing Equals/GetHashCode if present",
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

---

### 4.3 Output Format

#### Success Response

```json
{
  "success": true,
  "operationId": "guid-string",
  "changes": {
    "filesModified": ["C:\path\to\Person.cs"],
    "filesCreated": [],
    "filesDeleted": []
  },
  "type": {
    "name": "Person",
    "kind": "Class"
  },
  "comparisonMembers": [
    { "name": "Id", "type": "int", "kind": "Property" },
    { "name": "Name", "type": "string", "kind": "Property" },
    { "name": "_birthDate", "type": "DateTime", "kind": "Field" }
  ],
  "generatedMethods": [
    {
      "name": "Equals",
      "signature": "public override bool Equals(object obj)",
      "insertedAtLine": 45
    },
    {
      "name": "GetHashCode",
      "signature": "public override int GetHashCode()",
      "insertedAtLine": 55
    },
    {
      "name": "Equals",
      "signature": "public bool Equals(Person other)",
      "insertedAtLine": 50,
      "note": "IEquatable<Person> implementation"
    }
  ],
  "addedInterfaces": ["IEquatable<Person>"],
  "executionTimeMs": 410
}
```

---

### 4.4 Error Codes

| Code | Constant | Message |
|------|----------|---------|
| 3062 | OVERRIDE_ALREADY_EXISTS | {method} already overridden; set replaceExisting=true |
| 3064 | TYPE_IS_STATIC | Cannot generate equality for static class |
| 3076 | NO_COMPARABLE_MEMBERS | Type has no fields or properties for comparison |
| 2012 | MEMBER_NOT_FOUND | Member '{name}' not found in type |

---

### 4.5 Validation Rules

| Rule ID | Field | Rule | Error Code | Stage |
|---------|-------|------|------------|-------|
| IV-GE01 | sourceFile | Must be valid path to .cs file | 1001 | Validating |
| IV-GE02 | typeName | Must be valid C# identifier | 1003 | Validating |
| IV-GE03 | members | Each element must be valid identifier | 1012 | Validating |
| SV-GE01 | typeName | Type must exist in file | 2015 | Resolving |
| SV-GE02 | typeName | Type must have comparable members | 3076 | Computing |
| SV-GE03 | members | Each member must exist in type | 2012 | Computing |
| SV-GE04 | typeName | Type must not be static | 3064 | Computing |
| SV-GE05 | replaceExisting | Existing overrides require replace flag | 3062 | Computing |

---

### 4.6 Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-GE01 | Happy Path | Generate for class with fields only | P0 |
| TC-GE02 | Happy Path | Generate for class with properties | P0 |
| TC-GE03 | Happy Path | Generate with IEquatable<T> | P0 |
| TC-GE04 | Happy Path | Generate with operators | P1 |
| TC-GE05 | Happy Path | Generate for struct | P0 |
| TC-GE06 | Complex | Class with nullable reference types | P1 |
| TC-GE07 | Complex | Class with collection members | P2 |
| TC-GE08 | Complex | Generic class | P1 |
| TC-GE09 | Edge | Selected members only | P0 |
| TC-GE10 | Edge | Replace existing implementation | P1 |
| TC-GE11 | Edge | Class inheriting from non-object | P1 |
| TC-GE12 | Negative | No comparable members | P0 |
| TC-GE13 | Negative | Static class | P0 |
| TC-GE14 | Negative | Existing override without replace | P0 |
| TC-GE15 | Negative | Specified member not found | P0 |
| TC-GE16 | Preview | Preview returns correct diff | P0 |


---

## 5. Error Code Summary (3060-3079)

| Code | Constant | Operation | Message |
|------|----------|-----------|---------|
| 3060 | CONSTRUCTOR_EXISTS | generate_constructor | Constructor with same signature exists |
| 3061 | MEMBER_ALREADY_IMPLEMENTED | implement_interface | Interface member already implemented |
| 3062 | OVERRIDE_ALREADY_EXISTS | generate_overrides, generate_equals_hashcode | Override already exists |
| 3063 | NO_OVERRIDABLE_MEMBERS | generate_overrides | No overridable members in base |
| 3064 | TYPE_IS_STATIC | all generate ops | Cannot generate for static class |
| 3065 | INTERFACE_MEMBER_CONFLICT | implement_interface | Interface member conflicts with existing |
| 3070 | MEMBER_IS_SEALED | generate_overrides | Member is sealed and cannot be overridden |
| 3071 | TYPE_DOES_NOT_IMPLEMENT | implement_interface | Type does not declare interface |
| 3072 | CANNOT_IMPLEMENT_ON_INTERFACE | implement_interface | Interfaces cannot contain implementations |
| 3073 | METHOD_ALREADY_EXISTS | generate_method_stub | Method with signature already exists |
| 3074 | TARGET_NOT_EDITABLE | generate_method_stub | Target type is in external assembly |
| 3075 | CANNOT_INFER_RETURN_TYPE | generate_method_stub | Cannot infer return type |
| 3076 | NO_COMPARABLE_MEMBERS | generate_equals_hashcode | No fields/properties for comparison |

---

## 6. Cross-Cutting Concerns

### 6.1 Code Generation Style

All generated code shall:
- Follow C# naming conventions (PascalCase for methods/properties, camelCase for parameters)
- Include appropriate null checks where applicable
- Use modern C# features when target framework supports them
- Include XML documentation comments if type/project uses them
- Respect EditorConfig settings if present in workspace

### 6.2 Insertion Location

Generated members shall be inserted:
1. After existing members of same kind (methods with methods, properties with properties)
2. At end of type if no similar members exist
3. Respecting any #region boundaries when present
4. Maintaining consistent indentation with surrounding code

### 6.3 Atomicity

All operations shall:
- Complete fully or roll back entirely
- Not leave partial implementations
- Preserve workspace state on failure

### 6.4 Preview Mode Contract

When preview=true:
- All validation executes
- All code generation computes
- No filesystem writes occur
- Response includes full diff of proposed changes
- Subsequent non-preview call may yield different results if files changed

---

## 7. Open Questions

| ID | Question | Impact | Status |
|----|----------|--------|--------|
| OQ-01 | Should generate_method_stub support extension methods? | generate_method_stub scope | Open |
| OQ-02 | Default visibility for interface implementations when explicit=false? | implement_interface behavior | Open |
| OQ-03 | Support for records with positional parameters in generate_equals_hashcode? | Records already have these | Open |
| OQ-04 | Should generated code include #nullable enable directives? | NRT support | Open |

---

## 8. Assumptions and Constraints

### Assumptions

1. Target workspace uses MSBuild-style projects
2. Source files are UTF-8 encoded
3. Roslyn semantic model is available for all editable files
4. External assemblies are read-only (metadata reference only)

### Constraints

1. Only one generate operation executes at a time per workspace
2. Generated code cannot reference types not already imported
3. Maximum method body complexity is bounded by reasonable defaults
4. File system permissions allow write access to source files
