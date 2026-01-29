# Business Analysis: Extract Operations

**Document ID:** BA-EXT-001  
**Version:** 1.0  
**Status:** Draft  
**Last Updated:** 2026-01-28

---

## 1. Document Purpose

This document provides detailed requirements specifications for four EXTRACT refactoring operations in the Roslyn MCP Server:

| Operation | Use Case ID | Priority |
|-----------|-------------|----------|
| extract_interface | UC-E2 | Tier 1 - Critical |
| extract_base_class | UC-E3 | Tier 3 - Valuable |
| extract_variable | UC-E4 | Tier 2 - Important |
| extract_constant | UC-E5 | Tier 2 - Important |

---

## 2. UC-E2: Extract Interface

### 2.1 Overview

| Property | Value |
|----------|-------|
| ID | UC-E2 |
| Name | Extract Interface |
| Actor | AI Agent (via MCP) |
| Priority | Tier 1 - Critical |
| Complexity | Medium |
| Error Code Range | 3040-3042 |

### 2.2 Description

Create a new interface from selected public members of a class, struct, or record. The source type is optionally updated to implement the extracted interface. Enables dependency inversion and testability improvements.

### 2.3 Actors

| Actor | Role | Interest |
|-------|------|----------|
| AI Agent | Primary | Executes refactoring via MCP tool call |
| Developer | Beneficiary | Receives cleaner abstraction, improved testability |
| Solution | Affected System | Types and references updated |

### 2.4 Preconditions

| ID | Condition | Verification | Error Code |
|----|-----------|--------------|------------|
| PRE-E2.1 | Workspace loaded and ready | WorkspaceState == Ready | 2005 |
| PRE-E2.2 | Source file exists in workspace | Document in solution | 2001/2002 |
| PRE-E2.3 | Target type exists and is class/struct/record | Type resolved in file | 2015 |
| PRE-E2.4 | Interface name provided | Non-empty string | 1005 |
| PRE-E2.5 | Interface name is valid identifier | C# identifier rules | 1003 |
| PRE-E2.6 | At least one extractable member exists | Public instance members present | 3040 |

### 2.5 Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-E2.1 | Interface created | Interface declaration exists |
| POST-E2.2 | Interface contains member signatures | All selected members declared |
| POST-E2.3 | Source type implements interface | Base list contains interface |
| POST-E2.4 | Solution compiles | Zero compilation errors |
| POST-E2.5 | Separate file created (if requested) | File exists at expected path |

### 2.6 Main Flow

```
1. Agent calls extract_interface with parameters
2. System validates input parameters (sourceFile, typeName, interfaceName)
3. System locates source type in workspace
4. System identifies extractable members (public, non-static)
5. System filters to requested members (or all if none specified)
6. System generates interface declaration with member signatures
7. System determines interface location (same file or separate)
8. System updates source type base list to implement interface
9. System writes changes to workspace
10. System returns success response with change details
```

### 2.7 Alternate Flows

| ID | Condition | Flow |
|----|-----------|------|
| AF-E2.1 | No members specified | Extract all public instance members |
| AF-E2.2 | separateFile=true | Create new file named I{TypeName}.cs or {InterfaceName}.cs |
| AF-E2.3 | preview=true | Return pending changes without applying |
| AF-E2.4 | Generic source type | Include type parameters in interface |
| AF-E2.5 | Type already implements interfaces | Append to existing base list |

### 2.8 Exception Flows

| ID | Trigger | Error Code | Response |
|----|---------|------------|----------|
| EF-E2.1 | No extractable public members | 3040 | NO_EXTRACTABLE_MEMBERS |
| EF-E2.2 | Interface name already exists in namespace | 3003 | NAME_COLLISION |
| EF-E2.3 | Selected member does not exist | 2012 | MEMBER_NOT_FOUND |
| EF-E2.4 | Selected member is not public | 3041 | MEMBER_NOT_PUBLIC |
| EF-E2.5 | Selected member is static | 3042 | STATIC_MEMBER_NOT_EXTRACTABLE |

### 2.9 Business Rules

| ID | Rule | Rationale |
|----|------|-----------|
| BR-E2.1 | Only public members extractable to interface | Interface semantics require public visibility |
| BR-E2.2 | Static members excluded | Interfaces define instance contracts |
| BR-E2.3 | Properties include get/set as declared | Signature fidelity |
| BR-E2.4 | Events preserve signature exactly | Signature fidelity |
| BR-E2.5 | Interface name should start with 'I' | C# convention (warning, not error) |
| BR-E2.6 | Generic type parameters propagate | Type correctness |
| BR-E2.7 | Constraints propagate to interface | Type correctness |

### 2.10 Input Parameters (MCP Schema)

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["sourceFile", "typeName", "interfaceName"],
  "properties": {
    "sourceFile": {
      "type": "string",
      "description": "Absolute path to source file containing the type"
    },
    "typeName": {
      "type": "string",
      "description": "Name of the class/struct/record to extract from",
      "minLength": 1
    },
    "interfaceName": {
      "type": "string",
      "description": "Name for the new interface",
      "minLength": 1
    },
    "members": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Member names to include (all public if omitted)"
    },
    "separateFile": {
      "type": "boolean",
      "description": "Create interface in separate file",
      "default": false
    },
    "targetPath": {
      "type": "string",
      "description": "Path for separate file (defaults to same directory)"
    },
    "preview": {
      "type": "boolean",
      "description": "Return changes without applying",
      "default": false
    }
  },
  "additionalProperties": false
}
```

### 2.11 Output Format

#### Success Response

```json
{
  "success": true,
  "operationId": "guid-string",
  "interface": {
    "name": "IUserService",
    "fullyQualifiedName": "MyApp.Services.IUserService",
    "location": {
      "file": "C:/project/Services/IUserService.cs",
      "line": 5,
      "column": 1
    },
    "members": [
      { "name": "GetUser", "kind": "Method", "signature": "User GetUser(int id)" },
      { "name": "SaveUser", "kind": "Method", "signature": "void SaveUser(User user)" }
    ]
  },
  "sourceType": {
    "name": "UserService",
    "implementsInterface": true
  },
  "changes": {
    "filesModified": ["C:/project/Services/UserService.cs"],
    "filesCreated": ["C:/project/Services/IUserService.cs"]
  },
  "executionTimeMs": 450
}
```

### 2.12 Validation Rules

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-E2.01 | sourceFile | Must be valid absolute path | 1001 |
| IV-E2.02 | sourceFile | Must have .cs extension | 1001 |
| IV-E2.03 | typeName | Must be valid C# identifier | 1003 |
| IV-E2.04 | interfaceName | Must be valid C# identifier | 1003 |
| IV-E2.05 | members | Each must be valid identifier | 1012 |
| IV-E2.06 | targetPath | If provided, must be valid path | 1002 |

| Rule ID | Semantic Rule | Error Code |
|---------|---------------|------------|
| SV-E2.01 | Type must exist in source file | 2015 |
| SV-E2.02 | All specified members must exist in type | 2012 |
| SV-E2.03 | Interface name must not exist in same namespace | 3003 |
| SV-E2.04 | At least one public instance member required | 3040 |
| SV-E2.05 | Specified members must be public | 3041 |
| SV-E2.06 | Specified members must not be static | 3042 |

### 2.13 Test Scenarios

| ID | Category | Description | Priority | Expected Outcome |
|----|----------|-------------|----------|------------------|
| TC-E2.01 | Happy Path | Extract all public members from class | P0 | Interface created, class implements it |
| TC-E2.02 | Happy Path | Extract selected members only | P0 | Interface contains only selected members |
| TC-E2.03 | Happy Path | Extract to separate file | P0 | New file created with interface |
| TC-E2.04 | Happy Path | Extract from generic class | P1 | Interface includes type parameters |
| TC-E2.05 | Happy Path | Extract from class with existing base | P1 | Interface appended to base list |
| TC-E2.06 | Happy Path | Extract properties and methods | P0 | Both kinds in interface |
| TC-E2.07 | Happy Path | Extract events | P1 | Events in interface with correct signature |
| TC-E2.08 | Edge | Class with only private members | P0 | Error 3040: NO_EXTRACTABLE_MEMBERS |
| TC-E2.09 | Edge | Class with only static members | P0 | Error 3040: NO_EXTRACTABLE_MEMBERS |
| TC-E2.10 | Edge | Interface name already exists | P0 | Error 3003: NAME_COLLISION |
| TC-E2.11 | Edge | Member name not in type | P0 | Error 2012: MEMBER_NOT_FOUND |
| TC-E2.12 | Edge | Static member in selection | P0 | Error 3042: STATIC_MEMBER_NOT_EXTRACTABLE |
| TC-E2.13 | Edge | Interface name without I prefix | P1 | Warning but proceeds |
| TC-E2.14 | Preview | Preview mode returns correct diff | P0 | Changes described, not applied |
| TC-E2.15 | Constraint | Generic type with constraints | P1 | Constraints preserved in interface |

---

## 3. UC-E3: Extract Base Class

### 3.1 Overview

| Property | Value |
|----------|-------|
| ID | UC-E3 |
| Name | Extract Base Class |
| Actor | AI Agent (via MCP) |
| Priority | Tier 3 - Valuable |
| Complexity | High |
| Error Code Range | 3043-3046 |

### 3.2 Description

Create a new base class from selected members of an existing class. Member implementations are moved to the new base class, and the original class inherits from it.

### 3.3 Preconditions

| ID | Condition | Verification | Error Code |
|----|-----------|--------------|------------|
| PRE-E3.1 | Workspace loaded and ready | WorkspaceState == Ready | 2005 |
| PRE-E3.2 | Source file exists in workspace | Document in solution | 2001/2002 |
| PRE-E3.3 | Source type is a class | Not struct, interface, enum | 3043 |
| PRE-E3.4 | Source type is not sealed | Can derive from | 3044 |
| PRE-E3.5 | Source type has no existing base class | Single inheritance | 3045 |
| PRE-E3.6 | Base class name provided | Non-empty string | 1005 |
| PRE-E3.7 | Base class name is valid identifier | C# identifier rules | 1003 |

### 3.4 Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-E3.1 | Base class created | Class declaration exists |
| POST-E3.2 | Selected members moved to base | Members in base class |
| POST-E3.3 | Original class inherits from base | Base list updated |
| POST-E3.4 | Solution compiles | Zero compilation errors |

### 3.5 Main Flow

1. Agent calls extract_base_class with parameters
2. System validates input parameters
3. System locates source class in workspace
4. System verifies class is not sealed and has no conflicting base
5. System identifies members to extract
6. System analyzes member dependencies
7. System generates base class with extracted members
8. System modifies source class to inherit from base
9. System adjusts member visibility as needed
10. System returns success response

### 3.6 Exception Flows

| ID | Trigger | Error Code | Response |
|----|---------|------------|----------|
| EF-E3.1 | Source is not a class | 3043 | TYPE_NOT_CLASS |
| EF-E3.2 | Source class is sealed | 3044 | TYPE_IS_SEALED |
| EF-E3.3 | Class already has base class | 3045 | HAS_EXISTING_BASE |
| EF-E3.4 | Base class name collision | 3003 | NAME_COLLISION |
| EF-E3.5 | Member depends on non-movable member | 3046 | MEMBER_DEPENDENCY_CONFLICT |
| EF-E3.6 | Would create circular inheritance | 3005 | CIRCULAR_REFERENCE |

### 3.7 Business Rules

| ID | Rule | Rationale |
|----|------|-----------|
| BR-E3.1 | Only classes can have base classes extracted | Language constraint |
| BR-E3.2 | Sealed classes cannot participate | Cannot derive from sealed |
| BR-E3.3 | Private members become protected in base | Accessibility for derived |
| BR-E3.4 | Constructors not moved | Constructors not inherited |
| BR-E3.5 | Static members may be moved | Shared across hierarchy |
| BR-E3.6 | Dependencies must be resolvable | No broken references |
| BR-E3.7 | Virtual members remain virtual | Preserve override semantics |

### 3.8 Input Parameters (MCP Schema)

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | Absolute path to source file |
| typeName | string | Yes | Name of class to extract from |
| baseClassName | string | Yes | Name for new base class |
| members | string[] | No | Member names to move |
| makeAbstract | boolean | No | Create abstract base class (default: false) |
| separateFile | boolean | No | Create in separate file (default: true) |
| targetPath | string | No | Path for separate file |
| preview | boolean | No | Preview mode (default: false) |

### 3.9 Validation Rules

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-E3.01 | sourceFile | Must be valid absolute path | 1001 |
| IV-E3.02 | typeName | Must be valid C# identifier | 1003 |
| IV-E3.03 | baseClassName | Must be valid C# identifier | 1003 |
| IV-E3.04 | members | Each must be valid identifier | 1012 |

| Rule ID | Semantic Rule | Error Code |
|---------|---------------|------------|
| SV-E3.01 | Type must exist in source file | 2015 |
| SV-E3.02 | Type must be a class | 3043 |
| SV-E3.03 | Class must not be sealed | 3044 |
| SV-E3.04 | Class must not have existing base class | 3045 |
| SV-E3.05 | Base class name must not exist in namespace | 3003 |
| SV-E3.06 | All member dependencies resolvable | 3046 |

### 3.10 Test Scenarios

| ID | Category | Description | Priority | Expected Outcome |
|----|----------|-------------|----------|------------------|
| TC-E3.01 | Happy Path | Extract members to new base class | P0 | Base created, derived inherits |
| TC-E3.02 | Happy Path | Extract to separate file | P0 | New file with base class |
| TC-E3.03 | Happy Path | Create abstract base class | P1 | Abstract modifier added |
| TC-E3.04 | Happy Path | Private members become protected | P0 | Visibility elevated |
| TC-E3.05 | Happy Path | Virtual members preserved | P1 | Virtual modifier retained |
| TC-E3.06 | Edge | Struct as source type | P0 | Error 3043: TYPE_NOT_CLASS |
| TC-E3.07 | Edge | Sealed class as source | P0 | Error 3044: TYPE_IS_SEALED |
| TC-E3.08 | Edge | Class with existing base | P0 | Error 3045: HAS_EXISTING_BASE |
| TC-E3.09 | Edge | Base class name exists | P0 | Error 3003: NAME_COLLISION |
| TC-E3.10 | Edge | Member dependency conflict | P1 | Error 3046: MEMBER_DEPENDENCY_CONFLICT |
| TC-E3.11 | Preview | Preview mode shows changes | P0 | Changes described, not applied |
| TC-E3.12 | Complex | Generic class extraction | P1 | Type parameters propagated |

---

## 4. UC-E4: Extract Variable

### 4.1 Overview

| Property | Value |
|----------|-------|
| ID | UC-E4 |
| Name | Extract Variable |
| Actor | AI Agent (via MCP) |
| Priority | Tier 2 - Important |
| Complexity | Low |
| Error Code Range | 3047 |

### 4.2 Description

Extract a selected expression into a local variable. The expression is assigned to the new variable, and the original location is replaced with a reference to the variable.

### 4.3 Preconditions

| ID | Condition | Verification | Error Code |
|----|-----------|--------------|------------|
| PRE-E4.1 | Workspace loaded and ready | WorkspaceState == Ready | 2005 |
| PRE-E4.2 | Source file exists in workspace | Document in solution | 2001/2002 |
| PRE-E4.3 | Selection is valid expression | Expression syntax | 2013 |
| PRE-E4.4 | Expression has determinate type | Type resolvable | 3047 |
| PRE-E4.5 | Variable name provided | Non-empty string | 1005 |
| PRE-E4.6 | Variable name is valid identifier | C# identifier rules | 1003 |

### 4.4 Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-E4.1 | Variable declared before original expression | Declaration precedes usage |
| POST-E4.2 | Variable initialized with extracted expression | Assignment present |
| POST-E4.3 | Original expression replaced with variable reference | Identifier substituted |
| POST-E4.4 | Solution compiles | Zero compilation errors |

### 4.5 Main Flow

1. Agent calls extract_variable with parameters
2. System validates input parameters
3. System locates expression at specified selection
4. System determines expression type
5. System generates variable declaration with type inference
6. System inserts declaration before statement containing expression
7. System replaces expression with variable reference
8. System optionally replaces all identical occurrences
9. System returns success response

### 4.6 Exception Flows

| ID | Trigger | Error Code | Response |
|----|---------|------------|----------|
| EF-E4.1 | No expression at selection | 2013 | EXPRESSION_NOT_FOUND |
| EF-E4.2 | Variable name conflicts in scope | 3010 | NAME_CONFLICT_SCOPE |
| EF-E4.3 | Expression has void type | 3047 | VOID_EXPRESSION |
| EF-E4.4 | Expression is assignment target | 3047 | CANNOT_EXTRACT_LVALUE |

### 4.7 Business Rules

| ID | Rule | Rationale |
|----|------|-----------|
| BR-E4.1 | Expression must have non-void type | Variables hold values |
| BR-E4.2 | Declaration placed before containing statement | Scope correctness |
| BR-E4.3 | Type inference preferred when unambiguous | Conciseness |
| BR-E4.4 | Explicit type when inference unclear | Clarity |
| BR-E4.5 | Side effects evaluated once | Semantic preservation |

### 4.8 Input Parameters (MCP Schema)

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | Absolute path to source file |
| startLine | integer | Yes | 1-based start line of expression |
| startColumn | integer | Yes | 1-based start column of expression |
| endLine | integer | Yes | 1-based end line of expression |
| endColumn | integer | Yes | 1-based end column of expression |
| variableName | string | Yes | Name for the new variable |
| replaceAll | boolean | No | Replace all identical occurrences (default: false) |
| useVar | boolean | No | Use var for type declaration (default: true) |
| preview | boolean | No | Preview mode (default: false) |

### 4.9 Validation Rules

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-E4.01 | sourceFile | Must be valid absolute path | 1001 |
| IV-E4.02 | startLine | Must be >= 1 | 1006 |
| IV-E4.03 | startColumn | Must be >= 1 | 1007 |
| IV-E4.04 | endLine | Must be >= startLine | 1008 |
| IV-E4.05 | endColumn | If same line, must be > startColumn | 1008 |
| IV-E4.06 | variableName | Must be valid C# identifier | 1003 |

| Rule ID | Semantic Rule | Error Code |
|---------|---------------|------------|
| SV-E4.01 | Selection must contain valid expression | 2013 |
| SV-E4.02 | Expression must have non-void type | 3047 |
| SV-E4.03 | Variable name must not conflict in scope | 3010 |
| SV-E4.04 | Expression must not be assignment target | 3047 |

### 4.10 Test Scenarios

| ID | Category | Description | Priority | Expected Outcome |
|----|----------|-------------|----------|------------------|
| TC-E4.01 | Happy Path | Extract simple expression | P0 | Variable created, expression replaced |
| TC-E4.02 | Happy Path | Extract with type inference (var) | P0 | Uses var keyword |
| TC-E4.03 | Happy Path | Extract with explicit type | P1 | Uses explicit type name |
| TC-E4.04 | Happy Path | Replace all occurrences | P1 | All identical expressions replaced |
| TC-E4.05 | Happy Path | Extract method call result | P0 | Method called once, result stored |
| TC-E4.06 | Happy Path | Extract property access | P0 | Property accessed once |
| TC-E4.07 | Edge | Selection not on expression | P0 | Error 2013: EXPRESSION_NOT_FOUND |
| TC-E4.08 | Edge | Void method call | P0 | Error 3047: VOID_EXPRESSION |
| TC-E4.09 | Edge | Variable name exists in scope | P0 | Error 3010: NAME_CONFLICT_SCOPE |
| TC-E4.10 | Edge | Extract assignment target (lvalue) | P1 | Error 3047: CANNOT_EXTRACT_LVALUE |
| TC-E4.11 | Preview | Preview mode shows diff | P0 | Changes described, not applied |
| TC-E4.12 | Complex | Expression with generics | P1 | Type correctly inferred |
| TC-E4.13 | Complex | Expression in lambda | P1 | Correct scope handling |

---

## 5. UC-E5: Extract Constant

### 5.1 Overview

| Property | Value |
|----------|-------|
| ID | UC-E5 |
| Name | Extract Constant |
| Actor | AI Agent (via MCP) |
| Priority | Tier 2 - Important |
| Complexity | Low |
| Error Code Range | 3048-3049 |

### 5.2 Description

Extract a literal value into a named constant field. Numeric, string, boolean, and other compile-time constant values are extracted to const fields. Reference types that cannot be const are extracted to static readonly fields.

### 5.3 Preconditions

| ID | Condition | Verification | Error Code |
|----|-----------|--------------|------------|
| PRE-E5.1 | Workspace loaded and ready | WorkspaceState == Ready | 2005 |
| PRE-E5.2 | Source file exists in workspace | Document in solution | 2001/2002 |
| PRE-E5.3 | Selection is literal value | Literal syntax | 3048 |
| PRE-E5.4 | Constant name provided | Non-empty string | 1005 |
| PRE-E5.5 | Constant name is valid identifier | C# identifier rules | 1003 |

### 5.4 Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-E5.1 | Constant field declared in containing type | Field exists |
| POST-E5.2 | Field initialized with literal value | Assignment present |
| POST-E5.3 | Original literal replaced with constant reference | Field referenced |
| POST-E5.4 | Solution compiles | Zero compilation errors |

### 5.5 Main Flow

1. Agent calls extract_constant with parameters
2. System validates input parameters
3. System locates literal at specified selection
4. System determines if value is compile-time constant
5. System generates const or static readonly field as appropriate
6. System places field in containing type
7. System replaces literal with field reference
8. System optionally replaces all identical literals
9. System returns success response

### 5.6 Exception Flows

| ID | Trigger | Error Code | Response |
|----|---------|------------|----------|
| EF-E5.1 | Selection is not a literal | 3048 | NOT_LITERAL_VALUE |
| EF-E5.2 | Constant name conflicts | 3010 | NAME_CONFLICT_SCOPE |
| EF-E5.3 | Not inside a type | 3049 | NO_CONTAINING_TYPE |
| EF-E5.4 | Expression, not literal | 3037 | NOT_COMPILE_TIME_CONSTANT |

### 5.7 Business Rules

| ID | Rule | Rationale |
|----|------|-----------|
| BR-E5.1 | const for primitives and strings | Compile-time constant semantics |
| BR-E5.2 | static readonly for reference types | Runtime initialization |
| BR-E5.3 | Constant placed at class level | Type scope |
| BR-E5.4 | Private visibility by default | Encapsulation |
| BR-E5.5 | PascalCase naming convention | C# style guidelines |
| BR-E5.6 | Placed before first member or in constants region | Organization |

### 5.8 Input Parameters (MCP Schema)

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | Absolute path to source file |
| startLine | integer | Yes | 1-based start line of literal |
| startColumn | integer | Yes | 1-based start column of literal |
| endLine | integer | Yes | 1-based end line of literal |
| endColumn | integer | Yes | 1-based end column of literal |
| constantName | string | Yes | Name for the constant field |
| visibility | string | No | Access modifier (default: private) |
| replaceAll | boolean | No | Replace all identical literals (default: false) |
| preview | boolean | No | Preview mode (default: false) |

### 5.9 Validation Rules

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-E5.01 | sourceFile | Must be valid absolute path | 1001 |
| IV-E5.02 | startLine | Must be >= 1 | 1006 |
| IV-E5.03 | startColumn | Must be >= 1 | 1007 |
| IV-E5.04 | endLine | Must be >= startLine | 1008 |
| IV-E5.05 | endColumn | If same line, must be > startColumn | 1008 |
| IV-E5.06 | constantName | Must be valid C# identifier | 1003 |
| IV-E5.07 | visibility | Must be valid access modifier | 1014 |

| Rule ID | Semantic Rule | Error Code |
|---------|---------------|------------|
| SV-E5.01 | Selection must be literal value | 3048 |
| SV-E5.02 | Must be inside a type declaration | 3049 |
| SV-E5.03 | Constant name must not conflict in type | 3010 |
| SV-E5.04 | Value must be compile-time constant (for const) | 3037 |

### 5.10 Test Scenarios

| ID | Category | Description | Priority | Expected Outcome |
|----|----------|-------------|----------|------------------|
| TC-E5.01 | Happy Path | Extract integer literal | P0 | const int field created |
| TC-E5.02 | Happy Path | Extract string literal | P0 | const string field created |
| TC-E5.03 | Happy Path | Extract boolean literal | P1 | const bool field created |
| TC-E5.04 | Happy Path | Extract decimal/double literal | P1 | const with correct type |
| TC-E5.05 | Happy Path | Replace all occurrences | P0 | All matching literals replaced |
| TC-E5.06 | Happy Path | Custom visibility | P1 | Access modifier applied |
| TC-E5.07 | Edge | Non-literal expression | P0 | Error 3048: NOT_LITERAL_VALUE |
| TC-E5.08 | Edge | Outside type declaration | P0 | Error 3049: NO_CONTAINING_TYPE |
| TC-E5.09 | Edge | Constant name exists | P0 | Error 3010: NAME_CONFLICT_SCOPE |
| TC-E5.10 | Edge | Array initializer | P1 | Uses static readonly |
| TC-E5.11 | Edge | Interpolated string | P1 | Error or static readonly |
| TC-E5.12 | Preview | Preview mode shows diff | P0 | Changes described, not applied |
| TC-E5.13 | Naming | Naming convention warning | P2 | Warning if not PascalCase |

---

## 6. Error Code Allocation

### 6.1 Assigned Error Codes (3040-3049)

| Code | Constant | Message | Operation |
|------|----------|---------|-----------|
| 3040 | NO_EXTRACTABLE_MEMBERS | Type has no public extractable members | extract_interface |
| 3041 | MEMBER_NOT_PUBLIC | Selected member is not public | extract_interface |
| 3042 | STATIC_MEMBER_NOT_EXTRACTABLE | Static members cannot be extracted to interface | extract_interface |
| 3043 | TYPE_NOT_CLASS | Source type must be a class for base class extraction | extract_base_class |
| 3044 | TYPE_IS_SEALED | Sealed classes cannot have base class extracted | extract_base_class |
| 3045 | HAS_EXISTING_BASE | Class already has a base class | extract_base_class |
| 3046 | MEMBER_DEPENDENCY_CONFLICT | Member depends on non-movable members | extract_base_class |
| 3047 | VOID_EXPRESSION | Expression has void type or is not extractable | extract_variable |
| 3048 | NOT_LITERAL_VALUE | Selection is not a literal value | extract_constant |
| 3049 | NO_CONTAINING_TYPE | Code is not inside a type declaration | extract_constant |

### 6.2 Reused Error Codes

| Code | Constant | Reused By |
|------|----------|-----------|
| 1001 | INVALID_SOURCE_PATH | All operations |
| 1003 | INVALID_SYMBOL_NAME | All operations (for names) |
| 1005 | MISSING_REQUIRED_PARAM | All operations |
| 1006 | INVALID_LINE_NUMBER | extract_variable, extract_constant |
| 1007 | INVALID_COLUMN_NUMBER | extract_variable, extract_constant |
| 1008 | INVALID_SELECTION_RANGE | extract_variable, extract_constant |
| 1012 | INVALID_MEMBER_LIST | extract_interface, extract_base_class |
| 1014 | INVALID_VISIBILITY | extract_constant |
| 2001 | SOURCE_FILE_NOT_FOUND | All operations |
| 2002 | SOURCE_NOT_IN_WORKSPACE | All operations |
| 2005 | WORKSPACE_NOT_LOADED | All operations |
| 2012 | MEMBER_NOT_FOUND | extract_interface, extract_base_class |
| 2013 | EXPRESSION_NOT_FOUND | extract_variable |
| 2015 | TYPE_NOT_FOUND | extract_interface, extract_base_class |
| 3003 | NAME_COLLISION | extract_interface, extract_base_class |
| 3005 | CIRCULAR_REFERENCE | extract_base_class |
| 3010 | NAME_CONFLICT_SCOPE | extract_variable, extract_constant |
| 3037 | NOT_COMPILE_TIME_CONSTANT | extract_constant |

---

## 7. Cross-Operation Concerns

### 7.1 Preview Mode

All operations support preview=true:
- Executes full validation and change computation
- Returns pendingChanges array with file operations
- Does not modify workspace or filesystem
- No locks held after response

### 7.2 Atomicity

All operations are atomic:
- All changes apply together or none apply
- Rollback on any failure
- Workspace remains consistent

### 7.3 Compilation Preservation

All operations:
- Verify solution compiles after changes
- Return error 4004 if changes break compilation
- Include compilation diagnostics in error details

### 7.4 Timeout Behavior

| Operation | Default Timeout | Max Timeout |
|-----------|-----------------|-------------|
| extract_interface | 15s | 60s |
| extract_base_class | 30s | 120s |
| extract_variable | 10s | 30s |
| extract_constant | 10s | 30s |

---

## 8. Open Questions

| ID | Question | Impact | Owner |
|----|----------|--------|-------|
| OQ-1 | Should extract_interface update usages to use interface type? | High - affects scope | TBD |
| OQ-2 | Should extract_base_class support multiple derived classes? | Medium - future capability | TBD |
| OQ-3 | Should extract_constant support extraction to separate constants file? | Low - enhancement | TBD |
| OQ-4 | What behavior for extract_variable in expression-bodied members? | Medium - edge case | TBD |

---

## 9. Assumptions and Constraints

### 9.1 Assumptions

| ID | Assumption | Risk if False |
|----|------------|---------------|
| A-1 | Roslyn provides extract refactoring APIs | High - custom implementation needed |
| A-2 | Single file modifications suffice for extract_variable/constant | Low - always single file |
| A-3 | Interface naming convention (I-prefix) is advisory only | Low - user preference |

### 9.2 Constraints

| ID | Constraint | Rationale |
|----|------------|-----------|
| C-1 | C# language version determined by project | Syntax compatibility |
| C-2 | Maximum solution size: 100 projects | Performance baseline |
| C-3 | File encoding preserved | Existing tooling compatibility |

---

## 10. Revision History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-01-28 | BA | Initial specification |
