# Business Analysis: Inline Operations

## Document Overview

| Property | Value |
|----------|-------|
| Document ID | BA-INL-001 |
| Version | 1.0 |
| Status | Draft |
| Author | Business Analyst |
| Created | 2026-01-28 |

### Purpose

This document specifies requirements for three inline refactoring operations:
1. `inline_method` - Replace method calls with method body
2. `inline_variable` - Replace variable references with initialization expression
3. `inline_constant` - Replace constant references with literal value

### Scope

Covers MCP tool interface, validation rules, error handling, and test scenarios. Implementation targets Roslyn-based C# refactoring.

---

## 1. Inline Method (UC-I1)

### 1.1 Overview

| Property | Value |
|----------|-------|
| ID | UC-I1 |
| Name | Inline Method |
| Actor | AI Agent (via MCP) |
| Priority | Tier 4 - Specialized |
| Complexity | High |

### 1.2 Description

Replace all invocations of a method with the method body, substituting parameters with arguments. Optionally remove the method definition after inlining all call sites.

### 1.3 Preconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-I1.1 | Workspace loaded and Ready | WorkspaceState == Ready |
| PRE-I1.2 | Source file exists | Document in workspace |
| PRE-I1.3 | Method exists at specified location | IMethodSymbol resolvable |
| PRE-I1.4 | Method is not recursive | Call graph analysis |
| PRE-I1.5 | Method is not virtual/override/abstract | MethodKind and modifiers check |
| PRE-I1.6 | Method is not extern | Modifiers check |
| PRE-I1.7 | Method has a body | Not abstract/extern/partial |
| PRE-I1.8 | Method not part of interface implementation | InterfaceImplementations empty |

### 1.4 Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-I1.1 | All call sites replaced with method body | Syntax tree verification |
| POST-I1.2 | Parameters substituted with arguments | Argument-to-parameter mapping |
| POST-I1.3 | Local variable conflicts resolved | Renaming applied |
| POST-I1.4 | Method removed if removeMethod=true | Symbol absent |
| POST-I1.5 | Solution compiles | Zero compilation errors |
| POST-I1.6 | Semantics preserved | Behavior unchanged |

### 1.5 Main Success Scenario

| Step | Actor | System | State |
|------|-------|--------|-------|
| 1 | Agent sends inline_method request | - | Pending |
| 2 | - | Validate input parameters | Validating |
| 3 | - | Resolve method symbol | Resolving |
| 4 | - | Analyze method for inlineability | Resolving |
| 5 | - | Find all call sites | Computing |
| 6 | - | Compute inline transformations | Computing |
| 7 | - | Apply changes to workspace | Applying |
| 8 | - | Remove method if requested | Applying |
| 9 | - | Persist changes to filesystem | Committing |
| 10 | - | Return success response | Completed |

### 1.6 Alternative Flows

#### AF-I1.1: Single Call Site Mode
**Trigger**: callSiteLocation parameter provided
**Steps**:
1. Inline only at specified call site
2. Preserve method definition
3. Continue from Main Step 7

#### AF-I1.2: Expression-Bodied Method
**Trigger**: Method uses expression body syntax (`=>`)
**Steps**:
1. Extract expression from expression body
2. Parenthesize if necessary for operator precedence
3. Substitute at call site
4. Continue from Main Step 7

#### AF-I1.3: Method Has Out/Ref Parameters
**Trigger**: Method has ref/out parameters
**Steps**:
1. Declare temporary variables for out parameters
2. Map ref arguments to their locations
3. Generate compound statement block
4. Continue from Main Step 7

#### AF-I1.4: Method Accesses Instance Members
**Trigger**: Method is instance method accessing `this`
**Steps**:
1. If call site has explicit receiver, substitute for `this`
2. If implicit `this`, preserve as-is when in same type
3. Qualify member access when necessary
4. Continue from Main Step 7

#### AF-I1.5: Preview Mode
**Trigger**: preview=true
**Steps**:
1. Complete steps 1-6 (compute changes)
2. Return computed changes without applying
3. State remains Ready

### 1.7 Exception Flows

| ID | Trigger | Error Code | Response |
|----|---------|------------|----------|
| EF-I1.1 | Method not found | 2006 | METHOD_NOT_FOUND |
| EF-I1.2 | Method is recursive | 3050 | METHOD_IS_RECURSIVE |
| EF-I1.3 | Method is virtual/override/abstract | 3051 | METHOD_IS_VIRTUAL |
| EF-I1.4 | Method body too complex | 3054 | METHOD_TOO_COMPLEX |
| EF-I1.5 | No call sites found | 3055 | NO_CALL_SITES_FOUND |
| EF-I1.6 | Call site in read-only location | 3056 | CALL_SITE_NOT_EDITABLE |

### 1.8 Business Rules

| ID | Rule | Rationale |
|----|------|-----------|
| BR-I1.1 | Return statements converted to expression/value | Flow control transformation |
| BR-I1.2 | Local variables renamed if they conflict with scope | Name collision avoidance |
| BR-I1.3 | Arguments with side effects evaluated once via temp variable | Semantics preservation |
| BR-I1.4 | Type arguments substituted for generic parameters | Generic instantiation |
| BR-I1.5 | Await expressions preserved correctly in async context | Async correctness |
| BR-I1.6 | Method only removed when all call sites inlined | Safety |

### 1.9 Input Parameters (MCP Schema)

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sourceFile | string | Yes | - | Absolute path to file containing method |
| methodName | string | Yes | - | Name of method to inline |
| line | integer | No | - | Line number for disambiguation |
| column | integer | No | - | Column number for disambiguation |
| callSiteLocation | object | No | - | Specific call site (file, line, column) |
| removeMethod | boolean | No | true | Remove method after inlining all sites |
| preview | boolean | No | false | Return changes without applying |

#### callSiteLocation Object Schema

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| file | string | Yes | File containing call site |
| line | integer | Yes | Line of call site |
| column | integer | Yes | Column of call site |

### 1.10 Output Format

```json
{
  "success": true,
  "operation": "inline_method",
  "methodName": "CalculateTotal",
  "callSitesInlined": 3,
  "methodRemoved": true,
  "changes": [
    {
      "file": "C:/project/Order.cs",
      "changeType": "modified",
      "linesAdded": 15,
      "linesRemoved": 8
    }
  ]
}
```

### 1.11 Test Scenarios

| ID | Category | Description | Priority |
|----|----------|-------------|----------|
| TC-I1.01 | Happy Path | Inline simple void method | P0 |
| TC-I1.02 | Happy Path | Inline method returning value | P0 |
| TC-I1.03 | Happy Path | Inline method with parameters | P0 |
| TC-I1.04 | Happy Path | Inline at single call site only | P1 |
| TC-I1.05 | Happy Path | Inline and remove method | P0 |
| TC-I1.06 | Complex | Inline expression-bodied method | P1 |
| TC-I1.07 | Complex | Inline method with ref parameter | P1 |
| TC-I1.08 | Complex | Inline method with out parameter | P1 |
| TC-I1.09 | Complex | Inline generic method | P2 |
| TC-I1.10 | Complex | Inline async method | P2 |
| TC-I1.11 | Negative | Method is recursive | P0 |
| TC-I1.12 | Negative | Method is virtual | P0 |
| TC-I1.13 | Negative | Method not found | P0 |
| TC-I1.14 | Preview | Preview returns correct diff | P0 |

---

## 2. Inline Variable (UC-I2)

### 2.1 Overview

| Property | Value |
|----------|-------|
| ID | UC-I2 |
| Name | Inline Variable |
| Actor | AI Agent (via MCP) |
| Priority | Tier 2 - Important |
| Complexity | Low |

### 2.2 Description

Replace all references to a local variable with its initialization expression. Remove the variable declaration after inlining.

### 2.3 Preconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-I2.1 | Workspace loaded and Ready | WorkspaceState == Ready |
| PRE-I2.2 | Source file exists | Document in workspace |
| PRE-I2.3 | Variable exists at specified location | ILocalSymbol resolvable |
| PRE-I2.4 | Variable has initializer | Declaration has expression |
| PRE-I2.5 | Variable assigned exactly once | Single definite assignment |
| PRE-I2.6 | Variable not modified after initialization | No subsequent writes |

### 2.4 Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-I2.1 | Variable declaration removed | Syntax absent |
| POST-I2.2 | All references replaced with initializer | Substitution complete |
| POST-I2.3 | Parentheses added where needed | Operator precedence |
| POST-I2.4 | Solution compiles | Zero compilation errors |
| POST-I2.5 | Semantics preserved | Behavior unchanged |

### 2.5 Main Success Scenario

| Step | Actor | System | State |
|------|-------|--------|-------|
| 1 | Agent sends inline_variable request | - | Pending |
| 2 | - | Validate input parameters | Validating |
| 3 | - | Resolve variable symbol | Resolving |
| 4 | - | Verify single assignment | Computing |
| 5 | - | Verify no modification after init | Computing |
| 6 | - | Find all references | Computing |
| 7 | - | Compute inline transformations | Computing |
| 8 | - | Apply changes | Applying |
| 9 | - | Persist changes | Committing |
| 10 | - | Return success response | Completed |

### 2.6 Alternative Flows

#### AF-I2.1: Multiple Usages with Side-Effect Expression
**Trigger**: Initializer has side effects and variable used multiple times
**Steps**:
1. Detect side effect (method call, increment, etc.)
2. Reject inline operation
3. Return error 3053

#### AF-I2.2: Preview Mode
**Trigger**: preview=true
**Steps**:
1. Complete analysis and compute changes
2. Return without applying
3. State remains Ready

### 2.7 Exception Flows

| ID | Trigger | Error Code | Response |
|----|---------|------------|----------|
| EF-I2.1 | Variable not found | 2007 | VARIABLE_NOT_FOUND |
| EF-I2.2 | Variable modified after init | 3052 | VARIABLE_MODIFIED_AFTER_INIT |
| EF-I2.3 | Side-effect expression multi-use | 3053 | EXPRESSION_HAS_SIDE_EFFECTS |
| EF-I2.4 | Variable not initialized | 3057 | VARIABLE_NOT_INITIALIZED |
| EF-I2.5 | Variable is ref local | 3039 | CAPTURES_REF_LOCAL |

### 2.8 Business Rules

| ID | Rule | Rationale |
|----|------|-----------|
| BR-I2.1 | Side-effect expressions inlined only for single use | Semantics preservation |
| BR-I2.2 | Parentheses added to preserve operator precedence | Correctness |
| BR-I2.3 | Type casts preserved when necessary | Type safety |
| BR-I2.4 | Await expressions not duplicated in non-async context | Async correctness |

### 2.9 Input Parameters (MCP Schema)

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sourceFile | string | Yes | - | Absolute path to file |
| variableName | string | Yes | - | Name of variable to inline |
| line | integer | Yes | - | Line of variable declaration |
| column | integer | No | - | Column for disambiguation |
| preview | boolean | No | false | Return changes without applying |

### 2.10 Output Format

```json
{
  "success": true,
  "operation": "inline_variable",
  "variableName": "total",
  "usagesReplaced": 4,
  "declarationRemoved": true,
  "changes": [
    {
      "file": "C:/project/Calculator.cs",
      "changeType": "modified",
      "linesAdded": 0,
      "linesRemoved": 1
    }
  ]
}
```

### 2.11 Test Scenarios

| ID | Category | Description | Priority |
|----|----------|-------------|----------|
| TC-I2.01 | Happy Path | Inline single-use variable | P0 |
| TC-I2.02 | Happy Path | Inline multi-use pure expression | P0 |
| TC-I2.03 | Happy Path | Inline var-typed variable | P1 |
| TC-I2.04 | Complex | Expression requires parenthesization | P1 |
| TC-I2.05 | Complex | Variable in nested scope | P1 |
| TC-I2.06 | Negative | Variable modified after init | P0 |
| TC-I2.07 | Negative | Side-effect expression multi-use | P0 |
| TC-I2.08 | Negative | Variable not initialized | P0 |
| TC-I2.09 | Negative | Variable not found | P0 |
| TC-I2.10 | Preview | Preview returns correct changes | P0 |

---

## 3. Inline Constant (UC-I3)

### 3.1 Overview

| Property | Value |
|----------|-------|
| ID | UC-I3 |
| Name | Inline Constant |
| Actor | AI Agent (via MCP) |
| Priority | Tier 4 - Specialized |
| Complexity | Low |

### 3.2 Description

Replace all references to a constant (const field or static readonly) with its literal value. Optionally remove the constant declaration.

### 3.3 Preconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-I3.1 | Workspace loaded and Ready | WorkspaceState == Ready |
| PRE-I3.2 | Source file exists | Document in workspace |
| PRE-I3.3 | Constant exists at specified location | IFieldSymbol with IsConst or static readonly |
| PRE-I3.4 | Constant has compile-time value | HasConstantValue or literal initializer |
| PRE-I3.5 | Constant not used in attribute arguments | Attribute context check |

### 3.4 Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-I3.1 | All references replaced with literal | Substitution complete |
| POST-I3.2 | Constant declaration removed if requested | Symbol absent |
| POST-I3.3 | Solution compiles | Zero compilation errors |
| POST-I3.4 | Literal format appropriate | Numeric, string, bool literals |

### 3.5 Main Success Scenario

| Step | Actor | System | State |
|------|-------|--------|-------|
| 1 | Agent sends inline_constant request | - | Pending |
| 2 | - | Validate input parameters | Validating |
| 3 | - | Resolve constant symbol | Resolving |
| 4 | - | Extract constant value | Computing |
| 5 | - | Find all references across solution | Computing |
| 6 | - | Compute literal replacements | Computing |
| 7 | - | Apply changes | Applying |
| 8 | - | Remove declaration if requested | Applying |
| 9 | - | Persist changes | Committing |
| 10 | - | Return success response | Completed |

### 3.6 Alternative Flows

#### AF-I3.1: Constant Used in Attribute
**Trigger**: Constant referenced in attribute argument
**Steps**:
1. Detect attribute usage
2. Reject operation (attributes require compile-time constants)
3. Return error 3059

#### AF-I3.2: Cross-Project References
**Trigger**: Constant referenced from other projects
**Steps**:
1. Compute changes across all referencing projects
2. Ensure all projects remain compilable
3. Continue normally

#### AF-I3.3: Preserve Declaration
**Trigger**: removeConstant=false
**Steps**:
1. Replace all references with literal value
2. Keep constant declaration in place
3. Continue from Main Step 9

#### AF-I3.4: Preview Mode
**Trigger**: preview=true
**Steps**:
1. Complete analysis and compute changes
2. Return without applying
3. State remains Ready

### 3.7 Exception Flows

| ID | Trigger | Error Code | Response |
|----|---------|------------|----------|
| EF-I3.1 | Constant not found | 2008 | FIELD_NOT_FOUND |
| EF-I3.2 | Field is not constant | 3055 | NOT_A_CONSTANT |
| EF-I3.3 | Constant used in attribute | 3059 | CONSTANT_IN_ATTRIBUTE |
| EF-I3.4 | Value not representable as literal | 3037 | NOT_COMPILE_TIME_CONSTANT |
| EF-I3.5 | Constant is public API | 3056 | PUBLIC_API_CONSTANT |

### 3.8 Business Rules

| ID | Rule | Rationale |
|----|------|-----------|
| BR-I3.1 | String literals include escape sequences | Correct representation |
| BR-I3.2 | Numeric literals use appropriate suffix (L, F, M) | Type preservation |
| BR-I3.3 | Character literals use single quotes | C# syntax |
| BR-I3.4 | Null constants become null keyword | Literal representation |
| BR-I3.5 | Public constants warn before removal | API stability |

### 3.9 Input Parameters (MCP Schema)

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sourceFile | string | Yes | - | Absolute path to file |
| constantName | string | Yes | - | Name of constant to inline |
| typeName | string | No | - | Containing type for disambiguation |
| removeConstant | boolean | No | true | Remove declaration after inlining |
| preview | boolean | No | false | Return changes without applying |

### 3.10 Output Format

```json
{
  "success": true,
  "operation": "inline_constant",
  "constantName": "MaxRetries",
  "constantValue": "5",
  "usagesReplaced": 12,
  "declarationRemoved": true,
  "changes": [
    {
      "file": "C:/project/RetryPolicy.cs",
      "changeType": "modified",
      "linesAdded": 0,
      "linesRemoved": 1
    }
  ]
}
```

### 3.11 Test Scenarios

| ID | Category | Description | Priority |
|----|----------|-------------|----------|
| TC-I3.01 | Happy Path | Inline numeric const | P0 |
| TC-I3.02 | Happy Path | Inline string const | P0 |
| TC-I3.03 | Happy Path | Inline boolean const | P1 |
| TC-I3.04 | Happy Path | Inline const without removing | P1 |
| TC-I3.05 | Complex | Inline const across multiple files | P1 |
| TC-I3.06 | Complex | Inline static readonly field | P1 |
| TC-I3.07 | Negative | Constant used in attribute | P0 |
| TC-I3.08 | Negative | Field is not constant | P0 |
| TC-I3.09 | Negative | Constant not found | P0 |
| TC-I3.10 | Preview | Preview returns correct changes | P0 |

---

## 4. Error Codes (Range 3050-3059)

### 4.1 Inline Error Code Allocation

| Code | Constant | Message | Operation |
|------|----------|---------|-----------|
| 3050 | METHOD_IS_RECURSIVE | Recursive methods cannot be inlined | inline_method |
| 3051 | METHOD_IS_VIRTUAL | Virtual/override/abstract methods cannot be inlined | inline_method |
| 3052 | VARIABLE_MODIFIED_AFTER_INIT | Variable is modified after initialization | inline_variable |
| 3053 | EXPRESSION_HAS_SIDE_EFFECTS | Expression with side effects used multiple times | inline_variable |
| 3054 | METHOD_TOO_COMPLEX | Method body is too complex to inline | inline_method |
| 3055 | NO_CALL_SITES_FOUND / NOT_A_CONSTANT | No call sites found / Target field is not constant | inline_method / inline_constant |
| 3056 | CALL_SITE_NOT_EDITABLE / PUBLIC_API_CONSTANT | Call site not editable / Constant is public API | inline_method / inline_constant |
| 3057 | VARIABLE_NOT_INITIALIZED | Variable not initialized at declaration | inline_variable |
| 3058 | UNSAFE_CLOSURE_CAPTURE | Variable captured unsafely in closure | inline_variable |
| 3059 | CONSTANT_IN_ATTRIBUTE | Constant is used in attribute argument | inline_constant |

### 4.2 Reused Error Codes

| Code | Constant | Reused By |
|------|----------|-----------|
| 1001 | INVALID_SOURCE_PATH | All operations |
| 1003 | INVALID_SYMBOL_NAME | All operations |
| 1005 | MISSING_REQUIRED_PARAM | All operations |
| 1006 | INVALID_LINE_NUMBER | inline_variable |
| 2006 | METHOD_NOT_FOUND | inline_method |
| 2007 | VARIABLE_NOT_FOUND | inline_variable |
| 2008 | FIELD_NOT_FOUND | inline_constant |
| 3037 | NOT_COMPILE_TIME_CONSTANT | inline_constant |
| 3039 | CAPTURES_REF_LOCAL | inline_variable |

---

## 5. Validation Rules

### 5.1 Input Validation - Inline Method

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-IM01 | sourceFile | Must be absolute path | 1001 |
| IV-IM02 | sourceFile | Must end with .cs | 1001 |
| IV-IM03 | methodName | Must not be null or empty | 1005 |
| IV-IM04 | methodName | Must be valid C# identifier | 1003 |
| IV-IM05 | line | If provided, must be >= 1 | 1006 |

### 5.2 Input Validation - Inline Variable

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-IV01 | sourceFile | Must be absolute path | 1001 |
| IV-IV02 | sourceFile | Must end with .cs | 1001 |
| IV-IV03 | variableName | Must not be null or empty | 1005 |
| IV-IV04 | variableName | Must be valid C# identifier | 1003 |
| IV-IV05 | line | Must be >= 1 | 1006 |

### 5.3 Input Validation - Inline Constant

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-IC01 | sourceFile | Must be absolute path | 1001 |
| IV-IC02 | sourceFile | Must end with .cs | 1001 |
| IV-IC03 | constantName | Must not be null or empty | 1005 |
| IV-IC04 | constantName | Must be valid C# identifier | 1003 |

### 5.4 Semantic Validation

| Rule ID | Rule | Error Code | Stage |
|---------|------|------------|-------|
| SV-IM01 | Method must exist at location | 2006 | Resolving |
| SV-IM02 | Method must not be recursive | 3050 | Computing |
| SV-IM03 | Method must not be virtual/override | 3051 | Computing |
| SV-IV01 | Variable must exist at location | 2007 | Resolving |
| SV-IV02 | Variable must have initializer | 3057 | Computing |
| SV-IV03 | Variable must not be modified | 3052 | Computing |
| SV-IC01 | Field must exist at location | 2008 | Resolving |
| SV-IC02 | Field must be const or static readonly | 3055 | Computing |

---

## 6. Cross-Operation Concerns

### 6.1 Preview Mode

All operations support preview=true:
- Executes full validation and change computation
- Returns pendingChanges array with file operations
- Does not modify workspace or filesystem
- No locks held after response

### 6.2 Atomicity

All operations are atomic:
- All changes apply together or none apply
- Rollback on any failure
- Workspace remains consistent

### 6.3 Timeout Behavior

| Operation | Default Timeout | Max Timeout |
|-----------|-----------------|-------------|
| inline_method | 30s | 120s |
| inline_variable | 10s | 30s |
| inline_constant | 15s | 60s |

---

## 7. Test Scenario Priority Summary

| Priority | Count | Description |
|----------|-------|-------------|
| P0 | 18 | Critical - must pass for release |
| P1 | 11 | Important - should pass |
| P2 | 2 | Nice to have |

---

## 8. Open Questions

| ID | Question | Impact | Owner |
|----|----------|--------|-------|
| OQ-1 | Should inline_method support partial inlining? | Usability | PM |
| OQ-2 | How to handle inline_method when method has XML docs? | Documentation | BA |
| OQ-3 | Should inline_constant warn for magic numbers? | Safety | BA |
| OQ-4 | Maximum file size for cross-project inline? | Performance | Tech Lead |

---

## 9. Revision History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-01-28 | Business Analyst | Initial specification |
