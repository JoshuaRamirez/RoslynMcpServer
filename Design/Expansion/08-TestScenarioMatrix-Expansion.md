# Test Scenario Matrix - Expansion

## 1. Rename Operations Test Scenarios

### 1.1 rename_symbol

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-R1.01 | Happy Path | Rename class with references | P0 |
| TC-R1.02 | Happy Path | Rename method | P0 |
| TC-R1.03 | Happy Path | Rename property | P0 |
| TC-R1.04 | Happy Path | Rename field | P1 |
| TC-R1.05 | Happy Path | Rename local variable | P1 |
| TC-R1.06 | Happy Path | Rename parameter | P1 |
| TC-R1.07 | Cascade | Rename virtual method (whole chain) | P0 |
| TC-R1.08 | Cascade | Rename interface member + implementations | P0 |
| TC-R1.09 | Cascade | Rename class triggers file rename | P1 |
| TC-R1.10 | Edge | Rename with nameof() expressions | P1 |
| TC-R1.11 | Edge | Rename generic type parameter | P2 |
| TC-R1.12 | Edge | Rename in partial class | P1 |
| TC-R1.13 | Negative | Rename to existing name in scope | P0 |
| TC-R1.14 | Negative | Rename to C# keyword | P0 |
| TC-R1.15 | Negative | Rename constructor directly | P1 |
| TC-R1.16 | Negative | Rename external symbol | P1 |
| TC-R1.17 | Preview | Preview mode returns correct changes | P0 |

## 2. Extract Operations Test Scenarios

### 2.1 extract_method

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-E1.01 | Happy Path | Extract statements to void method | P0 |
| TC-E1.02 | Happy Path | Extract expression to returning method | P0 |
| TC-E1.03 | Happy Path | Extract with single input parameter | P0 |
| TC-E1.04 | Happy Path | Extract with multiple parameters | P0 |
| TC-E1.05 | Happy Path | Extract with return value | P0 |
| TC-E1.06 | Complex | Extract with out parameter | P1 |
| TC-E1.07 | Complex | Extract with ref parameter | P1 |
| TC-E1.08 | Complex | Extract with multiple return values (tuple) | P1 |
| TC-E1.09 | Auto | Auto-detect static method | P1 |
| TC-E1.10 | Auto | Auto-detect async method | P1 |
| TC-E1.11 | Negative | Invalid selection (partial statement) | P0 |
| TC-E1.12 | Negative | Contains yield return | P1 |
| TC-E1.13 | Negative | Unresolvable control flow | P1 |
| TC-E1.14 | Preview | Preview shows correct method signature | P0 |

### 2.2 extract_interface

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-E2.01 | Happy Path | Extract all public members | P0 |
| TC-E2.02 | Happy Path | Extract selected members only | P0 |
| TC-E2.03 | Happy Path | Extract to separate file | P0 |
| TC-E2.04 | Happy Path | Extract from generic class | P1 |
| TC-E2.05 | Complex | Update references to use interface | P1 |
| TC-E2.06 | Negative | No public members | P0 |
| TC-E2.07 | Negative | Interface name exists | P0 |

### 2.3 extract_variable

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-E3.01 | Happy Path | Extract simple expression | P0 |
| TC-E3.02 | Happy Path | Extract with type inference | P0 |
| TC-E3.03 | Complex | Replace all occurrences | P1 |
| TC-E3.04 | Negative | Expression has side effects | P1 |

## 3. Generate Operations Test Scenarios

### 3.1 generate_constructor

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-G1.01 | Happy Path | Generate from all fields | P0 |
| TC-G1.02 | Happy Path | Generate from selected fields | P0 |
| TC-G1.03 | Happy Path | Generate with null checks | P1 |
| TC-G1.04 | Negative | Constructor already exists | P0 |
| TC-G1.05 | Negative | Static class | P1 |

### 3.2 implement_interface

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-G2.01 | Happy Path | Implicit implementation | P0 |
| TC-G2.02 | Happy Path | Explicit implementation | P0 |
| TC-G2.03 | Happy Path | Interface with generics | P1 |
| TC-G2.04 | Negative | Already implemented | P0 |

### 3.3 generate_overrides

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-G3.01 | Happy Path | Override virtual method | P0 |
| TC-G3.02 | Happy Path | Override abstract method | P0 |
| TC-G3.03 | Happy Path | Override property | P1 |
| TC-G3.04 | Negative | No overridable members | P0 |

## 4. Organize Imports Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-O1.01 | sort_usings | Sort alphabetically | P0 |
| TC-O1.02 | sort_usings | System namespaces first | P0 |
| TC-O2.01 | remove_unused | Remove unused usings | P0 |
| TC-O2.02 | remove_unused | Keep used usings | P0 |
| TC-O3.01 | add_missing | Add missing usings | P0 |
| TC-O3.02 | add_missing | Handle ambiguous types | P1 |

## 5. Change Signature Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-S1.01 | add_parameter | Add with default value | P0 |
| TC-S1.02 | add_parameter | Add at specific position | P1 |
| TC-S2.01 | remove_parameter | Remove unused parameter | P0 |
| TC-S2.02 | remove_parameter | Error if parameter used | P0 |
| TC-S3.01 | reorder | Reorder parameters | P1 |
| TC-S4.01 | Cascade | Update all call sites | P0 |
| TC-S4.02 | Cascade | Update overrides | P0 |

## 6. Inline Operations Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-I1.01 | inline_variable | Inline single-use variable | P0 |
| TC-I1.02 | inline_variable | Error if modified | P0 |
| TC-I2.01 | inline_method | Inline simple method | P1 |
| TC-I2.02 | inline_method | Error if recursive | P0 |
| TC-I2.03 | inline_method | Error if virtual | P0 |

## 7. Encapsulation Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-F1.01 | encapsulate_field | Basic field encapsulation | P0 |
| TC-F1.02 | encapsulate_field | Update all references | P0 |
| TC-F1.03 | encapsulate_field | Custom property name | P1 |
| TC-F1.04 | encapsulate_field | Error if already private | P1 |

## 8. Convert Operations Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-C1.01 | convert_to_async | Convert void to Task | P0 |
| TC-C1.02 | convert_to_async | Convert T to Task T | P0 |
| TC-C1.03 | convert_to_async | Add await to calls | P0 |
| TC-C1.04 | convert_to_async | Error if already async | P0 |

## 9. Hierarchy Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-H1.01 | pull_member_up | Pull method to base | P0 |
| TC-H1.02 | pull_member_up | Pull as abstract | P1 |
| TC-H2.01 | push_member_down | Push to all derived | P1 |
| TC-H2.02 | push_member_down | Error if no derived | P0 |

## 10. Cross-Cutting Test Scenarios

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-X01 | Concurrency | Two operations simultaneous | P0 |
| TC-X02 | Preview | All operations support preview | P0 |
| TC-X03 | Compilation | All operations preserve compilation | P0 |
| TC-X04 | Timeout | Operations respect timeout | P1 |
| TC-X05 | Large Solution | Performance on 100+ projects | P1 |

## 11. Priority Summary

| Priority | Count | Description |
|----------|-------|-------------|
| P0 | 45 | Critical - must pass |
| P1 | 30 | Important - should pass |
| P2 | 5 | Nice to have |
