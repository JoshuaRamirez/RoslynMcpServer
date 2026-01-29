# Architecture Design: Inline Operations

## Document Overview

| Property | Value |
|----------|-------|
| Document ID | ARCH-INL-001 |
| Version | 1.0 |
| Status | Draft |
| Author | Software Architect |
| Created | 2026-01-28 |
| BA Reference | BA-Inline-Operations.md |

---

## 1. Operations Covered

| Operation | Class | Priority | Complexity |
|-----------|-------|----------|------------|
| inline_method | InlineMethodOperation | Tier 4 | High |
| inline_variable | InlineVariableOperation | Tier 2 | Low |
| inline_constant | InlineConstantOperation | Tier 4 | Low |

---

## 2. File Structure

### 2.1 Parameter Models (src/RoslynMcp.Contracts/Models/)

```
InlineMethodParams.cs
InlineVariableParams.cs
InlineConstantParams.cs
```

### 2.2 Operation Classes (src/RoslynMcp.Core/Refactoring/Inline/)

```
InlineMethodOperation.cs      (~700 lines)
InlineVariableOperation.cs    (~350 lines)
InlineConstantOperation.cs    (~400 lines)
```

### 2.3 Utilities (src/RoslynMcp.Core/Refactoring/Inline/)

```
MethodInliner.cs              - Method body transformation
VariableSubstituter.cs        - Expression substitution with precedence
LiteralFormatter.cs           - Constant to literal conversion
```

---

## 3. Class Designs

### 3.1 InlineMethodOperation

```csharp
namespace RoslynMcp.Core.Refactoring.Inline;

public sealed class InlineMethodOperation : RefactoringOperationBase<InlineMethodParams>
{
    protected override void ValidateParams(InlineMethodParams @params);
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private bool IsInlineable(IMethodSymbol method);
    private IReadOnlyList<InvocationExpressionSyntax> FindCallSites(IMethodSymbol method);
    private StatementSyntax[] TransformMethodBody(BlockSyntax body, ArgumentListSyntax args, IMethodSymbol method);
    private ExpressionSyntax TransformExpressionBody(ArrowExpressionClauseSyntax arrow, ArgumentListSyntax args);
}
```

**Inlineability Checks:**
- Not recursive (no self-calls)
- Not virtual/override/abstract
- Has body (not extern/partial)
- Not interface implementation

**Key Algorithms:**
- Parameter substitution: Map arguments to parameters, handle ref/out
- Local variable renaming: Avoid scope conflicts at call site
- Return statement handling: Convert to expression or goto pattern

### 3.2 InlineVariableOperation

```csharp
public sealed class InlineVariableOperation : RefactoringOperationBase<InlineVariableParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private ILocalSymbol FindVariable(SyntaxNode root, int line, int column);
    private bool IsSafeToInline(ILocalSymbol variable, SemanticModel model);
    private IReadOnlyList<IdentifierNameSyntax> FindReferences(ILocalSymbol variable, SyntaxNode scope);
    private ExpressionSyntax ParenthesizeIfNeeded(ExpressionSyntax expr, SyntaxNode parent);
}
```

**Safety Checks:**
- Single assignment (initialized at declaration)
- Not modified after initialization
- Side-effect expression used only once OR pure expression

**Key Algorithms:**
- Data flow analysis via `SemanticModel.AnalyzeDataFlow()`
- Precedence-aware parenthesization
- Declaration removal

### 3.3 InlineConstantOperation

```csharp
public sealed class InlineConstantOperation : RefactoringOperationBase<InlineConstantParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private IFieldSymbol FindConstant(SyntaxNode root, string name, string? typeName);
    private LiteralExpressionSyntax GetLiteralRepresentation(IFieldSymbol constant);
    private bool IsUsedInAttribute(IFieldSymbol constant, Solution solution);
}
```

**Key Algorithms:**
- Constant value extraction via `IFieldSymbol.ConstantValue`
- Literal formatting with correct suffixes (L, F, M, etc.)
- Cross-project reference finding

---

## 4. Parameter Models

### 4.1 InlineMethodParams

```csharp
public record InlineMethodParams
{
    public required string SourceFile { get; init; }
    public required string MethodName { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public CallSiteLocation? CallSiteLocation { get; init; }
    public bool RemoveMethod { get; init; } = true;
    public bool Preview { get; init; } = false;
}

public record CallSiteLocation
{
    public required string File { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
}
```

### 4.2 InlineVariableParams

```csharp
public record InlineVariableParams
{
    public required string SourceFile { get; init; }
    public required string VariableName { get; init; }
    public required int Line { get; init; }
    public int? Column { get; init; }
    public bool Preview { get; init; } = false;
}
```

### 4.3 InlineConstantParams

```csharp
public record InlineConstantParams
{
    public required string SourceFile { get; init; }
    public required string ConstantName { get; init; }
    public string? TypeName { get; init; }
    public bool RemoveConstant { get; init; } = true;
    public bool Preview { get; init; } = false;
}
```

---

## 5. Method Inlining Algorithm

### 5.1 Statement Body Transformation

```csharp
private StatementSyntax[] TransformMethodBody(
    BlockSyntax body,
    ArgumentListSyntax args,
    IMethodSymbol method)
{
    var statements = body.Statements.ToList();

    // 1. Substitute parameters with arguments
    var paramMap = BuildParameterMap(method.Parameters, args);
    statements = SubstituteParameters(statements, paramMap);

    // 2. Rename locals to avoid conflicts
    var localRenames = ComputeLocalRenames(statements, callSiteScope);
    statements = RenameLocals(statements, localRenames);

    // 3. Handle return statements
    if (method.ReturnsVoid)
    {
        // Remove return statements
        statements = RemoveReturnStatements(statements);
    }
    else
    {
        // Single return: convert to expression
        // Multiple returns: use result variable pattern
        statements = TransformReturns(statements, method.ReturnType);
    }

    return statements.ToArray();
}
```

### 5.2 Argument Handling for Side Effects

```csharp
private ArgumentInfo AnalyzeArgument(ArgumentSyntax arg, SemanticModel model)
{
    var expr = arg.Expression;

    return new ArgumentInfo
    {
        HasSideEffects = HasSideEffects(expr, model),
        UsedMultipleTimes = CountParameterUsages(paramSymbol) > 1,
        NeedsTemporary = HasSideEffects && UsedMultipleTimes
    };
}

// If argument has side effects and used multiple times,
// introduce temporary: var __temp = arg; then substitute __temp
```

---

## 6. Variable Inlining Algorithm

### 6.1 Safety Analysis

```csharp
private bool IsSafeToInline(ILocalSymbol variable, SemanticModel model)
{
    var dataFlow = model.AnalyzeDataFlow(containingStatement);

    // Must be definitely assigned at declaration
    if (!variable.HasExplicitDefaultValue && !HasInitializer(variable))
        return false;

    // Must not be written after initialization
    if (dataFlow.WrittenInside.Contains(variable))
        return false;

    // Side-effect expression can only be used once
    if (HasSideEffects(initializer) && CountUsages(variable) > 1)
        return false;

    return true;
}
```

### 6.2 Parenthesization Rules

```csharp
private ExpressionSyntax ParenthesizeIfNeeded(ExpressionSyntax expr, SyntaxNode parent)
{
    // Don't parenthesize identifiers, literals, or already parenthesized
    if (expr is IdentifierNameSyntax or LiteralExpressionSyntax or ParenthesizedExpressionSyntax)
        return expr;

    // Check if parent context requires parentheses
    if (parent is BinaryExpressionSyntax binary)
    {
        var exprPrecedence = GetPrecedence(expr);
        var parentPrecedence = GetPrecedence(binary);

        if (exprPrecedence < parentPrecedence)
            return SyntaxFactory.ParenthesizedExpression(expr);
    }

    // Member access, indexer, invocation don't need parens
    if (parent is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax or InvocationExpressionSyntax)
        return SyntaxFactory.ParenthesizedExpression(expr);

    return expr;
}
```

---

## 7. Constant Inlining Algorithm

### 7.1 Literal Representation

```csharp
private LiteralExpressionSyntax GetLiteralRepresentation(IFieldSymbol constant)
{
    var value = constant.ConstantValue;
    var type = constant.Type;

    return type.SpecialType switch
    {
        SpecialType.System_Int32 => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal((int)value)),

        SpecialType.System_Int64 => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal((long)value)),  // Adds L suffix

        SpecialType.System_Single => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal((float)value)),  // Adds F suffix

        SpecialType.System_Double => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal((double)value)),

        SpecialType.System_Decimal => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal((decimal)value)),  // Adds M suffix

        SpecialType.System_String => value == null
            ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
            : SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal((string)value)),

        SpecialType.System_Boolean => SyntaxFactory.LiteralExpression(
            (bool)value ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),

        SpecialType.System_Char => SyntaxFactory.LiteralExpression(
            SyntaxKind.CharacterLiteralExpression,
            SyntaxFactory.Literal((char)value)),

        _ => throw new RefactoringException(ErrorCodes.NotCompileTimeConstant,
            $"Cannot inline constant of type {type}")
    };
}
```

---

## 8. Error Handling

### 8.1 Error Code Mapping

| Error Code | Constant | Trigger |
|------------|----------|---------|
| 3050 | METHOD_IS_RECURSIVE | Method calls itself |
| 3051 | METHOD_IS_VIRTUAL | Virtual/override/abstract method |
| 3052 | VARIABLE_MODIFIED_AFTER_INIT | Variable written after declaration |
| 3053 | EXPRESSION_HAS_SIDE_EFFECTS | Side-effect expr used multiple times |
| 3054 | METHOD_TOO_COMPLEX | Method exceeds complexity threshold |
| 3055 | NO_CALL_SITES_FOUND | No invocations found |
| 3055 | NOT_A_CONSTANT | Field is not const/static readonly |
| 3056 | CALL_SITE_NOT_EDITABLE | Call site in read-only location |
| 3056 | PUBLIC_API_CONSTANT | Removing public constant |
| 3057 | VARIABLE_NOT_INITIALIZED | Variable has no initializer |
| 3058 | UNSAFE_CLOSURE_CAPTURE | Variable captured in closure |
| 3059 | CONSTANT_IN_ATTRIBUTE | Constant used in attribute |

---

## 9. Testing Strategy

### 9.1 InlineMethod Test Scenarios

| Scenario | Key Verification |
|----------|------------------|
| Simple void method | Statements inserted, method removed |
| Method with return | Return value captured/used correctly |
| Expression-bodied | Arrow converted to expression |
| ref/out parameters | Temporary variables created |
| Generic method | Type arguments substituted |
| Async method | await preserved correctly |

### 9.2 InlineVariable Test Scenarios

| Scenario | Key Verification |
|----------|------------------|
| Single use | Declaration removed, reference replaced |
| Multiple uses, pure | All references replaced |
| Side-effect, single use | Safe to inline |
| Side-effect, multi-use | Error 3053 |
| Needs parentheses | Correct precedence handling |

### 9.3 InlineConstant Test Scenarios

| Scenario | Key Verification |
|----------|------------------|
| int const | Literal without suffix |
| long const | Literal with L suffix |
| string const | Escaped string literal |
| null const | null keyword |
| Cross-project | All projects updated |
| In attribute | Error 3059 |

---

## 10. Performance Considerations

| Operation | Expected Time | Notes |
|-----------|---------------|-------|
| inline_method | <500ms | Depends on call site count |
| inline_variable | <50ms | Single file operation |
| inline_constant | <300ms | Cross-project reference finding |

**Complexity Thresholds for inline_method:**
- Statement count: ≤20
- Cyclomatic complexity: ≤10
- Local variable count: ≤10
- Nesting depth: ≤4

---

## 11. Build Sequence

**Phase 1: Foundation**
- Parameter models (3 files)
- Utility classes (3 files)

**Phase 2: Simple Operations**
- InlineVariableOperation
- InlineConstantOperation

**Phase 3: Complex Operation**
- InlineMethodOperation

**Phase 4: Integration**
- Cross-file testing
- Preview mode validation
- Performance profiling

---

## Revision History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-01-28 | Architect | Initial design |
