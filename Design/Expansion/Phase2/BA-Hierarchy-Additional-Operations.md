# Business Analysis: Hierarchy and Additional Operations

## Document Information
| Property | Value |
|----------|-------|
| Version | 1.0 |
| Status | Draft |
| Author | Business Analyst |
| Date | 2026-01-28 |
| Error Code Range | 3105-3130 |

---

## Part 1: Hierarchy Operations

---

## 1. UC-H1: Pull Member Up

### 1.1 Use Case Specification

| Property | Value |
|----------|-------|
| ID | UC-H1 |
| Name | pull_member_up |
| Priority | Tier 3 - Valuable |
| Complexity | High |

**Description**: Move a member (method, property, field, event) from a derived class to its base class or interface, consolidating shared behavior upward in the inheritance hierarchy.

**Actors**:
- Primary: MCP Client (Claude, IDE plugin)
- Secondary: Roslyn Workspace, File System

**Preconditions**:
| ID | Condition |
|----|-----------|
| PRE-H1.01 | Workspace loaded with solution |
| PRE-H1.02 | Source file exists and is part of workspace |
| PRE-H1.03 | Type exists in source file |
| PRE-H1.04 | Member exists in type |
| PRE-H1.05 | Base class or interface exists and is editable |
| PRE-H1.06 | Member dependencies resolvable in base context |

**Postconditions**:
| ID | Condition |
|----|-----------|
| POST-H1.01 | Member exists in target base class/interface |
| POST-H1.02 | Member removed from derived class (unless makeAbstract) |
| POST-H1.03 | All references remain valid |
| POST-H1.04 | Compilation succeeds |
| POST-H1.05 | If makeAbstract: abstract declaration in base, implementation stays in derived |

**Main Flow**:
1. Client invokes `pull_member_up` with source file, type, member, and options
2. System validates input parameters
3. System resolves symbol and locates member in type
4. System identifies target base class (explicit or nearest)
5. System analyzes member dependencies (fields, methods, types used)
6. System verifies dependencies are accessible from base context
7. System generates member declaration in base class
8. System removes member from derived class (or keeps as override if makeAbstract)
9. System updates all affected files
10. System returns operation result

**Alternate Flows**:

| ID | Condition | Flow |
|----|-----------|------|
| AF-H1.01 | makeAbstract=true | Generate abstract declaration in base; keep implementation as override in derived |
| AF-H1.02 | Multiple base classes | Use targetBaseClass parameter to select; error if not specified |
| AF-H1.03 | Pull to interface | Generate interface member signature only |
| AF-H1.04 | preview=true | Return change set without applying |

**Exception Flows**:

| ID | Condition | Error Code |
|----|-----------|------------|
| EX-H1.01 | Member depends on derived-only members | 3105 |
| EX-H1.02 | Base class is sealed | 3106 |
| EX-H1.03 | No common base exists | 3107 |
| EX-H1.04 | Conflicts with existing base member | 3108 |
| EX-H1.05 | Base class not editable (external) | 3109 |

### 1.2 MCP Tool Schema

```json
{
  "name": "pull_member_up",
  "description": "Move member from derived class to base class or interface",
  "inputSchema": {
    "type": "object",
    "properties": {
      "sourceFile": {
        "type": "string",
        "description": "Absolute path to file containing derived class"
      },
      "typeName": {
        "type": "string",
        "description": "Name of the derived class/type"
      },
      "memberName": {
        "type": "string",
        "description": "Name of member to pull up"
      },
      "targetBaseClass": {
        "type": "string",
        "description": "Target base class name (optional, uses nearest if omitted)"
      },
      "makeAbstract": {
        "type": "boolean",
        "default": false,
        "description": "Create abstract member in base, keep implementation in derived"
      },
      "preview": {
        "type": "boolean",
        "default": false,
        "description": "Preview changes without applying"
      }
    },
    "required": ["sourceFile", "typeName", "memberName"]
  }
}
```

### 1.3 Output Format

**Success Response**:
```json
{
  "success": true,
  "operation": "pull_member_up",
  "memberPulled": "CalculateTotal",
  "sourceType": "DerivedClass",
  "targetType": "BaseClass",
  "madeAbstract": false,
  "filesModified": [
    "C:/Project/DerivedClass.cs",
    "C:/Project/BaseClass.cs"
  ],
  "changes": [
    {
      "file": "C:/Project/BaseClass.cs",
      "changeType": "memberAdded",
      "member": "CalculateTotal"
    },
    {
      "file": "C:/Project/DerivedClass.cs",
      "changeType": "memberRemoved",
      "member": "CalculateTotal"
    }
  ]
}
```

### 1.4 Validation Rules

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-H1.01 | sourceFile | Must be valid absolute path | 1001 |
| IV-H1.02 | typeName | Must not be null/empty | 1005 |
| IV-H1.03 | memberName | Must not be null/empty | 1005 |
| IV-H1.04 | targetBaseClass | If provided, must exist as base | 2010 |
| SV-H1.01 | member | Must not depend on derived-only members | 3105 |
| SV-H1.02 | baseClass | Must not be sealed | 3106 |
| SV-H1.03 | member | Must not conflict with existing base member | 3108 |
| SV-H1.04 | baseClass | Must be editable (in workspace) | 3109 |

### 1.5 Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-H1.01 | Happy Path | Pull method to immediate base class | P0 |
| TC-H1.02 | Happy Path | Pull property to base class | P0 |
| TC-H1.03 | Happy Path | Pull field to base class | P1 |
| TC-H1.04 | Happy Path | Pull event to base class | P2 |
| TC-H1.05 | Abstract | Pull as abstract method | P1 |
| TC-H1.06 | Abstract | Pull as abstract property | P1 |
| TC-H1.07 | Interface | Pull method to interface | P1 |
| TC-H1.08 | Chain | Pull to grandparent base class | P2 |
| TC-H1.09 | Edge | Member uses protected base members | P1 |
| TC-H1.10 | Edge | Member with generic parameters | P2 |
| TC-H1.11 | Negative | Member depends on derived field | P0 |
| TC-H1.12 | Negative | Base class is sealed | P0 |
| TC-H1.13 | Negative | Name conflict in base | P0 |
| TC-H1.14 | Negative | Base class is external | P0 |
| TC-H1.15 | Preview | Preview returns correct changes | P0 |

---

## 2. UC-H2: Push Member Down

### 2.1 Use Case Specification

| Property | Value |
|----------|-------|
| ID | UC-H2 |
| Name | push_member_down |
| Priority | Tier 4 - Specialized |
| Complexity | High |

**Description**: Move a member from a base class to all derived classes, distributing shared behavior downward when it no longer belongs in the base.

**Actors**:
- Primary: MCP Client
- Secondary: Roslyn Workspace, File System

**Preconditions**:
| ID | Condition |
|----|-----------|
| PRE-H2.01 | Workspace loaded |
| PRE-H2.02 | Source file exists |
| PRE-H2.03 | Base type exists |
| PRE-H2.04 | Member exists in base type |
| PRE-H2.05 | At least one derived class exists |
| PRE-H2.06 | All derived classes are editable |

**Postconditions**:
| ID | Condition |
|----|-----------|
| POST-H2.01 | Member exists in all derived classes |
| POST-H2.02 | Member removed from base class (or made abstract) |
| POST-H2.03 | References updated appropriately |
| POST-H2.04 | Compilation succeeds |

**Main Flow**:
1. Client invokes push_member_down with parameters
2. System validates input
3. System locates member in base class
4. System discovers all derived classes in workspace
5. System verifies all derived classes are editable
6. System generates member copy in each derived class
7. System removes member from base (or makes abstract if leaveAbstract)
8. System updates references if needed
9. System returns result

**Alternate Flows**:

| ID | Condition | Flow |
|----|-----------|------|
| AF-H2.01 | leaveAbstract=true | Leave abstract declaration in base |
| AF-H2.02 | targetDerived specified | Push only to specified derived classes |
| AF-H2.03 | preview=true | Return changes without applying |

**Exception Flows**:

| ID | Condition | Error Code |
|----|-----------|------------|
| EX-H2.01 | No derived classes found | 2011 |
| EX-H2.02 | Derived class not editable | 3110 |
| EX-H2.03 | Conflicts with derived member | 3108 |
| EX-H2.04 | Member is required by base class contract | 3111 |

### 2.2 MCP Tool Schema

\`\`\`json
{
  "name": "push_member_down",
  "description": "Move member from base class to all derived classes",
  "inputSchema": {
    "type": "object",
    "properties": {
      "sourceFile": {
        "type": "string",
        "description": "Absolute path to file containing base class"
      },
      "typeName": {
        "type": "string",
        "description": "Name of the base class"
      },
      "memberName": {
        "type": "string",
        "description": "Name of member to push down"
      },
      "targetDerived": {
        "type": "array",
        "items": { "type": "string" },
        "description": "Specific derived classes to push to (optional, all if omitted)"
      },
      "leaveAbstract": {
        "type": "boolean",
        "default": false,
        "description": "Leave abstract declaration in base class"
      },
      "preview": {
        "type": "boolean",
        "default": false,
        "description": "Preview changes without applying"
      }
    },
    "required": ["sourceFile", "typeName", "memberName"]
  }
}
\`\`\`

### 2.3 Output Format

**Success Response**:
\`\`\`json
{
  "success": true,
  "operation": "push_member_down",
  "memberPushed": "ProcessData",
  "sourceType": "BaseProcessor",
  "targetTypes": ["ConcreteProcessorA", "ConcreteProcessorB"],
  "leftAbstract": false,
  "filesModified": [
    "C:/Project/BaseProcessor.cs",
    "C:/Project/ConcreteProcessorA.cs",
    "C:/Project/ConcreteProcessorB.cs"
  ]
}
\`\`\`

### 2.4 Validation Rules

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-H2.01 | sourceFile | Must be valid path | 1001 |
| IV-H2.02 | typeName | Must exist | 2015 |
| IV-H2.03 | memberName | Must exist in type | 2012 |
| SV-H2.01 | derivedClasses | At least one must exist | 2011 |
| SV-H2.02 | derivedClasses | All must be editable | 3110 |
| SV-H2.03 | member | Must not conflict with derived | 3108 |

### 2.5 Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-H2.01 | Happy Path | Push method to all derived | P0 |
| TC-H2.02 | Happy Path | Push property to all derived | P1 |
| TC-H2.03 | Selective | Push to specific derived classes | P1 |
| TC-H2.04 | Abstract | Leave abstract in base | P1 |
| TC-H2.05 | Chain | Push through multiple inheritance levels | P2 |
| TC-H2.06 | Negative | No derived classes exist | P0 |
| TC-H2.07 | Negative | Derived class is external | P0 |
| TC-H2.08 | Negative | Conflicts with derived member | P0 |
| TC-H2.09 | Preview | Preview mode correct | P0 |

---

## Part 2: Additional Operations

---

## 3. UC-A1: Introduce Field

### 3.1 Use Case Specification

| Property | Value |
|----------|-------|
| ID | UC-A1 |
| Name | introduce_field |
| Priority | Tier 3 - Valuable |
| Complexity | Medium |

**Description**: Extract an expression or literal value to a class-level field, promoting local knowledge to class state.

**Actors**:
- Primary: MCP Client
- Secondary: Roslyn Workspace

**Preconditions**:
| ID | Condition |
|----|-----------|
| PRE-A1.01 | Workspace loaded |
| PRE-A1.02 | Source file exists |
| PRE-A1.03 | Selection contains valid expression |
| PRE-A1.04 | Selection is within a type member |

**Postconditions**:
| ID | Condition |
|----|-----------|
| POST-A1.01 | New field exists in containing type |
| POST-A1.02 | Original expression replaced with field reference |
| POST-A1.03 | Field initialized appropriately |

**Main Flow**:
1. Client invokes introduce_field with selection and field name
2. System validates parameters
3. System extracts expression from selection
4. System infers field type from expression
5. System generates field declaration with initialization
6. System replaces original expression with field reference
7. System optionally replaces all identical expressions
8. System returns result

**Alternate Flows**:

| ID | Condition | Flow |
|----|-----------|------|
| AF-A1.01 | isReadonly=true | Generate readonly field |
| AF-A1.02 | isStatic=true | Generate static field |
| AF-A1.03 | initializeInConstructor=true | Move initialization to constructor |
| AF-A1.04 | replaceAll=true | Replace all identical expressions |

**Exception Flows**:

| ID | Condition | Error Code |
|----|-----------|------------|
| EX-A1.01 | No expression at selection | 2013 |
| EX-A1.02 | Field name already exists | 3003 |
| EX-A1.03 | Expression not valid for field init | 3112 |
| EX-A1.04 | Expression captures local state | 3113 |

### 3.2 MCP Tool Schema

\`\`\`json
{
  "name": "introduce_field",
  "description": "Extract expression to a class field",
  "inputSchema": {
    "type": "object",
    "properties": {
      "sourceFile": {
        "type": "string",
        "description": "Absolute path to source file"
      },
      "startLine": { "type": "integer", "description": "Selection start line" },
      "startColumn": { "type": "integer", "description": "Selection start column" },
      "endLine": { "type": "integer", "description": "Selection end line" },
      "endColumn": { "type": "integer", "description": "Selection end column" },
      "fieldName": {
        "type": "string",
        "description": "Name for the new field"
      },
      "isReadonly": {
        "type": "boolean",
        "default": false,
        "description": "Create as readonly field"
      },
      "isStatic": {
        "type": "boolean",
        "default": false,
        "description": "Create as static field"
      },
      "initializeInConstructor": {
        "type": "boolean",
        "default": false,
        "description": "Initialize in constructor instead of inline"
      },
      "replaceAll": {
        "type": "boolean",
        "default": false,
        "description": "Replace all identical expressions"
      },
      "preview": {
        "type": "boolean",
        "default": false
      }
    },
    "required": ["sourceFile", "startLine", "startColumn", "endLine", "endColumn", "fieldName"]
  }
}
\`\`\`

### 3.3 Output Format

\`\`\`json
{
  "success": true,
  "operation": "introduce_field",
  "fieldName": "_maxRetries",
  "fieldType": "int",
  "isReadonly": true,
  "isStatic": false,
  "replacementCount": 3,
  "filesModified": ["C:/Project/Service.cs"]
}
\`\`\`

### 3.4 Error Codes

| Code | Constant | Message |
|------|----------|---------|
| 3112 | EXPRESSION_NOT_FIELD_INITIALIZABLE | Expression cannot be used as field initializer |
| 3113 | EXPRESSION_CAPTURES_LOCAL | Expression captures local variable state |

### 3.5 Validation Rules

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-A1.01 | startLine | Must be >= 1 | 1006 |
| IV-A1.02 | fieldName | Must be valid identifier | 1003 |
| SV-A1.01 | fieldName | Must not exist in type | 3003 |
| SV-A1.02 | expression | Must not capture locals | 3113 |
| SV-A1.03 | expression | Must be valid field initializer | 3112 |

### 3.6 Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-A1.01 | Happy Path | Extract literal to field | P0 |
| TC-A1.02 | Happy Path | Extract expression to field | P0 |
| TC-A1.03 | Readonly | Create readonly field | P1 |
| TC-A1.04 | Static | Create static field | P1 |
| TC-A1.05 | Constructor | Initialize in constructor | P1 |
| TC-A1.06 | ReplaceAll | Replace multiple occurrences | P1 |
| TC-A1.07 | Negative | Field name exists | P0 |
| TC-A1.08 | Negative | Expression uses local variable | P0 |
| TC-A1.09 | Preview | Preview changes | P0 |

---

## 4. UC-A2: Introduce Parameter

### 4.1 Use Case Specification

| Property | Value |
|----------|-------|
| ID | UC-A2 |
| Name | introduce_parameter |
| Priority | Tier 3 - Valuable |
| Complexity | High |

**Description**: Replace a hardcoded value or expression with a method parameter, making the method more flexible and testable.

**Actors**:
- Primary: MCP Client
- Secondary: Roslyn Workspace

**Preconditions**:
| ID | Condition |
|----|-----------|
| PRE-A2.01 | Workspace loaded |
| PRE-A2.02 | Selection within method body |
| PRE-A2.03 | Selection contains valid expression |

**Postconditions**:
| ID | Condition |
|----|-----------|
| POST-A2.01 | New parameter added to method signature |
| POST-A2.02 | Original expression replaced with parameter reference |
| POST-A2.03 | All call sites updated with original value as argument |
| POST-A2.04 | Overriding/implementing methods updated if applicable |

**Main Flow**:
1. Client invokes introduce_parameter
2. System validates parameters
3. System extracts expression and infers type
4. System adds parameter to method signature
5. System replaces expression with parameter reference
6. System updates all call sites with original expression as argument
7. System updates overrides/implementations
8. System returns result

**Alternate Flows**:

| ID | Condition | Flow |
|----|-----------|------|
| AF-A2.01 | useOptional=true | Add parameter with default value |
| AF-A2.02 | replaceAll=true | Replace all identical expressions |
| AF-A2.03 | position specified | Insert parameter at specific position |

**Exception Flows**:

| ID | Condition | Error Code |
|----|-----------|------------|
| EX-A2.01 | No expression at selection | 2013 |
| EX-A2.02 | Parameter name exists | 3080 |
| EX-A2.03 | Breaks interface contract | 3084 |
| EX-A2.04 | Expression type unresolvable | 3114 |

### 4.2 MCP Tool Schema

\`\`\`json
{
  "name": "introduce_parameter",
  "description": "Add parameter for hardcoded value and update call sites",
  "inputSchema": {
    "type": "object",
    "properties": {
      "sourceFile": { "type": "string" },
      "startLine": { "type": "integer" },
      "startColumn": { "type": "integer" },
      "endLine": { "type": "integer" },
      "endColumn": { "type": "integer" },
      "parameterName": {
        "type": "string",
        "description": "Name for the new parameter"
      },
      "position": {
        "type": "integer",
        "description": "Position in parameter list (optional, appends if omitted)"
      },
      "useOptional": {
        "type": "boolean",
        "default": false,
        "description": "Make parameter optional with default value"
      },
      "replaceAll": {
        "type": "boolean",
        "default": false,
        "description": "Replace all identical expressions"
      },
      "preview": { "type": "boolean", "default": false }
    },
    "required": ["sourceFile", "startLine", "startColumn", "endLine", "endColumn", "parameterName"]
  }
}
\`\`\`

### 4.3 Output Format

\`\`\`json
{
  "success": true,
  "operation": "introduce_parameter",
  "parameterName": "timeout",
  "parameterType": "TimeSpan",
  "methodName": "SendRequest",
  "callSitesUpdated": 5,
  "overridesUpdated": 2,
  "filesModified": [
    "C:/Project/HttpClient.cs",
    "C:/Project/ApiService.cs"
  ]
}
\`\`\`

### 4.4 Error Codes

| Code | Constant | Message |
|------|----------|---------|
| 3114 | EXPRESSION_TYPE_UNRESOLVABLE | Cannot determine type of expression |

### 4.5 Validation Rules

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-A2.01 | parameterName | Must be valid identifier | 1003 |
| IV-A2.02 | position | Must be >= 0 and <= param count | 1011 |
| SV-A2.01 | parameterName | Must not exist in method | 3080 |
| SV-A2.02 | change | Must not break interface contract | 3084 |

### 4.6 Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-A2.01 | Happy Path | Introduce parameter from literal | P0 |
| TC-A2.02 | Happy Path | Introduce parameter from expression | P0 |
| TC-A2.03 | CallSites | Update all call sites | P0 |
| TC-A2.04 | Optional | Create optional parameter | P1 |
| TC-A2.05 | Position | Insert at specific position | P1 |
| TC-A2.06 | Override | Update override methods | P1 |
| TC-A2.07 | Negative | Parameter name exists | P0 |
| TC-A2.08 | Negative | Breaks interface contract | P0 |
| TC-A2.09 | Preview | Preview changes | P0 |

---

## 5. UC-A3: Make Method Static

### 5.1 Use Case Specification

| Property | Value |
|----------|-------|
| ID | UC-A3 |
| Name | make_method_static |
| Priority | Tier 3 - Valuable |
| Complexity | Medium |

**Description**: Add static modifier to a method that does not use instance state, and update all call sites to use static invocation.

**Actors**:
- Primary: MCP Client
- Secondary: Roslyn Workspace

**Preconditions**:
| ID | Condition |
|----|-----------|
| PRE-A3.01 | Workspace loaded |
| PRE-A3.02 | Method exists |
| PRE-A3.03 | Method does not use instance members |

**Postconditions**:
| ID | Condition |
|----|-----------|
| POST-A3.01 | Method has static modifier |
| POST-A3.02 | All call sites updated to static invocation |
| POST-A3.03 | Compilation succeeds |

**Main Flow**:
1. Client invokes make_method_static
2. System validates parameters
3. System locates method
4. System verifies method does not use instance state
5. System adds static modifier
6. System updates all call sites (instance.Method() -> Type.Method())
7. System returns result

**Alternate Flows**:

| ID | Condition | Flow |
|----|-----------|------|
| AF-A3.01 | passInstanceAsParameter=true | Add this as first parameter for instance member access |

**Exception Flows**:

| ID | Condition | Error Code |
|----|-----------|------------|
| EX-A3.01 | Method not found | 2006 |
| EX-A3.02 | Method uses instance members | 3115 |
| EX-A3.03 | Method is virtual/override | 3051 |
| EX-A3.04 | Method implements interface | 3116 |

### 5.2 MCP Tool Schema

\`\`\`json
{
  "name": "make_method_static",
  "description": "Add static modifier to method and update call sites",
  "inputSchema": {
    "type": "object",
    "properties": {
      "sourceFile": { "type": "string" },
      "typeName": { "type": "string", "description": "Containing type name" },
      "methodName": { "type": "string", "description": "Method to make static" },
      "line": { "type": "integer", "description": "Line for disambiguation (optional)" },
      "passInstanceAsParameter": {
        "type": "boolean",
        "default": false,
        "description": "Add instance as first parameter if method uses instance members"
      },
      "preview": { "type": "boolean", "default": false }
    },
    "required": ["sourceFile", "typeName", "methodName"]
  }
}
\`\`\`

### 5.3 Output Format

\`\`\`json
{
  "success": true,
  "operation": "make_method_static",
  "methodName": "CalculateHash",
  "typeName": "StringHelper",
  "callSitesUpdated": 12,
  "filesModified": [
    "C:/Project/StringHelper.cs",
    "C:/Project/Validator.cs"
  ]
}
\`\`\`

### 5.4 Error Codes

| Code | Constant | Message |
|------|----------|---------|
| 3115 | METHOD_USES_INSTANCE_STATE | Method references instance members |
| 3116 | METHOD_IMPLEMENTS_INTERFACE | Static methods cannot implement interface members |

### 5.5 Validation Rules

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-A3.01 | methodName | Must not be null/empty | 1005 |
| SV-A3.01 | method | Must not use instance state | 3115 |
| SV-A3.02 | method | Must not be virtual/override | 3051 |
| SV-A3.03 | method | Must not implement interface | 3116 |

### 5.6 Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-A3.01 | Happy Path | Make pure method static | P0 |
| TC-A3.02 | CallSites | Update instance call sites | P0 |
| TC-A3.03 | PassInstance | Convert to static with instance parameter | P1 |
| TC-A3.04 | Negative | Method uses instance field | P0 |
| TC-A3.05 | Negative | Method is virtual | P0 |
| TC-A3.06 | Negative | Method implements interface | P0 |
| TC-A3.07 | Preview | Preview changes | P0 |

---

## 6. UC-A4: Invert If

### 6.1 Use Case Specification

| Property | Value |
|----------|-------|
| ID | UC-A4 |
| Name | invert_if |
| Priority | Tier 2 - Important |
| Complexity | Low |

**Description**: Flip an if statement condition and swap the if/else branches, often used to reduce nesting or improve readability.

**Actors**:
- Primary: MCP Client
- Secondary: Roslyn Workspace

**Preconditions**:
| ID | Condition |
|----|-----------|
| PRE-A4.01 | Workspace loaded |
| PRE-A4.02 | Location points to if statement |

**Postconditions**:
| ID | Condition |
|----|-----------|
| POST-A4.01 | Condition is logically negated |
| POST-A4.02 | If and else branches are swapped |
| POST-A4.03 | Semantics preserved |

**Main Flow**:
1. Client invokes invert_if with file and location
2. System validates parameters
3. System locates if statement at position
4. System inverts condition (applies De Morgan laws where applicable)
5. System swaps if and else branches
6. System handles missing else (creates empty else or restructures)
7. System returns result

**Alternate Flows**:

| ID | Condition | Flow |
|----|-----------|------|
| AF-A4.01 | No else branch | Create else with original if body, empty if body |
| AF-A4.02 | Condition is comparison | Simply flip operator |
| AF-A4.03 | Complex condition | Apply De Morgan laws |

**Exception Flows**:

| ID | Condition | Error Code |
|----|-----------|------------|
| EX-A4.01 | No if statement at location | 3117 |
| EX-A4.02 | Condition not invertible | 3118 |

### 6.2 MCP Tool Schema

\`\`\`json
{
  "name": "invert_if",
  "description": "Flip condition and swap if/else branches",
  "inputSchema": {
    "type": "object",
    "properties": {
      "sourceFile": { "type": "string" },
      "line": { "type": "integer", "description": "Line containing if statement" },
      "column": { "type": "integer", "description": "Column within if keyword (optional)" },
      "preview": { "type": "boolean", "default": false }
    },
    "required": ["sourceFile", "line"]
  }
}
\`\`\`

### 6.3 Output Format

\`\`\`json
{
  "success": true,
  "operation": "invert_if",
  "originalCondition": "x > 0",
  "invertedCondition": "x <= 0",
  "filesModified": ["C:/Project/Calculator.cs"]
}
\`\`\`

### 6.4 Error Codes

| Code | Constant | Message |
|------|----------|---------|
| 3117 | NO_IF_STATEMENT_AT_LOCATION | No if statement found at specified location |
| 3118 | CONDITION_NOT_INVERTIBLE | Condition cannot be safely inverted |

### 6.5 Validation Rules

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-A4.01 | line | Must be >= 1 | 1006 |
| SV-A4.01 | location | Must contain if statement | 3117 |

### 6.6 Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-A4.01 | Happy Path | Invert simple comparison | P0 |
| TC-A4.02 | Happy Path | Invert with else branch | P0 |
| TC-A4.03 | DeMorgan | Invert && to || | P1 |
| TC-A4.04 | DeMorgan | Invert complex boolean | P1 |
| TC-A4.05 | NoElse | Invert if without else | P1 |
| TC-A4.06 | Nested | Invert nested if | P1 |
| TC-A4.07 | Negative | No if at location | P0 |
| TC-A4.08 | Preview | Preview changes | P0 |

---

## 7. UC-A5: Add Braces

### 7.1 Use Case Specification

| Property | Value |
|----------|-------|
| ID | UC-A5 |
| Name | add_braces |
| Priority | Tier 2 - Important |
| Complexity | Low |

**Description**: Add braces to control statements (if, else, for, foreach, while, using) that have single-statement bodies without braces.

**Actors**:
- Primary: MCP Client
- Secondary: Roslyn Workspace

**Preconditions**:
| ID | Condition |
|----|-----------|
| PRE-A5.01 | Workspace loaded |
| PRE-A5.02 | Location contains braceless control statement |

**Postconditions**:
| ID | Condition |
|----|-----------|
| POST-A5.01 | Braces added around statement body |
| POST-A5.02 | Semantics preserved |

**Main Flow**:
1. Client invokes add_braces
2. System locates control statement
3. System identifies braceless body
4. System wraps body in braces with proper formatting
5. System returns result

**Alternate Flows**:

| ID | Condition | Flow |
|----|-----------|------|
| AF-A5.01 | scope=file | Add braces to all braceless statements in file |
| AF-A5.02 | scope=type | Add braces within specific type |

**Exception Flows**:

| ID | Condition | Error Code |
|----|-----------|------------|
| EX-A5.01 | No control statement at location | 3119 |
| EX-A5.02 | Already has braces | 3120 |

### 7.2 MCP Tool Schema

\`\`\`json
{
  "name": "add_braces",
  "description": "Add braces to control statements",
  "inputSchema": {
    "type": "object",
    "properties": {
      "sourceFile": { "type": "string" },
      "line": { "type": "integer", "description": "Line of control statement" },
      "column": { "type": "integer", "description": "Column (optional)" },
      "scope": {
        "type": "string",
        "enum": ["statement", "file", "type"],
        "default": "statement",
        "description": "Scope of operation"
      },
      "typeName": { "type": "string", "description": "Type name if scope=type" },
      "preview": { "type": "boolean", "default": false }
    },
    "required": ["sourceFile"]
  }
}
\`\`\`

### 7.3 Output Format

\`\`\`json
{
  "success": true,
  "operation": "add_braces",
  "statementsModified": 1,
  "scope": "statement",
  "filesModified": ["C:/Project/Handler.cs"]
}
\`\`\`

### 7.4 Error Codes

| Code | Constant | Message |
|------|----------|---------|
| 3119 | NO_CONTROL_STATEMENT | No control statement found at location |
| 3120 | ALREADY_HAS_BRACES | Statement already has braces |

### 7.5 Validation Rules

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-A5.01 | line | Required if scope=statement | 1006 |
| IV-A5.02 | typeName | Required if scope=type | 1005 |
| SV-A5.01 | location | Must contain control statement | 3119 |

### 7.6 Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-A5.01 | Happy Path | Add braces to if | P0 |
| TC-A5.02 | Happy Path | Add braces to else | P0 |
| TC-A5.03 | Happy Path | Add braces to for | P0 |
| TC-A5.04 | Happy Path | Add braces to foreach | P0 |
| TC-A5.05 | Happy Path | Add braces to while | P0 |
| TC-A5.06 | Scope | Add braces to entire file | P1 |
| TC-A5.07 | Scope | Add braces within type | P1 |
| TC-A5.08 | Negative | Already has braces | P0 |
| TC-A5.09 | Preview | Preview changes | P0 |

---

## 8. UC-A6: Remove Braces

### 8.1 Use Case Specification

| Property | Value |
|----------|-------|
| ID | UC-A6 |
| Name | remove_braces |
| Priority | Tier 4 - Specialized |
| Complexity | Low |

**Description**: Remove braces from control statements that have single-statement bodies, per code style preferences.

**Actors**:
- Primary: MCP Client
- Secondary: Roslyn Workspace

**Preconditions**:
| ID | Condition |
|----|-----------|
| PRE-A6.01 | Workspace loaded |
| PRE-A6.02 | Control statement has braces with single statement |

**Postconditions**:
| ID | Condition |
|----|-----------|
| POST-A6.01 | Braces removed |
| POST-A6.02 | Semantics preserved |

**Main Flow**:
1. Client invokes remove_braces
2. System locates control statement
3. System verifies body has exactly one statement
4. System removes braces
5. System formats result
6. System returns result

**Exception Flows**:

| ID | Condition | Error Code |
|----|-----------|------------|
| EX-A6.01 | No control statement | 3119 |
| EX-A6.02 | No braces to remove | 3121 |
| EX-A6.03 | Multiple statements in block | 3122 |

### 8.2 MCP Tool Schema

\`\`\`json
{
  "name": "remove_braces",
  "description": "Remove braces from single-statement control blocks",
  "inputSchema": {
    "type": "object",
    "properties": {
      "sourceFile": { "type": "string" },
      "line": { "type": "integer" },
      "column": { "type": "integer" },
      "scope": {
        "type": "string",
        "enum": ["statement", "file", "type"],
        "default": "statement"
      },
      "typeName": { "type": "string" },
      "preview": { "type": "boolean", "default": false }
    },
    "required": ["sourceFile"]
  }
}
\`\`\`

### 8.3 Output Format

\`\`\`json
{
  "success": true,
  "operation": "remove_braces",
  "statementsModified": 1,
  "filesModified": ["C:/Project/Handler.cs"]
}
\`\`\`

### 8.4 Error Codes

| Code | Constant | Message |
|------|----------|---------|
| 3121 | NO_BRACES_TO_REMOVE | Statement does not have braces |
| 3122 | MULTIPLE_STATEMENTS_IN_BLOCK | Block contains multiple statements |

### 8.5 Validation Rules

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-A6.01 | line | Required if scope=statement | 1006 |
| SV-A6.01 | block | Must contain exactly one statement | 3122 |
| SV-A6.02 | statement | Must have braces | 3121 |

### 8.6 Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-A6.01 | Happy Path | Remove braces from if | P0 |
| TC-A6.02 | Happy Path | Remove braces from for | P1 |
| TC-A6.03 | Scope | Remove from entire file | P2 |
| TC-A6.04 | Negative | Multiple statements | P0 |
| TC-A6.05 | Negative | No braces present | P0 |
| TC-A6.06 | Preview | Preview changes | P0 |

---

## 9. UC-A7: Sort Usings (Standalone)

### 9.1 Use Case Specification

| Property | Value |
|----------|-------|
| ID | UC-A7 |
| Name | sort_usings |
| Priority | Tier 3 - Valuable |
| Complexity | Low |

**Description**: Sort using directives alphabetically with configurable placement of System namespaces.

**Actors**:
- Primary: MCP Client
- Secondary: Roslyn Workspace

**Preconditions**:
| ID | Condition |
|----|-----------|
| PRE-A7.01 | Workspace loaded |
| PRE-A7.02 | File has using directives |

**Postconditions**:
| ID | Condition |
|----|-----------|
| POST-A7.01 | Usings sorted per configuration |
| POST-A7.02 | Semantics preserved |

**Main Flow**:
1. Client invokes sort_usings
2. System validates parameters
3. System identifies all using directives
4. System groups and sorts according to options
5. System rewrites using block
6. System returns result

**Alternate Flows**:

| ID | Condition | Flow |
|----|-----------|------|
| AF-A7.01 | systemFirst=true | Place System.* namespaces first |
| AF-A7.02 | separateGroups=true | Blank line between groups |

### 9.2 MCP Tool Schema

\`\`\`json
{
  "name": "sort_usings",
  "description": "Sort using directives alphabetically",
  "inputSchema": {
    "type": "object",
    "properties": {
      "sourceFile": { "type": "string" },
      "systemFirst": {
        "type": "boolean",
        "default": true,
        "description": "Place System namespaces first"
      },
      "separateGroups": {
        "type": "boolean",
        "default": false,
        "description": "Separate namespace groups with blank lines"
      },
      "preview": { "type": "boolean", "default": false }
    },
    "required": ["sourceFile"]
  }
}
\`\`\`

### 9.3 Output Format

\`\`\`json
{
  "success": true,
  "operation": "sort_usings",
  "usingCount": 15,
  "wasReordered": true,
  "filesModified": ["C:/Project/Service.cs"]
}
\`\`\`

### 9.4 Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-A7.01 | Happy Path | Sort alphabetically | P0 |
| TC-A7.02 | SystemFirst | System namespaces first | P0 |
| TC-A7.03 | Groups | Separate groups with blank lines | P1 |
| TC-A7.04 | Edge | Already sorted | P1 |
| TC-A7.05 | Edge | Global usings | P2 |
| TC-A7.06 | Preview | Preview changes | P0 |

---

## 10. UC-A8: Simplify Name

### 10.1 Use Case Specification

| Property | Value |
|----------|-------|
| ID | UC-A8 |
| Name | simplify_name |
| Priority | Tier 3 - Valuable |
| Complexity | Low |

**Description**: Remove redundant namespace qualifications from type references when appropriate using directives exist.

**Actors**:
- Primary: MCP Client
- Secondary: Roslyn Workspace

**Preconditions**:
| ID | Condition |
|----|-----------|
| PRE-A8.01 | Workspace loaded |
| PRE-A8.02 | File contains qualified type references |

**Postconditions**:
| ID | Condition |
|----|-----------|
| POST-A8.01 | Redundant qualifications removed |
| POST-A8.02 | Code remains unambiguous |
| POST-A8.03 | Compilation succeeds |

**Main Flow**:
1. Client invokes simplify_name
2. System validates parameters
3. System identifies qualified names in scope
4. System checks for each if simplification is valid
5. System removes redundant qualifications
6. System returns result

**Alternate Flows**:

| ID | Condition | Flow |
|----|-----------|------|
| AF-A8.01 | scope=selection | Simplify only in selection |
| AF-A8.02 | scope=file | Simplify entire file |
| AF-A8.03 | Would create ambiguity | Skip that reference |

**Exception Flows**:

| ID | Condition | Error Code |
|----|-----------|------------|
| EX-A8.01 | No simplifiable names | 3123 |
| EX-A8.02 | Simplification would cause ambiguity | Warning only |

### 10.2 MCP Tool Schema

\`\`\`json
{
  "name": "simplify_name",
  "description": "Remove redundant namespace qualifications",
  "inputSchema": {
    "type": "object",
    "properties": {
      "sourceFile": { "type": "string" },
      "line": { "type": "integer", "description": "Line of specific name (optional)" },
      "column": { "type": "integer", "description": "Column of specific name (optional)" },
      "scope": {
        "type": "string",
        "enum": ["location", "file"],
        "default": "file"
      },
      "preview": { "type": "boolean", "default": false }
    },
    "required": ["sourceFile"]
  }
}
\`\`\`

### 10.3 Output Format

\`\`\`json
{
  "success": true,
  "operation": "simplify_name",
  "simplificationsApplied": 8,
  "simplificationsSkipped": 2,
  "skippedReasons": [
    { "name": "System.Collections.Generic.List<T>", "reason": "Would conflict with local type" }
  ],
  "filesModified": ["C:/Project/DataProcessor.cs"]
}
\`\`\`

### 10.4 Error Codes

| Code | Constant | Message |
|------|----------|---------|
| 3123 | NO_SIMPLIFIABLE_NAMES | No names can be simplified |

### 10.5 Validation Rules

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-A8.01 | line | Required if scope=location | 1006 |
| SV-A8.01 | simplification | Must not create ambiguity | Skip (warning) |

### 10.6 Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-A8.01 | Happy Path | Simplify fully qualified name | P0 |
| TC-A8.02 | Happy Path | Simplify multiple names | P0 |
| TC-A8.03 | Scope | Simplify single location | P1 |
| TC-A8.04 | Skip | Skip ambiguous simplification | P0 |
| TC-A8.05 | Edge | Generic type qualification | P1 |
| TC-A8.06 | Edge | Nested type qualification | P1 |
| TC-A8.07 | Preview | Preview changes | P0 |

---

## Part 3: Error Code Summary

### New Error Codes (3105-3130 Range)

| Code | Constant | Message | Operation |
|------|----------|---------|-----------|
| 3105 | MEMBER_DEPENDS_ON_DERIVED | Member depends on derived members | pull_member_up |
| 3106 | BASE_CLASS_IS_SEALED | Base class is sealed | pull_member_up |
| 3107 | NO_COMMON_BASE | No common base class | pull_member_up |
| 3108 | CONFLICTS_WITH_DERIVED | Conflicts with derived member | pull_member_up, push_member_down |
| 3109 | BASE_CLASS_NOT_EDITABLE | Base class is not editable (external) | pull_member_up |
| 3110 | DERIVED_CLASS_NOT_EDITABLE | Derived class is not editable | push_member_down |
| 3111 | MEMBER_REQUIRED_BY_CONTRACT | Member required by base contract | push_member_down |
| 3112 | EXPRESSION_NOT_FIELD_INITIALIZABLE | Expression cannot be field initializer | introduce_field |
| 3113 | EXPRESSION_CAPTURES_LOCAL | Expression captures local state | introduce_field |
| 3114 | EXPRESSION_TYPE_UNRESOLVABLE | Cannot determine expression type | introduce_parameter |
| 3115 | METHOD_USES_INSTANCE_STATE | Method uses instance members | make_method_static |
| 3116 | METHOD_IMPLEMENTS_INTERFACE | Static cannot implement interface | make_method_static |
| 3117 | NO_IF_STATEMENT_AT_LOCATION | No if statement found | invert_if |
| 3118 | CONDITION_NOT_INVERTIBLE | Condition not invertible | invert_if |
| 3119 | NO_CONTROL_STATEMENT | No control statement found | add_braces, remove_braces |
| 3120 | ALREADY_HAS_BRACES | Already has braces | add_braces |
| 3121 | NO_BRACES_TO_REMOVE | No braces to remove | remove_braces |
| 3122 | MULTIPLE_STATEMENTS_IN_BLOCK | Multiple statements in block | remove_braces |
| 3123 | NO_SIMPLIFIABLE_NAMES | No names can be simplified | simplify_name |

---

## Part 4: Validation Rules Summary

### Input Validation Rules (IV-*)

| Rule ID | Operation | Field | Rule | Error Code |
|---------|-----------|-------|------|------------|
| IV-H1.01 | pull_member_up | sourceFile | Valid path | 1001 |
| IV-H1.02 | pull_member_up | typeName | Not null/empty | 1005 |
| IV-H1.03 | pull_member_up | memberName | Not null/empty | 1005 |
| IV-H2.01 | push_member_down | sourceFile | Valid path | 1001 |
| IV-H2.02 | push_member_down | typeName | Must exist | 2015 |
| IV-A1.01 | introduce_field | startLine | >= 1 | 1006 |
| IV-A1.02 | introduce_field | fieldName | Valid identifier | 1003 |
| IV-A2.01 | introduce_parameter | parameterName | Valid identifier | 1003 |
| IV-A2.02 | introduce_parameter | position | >= 0, <= count | 1011 |
| IV-A3.01 | make_method_static | methodName | Not null/empty | 1005 |
| IV-A4.01 | invert_if | line | >= 1 | 1006 |
| IV-A5.01 | add_braces | line | Required if scope=statement | 1006 |
| IV-A6.01 | remove_braces | line | Required if scope=statement | 1006 |
| IV-A8.01 | simplify_name | line | Required if scope=location | 1006 |

### Semantic Validation Rules (SV-*)

| Rule ID | Operation | Rule | Error Code |
|---------|-----------|------|------------|
| SV-H1.01 | pull_member_up | Must not depend on derived | 3105 |
| SV-H1.02 | pull_member_up | Base not sealed | 3106 |
| SV-H1.03 | pull_member_up | No conflict in base | 3108 |
| SV-H1.04 | pull_member_up | Base must be editable | 3109 |
| SV-H2.01 | push_member_down | Derived classes exist | 2011 |
| SV-H2.02 | push_member_down | Derived editable | 3110 |
| SV-A1.01 | introduce_field | Name not exists | 3003 |
| SV-A1.02 | introduce_field | No local capture | 3113 |
| SV-A2.01 | introduce_parameter | Name not exists | 3080 |
| SV-A2.02 | introduce_parameter | No interface break | 3084 |
| SV-A3.01 | make_method_static | No instance state | 3115 |
| SV-A3.02 | make_method_static | Not virtual | 3051 |
| SV-A3.03 | make_method_static | Not interface impl | 3116 |
| SV-A4.01 | invert_if | If statement exists | 3117 |
| SV-A5.01 | add_braces | Control statement exists | 3119 |
| SV-A6.01 | remove_braces | Single statement | 3122 |
| SV-A8.01 | simplify_name | No ambiguity | Skip |

---

## Part 5: Test Scenario Priority Matrix

### Priority Distribution

| Priority | Count | Percentage |
|----------|-------|------------|
| P0 | 38 | 55% |
| P1 | 25 | 36% |
| P2 | 6 | 9% |
| **Total** | **69** | 100% |

### P0 (Critical) Scenarios Summary

| ID | Operation | Description |
|----|-----------|-------------|
| TC-H1.01 | pull_member_up | Pull method to base |
| TC-H1.02 | pull_member_up | Pull property |
| TC-H1.11 | pull_member_up | Depends on derived |
| TC-H1.12 | pull_member_up | Base sealed |
| TC-H1.13 | pull_member_up | Name conflict |
| TC-H1.14 | pull_member_up | Base external |
| TC-H1.15 | pull_member_up | Preview |
| TC-H2.01 | push_member_down | Push to all derived |
| TC-H2.06 | push_member_down | No derived |
| TC-H2.07 | push_member_down | Derived external |
| TC-H2.08 | push_member_down | Conflict |
| TC-H2.09 | push_member_down | Preview |
| TC-A1.01 | introduce_field | Literal to field |
| TC-A1.02 | introduce_field | Expression |
| TC-A1.07 | introduce_field | Name exists |
| TC-A1.08 | introduce_field | Uses local |
| TC-A1.09 | introduce_field | Preview |
| TC-A2.01 | introduce_parameter | From literal |
| TC-A2.02 | introduce_parameter | From expression |
| TC-A2.03 | introduce_parameter | Update call sites |
| TC-A2.07 | introduce_parameter | Name exists |
| TC-A2.08 | introduce_parameter | Breaks interface |
| TC-A2.09 | introduce_parameter | Preview |
| TC-A3.01 | make_method_static | Pure method |
| TC-A3.02 | make_method_static | Update calls |
| TC-A3.04 | make_method_static | Uses instance |
| TC-A3.05 | make_method_static | Virtual |
| TC-A3.06 | make_method_static | Interface |
| TC-A3.07 | make_method_static | Preview |
| TC-A4.01 | invert_if | Simple comparison |
| TC-A4.02 | invert_if | With else |
| TC-A4.07 | invert_if | No if |
| TC-A4.08 | invert_if | Preview |
| TC-A5.01-05 | add_braces | Control statements |
| TC-A5.08 | add_braces | Already has |
| TC-A5.09 | add_braces | Preview |
| TC-A6.01 | remove_braces | From if |
| TC-A6.04 | remove_braces | Multiple stmts |
| TC-A6.05 | remove_braces | No braces |
| TC-A6.06 | remove_braces | Preview |
| TC-A7.01 | sort_usings | Alphabetical |
| TC-A7.02 | sort_usings | System first |
| TC-A7.06 | sort_usings | Preview |
| TC-A8.01 | simplify_name | Fully qualified |
| TC-A8.02 | simplify_name | Multiple |
| TC-A8.04 | simplify_name | Skip ambiguous |
| TC-A8.07 | simplify_name | Preview |

---

## Part 6: Open Questions

| ID | Question | Impact | Resolution Required By |
|----|----------|--------|------------------------|
| OQ-01 | For pull_member_up to interface, should we add the interface to the type if not already implemented? | Interface flow | Design phase |
| OQ-02 | For push_member_down with leaveAbstract, should existing overrides be preserved or replaced? | Override handling | Implementation |
| OQ-03 | Should introduce_field support const for compile-time constant expressions? | Feature scope | Design phase |
| OQ-04 | For invert_if, should we apply De Morgan laws automatically or preserve original structure? | Code style | User preference setting |
| OQ-05 | Should sort_usings handle file-scoped namespace differently? | C# 10+ support | Implementation |

---

## Part 7: Assumptions and Constraints

### Assumptions

1. All source files are valid C# syntax
2. Roslyn workspace accurately reflects file system state
3. User has write permissions to all affected files
4. .NET SDK compatible with C# features used

### Constraints

1. Error codes must be in range 3105-3130 for this phase
2. Operations must preserve compilation success
3. Preview mode must be supported for all operations
4. Operations must respect solution boundaries (no cross-solution changes)

---

## Document History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-01-28 | BA | Initial draft |
