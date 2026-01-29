# Use Case Specifications - Extract Operations

## UC-E1: Extract Method

### Overview
| Property | Value |
|----------|-------|
| ID | UC-E1 |
| Name | Extract Method |
| Actor | AI Agent (via MCP) |
| Priority | Tier 1 - Critical |
| Complexity | High |

### Description
Extract selected statements or expression into a new method. Automatically determines parameters (values read), return value (values written), and local variables.

### Preconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-E1.1 | Workspace loaded and Ready | WorkspaceState == Ready |
| PRE-E1.2 | Source file exists | Document in workspace |
| PRE-E1.3 | Selection is valid extractable code | Syntax analysis |
| PRE-E1.4 | Selection forms complete statements | Not partial expression |
| PRE-E1.5 | New method name provided | Valid identifier |

### Postconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| POST-E1.1 | New method created | Method exists in type |
| POST-E1.2 | Original code replaced with call | Call site correct |
| POST-E1.3 | Parameters correctly inferred | Input values as params |
| POST-E1.4 | Return value correctly handled | Output value returned |
| POST-E1.5 | Solution compiles | Zero errors |

### Business Rules
| ID | Rule | Rationale |
|----|------|-----------|
| BR-E1.1 | Method placed in same type as source | Locality |
| BR-E1.2 | Method visibility defaults to private | Encapsulation |
| BR-E1.3 | Static if no instance access | Correctness |
| BR-E1.4 | Async if contains await | Async correctness |
| BR-E1.5 | Parameters ordered: regular, ref, out | Convention |

### Exception Flows
| ID | Trigger | Error Code |
|----|---------|------------|
| EF-E1.1 | Invalid selection | 3030 |
| EF-E1.2 | Contains yield | 3031 |
| EF-E1.3 | Unresolvable control flow | 3032 |

---

## UC-E2: Extract Interface

### Overview
| Property | Value |
|----------|-------|
| ID | UC-E2 |
| Name | Extract Interface |
| Priority | Tier 1 - Critical |
| Complexity | Medium |

### Description
Create a new interface from selected members of a class/struct. Optionally update the original type to implement the interface.

### Preconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-E2.1 | Workspace loaded | WorkspaceState == Ready |
| PRE-E2.2 | Target type exists | Class/struct/record in workspace |
| PRE-E2.3 | Members selected or all public | At least one member |
| PRE-E2.4 | Interface name provided | Valid identifier |

### Postconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| POST-E2.1 | Interface created | Interface exists |
| POST-E2.2 | Members declared in interface | Signatures match |
| POST-E2.3 | Original type implements interface | Base list updated |
| POST-E2.4 | Solution compiles | Zero errors |

### Business Rules
| ID | Rule | Rationale |
|----|------|-----------|
| BR-E2.1 | Only public members extractable | Interface semantics |
| BR-E2.2 | Properties include get/set as declared | Signature match |
| BR-E2.3 | Static members excluded | Instance interface |

---

## UC-E3: Extract Base Class

### Overview
| Property | Value |
|----------|-------|
| ID | UC-E3 |
| Name | Extract Base Class |
| Priority | Tier 3 - Valuable |
| Complexity | High |

### Description
Create a new base class from selected members, moving implementations up.

### Preconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-E3.1 | Workspace loaded | WorkspaceState == Ready |
| PRE-E3.2 | Source type is class | Not struct |
| PRE-E3.3 | Source type not sealed | Can be derived |
| PRE-E3.4 | Base class name provided | Valid identifier |

### Postconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| POST-E3.1 | Base class created | Class exists |
| POST-E3.2 | Members moved to base | In base class |
| POST-E3.3 | Original inherits from base | Inheritance |
| POST-E3.4 | Solution compiles | Zero errors |

---

## UC-E4: Extract Variable

### Overview
| Property | Value |
|----------|-------|
| ID | UC-E4 |
| Name | Extract Variable |
| Priority | Tier 2 - Important |
| Complexity | Low |

### Description
Extract a selected expression into a local variable.

### Preconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| PRE-E4.1 | Workspace loaded | WorkspaceState == Ready |
| PRE-E4.2 | Selection is valid expression | Assignable |
| PRE-E4.3 | Variable name provided | Valid identifier |

### Postconditions
| ID | Condition | Verification |
|----|-----------|--------------|
| POST-E4.1 | Variable declared | Declaration exists |
| POST-E4.2 | Expression assigned | Initialization |
| POST-E4.3 | Original replaced | Reference to variable |

---

## UC-E5: Extract Constant

### Overview
| Property | Value |
|----------|-------|
| ID | UC-E5 |
| Name | Extract Constant |
| Priority | Tier 2 - Important |
| Complexity | Low |

### Description
Extract a literal value into a named constant.

### Business Rules
| ID | Rule | Rationale |
|----|------|-----------|
| BR-E5.1 | const for primitives and strings | Compile-time |
| BR-E5.2 | static readonly for reference types | Runtime |
| BR-E5.3 | Constant placed at class level | Scope |
