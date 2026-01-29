# Business Analysis: Signature Operations

## Document Information

| Property | Value |
|----------|-------|
| Document ID | BA-SIG-001 |
| Version | 1.0 |
| Status | Draft |
| Author | Business Analyst |
| Phase | Phase 2 - Advanced Refactoring |
| Error Code Range | 3080-3089 |

---

## 1. Executive Summary

Signature operations modify method declarations and propagate changes to all call sites, overrides, and interface implementations. These operations are high-complexity refactorings requiring cross-solution analysis and coordinated updates.

### 1.1 Operations in Scope

| Operation ID | Operation Name | Priority | Complexity |
|--------------|----------------|----------|------------|
| UC-S1 | add_parameter | Tier 3 - Valuable | High |
| UC-S2 | remove_parameter | Tier 3 - Valuable | High |
| UC-S3 | reorder_parameters | Tier 3 - Valuable | High |
| UC-S4 | change_return_type | Tier 3 - Valuable | High |

### 1.2 Business Value

Signature operations enable safe API evolution without manual find-and-replace:
- **add_parameter**: Extend method contracts with new inputs
- **remove_parameter**: Clean up unused parameters (code hygiene)
- **reorder_parameters**: Improve API ergonomics and consistency
- **change_return_type**: Adapt return contracts for new requirements

---

## 2. UC-S1: Add Parameter

### 2.1 Overview

| Property | Value |
|----------|-------|
| ID | UC-S1 |
| Name | Add Parameter |
| Actor | AI Agent (via MCP) |
| Priority | Tier 3 - Valuable |
| Complexity | High |

### 2.2 Description

Add a new parameter to a method signature. All call sites receive the default value. All overrides and interface implementations receive the new parameter.

### 2.3 Preconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-S1.1 | Workspace loaded and Ready | WorkspaceState == Ready |
| PRE-S1.2 | Source file exists in workspace | Document in workspace |
| PRE-S1.3 | Method exists at location or by name | Method symbol resolvable |
| PRE-S1.4 | Parameter name is valid C# identifier | Regex validation |
| PRE-S1.5 | Parameter type is valid C# type | Type resolution |
| PRE-S1.6 | Parameter name does not already exist | No duplicate names |
| PRE-S1.7 | Position is valid (0 to paramCount) | Range validation |

### 2.4 Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-S1.1 | Parameter added to method signature | Parse and verify |
| POST-S1.2 | All call sites updated with default value | Call site inspection |
| POST-S1.3 | All overrides updated with new parameter | Override inspection |
| POST-S1.4 | Interface implementations updated | Implementation inspection |
| POST-S1.5 | Solution compiles | Zero compilation errors |

### 2.5 Main Success Scenario

| Step | Actor | System | State |
|------|-------|--------|-------|
| 1 | Agent sends add_parameter request | - | Pending |
| 2 | - | Validate input parameters | Validating |
| 3 | - | Resolve method symbol | Resolving |
| 4 | - | Find all call sites | Resolving |
| 5 | - | Find all overrides/implementations | Resolving |
| 6 | - | Check for naming conflicts | Computing |
| 7 | - | Compute text changes for declaration | Computing |
| 8 | - | Compute text changes for call sites | Computing |
| 9 | - | Apply changes to workspace | Applying |
| 10 | - | Persist to filesystem | Committing |
| 11 | - | Return success response | Completed |

### 2.6 Alternative Flows

#### AF-S1.1: Add Parameter to Interface Method
**Trigger**: Method is interface member
**Steps**:
1. Find all implementing types
2. Add parameter to interface declaration
3. Add parameter to all implementations
4. Update call sites for each implementation
5. Continue from Main Step 9

#### AF-S1.2: Add Parameter to Virtual Method
**Trigger**: Method is virtual/override
**Steps**:
1. Find entire override chain (base to most-derived)
2. Add parameter to all methods in chain
3. Update call sites for each level
4. Continue from Main Step 9

#### AF-S1.3: Position at End (Default)
**Trigger**: Position not specified
**Steps**:
1. Place parameter at end of parameter list
2. Continue from Main Step 7

#### AF-S1.4: Position Before Params Array
**Trigger**: Method has params parameter
**Steps**:
1. If position not specified, place before params
2. Params must remain last
3. Continue from Main Step 7

### 2.7 Exception Flows

| ID | Trigger | Error Code | Recovery |
|----|---------|------------|----------|
| EF-S1.1 | Parameter name already exists | 3080 | Return existing parameter info |
| EF-S1.2 | New signature matches existing overload | 3083 | Return conflicting overload |
| EF-S1.3 | Breaks interface contract | 3084 | Return interface info |
| EF-S1.4 | Invalid parameter type | 1010 | Return type resolution error |
| EF-S1.5 | Invalid default value for type | 1013 | Return type mismatch details |
| EF-S1.6 | Position out of range | 1011 | Return valid range |

### 2.8 Business Rules

| ID | Rule | Rationale |
|----|------|-----------|
| BR-S1.1 | Default value required for call site updates | Maintain compilability |
| BR-S1.2 | Override chain updated together | Polymorphism preserved |
| BR-S1.3 | Interface implementations updated | Contract consistency |
| BR-S1.4 | Params parameter always last | C# language requirement |
| BR-S1.5 | Optional parameters after required | C# language requirement |
| BR-S1.6 | Default position is end of required params | Least disruptive |

### 2.9 Input Parameters (MCP Schema)

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sourceFile | string | Yes | - | Absolute path to file containing method |
| methodName | string | Yes | - | Name of method to modify |
| parameterName | string | Yes | - | Name for the new parameter |
| parameterType | string | Yes | - | C# type of the new parameter |
| defaultValue | string | No | null | Default value for call sites |
| position | integer | No | -1 | Position (0-indexed); -1 = end |
| line | integer | No | - | Line for disambiguation |
| column | integer | No | - | Column for disambiguation |
| updateOverrides | boolean | No | true | Update override chain |
| updateImplementations | boolean | No | true | Update interface implementations |
| preview | boolean | No | false | Preview mode (no changes written) |

### 2.10 Output Format

```json
{
  "success": true,
  "operation": "add_parameter",
  "method": {
    "name": "ProcessData",
    "containingType": "DataProcessor",
    "file": "/path/to/file.cs"
  },
  "parameter": {
    "name": "timeout",
    "type": "int",
    "position": 2,
    "defaultValue": "30"
  },
  "changes": {
    "declarationsModified": 3,
    "callSitesUpdated": 15,
    "filesModified": 8
  },
  "modifiedFiles": [
    "/path/to/file1.cs",
    "/path/to/file2.cs"
  ]
}
```

---

## 3. UC-S2: Remove Parameter

### 3.1 Overview

| Property | Value |
|----------|-------|
| ID | UC-S2 |
| Name | Remove Parameter |
| Actor | AI Agent (via MCP) |
| Priority | Tier 3 - Valuable |
| Complexity | High |

### 3.2 Description

Remove a parameter from a method signature. Corresponding arguments removed from all call sites. Override chain and interface implementations updated.

### 3.3 Preconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-S2.1 | Workspace loaded and Ready | WorkspaceState == Ready |
| PRE-S2.2 | Source file exists in workspace | Document in workspace |
| PRE-S2.3 | Method exists at location or by name | Method symbol resolvable |
| PRE-S2.4 | Parameter exists in method signature | Parameter found |
| PRE-S2.5 | Parameter not used in method body (or force=true) | Usage analysis |

### 3.4 Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-S2.1 | Parameter removed from method signature | Parse and verify |
| POST-S2.2 | Arguments removed from all call sites | Call site inspection |
| POST-S2.3 | Parameter removed from all overrides | Override inspection |
| POST-S2.4 | Parameter removed from interface implementations | Implementation inspection |
| POST-S2.5 | Solution compiles | Zero compilation errors |

### 3.5 Main Success Scenario

| Step | Actor | System | State |
|------|-------|--------|-------|
| 1 | Agent sends remove_parameter request | - | Pending |
| 2 | - | Validate input parameters | Validating |
| 3 | - | Resolve method symbol | Resolving |
| 4 | - | Locate parameter in signature | Resolving |
| 5 | - | Analyze parameter usage in method body | Computing |
| 6 | - | Find all call sites | Resolving |
| 7 | - | Find all overrides/implementations | Resolving |
| 8 | - | Compute text changes for declarations | Computing |
| 9 | - | Compute text changes for call sites | Computing |
| 10 | - | Apply changes to workspace | Applying |
| 11 | - | Persist to filesystem | Committing |
| 12 | - | Return success response | Completed |

### 3.6 Alternative Flows

#### AF-S2.1: Parameter Used but Force=true
**Trigger**: Parameter is referenced in method body, force=true
**Steps**:
1. Remove parameter from signature
2. Replace usages in method body with default value or throw
3. Warn about breaking changes
4. Continue from Main Step 8

#### AF-S2.2: Remove from Interface Method
**Trigger**: Method is interface member
**Steps**:
1. Find all implementing types
2. Remove parameter from interface declaration
3. Remove parameter and usages from all implementations
4. Update call sites
5. Continue from Main Step 10

#### AF-S2.3: Remove from Virtual Method
**Trigger**: Method is virtual/override
**Steps**:
1. Find entire override chain
2. Remove parameter from all methods in chain
3. Update call sites for each level
4. Continue from Main Step 10

### 3.7 Exception Flows

| ID | Trigger | Error Code | Recovery |
|----|---------|------------|----------|
| EF-S2.1 | Parameter used by callers | 3081 | Return usage locations |
| EF-S2.2 | Parameter not found | 2016 | Return available parameters |
| EF-S2.3 | New signature matches overload | 3083 | Return conflicting overload |
| EF-S2.4 | Breaks interface contract | 3084 | Return interface info |
| EF-S2.5 | Parameter used in method body (force=false) | 3085 | Return usage count and locations |

### 3.8 Business Rules

| ID | Rule | Rationale |
|----|------|-----------|
| BR-S2.1 | Unused parameters can be removed safely | No semantic change |
| BR-S2.2 | Used parameters require force flag | Prevent accidental breakage |
| BR-S2.3 | Override chain updated together | Polymorphism preserved |
| BR-S2.4 | Named arguments updated correctly | Semantic preservation |
| BR-S2.5 | Removing last param removes trailing comma | Syntax correctness |

### 3.9 Input Parameters (MCP Schema)

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sourceFile | string | Yes | - | Absolute path to file containing method |
| methodName | string | Yes | - | Name of method to modify |
| parameterName | string | Yes | - | Name of parameter to remove |
| line | integer | No | - | Line for disambiguation |
| column | integer | No | - | Column for disambiguation |
| force | boolean | No | false | Remove even if used in body |
| updateOverrides | boolean | No | true | Update override chain |
| updateImplementations | boolean | No | true | Update interface implementations |
| preview | boolean | No | false | Preview mode (no changes written) |

### 3.10 Output Format

```json
{
  "success": true,
  "operation": "remove_parameter",
  "method": {
    "name": "ProcessData",
    "containingType": "DataProcessor",
    "file": "/path/to/file.cs"
  },
  "removedParameter": {
    "name": "legacyFlag",
    "type": "bool",
    "position": 1
  },
  "changes": {
    "declarationsModified": 3,
    "callSitesUpdated": 22,
    "filesModified": 10
  },
  "warnings": [
    "Parameter was used in method body; replaced with default(bool)"
  ],
  "modifiedFiles": [
    "/path/to/file1.cs",
    "/path/to/file2.cs"
  ]
}
```

---

## 4. UC-S3: Reorder Parameters

### 4.1 Overview

| Property | Value |
|----------|-------|
| ID | UC-S3 |
| Name | Reorder Parameters |
| Actor | AI Agent (via MCP) |
| Priority | Tier 3 - Valuable |
| Complexity | High |

### 4.2 Description

Change the order of parameters in a method signature. All call sites updated to maintain semantic equivalence. Override chain and implementations updated.

### 4.3 Preconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-S3.1 | Workspace loaded and Ready | WorkspaceState == Ready |
| PRE-S3.2 | Source file exists in workspace | Document in workspace |
| PRE-S3.3 | Method exists at location or by name | Method symbol resolvable |
| PRE-S3.4 | Method has >= 2 parameters | Parameter count check |
| PRE-S3.5 | New order is valid permutation | All indices valid and unique |

### 4.4 Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-S3.1 | Parameters in new order | Parse and verify |
| POST-S3.2 | Call site arguments reordered | Call site inspection |
| POST-S3.3 | Override signatures updated | Override inspection |
| POST-S3.4 | Implementation signatures updated | Implementation inspection |
| POST-S3.5 | Solution compiles | Zero compilation errors |
| POST-S3.6 | Semantic equivalence preserved | Same runtime behavior |

### 4.5 Main Success Scenario

| Step | Actor | System | State |
|------|-------|--------|-------|
| 1 | Agent sends reorder_parameters request | - | Pending |
| 2 | - | Validate input parameters | Validating |
| 3 | - | Resolve method symbol | Resolving |
| 4 | - | Validate new order is permutation | Validating |
| 5 | - | Find all call sites | Resolving |
| 6 | - | Find all overrides/implementations | Resolving |
| 7 | - | Check for overload conflicts | Computing |
| 8 | - | Compute declaration changes | Computing |
| 9 | - | Compute call site argument reordering | Computing |
| 10 | - | Apply changes to workspace | Applying |
| 11 | - | Persist to filesystem | Committing |
| 12 | - | Return success response | Completed |

### 4.6 Alternative Flows

#### AF-S3.1: Named Arguments at Call Sites
**Trigger**: Call site uses named arguments
**Steps**:
1. Named arguments do not need positional reordering
2. Verify argument names still match parameter names
3. Continue from Main Step 10

#### AF-S3.2: Mixed Named and Positional
**Trigger**: Call site has mixed argument styles
**Steps**:
1. Reorder positional arguments
2. Leave named arguments in place
3. Validate semantic equivalence
4. Continue from Main Step 10

#### AF-S3.3: Params Array Involved
**Trigger**: Method has params parameter
**Steps**:
1. Params must remain in final position
2. Validate new order respects this constraint
3. Continue from Main Step 8

### 4.7 Exception Flows

| ID | Trigger | Error Code | Recovery |
|----|---------|------------|----------|
| EF-S3.1 | New order creates overload conflict | 3083 | Return conflicting overload |
| EF-S3.2 | Params not at end in new order | 3086 | Return constraint explanation |
| EF-S3.3 | Optional before required in new order | 3087 | Return constraint explanation |
| EF-S3.4 | Method not found | 2006 | Return search suggestions |
| EF-S3.5 | Invalid permutation array | 1011 | Return valid format example |

### 4.8 Business Rules

| ID | Rule | Rationale |
|----|------|-----------|
| BR-S3.1 | Params parameter must remain last | C# language requirement |
| BR-S3.2 | Optional parameters after required | C# language requirement |
| BR-S3.3 | Named arguments unaffected by reorder | Semantic preservation |
| BR-S3.4 | Positional arguments reordered to match | Correctness |
| BR-S3.5 | Override chain reordered identically | Signature consistency |

### 4.9 Input Parameters (MCP Schema)

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sourceFile | string | Yes | - | Absolute path to file containing method |
| methodName | string | Yes | - | Name of method to modify |
| newOrder | integer[] | Yes | - | New parameter order (0-indexed positions) |
| line | integer | No | - | Line for disambiguation |
| column | integer | No | - | Column for disambiguation |
| updateOverrides | boolean | No | true | Update override chain |
| updateImplementations | boolean | No | true | Update interface implementations |
| preview | boolean | No | false | Preview mode (no changes written) |

### 4.10 Output Format

```json
{
  "success": true,
  "operation": "reorder_parameters",
  "method": {
    "name": "ProcessData",
    "containingType": "DataProcessor",
    "file": "/path/to/file.cs"
  },
  "reordering": {
    "originalOrder": ["source", "destination", "options"],
    "newOrder": ["options", "source", "destination"],
    "mapping": [2, 0, 1]
  },
  "changes": {
    "declarationsModified": 3,
    "callSitesUpdated": 18,
    "filesModified": 7
  },
  "modifiedFiles": [
    "/path/to/file1.cs",
    "/path/to/file2.cs"
  ]
}
```

---

## 5. UC-S4: Change Return Type

### 5.1 Overview

| Property | Value |
|----------|-------|
| ID | UC-S4 |
| Name | Change Return Type |
| Actor | AI Agent (via MCP) |
| Priority | Tier 3 - Valuable |
| Complexity | High |

### 5.2 Description

Change the return type of a method. Return statements updated if possible. Call sites using the return value flagged or updated.

### 5.3 Preconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-S4.1 | Workspace loaded and Ready | WorkspaceState == Ready |
| PRE-S4.2 | Source file exists in workspace | Document in workspace |
| PRE-S4.3 | Method exists at location or by name | Method symbol resolvable |
| PRE-S4.4 | New return type is valid C# type | Type resolution |
| PRE-S4.5 | New return type different from current | Type comparison |

### 5.4 Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-S4.1 | Method has new return type | Parse and verify |
| POST-S4.2 | Return statements updated (if convertible) | Statement inspection |
| POST-S4.3 | Override chain updated | Override inspection |
| POST-S4.4 | Interface implementations updated | Implementation inspection |
| POST-S4.5 | Solution compiles (or warnings for call sites) | Compilation check |

### 5.5 Main Success Scenario

| Step | Actor | System | State |
|------|-------|--------|-------|
| 1 | Agent sends change_return_type request | - | Pending |
| 2 | - | Validate input parameters | Validating |
| 3 | - | Resolve method symbol | Resolving |
| 4 | - | Analyze return type compatibility | Computing |
| 5 | - | Find all return statements | Resolving |
| 6 | - | Find all call sites using return value | Resolving |
| 7 | - | Find all overrides/implementations | Resolving |
| 8 | - | Compute declaration changes | Computing |
| 9 | - | Compute return statement changes | Computing |
| 10 | - | Identify call site impacts | Computing |
| 11 | - | Apply changes to workspace | Applying |
| 12 | - | Persist to filesystem | Committing |
| 13 | - | Return response with call site warnings | Completed |

### 5.6 Alternative Flows

#### AF-S4.1: Void to Non-Void
**Trigger**: Current type is void, new type is not
**Steps**:
1. Add return statements where missing
2. Use default value or placeholder
3. Flag locations for developer review
4. Continue from Main Step 11

#### AF-S4.2: Non-Void to Void
**Trigger**: Current type is non-void, new type is void
**Steps**:
1. Remove return expressions (keep return;)
2. Flag call sites that use return value
3. Provide list of impacted locations
4. Continue from Main Step 11

#### AF-S4.3: Compatible Type Conversion
**Trigger**: New type is implicitly convertible from old
**Steps**:
1. Return statements may not need changes
2. Verify implicit conversion applies
3. Continue from Main Step 11

#### AF-S4.4: Task/Task<T> Conversions
**Trigger**: Async method return type change
**Steps**:
1. Handle Task -> Task<T> conversion
2. Handle Task<T> -> Task<U> conversion
3. Update awaited expressions at call sites
4. Continue from Main Step 11

### 5.7 Exception Flows

| ID | Trigger | Error Code | Recovery |
|----|---------|------------|----------|
| EF-S4.1 | Return type incompatible | 3082 | Return conversion options |
| EF-S4.2 | New signature matches overload | 3083 | Return conflicting overload |
| EF-S4.3 | Breaks interface contract | 3084 | Return interface info |
| EF-S4.4 | Invalid return type | 1015 | Return type resolution error |
| EF-S4.5 | Method not found | 2006 | Return search suggestions |
| EF-S4.6 | Cannot convert return statements | 3088 | Return incompatible statement locations |

### 5.8 Business Rules

| ID | Rule | Rationale |
|----|------|-----------|
| BR-S4.1 | Override chain must have compatible return types | Covariant return rules |
| BR-S4.2 | Interface implementations match exactly | Contract compliance |
| BR-S4.3 | Void methods have no return expressions | Language rules |
| BR-S4.4 | Async methods return Task types | Async pattern |
| BR-S4.5 | Call sites using return value flagged | Developer awareness |
| BR-S4.6 | Implicit conversions applied when safe | Minimize changes |

### 5.9 Input Parameters (MCP Schema)

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sourceFile | string | Yes | - | Absolute path to file containing method |
| methodName | string | Yes | - | Name of method to modify |
| newReturnType | string | Yes | - | New return type (C# type syntax) |
| line | integer | No | - | Line for disambiguation |
| column | integer | No | - | Column for disambiguation |
| updateOverrides | boolean | No | true | Update override chain |
| updateImplementations | boolean | No | true | Update interface implementations |
| convertReturnStatements | boolean | No | true | Attempt return statement conversion |
| preview | boolean | No | false | Preview mode (no changes written) |

### 5.10 Output Format

```json
{
  "success": true,
  "operation": "change_return_type",
  "method": {
    "name": "ProcessData",
    "containingType": "DataProcessor",
    "file": "/path/to/file.cs"
  },
  "returnType": {
    "original": "bool",
    "new": "ProcessingResult"
  },
  "changes": {
    "declarationsModified": 3,
    "returnStatementsUpdated": 5,
    "filesModified": 4
  },
  "callSiteImpacts": [
    {
      "file": "/path/to/caller.cs",
      "line": 45,
      "usageType": "assignment",
      "recommendation": "Update variable type to ProcessingResult"
    }
  ],
  "modifiedFiles": [
    "/path/to/file1.cs",
    "/path/to/file2.cs"
  ]
}
```

---

## 6. Error Codes (3080-3089)

### 6.1 Error Code Definition

| Code | Constant | Message | Operation(s) |
|------|----------|---------|--------------|
| 3080 | PARAMETER_ALREADY_EXISTS | Parameter with this name already exists | add_parameter |
| 3081 | PARAMETER_REQUIRED_BY_CALLERS | Parameter is used by callers and cannot be safely removed | remove_parameter |
| 3082 | RETURN_TYPE_INCOMPATIBLE | New return type is not compatible with existing return statements | change_return_type |
| 3083 | SIGNATURE_MATCHES_OVERLOAD | Modified signature matches an existing overload | add_parameter, remove_parameter, reorder_parameters, change_return_type |
| 3084 | BREAKS_INTERFACE_CONTRACT | Change would break interface contract implementation | all signature operations |
| 3085 | PARAMETER_USED_IN_BODY | Parameter is referenced in method body | remove_parameter |
| 3086 | PARAMS_NOT_LAST | Params parameter must be at end of parameter list | reorder_parameters, add_parameter |
| 3087 | OPTIONAL_BEFORE_REQUIRED | Optional parameters cannot precede required parameters | reorder_parameters, add_parameter |
| 3088 | CANNOT_CONVERT_RETURN | Return statements cannot be converted to new type | change_return_type |
| 3089 | CALL_SITE_INCOMPATIBLE | Call site cannot be automatically updated | all signature operations |

### 6.2 Error Response Format

```json
{
  "success": false,
  "errorCode": 3080,
  "errorType": "PARAMETER_ALREADY_EXISTS",
  "message": "Parameter 'timeout' already exists in method 'ProcessData'",
  "details": {
    "method": "ProcessData",
    "existingParameter": {
      "name": "timeout",
      "type": "int",
      "position": 2
    }
  },
  "suggestions": [
    "Use a different parameter name",
    "Remove existing parameter first"
  ]
}
```

---

## 7. Validation Rules

### 7.1 Input Validation Rules

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-S01 | parameterName | Must be valid C# identifier | 1003 |
| IV-S02 | parameterType | Must be valid C# type | 1010 |
| IV-S03 | position | Must be >= 0 and <= param count | 1011 |
| IV-S04 | defaultValue | Must be valid expression for type | 1013 |
| IV-S05 | newReturnType | Must be valid C# type | 1015 |
| IV-S06 | newOrder | Must be valid permutation of indices | 1011 |
| IV-S07 | methodName | Must not be null or empty | 1005 |
| IV-S08 | sourceFile | Must be valid path in workspace | 1001 |

### 7.2 Semantic Validation Rules

| Rule ID | Rule | Error Code | Stage |
|---------|------|------------|-------|
| SV-S01 | New parameter name must not exist in signature | 3080 | Computing |
| SV-S02 | Removed parameter usage analysis required | 3081/3085 | Computing |
| SV-S03 | New signature must not match existing overload | 3083 | Computing |
| SV-S04 | Change must not break interface contract | 3084 | Computing |
| SV-S05 | Params must remain last in order | 3086 | Validating |
| SV-S06 | Optional parameters must follow required | 3087 | Validating |
| SV-S07 | Return statements must be convertible | 3088 | Computing |
| SV-S08 | All call sites must be updatable | 3089 | Computing |

### 7.3 Validation Execution Order

1. **Input Validation** (IV-*): Immediate syntactic checks
2. **Environment Validation**: Workspace state verification
3. **Resource Validation**: Symbol resolution
4. **Semantic Validation** (SV-*): Requires loaded workspace
5. **Filesystem Validation**: Write permission check (before commit)

---

## 8. Test Scenarios

### 8.1 Add Parameter Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-S1.01 | Happy Path | Add parameter with default value | P0 |
| TC-S1.02 | Happy Path | Add parameter at specific position | P0 |
| TC-S1.03 | Happy Path | Add parameter at end (default) | P0 |
| TC-S1.04 | Cascade | Add to interface method with implementations | P0 |
| TC-S1.05 | Cascade | Add to virtual method with overrides | P0 |
| TC-S1.06 | Cascade | Update all call sites with default | P0 |
| TC-S1.07 | Edge | Add before params array | P1 |
| TC-S1.08 | Edge | Add optional parameter after other optionals | P1 |
| TC-S1.09 | Edge | Add to method with named arguments at call sites | P1 |
| TC-S1.10 | Negative | Duplicate parameter name | P0 |
| TC-S1.11 | Negative | Invalid parameter type | P0 |
| TC-S1.12 | Negative | Position out of range | P0 |
| TC-S1.13 | Negative | Signature matches existing overload | P0 |
| TC-S1.14 | Negative | Invalid default value for type | P1 |
| TC-S1.15 | Preview | Preview mode returns changes without applying | P0 |

### 8.2 Remove Parameter Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-S2.01 | Happy Path | Remove unused parameter | P0 |
| TC-S2.02 | Happy Path | Remove parameter from multi-param method | P0 |
| TC-S2.03 | Cascade | Remove from interface method | P0 |
| TC-S2.04 | Cascade | Remove from virtual method chain | P0 |
| TC-S2.05 | Cascade | Update all call sites | P0 |
| TC-S2.06 | Edge | Remove with named arguments | P1 |
| TC-S2.07 | Edge | Remove last parameter | P1 |
| TC-S2.08 | Edge | Remove only non-params parameter | P1 |
| TC-S2.09 | Force | Remove used parameter with force=true | P1 |
| TC-S2.10 | Negative | Parameter not found | P0 |
| TC-S2.11 | Negative | Parameter used in body without force | P0 |
| TC-S2.12 | Negative | Removal creates overload conflict | P0 |
| TC-S2.13 | Preview | Preview mode returns changes without applying | P0 |

### 8.3 Reorder Parameters Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-S3.01 | Happy Path | Reorder two parameters | P0 |
| TC-S3.02 | Happy Path | Reorder multiple parameters | P0 |
| TC-S3.03 | Cascade | Reorder interface method | P0 |
| TC-S3.04 | Cascade | Reorder virtual method chain | P0 |
| TC-S3.05 | Cascade | Update positional arguments at call sites | P0 |
| TC-S3.06 | Edge | Named arguments unchanged | P1 |
| TC-S3.07 | Edge | Mixed named and positional arguments | P1 |
| TC-S3.08 | Edge | Params stays last | P1 |
| TC-S3.09 | Negative | Invalid permutation array | P0 |
| TC-S3.10 | Negative | Params not last in new order | P0 |
| TC-S3.11 | Negative | Optional before required | P0 |
| TC-S3.12 | Negative | Reorder creates overload conflict | P1 |
| TC-S3.13 | Preview | Preview mode returns changes without applying | P0 |

### 8.4 Change Return Type Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-S4.01 | Happy Path | Change to compatible type (widening) | P0 |
| TC-S4.02 | Happy Path | Change to different type | P0 |
| TC-S4.03 | Cascade | Change interface method return type | P0 |
| TC-S4.04 | Cascade | Change virtual method return type | P0 |
| TC-S4.05 | Void | Change void to returning | P0 |
| TC-S4.06 | Void | Change returning to void | P0 |
| TC-S4.07 | Async | Change Task to Task<T> | P1 |
| TC-S4.08 | Async | Change Task<T> to Task<U> | P1 |
| TC-S4.09 | Edge | Call sites using return value | P0 |
| TC-S4.10 | Edge | Return statements with expressions | P1 |
| TC-S4.11 | Negative | Invalid return type | P0 |
| TC-S4.12 | Negative | Incompatible return statements | P0 |
| TC-S4.13 | Negative | Breaks interface contract | P0 |
| TC-S4.14 | Preview | Preview mode returns changes without applying | P0 |

### 8.5 Cross-Cutting Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-SX.01 | Concurrency | Concurrent signature operations | P0 |
| TC-SX.02 | Preview | All operations support preview mode | P0 |
| TC-SX.03 | Compilation | All operations preserve solution compilation | P0 |
| TC-SX.04 | Timeout | Operations respect timeout limits | P1 |
| TC-SX.05 | Large Solution | Performance on 50+ project solution | P1 |
| TC-SX.06 | Cross-Project | Method in shared project with multiple consumers | P1 |

---

## 9. Dependencies and Infrastructure

### 9.1 Required Infrastructure Components

| Component | Purpose | New/Existing |
|-----------|---------|--------------|
| MethodSymbolResolver | Resolve method symbols with overload handling | New |
| SignatureAnalyzer | Parse and modify method signatures | New |
| CallSiteFinder | Find all invocations of a method | New |
| OverrideChainAnalyzer | Find virtual/override chains | New |
| InterfaceImplementationFinder | Find interface implementations | New |
| ArgumentReorderer | Reorder call site arguments | New |
| ReturnStatementAnalyzer | Analyze and modify return statements | New |

### 9.2 Shared Infrastructure (Existing)

| Component | Purpose |
|-----------|---------|
| MSBuildWorkspaceProvider | Solution loading |
| ReferenceTracker | Cross-solution reference updates |
| AtomicFileWriter | Transactional file operations |
| DocumentChangeBuilder | Build change sets |

---

## 10. Open Questions

| ID | Question | Impact | Owner |
|----|----------|--------|-------|
| OQ-S1 | Should add_parameter auto-generate default value if not provided? | API design | Architect |
| OQ-S2 | How to handle generic method type parameter changes? | Scope | Tech Lead |
| OQ-S3 | Should reorder_parameters support partial reorder (swap two)? | UX | Product |
| OQ-S4 | Error or warning for call sites using changed return type? | Behavior | Architect |
| OQ-S5 | Support for expression-bodied members in return type changes? | Implementation | Dev Lead |

---

## 11. Assumptions and Constraints

### 11.1 Assumptions

| ID | Assumption |
|----|------------|
| A-S1 | All source files are writable |
| A-S2 | Solution compiles before operation |
| A-S3 | Methods have unique signatures within containing type |
| A-S4 | External assemblies cannot be modified |

### 11.2 Constraints

| ID | Constraint |
|----|------------|
| C-S1 | C# language rules for parameter ordering must be respected |
| C-S2 | Interface contract changes require all implementations to update |
| C-S3 | Override chains must maintain signature compatibility |
| C-S4 | Compilation errors from call sites may be unavoidable |
| C-S5 | Generic type constraints limit return type changes |

---

## 12. Revision History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-01-28 | Business Analyst | Initial draft |
