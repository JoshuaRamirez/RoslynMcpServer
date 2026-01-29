# Test Scenario Matrix - Roslyn MCP Move Server

## 1. Use Case to Test Scenario Mapping

### 1.1 UC-1: Move Type to File

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-1.01 | Happy Path | Move class to new empty file | P0 |
| TC-1.02 | Happy Path | Move class to existing file | P0 |
| TC-1.03 | Happy Path | Move interface to new file | P0 |
| TC-1.04 | Happy Path | Move struct to new file | P1 |
| TC-1.05 | Happy Path | Move enum to new file | P1 |
| TC-1.06 | Happy Path | Move record to new file | P1 |
| TC-1.07 | Happy Path | Move delegate to new file | P2 |
| TC-1.08 | Happy Path | Move with nested types | P1 |
| TC-1.09 | Happy Path | Move from multi-type file | P0 |
| TC-1.10 | Happy Path | Preview mode returns changes | P0 |
| TC-1.11 | References | References in same file updated | P0 |
| TC-1.12 | References | References in same project updated | P0 |
| TC-1.13 | References | References across projects updated | P0 |
| TC-1.14 | References | Using directives added correctly | P0 |
| TC-1.15 | References | Unused using directives removed | P2 |
| TC-1.16 | Edge | Source file emptied and deleted | P1 |
| TC-1.17 | Edge | Target file in subdirectory created | P1 |
| TC-1.18 | Edge | Type with XML docs preserved | P1 |
| TC-1.19 | Edge | Type with attributes preserved | P1 |
| TC-1.20 | Edge | Generic type moved correctly | P1 |

### 1.2 UC-2: Move Type to Namespace

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-2.01 | Happy Path | Change namespace of class | P0 |
| TC-2.02 | Happy Path | Change to nested namespace | P0 |
| TC-2.03 | Happy Path | Change to parent namespace | P1 |
| TC-2.04 | Happy Path | Change to unrelated namespace | P1 |
| TC-2.05 | Happy Path | Preview mode returns changes | P0 |
| TC-2.06 | References | Using directives updated | P0 |
| TC-2.07 | References | Fully qualified names updated | P1 |
| TC-2.08 | References | Global using handled | P2 |
| TC-2.09 | Edge | File-scoped namespace updated | P1 |
| TC-2.10 | Edge | Block namespace updated | P1 |
| TC-2.11 | Combined | Namespace + file location update | P1 |

### 1.3 UC-3: Diagnose Environment

| Scenario ID | Category | Description | Priority |
|-------------|----------|-------------|----------|
| TC-3.01 | Happy Path | All components healthy | P0 |
| TC-3.02 | Happy Path | Solution loaded reported | P0 |
| TC-3.03 | Happy Path | Verbose mode detailed info | P1 |
| TC-3.04 | Degraded | MSBuild not found reported | P0 |
| TC-3.05 | Degraded | SDK not found reported | P1 |
| TC-3.06 | Degraded | Solution load failure details | P0 |

---

## 2. Equivalence Classes

### 2.1 Symbol Type Equivalence Classes

| Class | Members | Representative | Test Focus |
|-------|---------|----------------|------------|
| EC-ST1 | Class | ClassDeclaration | Primary path |
| EC-ST2 | Struct | StructDeclaration | Value type handling |
| EC-ST3 | Interface | InterfaceDeclaration | No implementation |
| EC-ST4 | Enum | EnumDeclaration | Minimal body |
| EC-ST5 | Record | RecordDeclaration | Modern syntax |
| EC-ST6 | Delegate | DelegateDeclaration | Single line |

### 2.2 File State Equivalence Classes

| Class | Condition | Representative | Test Focus |
|-------|-----------|----------------|------------|
| EC-FS1 | Target does not exist | New file creation | Create path |
| EC-FS2 | Target exists, empty | Append to empty | Edge case |
| EC-FS3 | Target exists with content | Merge with existing | Merge logic |
| EC-FS4 | Source has one type | Source deletion | Cleanup |
| EC-FS5 | Source has multiple types | Partial extraction | Preservation |

### 2.3 Reference Count Equivalence Classes

| Class | Reference Count | Test Focus |
|-------|-----------------|------------|
| EC-RC1 | 0 references | Unreferenced type |
| EC-RC2 | 1-10 references | Small scale |
| EC-RC3 | 11-100 references | Medium scale |
| EC-RC4 | 100+ references | Performance |

---

## 3. Boundary Conditions

### 3.1 Path Boundaries

| ID | Boundary | Test Value | Expected |
|----|----------|------------|----------|
| BC-P01 | Min path length | C:/a.cs | Valid |
| BC-P02 | Max path length | 260 char path | Platform-dependent |
| BC-P03 | Deeply nested | 20+ level path | Valid |
| BC-P04 | Root level | C:/File.cs | Valid |

### 3.2 Symbol Name Boundaries

| ID | Boundary | Test Value | Expected |
|----|----------|------------|----------|
| BC-S01 | Single char | A | Valid |
| BC-S02 | Starts underscore | _Test | Valid |
| BC-S03 | All digits after first | A123 | Valid |
| BC-S04 | Max reasonable length | 200 chars | Valid |
| BC-S05 | Unicode chars | Valid unicode | Valid |

### 3.3 Line Number Boundaries

| ID | Boundary | Test Value | Expected |
|----|----------|------------|----------|
| BC-L01 | Minimum valid | 1 | Valid |
| BC-L02 | Zero | 0 | Error 1006 |
| BC-L03 | Negative | -1 | Error 1006 |
| BC-L04 | Very large | 1000000 | Valid syntax, may not exist |

### 3.4 Namespace Boundaries

| ID | Boundary | Test Value | Expected |
|----|----------|------------|----------|
| BC-N01 | Single segment | MyNamespace | Valid |
| BC-N02 | Many segments | A.B.C.D.E.F.G.H | Valid |
| BC-N03 | Keyword escaped | @class | Valid |

---

## 4. Negative Test Cases

### 4.1 Input Validation Failures

| ID | Test | Input | Expected Error |
|----|------|-------|----------------|
| NT-I01 | Missing sourceFile | sourceFile=null | 1005 |
| NT-I02 | Missing symbolName | symbolName=null | 1005 |
| NT-I03 | Missing targetFile | targetFile=null | 1005 |
| NT-I04 | Relative source path | ./file.cs | 1001 |
| NT-I05 | Relative target path | ./target.cs | 1002 |
| NT-I06 | Non-cs extension | file.txt | 1001 |
| NT-I07 | Invalid symbol chars | 123Class | 1003 |
| NT-I08 | Invalid namespace | 123.Namespace | 1004 |
| NT-I09 | Zero line number | line=0 | 1006 |
| NT-I10 | Negative line | line=-5 | 1006 |

### 4.2 Resource Not Found Failures

| ID | Test | Input | Expected Error |
|----|------|-------|----------------|
| NT-R01 | Source file missing | non-existent path | 2001 |
| NT-R02 | Source not in solution | external file | 2002 |
| NT-R03 | Symbol not in file | wrong name | 2003 |
| NT-R04 | Symbol at wrong line | wrong line | 2003 |
| NT-R05 | No workspace loaded | before load | 2005 |

### 4.3 Semantic Failures

| ID | Test | Scenario | Expected Error |
|----|------|----------|----------------|
| NT-S01 | Method symbol | try to move method | 3001 |
| NT-S02 | Property symbol | try to move property | 3001 |
| NT-S03 | Field symbol | try to move field | 3001 |
| NT-S04 | Nested type | try to move nested class | 3002 |
| NT-S05 | Name collision | target has same type | 3003 |
| NT-S06 | Same location | source=target | 3004 |
| NT-S07 | Same namespace | current=target | 3004 |

### 4.4 System Failures

| ID | Test | Scenario | Expected Error |
|----|------|----------|----------------|
| NT-Y01 | Concurrent operation | two moves at once | 4001 |
| NT-Y02 | Read-only file | target is read-only | 4002 |
| NT-Y03 | Locked file | file locked by process | 4002 |

---

## 5. Integration Test Scenarios

### 5.1 End-to-End Scenarios

| ID | Scenario | Steps | Validation |
|----|----------|-------|------------|
| IT-01 | Full refactoring cycle | Load, Move, Verify compile | Zero errors |
| IT-02 | Preview then execute | Preview, Verify, Execute | Consistent |
| IT-03 | Multi-project solution | Move type across projects | References valid |
| IT-04 | Large solution | 100+ project solution | Performance acceptable |

### 5.2 Regression Scenarios

| ID | Scenario | Risk | Validation |
|----|----------|------|------------|
| RT-01 | XML comments preserved | Data loss | Comments present post-move |
| RT-02 | Attributes preserved | Metadata loss | Attributes present |
| RT-03 | Formatting preserved | Style change | Similar formatting |
| RT-04 | Encoding preserved | Corruption | Same encoding post-write |

---

## 6. Performance Test Thresholds

| Metric | Small Project | Medium Project | Large Project |
|--------|---------------|----------------|---------------|
| Project count | 1-5 | 6-50 | 51+ |
| Document count | 1-50 | 51-500 | 501+ |
| Move duration | < 2s | < 10s | < 60s |
| Memory delta | < 50MB | < 200MB | < 1GB |
