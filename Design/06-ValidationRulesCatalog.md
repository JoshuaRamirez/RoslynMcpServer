# Validation Rules Catalog - Roslyn MCP Move Server

## 1. Input Validation Rules

### 1.1 Path Validation

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-P01 | sourceFile | Must not be null or empty | 1005 |
| IV-P02 | sourceFile | Must be absolute path | 1001 |
| IV-P03 | sourceFile | Must end with .cs | 1001 |
| IV-P04 | sourceFile | Must exist on filesystem | 2001 |
| IV-P05 | targetFile | Must not be null or empty | 1005 |
| IV-P06 | targetFile | Must be absolute path | 1002 |
| IV-P07 | targetFile | Must end with .cs | 1002 |
| IV-P08 | targetFile | Parent directory must exist | 1002 |
| IV-P09 | solutionPath | If provided, must be absolute | 1001 |
| IV-P10 | solutionPath | If provided, must end with .sln or .csproj | 1001 |

### 1.2 Symbol Name Validation

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-S01 | symbolName | Must not be null or empty | 1005 |
| IV-S02 | symbolName | Must start with letter or underscore | 1003 |
| IV-S03 | symbolName | Must contain only valid identifier chars | 1003 |
| IV-S04 | symbolName | If qualified, each part must be valid | 1003 |
| IV-S05 | symbolName | Must be <= 500 characters | 1003 |

### 1.3 Namespace Validation

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-N01 | targetNamespace | Must not be null or empty | 1005 |
| IV-N02 | targetNamespace | Each segment must be valid identifier | 1004 |
| IV-N03 | targetNamespace | Must not start with digit | 1004 |
| IV-N04 | targetNamespace | Must not contain reserved keywords alone | 1004 |

### 1.4 Numeric Validation

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-L01 | line | If provided, must be >= 1 | 1006 |
| IV-L02 | line | Must be integer (not float) | 1006 |

---

## 2. Business Validation Rules (Semantic)

### 2.1 Symbol Resolution

| Rule ID | Rule | Error Code | Stage |
|---------|------|------------|-------|
| BV-R01 | Source file must be part of loaded workspace | 2002 | Resolving |
| BV-R02 | Symbol must exist in source file | 2003 | Resolving |
| BV-R03 | If line provided, symbol must be at that line | 2003 | Resolving |
| BV-R04 | If multiple matches, line must be provided | 2004 | Resolving |
| BV-R05 | Symbol must be a type (not method/field/etc) | 3001 | Resolving |
| BV-R06 | Symbol must be top-level (not nested) | 3002 | Resolving |

### 2.2 Move to File Rules

| Rule ID | Rule | Error Code | Stage |
|---------|------|------------|-------|
| BV-F01 | Target file must not equal source file | 3004 | Validating |
| BV-F02 | If target exists, no type with same name/ns | 3003 | Computing |
| BV-F03 | Target directory must be writable | 4002 | Committing |
| BV-F04 | If createTargetFile=false, target must exist | 2001 | Validating |

### 2.3 Move to Namespace Rules

| Rule ID | Rule | Error Code | Stage |
|---------|------|------------|-------|
| BV-N01 | Target namespace must differ from current | 3004 | Validating |
| BV-N02 | No type with same name in target namespace | 3003 | Computing |
| BV-N03 | Move must not break internal references | 3006 | Computing |
| BV-N04 | Move must not create circular dependency | 3005 | Computing |

### 2.4 Compilation Integrity

| Rule ID | Rule | Error Code | Stage |
|---------|------|------------|-------|
| BV-C01 | All references must be resolvable post-move | 4004 | Computing |
| BV-C02 | Accessibility constraints preserved | 3006 | Computing |
| BV-C03 | No new ambiguous references created | 4004 | Computing |

---

## 3. Filesystem Validation Rules

### 3.1 Read Operations

| Rule ID | Rule | Error Code |
|---------|------|------------|
| FS-R01 | Source file must be readable | 4002 |
| FS-R02 | Source file must not be locked | 4002 |
| FS-R03 | Source file encoding must be detectable | 4002 |

### 3.2 Write Operations

| Rule ID | Rule | Error Code |
|---------|------|------------|
| FS-W01 | Target directory must be writable | 4002 |
| FS-W02 | Target file must not be read-only | 4002 |
| FS-W03 | Sufficient disk space available | 4002 |
| FS-W04 | File path must not exceed OS limits | 4002 |

### 3.3 Delete Operations

| Rule ID | Rule | Error Code |
|---------|------|------------|
| FS-D01 | Source file deletable if emptied | 4002 |
| FS-D02 | No other process holding file lock | 4002 |

---

## 4. Environment Validation Rules

### 4.1 Runtime Dependencies

| Rule ID | Dependency | Check | Error Code |
|---------|------------|-------|------------|
| EV-D01 | Roslyn assemblies | Assembly.Load succeeds | 5002 |
| EV-D02 | MSBuild | MSBuildLocator finds instance | 5001 |
| EV-D03 | .NET SDK | SDK path resolvable | 5003 |

### 4.2 Workspace State

| Rule ID | Rule | Error Code |
|---------|------|------------|
| EV-W01 | Workspace must be in Ready state for refactoring | 2005 |
| EV-W02 | No concurrent operation in progress | 4001 |
| EV-W03 | Solution must be loaded | 2005 |

---

## 5. Validation Execution Order

### 5.1 move_type_to_file Validation Sequence



### 5.2 move_type_to_namespace Validation Sequence



### 5.3 diagnose Validation Sequence



---

## 5. Validation Execution Order

### 5.1 move_type_to_file Validation Sequence

1. Input Validation - immediate, before workspace access
   - IV-P01 through IV-P08
   - IV-S01 through IV-S05
   - IV-L01, IV-L02

2. Environment Validation
   - EV-W01, EV-W02, EV-W03

3. Resource Validation - requires workspace
   - BV-R01: Source in workspace
   - BV-F01: Target differs from source

4. Symbol Resolution Validation
   - BV-R02 through BV-R06

5. Semantic Validation - requires symbol
   - BV-F02: No name collision
   - BV-C01 through BV-C03

6. Filesystem Validation - before write
   - FS-R01 through FS-R03
   - FS-W01 through FS-W04

### 5.2 move_type_to_namespace Validation Sequence

1. Input Validation
   - IV-P01 through IV-P04
   - IV-S01 through IV-S05
   - IV-N01 through IV-N04

2. Environment Validation
   - EV-W01, EV-W02, EV-W03

3. Symbol Resolution Validation
   - BV-R01 through BV-R06

4. Semantic Validation
   - BV-N01 through BV-N04
   - BV-C01 through BV-C03

5. Filesystem Validation
   - FS-R01 through FS-R03
   - FS-W01 through FS-W04
