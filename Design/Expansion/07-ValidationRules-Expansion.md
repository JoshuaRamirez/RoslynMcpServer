# Validation Rules - Expansion

## 1. Input Validation Rules - Rename

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-R01 | newName | Must not be null or empty | 1005 |
| IV-R02 | newName | Must be valid C# identifier | 1009 |
| IV-R03 | newName | Must not be C# keyword (unless @prefixed) | 3011 |
| IV-R04 | newName | Must not equal current name | 3004 |
| IV-R05 | column | If provided, must be >= 1 | 1007 |

## 2. Input Validation Rules - Extract Method

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-E01 | startLine | Must be >= 1 | 1006 |
| IV-E02 | startColumn | Must be >= 1 | 1007 |
| IV-E03 | endLine | Must be >= startLine | 1008 |
| IV-E04 | endColumn | If endLine=startLine, must be > startColumn | 1008 |
| IV-E05 | methodName | Must be valid C# identifier | 1003 |
| IV-E06 | visibility | Must be valid access modifier | 1014 |

## 3. Input Validation Rules - Extract Interface

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-I01 | typeName | Must exist in source file | 2015 |
| IV-I02 | interfaceName | Must start with I by convention | Warning |
| IV-I03 | interfaceName | Must be valid C# identifier | 1003 |
| IV-I04 | members | All members must exist in type | 1012 |

## 4. Input Validation Rules - Generate

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-G01 | typeName | Must exist in source file | 2015 |
| IV-G02 | members | All members must be fields or properties | 1012 |
| IV-G03 | interfaceName | Must exist in workspace | 2009 |

## 5. Input Validation Rules - Change Signature

| Rule ID | Field | Rule | Error Code |
|---------|-------|------|------------|
| IV-S01 | parameterName | Must be valid C# identifier | 1003 |
| IV-S02 | parameterType | Must be valid C# type | 1010 |
| IV-S03 | position | Must be >= 0 and <= param count | 1011 |
| IV-S04 | defaultValue | Must be valid expression for type | 1013 |

## 6. Semantic Validation Rules - Rename

| Rule ID | Rule | Error Code | Stage |
|---------|------|------------|-------|
| SV-R01 | New name must not conflict with symbols in scope | 3010 | Computing |
| SV-R02 | Rename must not hide base class member | 3012 | Computing |
| SV-R03 | Rename must not conflict with type parameters | 3013 | Computing |
| SV-R04 | Constructor names cannot be changed | 3015 | Validating |
| SV-R05 | Destructor names cannot be changed | 3016 | Validating |
| SV-R06 | Operator names cannot be changed | 3017 | Validating |
| SV-R07 | External symbols cannot be renamed | 3018 | Validating |

## 7. Semantic Validation Rules - Extract Method

| Rule ID | Rule | Error Code | Stage |
|---------|------|------------|-------|
| SV-E01 | Selection must form complete statements | 3030 | Resolving |
| SV-E02 | Selection must not contain yield | 3031 | Computing |
| SV-E03 | Selection must have resolvable control flow | 3032 | Computing |
| SV-E04 | Selection must not cross scope boundaries | 3033 | Computing |
| SV-E05 | Selection must have single entry point | 3034 | Computing |
| SV-E06 | Selection must have analyzable exit points | 3035 | Computing |
| SV-E07 | Selection must not capture ref locals | 3039 | Computing |

## 8. Semantic Validation Rules - Extract Interface

| Rule ID | Rule | Error Code | Stage |
|---------|------|------------|-------|
| SV-I01 | Selected members must be public | 3036 | Computing |
| SV-I02 | Interface name must not already exist | 3003 | Computing |

## 9. Semantic Validation Rules - Inline

| Rule ID | Rule | Error Code | Stage |
|---------|------|------------|-------|
| SV-N01 | Method must not be recursive | 3050 | Computing |
| SV-N02 | Method must not be virtual/override/abstract | 3051 | Computing |
| SV-N03 | Variable must be assigned exactly once | 3052 | Computing |
| SV-N04 | Variable must not be modified after init | 3052 | Computing |
| SV-N05 | Expression must be safe to duplicate | 3053 | Computing |

## 10. Semantic Validation Rules - Generate

| Rule ID | Rule | Error Code | Stage |
|---------|------|------------|-------|
| SV-G01 | Constructor with signature must not exist | 3060 | Computing |
| SV-G02 | Interface member must not already exist | 3061 | Computing |
| SV-G03 | Override must not already exist | 3062 | Computing |
| SV-G04 | Base must have overridable members | 3063 | Computing |
| SV-G05 | Type must not be static | 3064 | Computing |

## 11. Semantic Validation Rules - Change Signature

| Rule ID | Rule | Error Code | Stage |
|---------|------|------------|-------|
| SV-S01 | New parameter name must not exist | 3080 | Computing |
| SV-S02 | Removed parameter must not be used | 3081 | Computing |
| SV-S03 | New signature must not match overload | 3083 | Computing |
| SV-S04 | Change must not break interface contract | 3084 | Computing |

## 12. Semantic Validation Rules - Hierarchy

| Rule ID | Rule | Error Code | Stage |
|---------|------|------------|-------|
| SV-H01 | Member must not depend on derived members | 3105 | Computing |
| SV-H02 | Target must not be sealed | 3106 | Computing |
| SV-H03 | Members must have common base | 3107 | Computing |
| SV-H04 | Member must not conflict with derived | 3108 | Computing |

## 13. Validation Execution Order

### All Operations
1. Input Validation (IV-*) - immediate
2. Environment Validation (workspace state)
3. Resource Validation (symbol resolution)
4. Semantic Validation (SV-*) - requires workspace
5. Filesystem Validation (before write)
