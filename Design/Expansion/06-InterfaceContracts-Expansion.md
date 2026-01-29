# Interface Contracts - Expansion

## 1. Rename Operations - rename_symbol

### Input Parameters
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sourceFile | string | Yes | - | Absolute path to file |
| symbolName | string | Yes | - | Current symbol name |
| newName | string | Yes | - | New symbol name |
| line | integer | No | - | Line for disambiguation |
| column | integer | No | - | Column for disambiguation |
| renameOverloads | boolean | No | false | Rename all overloads |
| renameImplementations | boolean | No | true | Rename implementations |
| renameFile | boolean | No | true | Rename file if type |
| preview | boolean | No | false | Preview mode |

## 2. Extract Operations

### 2.1 extract_method
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| startLine | integer | Yes | Start line |
| startColumn | integer | Yes | Start column |
| endLine | integer | Yes | End line |
| endColumn | integer | Yes | End column |
| methodName | string | Yes | New method name |
| visibility | string | No | Method visibility |
| makeStatic | boolean | No | Force static |
| preview | boolean | No | Preview mode |

### 2.2 extract_interface
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| typeName | string | Yes | Source type |
| interfaceName | string | Yes | New interface name |
| members | string[] | No | Members to include |
| separateFile | boolean | No | Separate file |
| preview | boolean | No | Preview mode |

### 2.3 extract_variable
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| startLine | integer | Yes | Start line |
| startColumn | integer | Yes | Start column |
| endLine | integer | Yes | End line |
| endColumn | integer | Yes | End column |
| variableName | string | Yes | Variable name |
| replaceAll | boolean | No | Replace all |
| preview | boolean | No | Preview mode |

## 3. Generate Operations

### 3.1 generate_constructor
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| typeName | string | Yes | Type name |
| members | string[] | No | Members to init |
| addNullChecks | boolean | No | Add null checks |
| preview | boolean | No | Preview mode |

### 3.2 implement_interface
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| typeName | string | Yes | Type name |
| interfaceName | string | Yes | Interface name |
| explicit | boolean | No | Explicit impl |
| preview | boolean | No | Preview mode |

### 3.3 generate_overrides
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| typeName | string | Yes | Type name |
| members | string[] | No | Members to override |
| callBase | boolean | No | Call base |
| preview | boolean | No | Preview mode |

## 4. Organize Imports

### 4.1 sort_usings
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| systemFirst | boolean | No | System first |
| preview | boolean | No | Preview mode |

### 4.2 remove_unused_usings
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| preview | boolean | No | Preview mode |

### 4.3 add_missing_usings
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| preview | boolean | No | Preview mode |

## 5. Change Signature

### 5.1 add_parameter
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| methodName | string | Yes | Method name |
| parameterName | string | Yes | Param name |
| parameterType | string | Yes | Param type |
| defaultValue | string | No | Default value |
| position | integer | No | Position |
| preview | boolean | No | Preview mode |

### 5.2 remove_parameter
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| methodName | string | Yes | Method name |
| parameterName | string | Yes | Param to remove |
| preview | boolean | No | Preview mode |

## 6. Inline Operations

### 6.1 inline_variable
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| variableName | string | Yes | Variable name |
| line | integer | Yes | Declaration line |
| preview | boolean | No | Preview mode |

### 6.2 inline_method
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| methodName | string | Yes | Method name |
| removeMethod | boolean | No | Remove after |
| preview | boolean | No | Preview mode |

## 7. Encapsulation

### 7.1 encapsulate_field
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| fieldName | string | Yes | Field name |
| propertyName | string | No | Property name |
| preview | boolean | No | Preview mode |

## 8. Convert Operations

### 8.1 convert_to_async
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| methodName | string | Yes | Method name |
| preview | boolean | No | Preview mode |

## 9. Hierarchy Operations

### 9.1 pull_member_up
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| typeName | string | Yes | Type name |
| memberName | string | Yes | Member name |
| targetBaseClass | string | No | Target base |
| makeAbstract | boolean | No | Make abstract |
| preview | boolean | No | Preview mode |

### 9.2 push_member_down
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sourceFile | string | Yes | File path |
| typeName | string | Yes | Type name |
| memberName | string | Yes | Member name |
| preview | boolean | No | Preview mode |
