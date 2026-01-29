# Use Case Specifications - Rename Operations

## UC-R1: Rename Symbol

### Overview
| Property | Value |
|----------|-------|
| ID | UC-R1 |
| Name | Rename Symbol |
| Actor | AI Agent (via MCP) |
| Priority | Tier 1 - Critical |
| Complexity | Medium |

### Description
Rename any named symbol (class, method, property, field, variable, parameter, namespace) across the entire solution, updating all references to maintain compilability.

---

### Preconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-R1.1 | Workspace is loaded and in Ready state | WorkspaceState == Ready |
| PRE-R1.2 | Source file exists in workspace | Document exists |
| PRE-R1.3 | Symbol exists at location | Symbol resolvable |
| PRE-R1.4 | Symbol is renameable | Not an implicit/compiler-generated symbol |
| PRE-R1.5 | New name is valid C# identifier | Regex validation |
| PRE-R1.6 | New name differs from current | newName \!= currentName |

---

### Postconditions

| ID | Condition | Verification |
|----|-----------|--------------|
| POST-R1.1 | Symbol has new name | Parse and verify |
| POST-R1.2 | All references updated | Find references returns new name |
| POST-R1.3 | Solution compiles | Zero compilation errors |
| POST-R1.4 | No naming conflicts introduced | No ambiguous references |
| POST-R1.5 | Documentation comments updated | XML refs updated |

---

### Main Success Scenario

| Step | Actor | System | State |
|------|-------|--------|-------|
| 1 | Agent sends rename_symbol request | - | Pending |
| 2 | - | Validate input parameters | Validating |
| 3 | - | Resolve symbol at location | Resolving |
| 4 | - | Find all references (declarations + usages) | Resolving |
| 5 | - | Check for naming conflicts | Computing |
| 6 | - | Compute text changes | Computing |
| 7 | - | Apply changes to workspace | Applying |
| 8 | - | Persist to filesystem | Committing |
| 9 | - | Return success response | Completed |

---

### Alternative Flows

#### AF-R1.1: Rename Overloaded Method
**Trigger**: Symbol is one of multiple overloads
**Steps**:
1. Rename only the specific overload
2. Other overloads retain original name
3. Callers updated based on overload resolution
4. Continue from Main Step 7

#### AF-R1.2: Rename Interface Member
**Trigger**: Symbol is interface member with implementations
**Steps**:
1. Find all implementing members
2. Option: rename implementations too (default: yes)
3. If yes, rename interface member and all implementations
4. If no, rename only interface member (may break implementations)
5. Continue from Main Step 7

#### AF-R1.3: Rename Virtual/Override Method
**Trigger**: Symbol is virtual method or override
**Steps**:
1. Find entire override chain (base to most-derived)
2. Rename all members in chain
3. Continue from Main Step 7

#### AF-R1.4: Rename Triggers File Rename
**Trigger**: Symbol is type and type name matches filename
**Steps**:
1. Detect filename matches typename.cs
2. If renameFile=true (default for types), also rename file
3. Update project references to file
4. Continue from Main Step 7

---

### Exception Flows

#### EF-R1.1: Name Conflict
**Trigger**: New name conflicts with existing symbol in scope
**Response**: Error NAME_CONFLICT_SCOPE
**Recovery**: Return conflicting symbol location, suggest alternatives

#### EF-R1.2: Reserved Keyword
**Trigger**: New name is C# keyword without @ prefix
**Response**: Error RESERVED_KEYWORD
**Recovery**: Suggest @keyword syntax or alternative name

---

### Business Rules

| ID | Rule | Rationale |
|----|------|-----------|
| BR-R1.1 | Rename propagates through entire solution | Consistency |
| BR-R1.2 | Rename respects overload resolution | Correctness |
| BR-R1.3 | Virtual/override chains renamed together | Polymorphism preserved |
| BR-R1.4 | Interface implementations optionally renamed | Flexibility |
| BR-R1.5 | Partial class/method names unified | Partial member consistency |
| BR-R1.6 | nameof() expressions always updated | Semantic correctness |

---

### Symbol-Specific Behavior

| Symbol Kind | Special Handling |
|-------------|------------------|
| Class/Struct/Record | May trigger file rename; constructors renamed too |
| Interface | Implementations optionally renamed |
| Method | Overloads handled individually; virtuals chain |
| Property | Backing field renamed if auto-property |
| Field | Private fields may have conventions (_field) |
| Parameter | Local to method; no cross-file impact |
| Local Variable | Local to scope; no cross-file impact |
| Type Parameter | Generic type constraints updated |
| Namespace | All types in namespace updated; folder optional |

