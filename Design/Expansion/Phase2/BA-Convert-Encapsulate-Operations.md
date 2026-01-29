# Business Analysis: Convert and Encapsulate Operations

## Document Control
| Property | Value |
|----------|-------|
| Document ID | BA-Phase2-ConvEnc |
| Version | 1.0 |
| Date | 2026-01-28 |
| Status | Draft |
| Error Code Range | 3090-3104 |

---

## 1. Overview

This specification defines five refactoring operations for Phase 2 expansion:

| Category | Operation | Tool Name | Complexity |
|----------|-----------|-----------|------------|
| Convert | Convert to Async | `convert_to_async` | High |
| Convert | ForEach to LINQ | `convert_foreach_to_linq` | Medium |
| Convert | To Expression Body | `convert_to_expression_body` | Low |
| Convert | To Interpolated String | `convert_to_interpolated_string` | Low |
| Encapsulate | Encapsulate Field | `encapsulate_field` | Low |

---

## 2. Use Case Specifications

### UC-CV1: Convert to Async

#### Overview
| Property | Value |
|----------|-------|
| ID | UC-CV1 |
| Name | Convert to Async |
| Actor | AI Agent (via MCP) |
| Priority | Tier 3 - Valuable |
| Complexity | High |

#### Description
Convert a synchronous method to an asynchronous method using the async/await pattern. Transforms the return type to `Task` or `Task<T>`, adds the `async` modifier, and replaces blocking calls with awaited equivalents.

#### Preconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-CV1.1 | Workspace loaded and Ready | WorkspaceState == Ready |
| PRE-CV1.2 | Source file exists in workspace | Document in workspace |
| PRE-CV1.3 | Method exists at specified location | Symbol resolution |
| PRE-CV1.4 | Method is not already async | No async modifier |
| PRE-CV1.5 | Method is not a constructor/destructor/operator | Symbol kind check |

#### Postconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| POST-CV1.1 | Method marked with `async` modifier | Syntax check |
| POST-CV1.2 | Return type is `Task` (void) or `Task<T>` | Type check |
| POST-CV1.3 | Blocking calls replaced with `await` | Call site transformation |
| POST-CV1.4 | Method name suffixed with "Async" (if option enabled) | Naming convention |
| POST-CV1.5 | Solution compiles | Zero errors |

#### Main Flow
| Step | Actor | Action |
|------|-------|--------|
| 1 | Agent | Sends `convert_to_async` request with file path and method name |
| 2 | Server | Validates input parameters |
| 3 | Server | Locates method in semantic model |
| 4 | Server | Validates method is not already async |
| 5 | Server | Analyzes method body for blocking calls |
| 6 | Server | Transforms return type (void->Task, T->Task<T>) |
| 7 | Server | Adds `async` modifier |
| 8 | Server | Replaces blocking calls with awaited versions |
| 9 | Server | Optionally renames method with "Async" suffix |
| 10 | Server | Updates all callers to await the method |
| 11 | Server | Returns result with modified files |

#### Alternate Flows
| ID | Trigger | Flow |
|----|---------|------|
| AF-CV1.1 | Preview mode | Return proposed changes without applying |
| AF-CV1.2 | Method has no blocking calls | Add async/Task wrapper only |
| AF-CV1.3 | Method is interface implementation | Transform interface member signature too |
| AF-CV1.4 | Method is virtual/override | Transform entire hierarchy |

#### Exception Flows
| ID | Trigger | Error Code | Message |
|----|---------|------------|---------|
| EF-CV1.1 | Method already async | 3090 | Method is already asynchronous |
| EF-CV1.2 | Method is constructor | 3015 | Constructors cannot be made async |
| EF-CV1.3 | Method contains out/ref params | 3091 | Async methods cannot have ref/out parameters |
| EF-CV1.4 | Method not found | 2006 | No method found at specified location |

#### Business Rules
| ID | Rule | Rationale |
|----|------|-----------|
| BR-CV1.1 | `void` becomes `Task`, `T` becomes `Task<T>` | Async return semantics |
| BR-CV1.2 | `.Result` becomes `await`, `.Wait()` removed | Avoid deadlocks |
| BR-CV1.3 | `Thread.Sleep` becomes `Task.Delay` | Non-blocking delay |
| BR-CV1.4 | Cascade to callers requires updateCallers option | Prevent unexpected scope |

---

### UC-CV2: Convert ForEach to LINQ

#### Overview
| Property | Value |
|----------|-------|
| ID | UC-CV2 |
| Name | Convert ForEach to LINQ |
| Actor | AI Agent (via MCP) |
| Priority | Tier 4 - Specialized |
| Complexity | Medium |

#### Description
Convert a `foreach` loop into an equivalent LINQ expression. Detects common patterns (Select, Where, Any, All, First, etc.) and transforms the loop into the appropriate LINQ method chain.

#### Preconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-CV2.1 | Workspace loaded and Ready | WorkspaceState == Ready |
| PRE-CV2.2 | Source file exists in workspace | Document in workspace |
| PRE-CV2.3 | ForEach statement exists at location | Statement at line |
| PRE-CV2.4 | Loop body is transformable to LINQ | Pattern analysis |

#### Postconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| POST-CV2.1 | ForEach replaced with LINQ expression | Syntax replacement |
| POST-CV2.2 | Semantic equivalence maintained | Same behavior |
| POST-CV2.3 | Required using directives added | System.Linq present |
| POST-CV2.4 | Solution compiles | Zero errors |

#### Main Flow
| Step | Actor | Action |
|------|-------|--------|
| 1 | Agent | Sends `convert_foreach_to_linq` request with file and location |
| 2 | Server | Validates input parameters |
| 3 | Server | Locates foreach statement at specified line |
| 4 | Server | Analyzes loop pattern (projection, filter, aggregation) |
| 5 | Server | Determines appropriate LINQ method(s) |
| 6 | Server | Constructs equivalent LINQ expression |
| 7 | Server | Replaces foreach with LINQ |
| 8 | Server | Ensures System.Linq using directive present |
| 9 | Server | Returns result with modified file |

#### Alternate Flows
| ID | Trigger | Flow |
|----|---------|------|
| AF-CV2.1 | Preview mode | Return proposed changes without applying |
| AF-CV2.2 | Multiple LINQ methods needed | Chain methods appropriately |
| AF-CV2.3 | Loop builds list | Convert to `.ToList()` |
| AF-CV2.4 | Loop finds first match | Convert to `.FirstOrDefault()` |

#### Exception Flows
| ID | Trigger | Error Code | Message |
|----|---------|------------|---------|
| EF-CV2.1 | No foreach at location | 2014 | No foreach statement found at location |
| EF-CV2.2 | Loop has side effects | 3092 | ForEach body has side effects incompatible with LINQ |
| EF-CV2.3 | Loop modifies collection | 3093 | Cannot convert: loop modifies source collection |
| EF-CV2.4 | Complex control flow | 3094 | Loop contains break/continue/goto incompatible with LINQ |

#### Business Rules
| ID | Rule | Rationale |
|----|------|-----------|
| BR-CV2.1 | Simple projection -> `.Select()` | Map operation |
| BR-CV2.2 | Conditional add -> `.Where()` | Filter operation |
| BR-CV2.3 | Bool accumulator -> `.Any()` or `.All()` | Existence check |
| BR-CV2.4 | Sum/count -> `.Sum()` or `.Count()` | Aggregation |
| BR-CV2.5 | First match -> `.FirstOrDefault()` | Search with early exit |

#### Detectable Patterns
| Pattern | Loop Structure | LINQ Equivalent |
|---------|----------------|-----------------|
| Projection | `foreach(x) { list.Add(f(x)); }` | `source.Select(x => f(x)).ToList()` |
| Filter | `foreach(x) { if(p(x)) list.Add(x); }` | `source.Where(x => p(x)).ToList()` |
| Any | `foreach(x) { if(p(x)) { found=true; break; } }` | `source.Any(x => p(x))` |
| All | `foreach(x) { if(!p(x)) { allMatch=false; break; } }` | `source.All(x => p(x))` |
| First | `foreach(x) { if(p(x)) { result=x; break; } }` | `source.FirstOrDefault(x => p(x))` |
| Sum | `foreach(x) { sum += x.Value; }` | `source.Sum(x => x.Value)` |
| Count | `foreach(x) { if(p(x)) count++; }` | `source.Count(x => p(x))` |

---

### UC-CV3: Convert to Expression Body

#### Overview
| Property | Value |
|----------|-------|
| ID | UC-CV3 |
| Name | Convert to Expression Body |
| Actor | AI Agent (via MCP) |
| Priority | Tier 4 - Specialized |
| Complexity | Low |

#### Description
Convert a block-bodied method, property, or lambda to an expression-bodied equivalent using the `=>` syntax.

#### Preconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-CV3.1 | Workspace loaded and Ready | WorkspaceState == Ready |
| PRE-CV3.2 | Source file exists in workspace | Document in workspace |
| PRE-CV3.3 | Member exists at specified location | Symbol resolution |
| PRE-CV3.4 | Body contains single statement/expression | Block analysis |

#### Postconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| POST-CV3.1 | Block body replaced with expression body | Syntax transformation |
| POST-CV3.2 | Semantic equivalence maintained | Same behavior |
| POST-CV3.3 | Solution compiles | Zero errors |

#### Main Flow
| Step | Actor | Action |
|------|-------|--------|
| 1 | Agent | Sends `convert_to_expression_body` request |
| 2 | Server | Validates input parameters |
| 3 | Server | Locates member at specified location |
| 4 | Server | Validates body contains single expression/statement |
| 5 | Server | Extracts the expression |
| 6 | Server | Replaces block body with `=> expression` |
| 7 | Server | Returns result with modified file |

#### Alternate Flows
| ID | Trigger | Flow |
|----|---------|------|
| AF-CV3.1 | Preview mode | Return proposed changes without applying |
| AF-CV3.2 | Property getter only | Convert get accessor |
| AF-CV3.3 | Constructor with single assignment | Convert constructor body |

#### Exception Flows
| ID | Trigger | Error Code | Message |
|----|---------|------------|---------|
| EF-CV3.1 | Member not found | 2012 | No member found at specified location |
| EF-CV3.2 | Multiple statements | 3095 | Body contains multiple statements |
| EF-CV3.3 | Already expression body | 3096 | Member already uses expression body |
| EF-CV3.4 | Void method with expression | 3097 | Cannot use expression body for void method with non-expression statement |

#### Business Rules
| ID | Rule | Rationale |
|----|------|-----------|
| BR-CV3.1 | `{ return x; }` -> `=> x` | Return expression |
| BR-CV3.2 | `{ x = value; }` -> `=> x = value` | Assignment expression |
| BR-CV3.3 | `{ M(); }` -> `=> M()` | Void method with single call |
| BR-CV3.4 | Properties with both get/set not convertible | Syntax limitation |

---

### UC-CV4: Convert to Interpolated String

#### Overview
| Property | Value |
|----------|-------|
| ID | UC-CV4 |
| Name | Convert to Interpolated String |
| Actor | AI Agent (via MCP) |
| Priority | Tier 4 - Specialized |
| Complexity | Low |

#### Description
Convert `String.Format()` calls or string concatenation to interpolated string syntax (`$"..."`).

#### Preconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-CV4.1 | Workspace loaded and Ready | WorkspaceState == Ready |
| PRE-CV4.2 | Source file exists in workspace | Document in workspace |
| PRE-CV4.3 | String.Format or concatenation at location | Expression analysis |

#### Postconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| POST-CV4.1 | Expression replaced with interpolated string | Syntax replacement |
| POST-CV4.2 | Semantic equivalence maintained | Same output |
| POST-CV4.3 | Solution compiles | Zero errors |

#### Main Flow
| Step | Actor | Action |
|------|-------|--------|
| 1 | Agent | Sends `convert_to_interpolated_string` request |
| 2 | Server | Validates input parameters |
| 3 | Server | Locates expression at specified location |
| 4 | Server | Identifies pattern (String.Format or concatenation) |
| 5 | Server | Extracts format string and arguments |
| 6 | Server | Constructs interpolated string |
| 7 | Server | Replaces original expression |
| 8 | Server | Returns result with modified file |

#### Alternate Flows
| ID | Trigger | Flow |
|----|---------|------|
| AF-CV4.1 | Preview mode | Return proposed changes without applying |
| AF-CV4.2 | Concatenation with + operator | Convert to single interpolation |
| AF-CV4.3 | Format specifiers present | Preserve format specifiers in interpolation |

#### Exception Flows
| ID | Trigger | Error Code | Message |
|----|---------|------------|---------|
| EF-CV4.1 | No convertible expression | 2013 | No String.Format or concatenation found at location |
| EF-CV4.2 | Dynamic format string | 3098 | Cannot convert: format string is not a literal |
| EF-CV4.3 | Already interpolated | 3099 | Expression is already an interpolated string |

#### Business Rules
| ID | Rule | Rationale |
|----|------|-----------|
| BR-CV4.1 | `String.Format("{0}", x)` -> `$"{x}"` | Direct substitution |
| BR-CV4.2 | `String.Format("{0:N2}", x)` -> `$"{x:N2}"` | Preserve format |
| BR-CV4.3 | `a + " " + b` -> `$"{a} {b}"` | Concatenation |
| BR-CV4.4 | Escape braces: `{{` remains `{{` | Literal braces |

---

### UC-EF1: Encapsulate Field

#### Overview
| Property | Value |
|----------|-------|
| ID | UC-EF1 |
| Name | Encapsulate Field |
| Actor | AI Agent (via MCP) |
| Priority | Tier 3 - Valuable |
| Complexity | Low |

#### Description
Convert a public or internal field to a private field with a public property wrapper. Updates all references to use the property.

#### Preconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-EF1.1 | Workspace loaded and Ready | WorkspaceState == Ready |
| PRE-EF1.2 | Source file exists in workspace | Document in workspace |
| PRE-EF1.3 | Field exists at specified location | Field declaration found |
| PRE-EF1.4 | Field is not already private with property | Not already encapsulated |

#### Postconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| POST-EF1.1 | Field is private | Accessibility check |
| POST-EF1.2 | Property wrapping field exists | Property declaration |
| POST-EF1.3 | All references updated to use property | Reference analysis |
| POST-EF1.4 | Solution compiles | Zero errors |

#### Main Flow
| Step | Actor | Action |
|------|-------|--------|
| 1 | Agent | Sends `encapsulate_field` request with file and field name |
| 2 | Server | Validates input parameters |
| 3 | Server | Locates field declaration |
| 4 | Server | Validates field accessibility (public/internal/protected) |
| 5 | Server | Generates property name (default: PascalCase of field) |
| 6 | Server | Creates property with get/set accessors |
| 7 | Server | Changes field to private |
| 8 | Server | Renames field with underscore prefix (convention) |
| 9 | Server | Finds all references to original field |
| 10 | Server | Updates external references to use property |
| 11 | Server | Returns result with modified files |

#### Alternate Flows
| ID | Trigger | Flow |
|----|---------|------|
| AF-EF1.1 | Preview mode | Return proposed changes without applying |
| AF-EF1.2 | Custom property name provided | Use provided name |
| AF-EF1.3 | Field is readonly | Generate property with get-only accessor |
| AF-EF1.4 | Field has initializer | Preserve initializer on field |

#### Exception Flows
| ID | Trigger | Error Code | Message |
|----|---------|------------|---------|
| EF-EF1.1 | Field not found | 2008 | No field found with specified name |
| EF-EF1.2 | Already private with property | 3100 | Field is already encapsulated |
| EF-EF1.3 | Property name conflicts | 3003 | Property name conflicts with existing member |
| EF-EF1.4 | Field is const | 3101 | Cannot encapsulate const field |

#### Business Rules
| ID | Rule | Rationale |
|----|------|-----------|
| BR-EF1.1 | Field becomes `private` | Encapsulation principle |
| BR-EF1.2 | Property uses PascalCase | C# naming convention |
| BR-EF1.3 | Field renamed to `_camelCase` | Backing field convention |
| BR-EF1.4 | `readonly` field -> get-only property | Preserve immutability |
| BR-EF1.5 | Internal references in same class use field | Avoid indirection |

---

## 3. MCP Tool Interface Contracts

### 3.1 convert_to_async

#### Input Parameters
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sourceFile | string | Yes | - | Absolute path to source file |
| methodName | string | Yes | - | Name of method to convert |
| line | integer | No | - | Line number for disambiguation |
| column | integer | No | - | Column number for disambiguation |
| addAsyncSuffix | boolean | No | true | Add "Async" suffix to method name |
| updateCallers | boolean | No | false | Update callers to await |
| preview | boolean | No | false | Preview mode - return changes without applying |

#### Output Format (Success)
```json
{
  "success": true,
  "operation": "convert_to_async",
  "methodConverted": "ProcessDataAsync",
  "originalName": "ProcessData",
  "returnType": "Task<int>",
  "awaitedCalls": [
    { "original": "client.Get(url)", "converted": "await client.GetAsync(url)" }
  ],
  "filesModified": [
    {
      "path": "/path/to/file.cs",
      "changes": [
        { "type": "signature", "line": 15, "description": "Added async modifier and Task return" },
        { "type": "body", "line": 18, "description": "Replaced blocking call with await" }
      ]
    }
  ],
  "callersUpdated": 3
}
```

#### Output Format (Error)
```json
{
  "success": false,
  "error": {
    "code": 3090,
    "message": "Method is already asynchronous",
    "details": { "methodName": "ProcessDataAsync" }
  }
}
```

---

### 3.2 convert_foreach_to_linq

#### Input Parameters
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sourceFile | string | Yes | - | Absolute path to source file |
| line | integer | Yes | - | Line number of foreach statement |
| column | integer | No | 1 | Column (for multi-statement lines) |
| preferQuerySyntax | boolean | No | false | Use query syntax instead of method syntax |
| preview | boolean | No | false | Preview mode |

#### Output Format (Success)
```json
{
  "success": true,
  "operation": "convert_foreach_to_linq",
  "pattern": "filter_and_project",
  "linqMethods": ["Where", "Select", "ToList"],
  "originalCode": "foreach (var item in items) { if (item.Active) results.Add(item.Name); }",
  "convertedCode": "var results = items.Where(item => item.Active).Select(item => item.Name).ToList();",
  "filesModified": [
    {
      "path": "/path/to/file.cs",
      "changes": [
        { "type": "replace", "startLine": 25, "endLine": 28, "description": "Replaced foreach with LINQ" }
      ]
    }
  ],
  "usingsAdded": ["System.Linq"]
}
```

---

### 3.3 convert_to_expression_body

#### Input Parameters
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sourceFile | string | Yes | - | Absolute path to source file |
| memberName | string | No | - | Member name (if not using location) |
| line | integer | No | - | Line number of member |
| column | integer | No | - | Column for disambiguation |
| preview | boolean | No | false | Preview mode |

#### Output Format (Success)
```json
{
  "success": true,
  "operation": "convert_to_expression_body",
  "memberType": "method",
  "memberName": "GetFullName",
  "originalBody": "{ return FirstName + \" \" + LastName; }",
  "expressionBody": "=> FirstName + \" \" + LastName",
  "filesModified": [
    {
      "path": "/path/to/file.cs",
      "changes": [
        { "type": "replace", "line": 42, "description": "Converted to expression body" }
      ]
    }
  ]
}
```

---

### 3.4 convert_to_interpolated_string

#### Input Parameters
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sourceFile | string | Yes | - | Absolute path to source file |
| line | integer | Yes | - | Line number of expression |
| column | integer | No | 1 | Column for disambiguation |
| preview | boolean | No | false | Preview mode |

#### Output Format (Success)
```json
{
  "success": true,
  "operation": "convert_to_interpolated_string",
  "sourceType": "string_format",
  "originalExpression": "String.Format(\"Hello {0}, you have {1} messages\", name, count)",
  "interpolatedString": "$\"Hello {name}, you have {count} messages\"",
  "filesModified": [
    {
      "path": "/path/to/file.cs",
      "changes": [
        { "type": "replace", "line": 55, "description": "Converted to interpolated string" }
      ]
    }
  ]
}
```

---

### 3.5 encapsulate_field

#### Input Parameters
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sourceFile | string | Yes | - | Absolute path to source file |
| fieldName | string | Yes | - | Name of field to encapsulate |
| propertyName | string | No | - | Custom property name (default: PascalCase of field) |
| updateReferences | boolean | No | true | Update all external references |
| preview | boolean | No | false | Preview mode |

#### Output Format (Success)
```json
{
  "success": true,
  "operation": "encapsulate_field",
  "originalField": "name",
  "newFieldName": "_name",
  "propertyName": "Name",
  "propertyAccessors": { "get": true, "set": true },
  "referencesUpdated": 12,
  "filesModified": [
    {
      "path": "/path/to/Person.cs",
      "changes": [
        { "type": "modify", "line": 8, "description": "Changed field to private _name" },
        { "type": "insert", "line": 9, "description": "Added Name property" }
      ]
    },
    {
      "path": "/path/to/Program.cs",
      "changes": [
        { "type": "replace", "line": 22, "description": "Updated reference: person.name -> person.Name" }
      ]
    }
  ]
}
```

---

## 4. Error Code Definitions

### Convert Operation Errors (3090-3099)

| Code | Constant | Message | Operation |
|------|----------|---------|-----------|
| 3090 | METHOD_ALREADY_ASYNC | Method is already asynchronous | convert_to_async |
| 3091 | ASYNC_INCOMPATIBLE_PARAMS | Async methods cannot have ref/out parameters | convert_to_async |
| 3092 | LOOP_HAS_SIDE_EFFECTS | ForEach body has side effects incompatible with LINQ | convert_foreach_to_linq |
| 3093 | LOOP_MODIFIES_COLLECTION | Cannot convert: loop modifies source collection | convert_foreach_to_linq |
| 3094 | LOOP_COMPLEX_CONTROL_FLOW | Loop contains break/continue/goto incompatible with LINQ | convert_foreach_to_linq |
| 3095 | MULTIPLE_STATEMENTS | Body contains multiple statements | convert_to_expression_body |
| 3096 | ALREADY_EXPRESSION_BODY | Member already uses expression body | convert_to_expression_body |
| 3097 | VOID_EXPRESSION_INVALID | Cannot use expression body for void method with non-expression statement | convert_to_expression_body |
| 3098 | DYNAMIC_FORMAT_STRING | Cannot convert: format string is not a literal | convert_to_interpolated_string |
| 3099 | ALREADY_INTERPOLATED | Expression is already an interpolated string | convert_to_interpolated_string |

### Encapsulate Operation Errors (3100-3104)

| Code | Constant | Message | Operation |
|------|----------|---------|-----------|
| 3100 | ALREADY_ENCAPSULATED | Field is already encapsulated | encapsulate_field |
| 3101 | CANNOT_ENCAPSULATE_CONST | Cannot encapsulate const field | encapsulate_field |
| 3102 | CANNOT_ENCAPSULATE_STATIC_READONLY | Cannot encapsulate static readonly field in this context | encapsulate_field |
| 3103 | PROPERTY_CONFLICTS_EVENT | Property name conflicts with existing event | encapsulate_field |
| 3104 | FIELD_IN_STRUCT_REF_ISSUES | Encapsulating field in struct may cause ref issues | encapsulate_field |

---

## 5. Validation Rules

### 5.1 Input Validation Rules - Convert to Async

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-CV1.01 | sourceFile | Must be absolute path | 1001 |
| IV-CV1.02 | sourceFile | Must exist in workspace | 2002 |
| IV-CV1.03 | methodName | Must be valid C# identifier | 1003 |
| IV-CV1.04 | line | If provided, must be >= 1 | 1006 |
| IV-CV1.05 | column | If provided, must be >= 1 | 1007 |

### 5.2 Input Validation Rules - ForEach to LINQ

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-CV2.01 | sourceFile | Must be absolute path | 1001 |
| IV-CV2.02 | sourceFile | Must exist in workspace | 2002 |
| IV-CV2.03 | line | Must be >= 1 | 1006 |

### 5.3 Input Validation Rules - Expression Body

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-CV3.01 | sourceFile | Must be absolute path | 1001 |
| IV-CV3.02 | sourceFile | Must exist in workspace | 2002 |
| IV-CV3.03 | memberName | If provided, must be valid identifier | 1003 |
| IV-CV3.04 | line | If provided without memberName, required | 1005 |

### 5.4 Input Validation Rules - Interpolated String

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-CV4.01 | sourceFile | Must be absolute path | 1001 |
| IV-CV4.02 | sourceFile | Must exist in workspace | 2002 |
| IV-CV4.03 | line | Must be >= 1 | 1006 |

### 5.5 Input Validation Rules - Encapsulate Field

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-EF1.01 | sourceFile | Must be absolute path | 1001 |
| IV-EF1.02 | sourceFile | Must exist in workspace | 2002 |
| IV-EF1.03 | fieldName | Must be valid C# identifier | 1003 |
| IV-EF1.04 | propertyName | If provided, must be valid identifier | 1003 |

### 5.6 Semantic Validation Rules

| Rule ID | Rule | Error Code | Stage | Operation |
|---------|------|------------|-------|-----------|
| SV-CV1.01 | Method must not already be async | 3090 | Computing | convert_to_async |
| SV-CV1.02 | Method must not have ref/out params | 3091 | Computing | convert_to_async |
| SV-CV2.01 | Loop body must be LINQ-compatible | 3092 | Computing | convert_foreach_to_linq |
| SV-CV2.02 | Loop must not modify source collection | 3093 | Computing | convert_foreach_to_linq |
| SV-CV2.03 | Loop must not have complex control flow | 3094 | Computing | convert_foreach_to_linq |
| SV-CV3.01 | Body must have single statement | 3095 | Computing | convert_to_expression_body |
| SV-CV3.02 | Member must not already be expression body | 3096 | Computing | convert_to_expression_body |
| SV-CV4.01 | Format string must be literal | 3098 | Computing | convert_to_interpolated_string |
| SV-CV4.02 | Expression must not already be interpolated | 3099 | Computing | convert_to_interpolated_string |
| SV-EF1.01 | Field must not already be encapsulated | 3100 | Computing | encapsulate_field |
| SV-EF1.02 | Field must not be const | 3101 | Computing | encapsulate_field |
| SV-EF1.03 | Property name must not conflict | 3003 | Computing | encapsulate_field |

---

## 6. Test Scenario Matrix

### 6.1 convert_to_async Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-CV1.01 | Happy Path | Convert void method to Task | P0 |
| TC-CV1.02 | Happy Path | Convert T-returning method to Task<T> | P0 |
| TC-CV1.03 | Happy Path | Add await to HttpClient.GetAsync | P0 |
| TC-CV1.04 | Happy Path | Replace Thread.Sleep with Task.Delay | P1 |
| TC-CV1.05 | Happy Path | Add Async suffix to method name | P1 |
| TC-CV1.06 | Cascade | Update callers to await | P1 |
| TC-CV1.07 | Cascade | Convert interface method + implementations | P1 |
| TC-CV1.08 | Cascade | Convert virtual method in hierarchy | P1 |
| TC-CV1.09 | Edge | Method with no blocking calls | P1 |
| TC-CV1.10 | Edge | Lambda expression conversion | P2 |
| TC-CV1.11 | Negative | Method already async | P0 |
| TC-CV1.12 | Negative | Method has out parameter | P0 |
| TC-CV1.13 | Negative | Method is constructor | P0 |
| TC-CV1.14 | Negative | Method not found | P0 |
| TC-CV1.15 | Preview | Preview mode returns correct changes | P0 |

### 6.2 convert_foreach_to_linq Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-CV2.01 | Happy Path | Convert simple projection (Select) | P0 |
| TC-CV2.02 | Happy Path | Convert filter pattern (Where) | P0 |
| TC-CV2.03 | Happy Path | Convert Any pattern | P0 |
| TC-CV2.04 | Happy Path | Convert All pattern | P0 |
| TC-CV2.05 | Happy Path | Convert First pattern | P1 |
| TC-CV2.06 | Happy Path | Convert Sum/Count pattern | P1 |
| TC-CV2.07 | Complex | Convert Where + Select chain | P0 |
| TC-CV2.08 | Complex | Convert with ToList() | P0 |
| TC-CV2.09 | Edge | Add System.Linq using | P0 |
| TC-CV2.10 | Edge | Query syntax option | P2 |
| TC-CV2.11 | Negative | Loop with side effects | P0 |
| TC-CV2.12 | Negative | Loop modifies collection | P0 |
| TC-CV2.13 | Negative | Loop with break/continue | P0 |
| TC-CV2.14 | Negative | No foreach at location | P0 |
| TC-CV2.15 | Preview | Preview mode returns correct changes | P0 |

### 6.3 convert_to_expression_body Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-CV3.01 | Happy Path | Convert method with return statement | P0 |
| TC-CV3.02 | Happy Path | Convert property getter | P0 |
| TC-CV3.03 | Happy Path | Convert void method with single call | P1 |
| TC-CV3.04 | Happy Path | Convert lambda | P1 |
| TC-CV3.05 | Edge | Constructor with single assignment | P2 |
| TC-CV3.06 | Negative | Body has multiple statements | P0 |
| TC-CV3.07 | Negative | Already expression body | P0 |
| TC-CV3.08 | Negative | Void with non-expression | P1 |
| TC-CV3.09 | Negative | Member not found | P0 |
| TC-CV3.10 | Preview | Preview mode returns correct changes | P0 |

### 6.4 convert_to_interpolated_string Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-CV4.01 | Happy Path | Convert String.Format with args | P0 |
| TC-CV4.02 | Happy Path | Convert concatenation | P0 |
| TC-CV4.03 | Happy Path | Preserve format specifiers | P0 |
| TC-CV4.04 | Edge | Handle escaped braces | P1 |
| TC-CV4.05 | Edge | Multiple arguments | P0 |
| TC-CV4.06 | Negative | Dynamic format string | P0 |
| TC-CV4.07 | Negative | Already interpolated | P0 |
| TC-CV4.08 | Negative | No convertible expression | P0 |
| TC-CV4.09 | Preview | Preview mode returns correct changes | P0 |

### 6.5 encapsulate_field Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-EF1.01 | Happy Path | Encapsulate public field | P0 |
| TC-EF1.02 | Happy Path | Update all references | P0 |
| TC-EF1.03 | Happy Path | Custom property name | P1 |
| TC-EF1.04 | Happy Path | Readonly field to get-only property | P1 |
| TC-EF1.05 | Happy Path | Field with initializer | P1 |
| TC-EF1.06 | Cascade | Update references across files | P0 |
| TC-EF1.07 | Edge | Internal field | P1 |
| TC-EF1.08 | Edge | Protected field | P1 |
| TC-EF1.09 | Negative | Already private with property | P0 |
| TC-EF1.10 | Negative | Const field | P0 |
| TC-EF1.11 | Negative | Property name conflicts | P0 |
| TC-EF1.12 | Negative | Field not found | P0 |
| TC-EF1.13 | Preview | Preview mode returns correct changes | P0 |

---

## 7. Test Priority Summary

| Priority | Count | Description |
|----------|-------|-------------|
| P0 | 38 | Critical - must pass for release |
| P1 | 18 | Important - should pass |
| P2 | 4 | Nice to have |
| **Total** | **60** | |

---

## 8. Dependencies and Integration

### 8.1 Roslyn APIs Required

| Operation | Primary Roslyn APIs |
|-----------|---------------------|
| convert_to_async | `SyntaxGenerator.AsyncModifier`, `DocumentEditor` |
| convert_foreach_to_linq | `ForEachStatementSyntax`, `LinqExpressionSyntax` |
| convert_to_expression_body | `ArrowExpressionClauseSyntax`, `SyntaxFactory` |
| convert_to_interpolated_string | `InterpolatedStringExpressionSyntax` |
| encapsulate_field | `PropertyDeclarationSyntax`, `FindReferences` |

### 8.2 Cross-Operation Considerations

| Scenario | Handling |
|----------|----------|
| convert_to_async + extract_method | Extracted method should preserve async |
| encapsulate_field + rename_symbol | Property name follows rename |
| convert_foreach_to_linq + extract_variable | LINQ result can be extracted |

---

## 9. Open Questions

| ID | Question | Impact | Status |
|----|----------|--------|--------|
| OQ-1 | Should convert_to_async handle ConfigureAwait by default? | Deadlock prevention | Open |
| OQ-2 | Should encapsulate_field support init-only properties (C# 9+)? | Feature scope | Open |
| OQ-3 | Should convert_foreach_to_linq detect async enumerable patterns? | IAsyncEnumerable support | Open |
| OQ-4 | Naming convention for backing field: `_name` vs `_Name` vs `m_name`? | Code style | Recommend `_camelCase` |

---

## 10. Assumptions and Constraints

### Assumptions
1. C# language version is 7.0+ (expression bodies on all members)
2. .NET Standard 2.0+ or .NET Core (Task-based async)
3. System.Linq is available for LINQ conversions
4. Solution is in compilable state before operations

### Constraints
1. Error codes 3090-3104 are reserved for this specification
2. Operations must be atomic - all changes or none
3. Preview mode must not modify any files
4. All operations must preserve compilation state
