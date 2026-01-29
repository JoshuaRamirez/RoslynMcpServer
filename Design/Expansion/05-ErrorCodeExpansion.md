# Error Code Expansion - Roslyn MCP Server

## 1. Error Code Taxonomy

The existing error code structure (1xxx-5xxx) is extended to support all new refactoring operations.

## 2. Input Validation Errors (1xxx) - Extended

### Existing Codes (1001-1006)
| Code | Constant | Message |
|------|----------|---------|
| 1001 | INVALID_SOURCE_PATH | Source file path is malformed |
| 1002 | INVALID_TARGET_PATH | Target file path is malformed |
| 1003 | INVALID_SYMBOL_NAME | Symbol name contains invalid characters |
| 1004 | INVALID_NAMESPACE | Namespace format is invalid |
| 1005 | MISSING_REQUIRED_PARAM | Required parameter not provided |
| 1006 | INVALID_LINE_NUMBER | Line number out of valid range |

### New Codes (1007-1016)
| Code | Constant | Message |
|------|----------|---------|
| 1007 | INVALID_COLUMN_NUMBER | Column number out of valid range |
| 1008 | INVALID_SELECTION_RANGE | Selection start must be before end |
| 1009 | INVALID_NEW_NAME | New name is invalid for this symbol type |
| 1010 | INVALID_PARAMETER_TYPE | Parameter type is not valid C# type |
| 1011 | INVALID_PARAMETER_POSITION | Parameter position out of range |
| 1012 | INVALID_MEMBER_LIST | Member list contains invalid member names |
| 1013 | INVALID_DEFAULT_VALUE | Default value does not match parameter type |
| 1014 | INVALID_VISIBILITY | Visibility modifier is not valid |
| 1015 | INVALID_RETURN_TYPE | Return type is not valid C# type |
| 1016 | EMPTY_SELECTION | Selection range is empty |

## 3. Resource Errors (2xxx) - Extended

### Existing Codes (2001-2005)
| Code | Constant | Message |
|------|----------|---------|
| 2001 | SOURCE_FILE_NOT_FOUND | Source file does not exist |
| 2002 | SOURCE_NOT_IN_WORKSPACE | Source file not part of loaded solution |
| 2003 | SYMBOL_NOT_FOUND | No symbol found at specified location |
| 2004 | SYMBOL_AMBIGUOUS | Multiple symbols match |
| 2005 | WORKSPACE_NOT_LOADED | No solution currently loaded |

### New Codes (2006-2018)
| Code | Constant | Message |
|------|----------|---------|
| 2006 | METHOD_NOT_FOUND | No method found at specified location |
| 2007 | VARIABLE_NOT_FOUND | No variable found at specified location |
| 2008 | FIELD_NOT_FOUND | No field found at specified location |
| 2009 | INTERFACE_NOT_FOUND | Interface not found in workspace |
| 2010 | BASE_CLASS_NOT_FOUND | Base class not found |
| 2011 | DERIVED_CLASSES_NOT_FOUND | No derived classes found |
| 2012 | MEMBER_NOT_FOUND | Member not found in type |
| 2013 | EXPRESSION_NOT_FOUND | No expression found at selection |
| 2014 | STATEMENT_NOT_FOUND | No statement found at selection |
| 2015 | TYPE_NOT_FOUND | Type not found at specified location |
| 2016 | PARAMETER_NOT_FOUND | Parameter not found |
| 2017 | CONSTRUCTOR_NOT_FOUND | No constructor found |
| 2018 | OVERRIDE_TARGET_NOT_FOUND | No overridable member found |

## 4. Semantic Errors (3xxx) - Extended

### Existing Codes (3001-3006)
| Code | Constant | Message |
|------|----------|---------|
| 3001 | SYMBOL_NOT_MOVEABLE | Symbol type cannot be moved |
| 3002 | SYMBOL_IS_NESTED | Nested types cannot be moved independently |
| 3003 | NAME_COLLISION | Target location already has type with same name |
| 3004 | SAME_LOCATION | Source and target are the same |
| 3005 | CIRCULAR_REFERENCE | Move would create circular dependency |
| 3006 | BREAKS_ACCESSIBILITY | Move would break accessibility constraints |

### Rename Errors (3010-3018)
| Code | Constant | Message |
|------|----------|---------|
| 3010 | NAME_CONFLICT_SCOPE | New name conflicts with existing symbol in scope |
| 3011 | RESERVED_KEYWORD | New name is a C# reserved keyword |
| 3012 | HIDES_BASE_MEMBER | New name hides inherited member |
| 3013 | CONFLICTS_WITH_TYPE_PARAMETER | New name conflicts with type parameter |
| 3014 | RENAME_WOULD_BREAK_OVERLOAD | Rename would create ambiguous overloads |
| 3015 | CANNOT_RENAME_CONSTRUCTOR | Constructors cannot be renamed directly |
| 3016 | CANNOT_RENAME_DESTRUCTOR | Destructors cannot be renamed directly |
| 3017 | CANNOT_RENAME_OPERATOR | Operators cannot be renamed |
| 3018 | CANNOT_RENAME_EXTERNAL | Cannot rename symbols from external assemblies |

### Extract Errors (3030-3039)
| Code | Constant | Message |
|------|----------|---------|
| 3030 | INVALID_SELECTION | Selection does not form valid extractable code |
| 3031 | CONTAINS_YIELD | Selection contains yield statements |
| 3032 | UNRESOLVABLE_CONTROL_FLOW | Selection has unresolvable control flow |
| 3033 | SELECTION_CROSSES_SCOPES | Selection crosses incompatible scopes |
| 3034 | MULTIPLE_ENTRY_POINTS | Selection has multiple entry points |
| 3035 | MULTIPLE_EXIT_POINTS | Selection has multiple exit points |
| 3036 | NO_EXTRACTABLE_MEMBERS | Type has no extractable members |
| 3037 | NOT_COMPILE_TIME_CONSTANT | Expression is not a compile-time constant |
| 3038 | EXPRESSION_HAS_SIDE_EFFECTS | Expression has side effects |
| 3039 | CAPTURES_REF_LOCAL | Selection captures ref local |

### Inline Errors (3050-3054)
| Code | Constant | Message |
|------|----------|---------|
| 3050 | METHOD_IS_RECURSIVE | Recursive methods cannot be inlined |
| 3051 | METHOD_IS_VIRTUAL | Virtual/override methods cannot be inlined |
| 3052 | VARIABLE_MODIFIED_AFTER_INIT | Variable is modified after initialization |
| 3053 | VARIABLE_USED_MULTIPLE_TIMES | Variable with side-effects used multiple times |
| 3054 | METHOD_TOO_COMPLEX | Method body is too complex to inline |

### Generate Errors (3060-3065)
| Code | Constant | Message |
|------|----------|---------|
| 3060 | CONSTRUCTOR_EXISTS | Constructor with same signature exists |
| 3061 | MEMBER_ALREADY_IMPLEMENTED | Interface member already implemented |
| 3062 | OVERRIDE_ALREADY_EXISTS | Override already exists |
| 3063 | NO_OVERRIDABLE_MEMBERS | No overridable members |
| 3064 | TYPE_IS_STATIC | Cannot add constructor to static class |
| 3065 | INTERFACE_MEMBER_CONFLICT | Interface member conflicts |

### Signature Errors (3080-3084)
| Code | Constant | Message |
|------|----------|---------|
| 3080 | PARAMETER_ALREADY_EXISTS | Parameter with this name exists |
| 3081 | PARAMETER_REQUIRED_BY_CALLERS | Parameter is used by callers |
| 3082 | RETURN_TYPE_INCOMPATIBLE | New return type incompatible |
| 3083 | SIGNATURE_MATCHES_OVERLOAD | New signature matches overload |
| 3084 | BREAKS_INTERFACE_CONTRACT | Breaks interface contract |

### Hierarchy Errors (3105-3108)
| Code | Constant | Message |
|------|----------|---------|
| 3105 | MEMBER_DEPENDS_ON_DERIVED | Member depends on derived members |
| 3106 | BASE_CLASS_IS_SEALED | Base class is sealed |
| 3107 | NO_COMMON_BASE | No common base class |
| 3108 | CONFLICTS_WITH_DERIVED | Conflicts with derived member |

## 5. System Errors (4xxx) - Unchanged

| Code | Constant | Message |
|------|----------|---------|
| 4001 | WORKSPACE_BUSY | Another operation in progress |
| 4002 | FILESYSTEM_ERROR | Cannot read/write files |
| 4003 | ROSLYN_ERROR | Roslyn operation failed |
| 4004 | COMPILATION_ERROR | Would break compilation |
| 4005 | TIMEOUT | Operation exceeded time limit |

## 6. Environment Errors (5xxx) - Unchanged

| Code | Constant | Message |
|------|----------|---------|
| 5001 | MSBUILD_NOT_FOUND | MSBuild not available |
| 5002 | ROSLYN_NOT_AVAILABLE | Roslyn assemblies not loadable |
| 5003 | SDK_NOT_FOUND | .NET SDK not found |
| 5004 | SOLUTION_LOAD_FAILED | Could not load solution |
