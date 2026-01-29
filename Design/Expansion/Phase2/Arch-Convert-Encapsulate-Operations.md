# Architecture Design: Convert & Encapsulate Operations

## Document Overview

| Property | Value |
|----------|-------|
| Document ID | ARCH-CVT-001 |
| Version | 1.0 |
| Status | Draft |
| Author | Software Architect |
| Created | 2026-01-28 |
| BA Reference | BA-Convert-Encapsulate-Operations.md |

---

## 1. Operations Covered

| Operation | Class | Priority | Complexity |
|-----------|-------|----------|------------|
| encapsulate_field | EncapsulateFieldOperation | Tier 2 | Medium |
| convert_to_async | ConvertToAsyncOperation | Tier 3 | High |
| convert_to_expression_body | ConvertToExpressionBodyOperation | Tier 4 | Low |
| convert_to_block_body | ConvertToBlockBodyOperation | Tier 4 | Low |
| convert_foreach_to_linq | ConvertForeachToLinqOperation | Tier 4 | Medium |

---

## 2. File Structure

### 2.1 Parameter Models (src/RoslynMcp.Contracts/Models/)

```
EncapsulateFieldParams.cs
ConvertToAsyncParams.cs
ConvertToExpressionBodyParams.cs
ConvertToBlockBodyParams.cs
ConvertForeachToLinqParams.cs
```

### 2.2 Operation Classes (src/RoslynMcp.Core/Refactoring/Convert/)

```
EncapsulateFieldOperation.cs              (~450 lines)
ConvertToAsyncOperation.cs                (~600 lines)
ConvertToExpressionBodyOperation.cs       (~250 lines)
ConvertToBlockBodyOperation.cs            (~200 lines)
ConvertForeachToLinqOperation.cs          (~400 lines)
```

### 2.3 Utilities (src/RoslynMcp.Core/Refactoring/Convert/)

```
PropertyGenerator.cs              - Property syntax generation
AsyncTransformer.cs               - Async/await transformation
LinqPatternMatcher.cs             - Foreach to LINQ pattern matching
```

---

## 3. Class Designs

### 3.1 EncapsulateFieldOperation

```csharp
namespace RoslynMcp.Core.Refactoring.Convert;

public sealed class EncapsulateFieldOperation : RefactoringOperationBase<EncapsulateFieldParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private PropertyDeclarationSyntax CreateProperty(IFieldSymbol field, string propertyName, string visibility);
    private FieldDeclarationSyntax UpdateFieldVisibility(FieldDeclarationSyntax field, SyntaxKind newVisibility);
    private SyntaxNode UpdateFieldReferences(SyntaxNode root, IFieldSymbol field, string propertyName);
}
```

**Key Algorithms:**
- Property generation with get/set accessors accessing backing field
- Field visibility change (usually to private)
- Reference update: external uses property, internal can use either

### 3.2 ConvertToAsyncOperation

```csharp
public sealed class ConvertToAsyncOperation : RefactoringOperationBase<ConvertToAsyncParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private MethodDeclarationSyntax ConvertMethodToAsync(MethodDeclarationSyntax method, SemanticModel model);
    private IReadOnlyList<ExpressionSyntax> FindAwaitableExpressions(BlockSyntax body, SemanticModel model);
    private ExpressionSyntax WrapWithAwait(ExpressionSyntax expr);
    private TypeSyntax WrapReturnTypeWithTask(TypeSyntax returnType);
}
```

**Key Algorithms:**
- Add async modifier
- Wrap return type with Task<T> or Task
- Add await to awaitable expressions
- Rename method with Async suffix (optional)

### 3.3 ConvertToExpressionBodyOperation

```csharp
public sealed class ConvertToExpressionBodyOperation : RefactoringOperationBase<ConvertToExpressionBodyParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private bool CanConvertToExpressionBody(MethodDeclarationSyntax method);
    private ArrowExpressionClauseSyntax CreateArrowExpression(BlockSyntax body);
}
```

**Conversion Rules:**
- Single return statement → `=> expr;`
- Single expression statement → `=> expr;`
- Property get-only → `=> expr;`

### 3.4 ConvertForeachToLinqOperation

```csharp
public sealed class ConvertForeachToLinqOperation : RefactoringOperationBase<ConvertForeachToLinqParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private LinqPattern AnalyzeForeachPattern(ForEachStatementSyntax foreach);
    private ExpressionSyntax GenerateLinqExpression(LinqPattern pattern, bool useMethodSyntax);
}
```

**Pattern Recognition:**
- Filter: `if (condition) list.Add(item)` → `.Where(x => condition)`
- Map: `list.Add(Transform(item))` → `.Select(x => Transform(x))`
- Filter + Map: Combined Where and Select

---

## 4. Encapsulate Field Algorithm

### 4.1 Property Generation

```csharp
private PropertyDeclarationSyntax CreateProperty(
    IFieldSymbol field,
    string propertyName,
    string visibility)
{
    var fieldType = SyntaxFactory.ParseTypeName(field.Type.ToDisplayString());
    var fieldName = field.Name;

    var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
        .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
            SyntaxFactory.IdentifierName(fieldName)))
        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

    var setter = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
        .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(fieldName),
                SyntaxFactory.IdentifierName("value"))))
        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

    return SyntaxFactory.PropertyDeclaration(fieldType, propertyName)
        .WithModifiers(ParseModifiers(visibility))
        .WithAccessorList(SyntaxFactory.AccessorList(
            SyntaxFactory.List(new[] { getter, setter })));
}
```

### 4.2 Reference Updates

```csharp
private SyntaxNode UpdateFieldReferences(
    SyntaxNode root,
    IFieldSymbol field,
    string propertyName)
{
    var rewriter = new FieldReferenceRewriter(field, propertyName);
    return rewriter.Visit(root);
}

private class FieldReferenceRewriter : CSharpSyntaxRewriter
{
    public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        // Replace field references with property references
        // But keep field access within the property itself
    }
}
```

---

## 5. Async Conversion Algorithm

### 5.1 Method Transformation

```csharp
private MethodDeclarationSyntax ConvertMethodToAsync(
    MethodDeclarationSyntax method,
    SemanticModel model)
{
    var newMethod = method;

    // 1. Add async modifier
    newMethod = newMethod.AddModifiers(
        SyntaxFactory.Token(SyntaxKind.AsyncKeyword));

    // 2. Wrap return type
    var returnType = method.ReturnType;
    if (returnType.ToString() == "void")
    {
        newMethod = newMethod.WithReturnType(
            SyntaxFactory.ParseTypeName("Task"));
    }
    else
    {
        newMethod = newMethod.WithReturnType(
            SyntaxFactory.GenericName("Task")
                .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                    SyntaxFactory.SingletonSeparatedList(returnType))));
    }

    // 3. Add await to awaitable calls
    var body = method.Body;
    var newBody = AddAwaits(body, model);
    newMethod = newMethod.WithBody(newBody);

    // 4. Optionally rename with Async suffix
    if (ShouldAddAsyncSuffix)
    {
        newMethod = newMethod.WithIdentifier(
            SyntaxFactory.Identifier(method.Identifier.Text + "Async"));
    }

    return newMethod;
}
```

### 5.2 Awaitable Detection

```csharp
private bool IsAwaitable(ExpressionSyntax expr, SemanticModel model)
{
    var typeInfo = model.GetTypeInfo(expr);
    var type = typeInfo.Type;

    if (type == null) return false;

    // Check for GetAwaiter method
    var getAwaiter = type.GetMembers("GetAwaiter")
        .OfType<IMethodSymbol>()
        .FirstOrDefault(m => m.Parameters.Length == 0);

    return getAwaiter != null;
}
```

---

## 6. Foreach to LINQ Patterns

### 6.1 Pattern Types

```csharp
public enum LinqPatternKind
{
    Filter,      // if (cond) list.Add(item)
    Map,         // list.Add(transform(item))
    FilterMap,   // if (cond) list.Add(transform(item))
    Any,         // if (cond) return true; return false;
    All,         // if (!cond) return false; return true;
    First,       // if (cond) return item; throw/return default;
    Count,       // if (cond) count++;
    Sum,         // sum += value;
    Aggregate    // result = combine(result, item);
}
```

### 6.2 Pattern Matching

```csharp
private LinqPattern AnalyzeForeachPattern(ForEachStatementSyntax foreach)
{
    var body = foreach.Statement;

    // Single if statement with Add call
    if (body is IfStatementSyntax ifStmt &&
        ifStmt.Statement is ExpressionStatementSyntax exprStmt &&
        exprStmt.Expression is InvocationExpressionSyntax invoc &&
        IsCollectionAdd(invoc))
    {
        var addArg = invoc.ArgumentList.Arguments[0].Expression;
        var isSimpleAdd = addArg is IdentifierNameSyntax id &&
            id.Identifier.Text == foreach.Identifier.Text;

        return new LinqPattern
        {
            Kind = isSimpleAdd ? LinqPatternKind.Filter : LinqPatternKind.FilterMap,
            FilterCondition = ifStmt.Condition,
            MapExpression = isSimpleAdd ? null : addArg
        };
    }

    // Direct Add call (Map pattern)
    if (body is ExpressionStatementSyntax directExpr &&
        directExpr.Expression is InvocationExpressionSyntax directInvoc &&
        IsCollectionAdd(directInvoc))
    {
        return new LinqPattern
        {
            Kind = LinqPatternKind.Map,
            MapExpression = directInvoc.ArgumentList.Arguments[0].Expression
        };
    }

    return null; // Cannot convert
}
```

---

## 7. Error Handling

### 7.1 Error Code Mapping

| Error Code | Constant | Trigger |
|------------|----------|---------|
| 3080 | FIELD_NOT_FOUND | Field doesn't exist |
| 3081 | PROPERTY_NAME_CONFLICTS | Property name already exists |
| 3082 | METHOD_ALREADY_ASYNC | Method is already async |
| 3083 | CANNOT_CONVERT_TO_ASYNC | Method has blocking calls |
| 3084 | NOT_SINGLE_STATEMENT | Can't convert to expression body |
| 3085 | FOREACH_NOT_CONVERTIBLE | Pattern not recognized |
| 3086 | ALREADY_EXPRESSION_BODY | Already expression-bodied |
| 3087 | ALREADY_BLOCK_BODY | Already block-bodied |

---

## 8. Testing Strategy

### 8.1 Test Categories

| Category | Scenarios |
|----------|-----------|
| Encapsulate | Public/private field, readonly, static |
| Async | Void, returning, already async, nested |
| Expression Body | Method, property, lambda |
| LINQ | Filter, map, combined, unsupported |

### 8.2 Test Fixtures

```
tests/RoslynMcp.Core.Tests/Fixtures/Convert/
├── FieldsToEncapsulate.cs
├── SyncMethods.cs
├── BlockBodies.cs
└── ForeachLoops.cs
```

---

## 9. Performance Considerations

| Operation | Expected Time | Notes |
|-----------|---------------|-------|
| encapsulate_field | <300ms | Reference updates |
| convert_to_async | <500ms | Call graph analysis |
| convert_to_expression_body | <50ms | Syntax transformation |
| convert_to_block_body | <50ms | Syntax transformation |
| convert_foreach_to_linq | <200ms | Pattern matching |

---

## 10. Build Sequence

**Phase 1: Simple Conversions**
- ConvertToExpressionBodyOperation
- ConvertToBlockBodyOperation

**Phase 2: Medium Complexity**
- EncapsulateFieldOperation
- ConvertForeachToLinqOperation

**Phase 3: High Complexity**
- ConvertToAsyncOperation

**Phase 4: Integration**
- Cross-operation testing
- Preview mode validation

---

## Revision History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-01-28 | Architect | Initial design |
