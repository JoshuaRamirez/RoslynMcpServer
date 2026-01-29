# Use Case Specifications - Other Operations

## 1. Inline Operations

### UC-I1: Inline Method
| Property | Value |
|----------|-------|
| ID | UC-I1 |
| Priority | Tier 4 - Specialized |
| Complexity | High |

**Description**: Replace all calls to a method with the method body.

**Preconditions**:
- Method is not recursive
- Method is not virtual/override/abstract

**Postconditions**:
- All call sites replaced with method body
- Method optionally removed

### UC-I2: Inline Variable
| Property | Value |
|----------|-------|
| ID | UC-I2 |
| Priority | Tier 2 - Important |
| Complexity | Low |

**Description**: Replace variable uses with its initialization expression.

**Preconditions**:
- Variable assigned exactly once
- Not modified after init

### UC-I3: Inline Constant
| Property | Value |
|----------|-------|
| ID | UC-I3 |
| Priority | Tier 4 - Specialized |
| Complexity | Low |

**Description**: Replace constant uses with literal value.

---

## 2. Generate Operations

### UC-G1: Generate Constructor
| Property | Value |
|----------|-------|
| ID | UC-G1 |
| Priority | Tier 1 - Critical |
| Complexity | Medium |

**Description**: Generate constructor initializing fields/properties.

**Postconditions**:
- Constructor exists with matching parameters
- Parameters assigned to members

### UC-G2: Generate Method Stub
| Property | Value |
|----------|-------|
| ID | UC-G2 |
| Priority | Tier 3 - Valuable |
| Complexity | Medium |

**Description**: Generate method from undefined call site.

### UC-G3: Generate Overrides
| Property | Value |
|----------|-------|
| ID | UC-G3 |
| Priority | Tier 2 - Important |
| Complexity | Medium |

**Description**: Generate override methods for base class members.

### UC-G4: Implement Interface (Explicit)
| Property | Value |
|----------|-------|
| ID | UC-G4 |
| Priority | Tier 2 - Important |
| Complexity | Medium |

**Description**: Generate explicit interface implementation.

### UC-G5: Implement Interface (Implicit)
| Property | Value |
|----------|-------|
| ID | UC-G5 |
| Priority | Tier 2 - Important |
| Complexity | Medium |

**Description**: Generate implicit interface implementation.

---

## 3. Organize Imports

### UC-O1: Sort Usings
| Property | Value |
|----------|-------|
| ID | UC-O1 |
| Priority | Tier 3 - Valuable |
| Complexity | Low |

**Description**: Sort using directives alphabetically.

### UC-O2: Remove Unused Usings
| Property | Value |
|----------|-------|
| ID | UC-O2 |
| Priority | Tier 2 - Important |
| Complexity | Low |

**Description**: Remove unused using directives.

### UC-O3: Add Missing Usings
| Property | Value |
|----------|-------|
| ID | UC-O3 |
| Priority | Tier 1 - Critical |
| Complexity | Medium |

**Description**: Add usings for unresolved types.

---

## 4. Change Signature

### UC-S1: Add Parameter
| Property | Value |
|----------|-------|
| ID | UC-S1 |
| Priority | Tier 3 - Valuable |
| Complexity | High |

**Description**: Add parameter to method and update call sites.

### UC-S2: Remove Parameter
| Property | Value |
|----------|-------|
| ID | UC-S2 |
| Priority | Tier 3 - Valuable |
| Complexity | High |

**Description**: Remove parameter from method and call sites.

### UC-S3: Reorder Parameters
| Property | Value |
|----------|-------|
| ID | UC-S3 |
| Priority | Tier 3 - Valuable |
| Complexity | High |

**Description**: Reorder method parameters and call sites.

### UC-S4: Change Return Type
| Property | Value |
|----------|-------|
| ID | UC-S4 |
| Priority | Tier 3 - Valuable |
| Complexity | High |

**Description**: Change method return type and usages.

---

## 5. Encapsulation

### UC-F1: Encapsulate Field
| Property | Value |
|----------|-------|
| ID | UC-F1 |
| Priority | Tier 3 - Valuable |
| Complexity | Low |

**Description**: Convert public field to private with property.

**Postconditions**:
- Field is private
- Property exposes field
- References updated

---

## 6. Convert Operations

### UC-C1: Convert to Async
| Property | Value |
|----------|-------|
| ID | UC-C1 |
| Priority | Tier 3 - Valuable |
| Complexity | High |

**Description**: Convert sync method to async.

**Postconditions**:
- Method marked async
- Return type is Task/Task T
- Blocking calls replaced with await

### UC-C2: Convert ForEach to LINQ
| Property | Value |
|----------|-------|
| ID | UC-C2 |
| Priority | Tier 4 - Specialized |
| Complexity | Medium |

**Description**: Convert foreach to LINQ expression.

### UC-C3: Convert Anonymous to Class
| Property | Value |
|----------|-------|
| ID | UC-C3 |
| Priority | Tier 4 - Specialized |
| Complexity | Medium |

**Description**: Convert anonymous type to named class/record.

### UC-C4: Convert Tuple to Struct
| Property | Value |
|----------|-------|
| ID | UC-C4 |
| Priority | Tier 4 - Specialized |
| Complexity | Medium |

**Description**: Convert tuple to named struct.

---

## 7. Hierarchy Operations

### UC-H1: Pull Member Up
| Property | Value |
|----------|-------|
| ID | UC-H1 |
| Priority | Tier 3 - Valuable |
| Complexity | High |

**Description**: Move member from derived to base class.

**Preconditions**:
- Base class exists and editable
- Member dependencies satisfiable in base

**Postconditions**:
- Member in base class
- Removed from derived

### UC-H2: Push Member Down
| Property | Value |
|----------|-------|
| ID | UC-H2 |
| Priority | Tier 4 - Specialized |
| Complexity | High |

**Description**: Move member from base to all derived classes.

**Preconditions**:
- At least one derived class exists
- All derived classes editable

**Postconditions**:
- Member in all derived classes
- Removed from base (or made abstract)
