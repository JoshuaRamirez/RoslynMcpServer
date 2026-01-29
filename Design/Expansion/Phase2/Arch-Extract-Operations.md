# Architecture Design: Extract Operations

## Document Overview

| Property | Value |
|----------|-------|
| Document ID | ARCH-EXT-001 |
| Version | 1.0 |
| Status | Draft |
| Author | Software Architect |
| Created | 2026-01-28 |
| BA Reference | BA-Extract-Operations.md |

---

## 1. Operations Covered

| Operation | Class | Priority | Complexity |
|-----------|-------|----------|------------|
| extract_interface | ExtractInterfaceOperation | Tier 1 | Medium |
| extract_base_class | ExtractBaseClassOperation | Tier 3 | High |
| extract_variable | ExtractVariableOperation | Tier 2 | Low |
| extract_constant | ExtractConstantOperation | Tier 2 | Low |

---

## 2. File Structure

### 2.1 Parameter Models (src/RoslynMcp.Contracts/Models/)

```
ExtractInterfaceParams.cs
ExtractBaseClassParams.cs
ExtractVariableParams.cs
ExtractConstantParams.cs
```

### 2.2 Operation Classes (src/RoslynMcp.Core/Refactoring/Extract/)

```
ExtractInterfaceOperation.cs    (~600 lines)
ExtractBaseClassOperation.cs    (~800 lines)
ExtractVariableOperation.cs     (~400 lines)
ExtractConstantOperation.cs     (~500 lines)
```

### 2.3 Utilities (src/RoslynMcp.Core/Refactoring/Extract/)

```
MemberSignatureBuilder.cs       - Interface/base class signature generation
ExpressionAnalyzer.cs          - Constant detection, equivalence checking
InsertionPointFinder.cs        - Optimal insertion points in types
```

---

## 3. Class Designs

### 3.1 ExtractInterfaceOperation

```csharp
namespace RoslynMcp.Core.Refactoring.Extract;

public sealed class ExtractInterfaceOperation : RefactoringOperationBase<ExtractInterfaceParams>
{
    public ExtractInterfaceOperation(WorkspaceContext context) : base(context) { }

    protected override void ValidateParams(ExtractInterfaceParams @params);
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private IReadOnlyList<ISymbol> SelectExtractableMembers(INamedTypeSymbol type, string[]? memberNames);
    private InterfaceDeclarationSyntax GenerateInterface(string name, IReadOnlyList<ISymbol> members);
    private ClassDeclarationSyntax AddInterfaceImplementation(ClassDeclarationSyntax classDecl, string interfaceName);
}
```

**Key Algorithms:**
- Member filtering: Public instance methods, properties, events only
- Signature generation via `SyntaxGenerator.InterfaceDeclaration()`
- Type parameter copying with variance (in/out) and constraints

### 3.2 ExtractBaseClassOperation

```csharp
public sealed class ExtractBaseClassOperation : RefactoringOperationBase<ExtractBaseClassParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private IReadOnlyList<ISymbol> AnalyzeMemberDependencies(INamedTypeSymbol type, string[] members);
    private ClassDeclarationSyntax GenerateBaseClass(string name, IReadOnlyList<ISymbol> members, bool isAbstract);
    private ClassDeclarationSyntax UpdateDerivedClass(ClassDeclarationSyntax derived, string baseName, IReadOnlyList<ISymbol> movedMembers);
}
```

**Key Algorithms:**
- Dependency analysis: Find fields/properties referenced by moved methods
- Accessibility adjustment: Private → Protected for derived access
- Base type list modification via `SyntaxFactory.SimpleBaseType()`

### 3.3 ExtractVariableOperation

```csharp
public sealed class ExtractVariableOperation : RefactoringOperationBase<ExtractVariableParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private ExpressionSyntax FindExpression(SyntaxNode root, TextSpan span);
    private IReadOnlyList<ExpressionSyntax> FindEquivalentExpressions(SyntaxNode scope, ExpressionSyntax target);
    private StatementSyntax CreateVariableDeclaration(string name, ExpressionSyntax expr, bool useVar);
}
```

**Key Algorithms:**
- Expression location via `FindNode()` with `getInnermostNodeForTie: true`
- Equivalence checking: Syntax structure + semantic symbol comparison
- Insertion point: Statement containing expression, in parent block

### 3.4 ExtractConstantOperation

```csharp
public sealed class ExtractConstantOperation : RefactoringOperationBase<ExtractConstantParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private LiteralExpressionSyntax FindLiteral(SyntaxNode root, TextSpan span);
    private bool IsCompileTimeConstant(LiteralExpressionSyntax literal, SemanticModel model);
    private FieldDeclarationSyntax CreateConstantField(string name, LiteralExpressionSyntax literal, string visibility);
}
```

**Key Algorithms:**
- Literal validation via `SemanticModel.GetConstantValue()`
- Modifier determination: `const` for primitives, `static readonly` for reference types
- Placement: Before first member or in constants region

---

## 4. Parameter Models

### 4.1 ExtractInterfaceParams

```csharp
public record ExtractInterfaceParams
{
    public required string SourceFile { get; init; }
    public required string TypeName { get; init; }
    public required string InterfaceName { get; init; }
    public string[]? Members { get; init; }  // null = all public members
    public bool SeparateFile { get; init; } = true;
    public string? TargetDirectory { get; init; }
    public bool Preview { get; init; } = false;
}
```

### 4.2 ExtractBaseClassParams

```csharp
public record ExtractBaseClassParams
{
    public required string SourceFile { get; init; }
    public required string TypeName { get; init; }
    public required string BaseClassName { get; init; }
    public required string[] Members { get; init; }
    public bool IsAbstract { get; init; } = false;
    public bool SeparateFile { get; init; } = true;
    public string? TargetDirectory { get; init; }
    public bool Preview { get; init; } = false;
}
```

### 4.3 ExtractVariableParams

```csharp
public record ExtractVariableParams
{
    public required string SourceFile { get; init; }
    public required int StartLine { get; init; }
    public required int StartColumn { get; init; }
    public required int EndLine { get; init; }
    public required int EndColumn { get; init; }
    public required string VariableName { get; init; }
    public bool ReplaceAll { get; init; } = false;
    public bool UseVar { get; init; } = true;
    public bool Preview { get; init; } = false;
}
```

### 4.4 ExtractConstantParams

```csharp
public record ExtractConstantParams
{
    public required string SourceFile { get; init; }
    public required int StartLine { get; init; }
    public required int StartColumn { get; init; }
    public required int EndLine { get; init; }
    public required int EndColumn { get; init; }
    public required string ConstantName { get; init; }
    public string Visibility { get; init; } = "private";
    public bool ReplaceAll { get; init; } = false;
    public bool Preview { get; init; } = false;
}
```

---

## 5. Dependencies

### 5.1 Internal Dependencies

| Component | Usage |
|-----------|-------|
| RefactoringOperationBase<T> | Base class, validation, commit |
| WorkspaceContext | Solution access, document retrieval |
| TypeSymbolResolver | Type/member resolution |
| ReferenceTracker | Find usages for reference updates |
| PathResolver | Path validation |
| AtomicFileWriter | Atomic file operations |

### 5.2 Roslyn APIs

| API | Usage |
|-----|-------|
| SyntaxGenerator | Platform-agnostic syntax generation |
| DocumentEditor | Batch document modifications |
| SemanticModel | Symbol resolution, type info |
| FindReferences | Cross-file reference updates |

---

## 6. Roslyn API Usage

### 6.1 Member Signature Generation

```csharp
var generator = SyntaxGenerator.GetGenerator(document);

// Interface method signature (no body)
var methodSignature = generator.MethodDeclaration(
    method.Name,
    parameters: method.Parameters.Select(p => generator.ParameterDeclaration(p)),
    returnType: generator.TypeExpression(method.ReturnType),
    accessibility: Accessibility.Public);

// Property signature
var propertySignature = generator.PropertyDeclaration(
    property.Name,
    generator.TypeExpression(property.Type),
    accessibility: Accessibility.Public,
    getAccessorStatements: null,  // no body = interface
    setAccessorStatements: property.SetMethod != null ? null : Array.Empty<SyntaxNode>());
```

### 6.2 Type Parameter Handling

```csharp
static TypeParameterListSyntax CopyTypeParameters(INamedTypeSymbol typeSymbol)
{
    var typeParams = typeSymbol.TypeParameters.Select(tp =>
    {
        var param = SyntaxFactory.TypeParameter(tp.Name);

        if (tp.Variance == VarianceKind.In)
            param = param.WithVarianceKeyword(SyntaxFactory.Token(SyntaxKind.InKeyword));
        else if (tp.Variance == VarianceKind.Out)
            param = param.WithVarianceKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword));

        return param;
    });

    return SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(typeParams));
}
```

### 6.3 Expression Equivalence

```csharp
private bool AreEquivalentExpressions(
    ExpressionSyntax a, ExpressionSyntax b,
    SemanticModel model)
{
    // Structural comparison
    if (!SyntaxFactory.AreEquivalent(a, b))
        return false;

    // Semantic comparison (same symbols referenced)
    var symbolA = model.GetSymbolInfo(a).Symbol;
    var symbolB = model.GetSymbolInfo(b).Symbol;

    return SymbolEqualityComparer.Default.Equals(symbolA, symbolB);
}
```

---

## 7. Error Handling

### 7.1 Error Code Mapping

| Error Code | Constant | Trigger |
|------------|----------|---------|
| 3040 | NO_EXTRACTABLE_MEMBERS | No public members to extract |
| 3041 | MEMBER_NOT_PUBLIC | Selected member is not public |
| 3042 | STATIC_MEMBER_NOT_EXTRACTABLE | Static members in interface |
| 3043 | TYPE_NOT_CLASS | extract_base_class on non-class |
| 3044 | TYPE_IS_SEALED | Sealed class cannot have base extracted |
| 3045 | HAS_EXISTING_BASE | Class already has base class |
| 3046 | MEMBER_DEPENDENCY_CONFLICT | Member depends on non-movable member |
| 3047 | VOID_EXPRESSION | Extract void expression |
| 3048 | NOT_LITERAL_VALUE | Selection is not literal |
| 3049 | NO_CONTAINING_TYPE | Code not inside type declaration |

### 7.2 Validation Flow

```
1. Input validation (throws 1xxx codes)
   └─ Path validation, identifier validation, range validation

2. Document resolution (throws 2xxx codes)
   └─ File exists, in workspace, parseable

3. Semantic validation (throws 3xxx codes)
   └─ Type exists, members valid, no conflicts

4. Transformation (throws 4xxx codes)
   └─ Changes compile, no breaking changes
```

---

## 8. Testing Strategy

### 8.1 Test Categories

| Category | Focus |
|----------|-------|
| Unit | Individual operation methods |
| Integration | Full operation with real workspace |
| Edge | Generic types, nested types, partial classes |
| Error | All error code paths |

### 8.2 Fixture Structure

```
tests/RoslynMcp.Core.Tests/Fixtures/Extract/
├── ExtractInterface/
│   ├── SimpleClass.cs
│   ├── GenericClass.cs
│   └── WithConstraints.cs
├── ExtractBaseClass/
│   ├── SimpleClass.cs
│   └── WithDependencies.cs
├── ExtractVariable/
│   └── Expressions.cs
└── ExtractConstant/
    └── Literals.cs
```

### 8.3 Test Naming Convention

```csharp
[Fact]
public void ExtractInterface_WithPublicMembers_CreatesInterfaceAndImplements() { }

[Fact]
public void ExtractVariable_ReplaceAll_ReplacesAllOccurrences() { }

[Fact]
public void ExtractConstant_WithSideEffects_ThrowsExpressionHasSideEffects() { }
```

---

## 9. Performance Considerations

| Operation | Expected Time | Bottleneck |
|-----------|---------------|------------|
| extract_interface | <100ms | Member filtering O(n) |
| extract_base_class | <500ms | Dependency analysis O(n*m) |
| extract_variable | <50ms | Expression finding O(n) |
| extract_constant | <200ms | Literal finding in large files |

**Optimization Opportunities:**
- Cache `SemanticModel` across operations in same session
- Use `SyntaxWalker` for targeted node collection
- Limit search scope for literals (containing type, not entire solution)

---

## 10. Build Sequence

**Phase 1: Foundation**
- Parameter models (4 files)
- Utility classes (3 files)

**Phase 2: Simple Operations**
- ExtractVariableOperation
- ExtractConstantOperation

**Phase 3: Complex Operations**
- ExtractInterfaceOperation
- ExtractBaseClassOperation

**Phase 4: Integration**
- Integration tests
- Preview mode refinement
- Performance validation

---

## Revision History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-01-28 | Architect | Initial design |
