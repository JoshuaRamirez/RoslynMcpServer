# Architecture Design: Hierarchy & Additional Operations

## Document Overview

| Property | Value |
|----------|-------|
| Document ID | ARCH-HIR-001 |
| Version | 1.0 |
| Status | Draft |
| Author | Software Architect |
| Created | 2026-01-28 |
| BA Reference | BA-Hierarchy-Additional-Operations.md |

---

## 1. Operations Covered

### 1.1 Hierarchy Operations

| Operation | Class | Priority | Complexity |
|-----------|-------|----------|------------|
| pull_members_up | PullMembersUpOperation | Tier 3 | High |
| push_members_down | PushMembersDownOperation | Tier 3 | High |
| use_base_type | UseBaseTypeOperation | Tier 4 | Medium |

### 1.2 Additional Operations

| Operation | Class | Priority | Complexity |
|-----------|-------|----------|------------|
| introduce_parameter | IntroduceParameterOperation | Tier 3 | Medium |
| introduce_field | IntroduceFieldOperation | Tier 3 | Low |
| make_static | MakeStaticOperation | Tier 4 | Medium |
| make_non_static | MakeNonStaticOperation | Tier 4 | Medium |
| safe_delete | SafeDeleteOperation | Tier 4 | Medium |

---

## 2. File Structure

### 2.1 Parameter Models (src/RoslynMcp.Contracts/Models/)

```
PullMembersUpParams.cs
PushMembersDownParams.cs
UseBaseTypeParams.cs
IntroduceParameterParams.cs
IntroduceFieldParams.cs
MakeStaticParams.cs
MakeNonStaticParams.cs
SafeDeleteParams.cs
```

### 2.2 Operation Classes

**Hierarchy (src/RoslynMcp.Core/Refactoring/Hierarchy/):**
```
PullMembersUpOperation.cs         (~600 lines)
PushMembersDownOperation.cs       (~550 lines)
UseBaseTypeOperation.cs           (~350 lines)
```

**Additional (src/RoslynMcp.Core/Refactoring/Additional/):**
```
IntroduceParameterOperation.cs    (~400 lines)
IntroduceFieldOperation.cs        (~300 lines)
MakeStaticOperation.cs            (~350 lines)
MakeNonStaticOperation.cs         (~350 lines)
SafeDeleteOperation.cs            (~450 lines)
```

### 2.3 Utilities

```
HierarchyAnalyzer.cs              - Type hierarchy navigation
MemberMover.cs                    - Cross-type member movement
UsageAnalyzer.cs                  - Safe deletion analysis
StaticAnalyzer.cs                 - Instance member detection
```

---

## 3. Hierarchy Operations

### 3.1 PullMembersUpOperation

```csharp
namespace RoslynMcp.Core.Refactoring.Hierarchy;

public sealed class PullMembersUpOperation : RefactoringOperationBase<PullMembersUpParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private INamedTypeSymbol GetTargetBaseType(INamedTypeSymbol derived, string? targetTypeName);
    private IReadOnlyList<ISymbol> ValidateMembersForPull(IReadOnlyList<ISymbol> members, INamedTypeSymbol target);
    private MemberDeclarationSyntax ConvertToAbstractOrVirtual(MemberDeclarationSyntax member, bool makeAbstract);
    private MemberDeclarationSyntax AddOverrideModifier(MemberDeclarationSyntax member);
}
```

**Key Algorithms:**
- Target type validation: Must be base class or interface
- Member accessibility adjustment: Protected minimum for base class
- Abstract/virtual determination: Based on whether derived implements
- Override chain maintenance

### 3.2 PushMembersDownOperation

```csharp
public sealed class PushMembersDownOperation : RefactoringOperationBase<PushMembersDownParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private IReadOnlyList<INamedTypeSymbol> GetDerivedTypes(INamedTypeSymbol baseType);
    private bool CanMoveMember(ISymbol member, INamedTypeSymbol target);
    private SyntaxNode AddMemberToType(SyntaxNode typeDecl, MemberDeclarationSyntax member);
}
```

**Key Algorithms:**
- Derived type discovery via `FindDerivedClasses()`
- Member copy to each derived type
- Base type member removal (if moving, not copying)
- Access modifier adjustment

### 3.3 UseBaseTypeOperation

```csharp
public sealed class UseBaseTypeOperation : RefactoringOperationBase<UseBaseTypeParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private IReadOnlyList<IdentifierNameSyntax> FindTypeReferences(INamedTypeSymbol type, SyntaxNode scope);
    private bool CanUseBaseType(ITypeSymbol usage, ITypeSymbol baseType);
    private TypeSyntax CreateBaseTypeReference(INamedTypeSymbol baseType);
}
```

**Key Algorithms:**
- Reference identification across workspace
- Usage compatibility check (does base type support all used members?)
- Selective replacement (local scope or solution-wide)

---

## 4. Pull Members Up Algorithm

### 4.1 Member Validation

```csharp
private IReadOnlyList<ISymbol> ValidateMembersForPull(
    IReadOnlyList<ISymbol> members,
    INamedTypeSymbol target)
{
    var valid = new List<ISymbol>();

    foreach (var member in members)
    {
        // Check if member already exists in target
        if (target.GetMembers(member.Name).Any())
            throw new RefactoringException(ErrorCodes.MemberAlreadyExists,
                $"Member '{member.Name}' already exists in {target.Name}");

        // Check for dependencies on derived-only members
        var dependencies = GetMemberDependencies(member);
        foreach (var dep in dependencies)
        {
            if (!IsAccessibleFrom(dep, target))
                throw new RefactoringException(ErrorCodes.DependencyNotAccessible,
                    $"Member '{member.Name}' depends on '{dep.Name}' which is not accessible from {target.Name}");
        }

        valid.Add(member);
    }

    return valid;
}
```

### 4.2 Abstract/Virtual Conversion

```csharp
private MemberDeclarationSyntax ConvertToAbstract(MethodDeclarationSyntax method)
{
    return method
        .WithModifiers(
            method.Modifiers
                .Add(SyntaxFactory.Token(SyntaxKind.AbstractKeyword))
                .Remove(method.Modifiers.First(m => m.IsKind(SyntaxKind.PrivateKeyword))))
        .WithBody(null)
        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
}

private MemberDeclarationSyntax ConvertToVirtual(MethodDeclarationSyntax method)
{
    return method
        .WithModifiers(
            method.Modifiers
                .Add(SyntaxFactory.Token(SyntaxKind.VirtualKeyword)));
}
```

---

## 5. Additional Operations

### 5.1 IntroduceParameterOperation

```csharp
public sealed class IntroduceParameterOperation : RefactoringOperationBase<IntroduceParameterParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Transforms local variable/expression into method parameter
    // Updates all call sites with the extracted value
}
```

**Algorithm:**
1. Find expression/variable at selection
2. Determine parameter type
3. Add parameter to method signature
4. Replace expression with parameter reference
5. Update all call sites with the value

### 5.2 IntroduceFieldOperation

```csharp
public sealed class IntroduceFieldOperation : RefactoringOperationBase<IntroduceFieldParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Transforms local variable/expression into class field
    // Optionally initializes in constructor
}
```

**Algorithm:**
1. Find expression at selection
2. Create field declaration
3. Replace expression with field reference
4. Optionally add constructor initialization

### 5.3 MakeStaticOperation

```csharp
public sealed class MakeStaticOperation : RefactoringOperationBase<MakeStaticParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    private bool CanMakeStatic(IMethodSymbol method);
    private IReadOnlyList<ISymbol> GetInstanceMemberReferences(IMethodSymbol method);
}
```

**Algorithm:**
1. Verify no instance member access (no `this.` references)
2. Add static modifier
3. Update call sites to use type name instead of instance
4. Handle method group conversions

### 5.4 SafeDeleteOperation

```csharp
public sealed class SafeDeleteOperation : RefactoringOperationBase<SafeDeleteParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    private IReadOnlyList<Location> FindUsages(ISymbol symbol);
    private bool CanSafelyDelete(ISymbol symbol, IReadOnlyList<Location> usages);
}
```

**Algorithm:**
1. Find all references to symbol
2. If no references, delete immediately
3. If references exist, return error with locations
4. Optional: Force delete with reference cleanup

---

## 6. Error Handling

### 6.1 Error Code Mapping

| Error Code | Constant | Trigger |
|------------|----------|---------|
| 3090 | MEMBER_ALREADY_EXISTS | Member exists in target type |
| 3091 | DEPENDENCY_NOT_ACCESSIBLE | Pulled member depends on inaccessible member |
| 3092 | NO_BASE_TYPE | Type has no base to pull to |
| 3093 | NO_DERIVED_TYPES | Type has no derived types to push to |
| 3094 | MEMBER_HAS_USAGES | Cannot safely delete - has references |
| 3095 | USES_INSTANCE_MEMBERS | Cannot make static - uses instance members |
| 3096 | ALREADY_STATIC | Member is already static |
| 3097 | ALREADY_INSTANCE | Member is already instance |
| 3098 | EXPRESSION_NOT_FOUND | No expression at selection |
| 3099 | CANNOT_INTRODUCE_PARAMETER | Expression not in method body |

---

## 7. Type Hierarchy Analysis

### 7.1 Finding Derived Types

```csharp
private async Task<IReadOnlyList<INamedTypeSymbol>> GetDerivedTypes(
    INamedTypeSymbol baseType,
    Solution solution)
{
    var derived = await SymbolFinder.FindDerivedClassesAsync(
        baseType, solution, transitive: false);

    // Also find implementing types for interfaces
    if (baseType.TypeKind == TypeKind.Interface)
    {
        var implementations = await SymbolFinder.FindImplementationsAsync(
            baseType, solution);
        derived = derived.Concat(implementations.OfType<INamedTypeSymbol>());
    }

    return derived.ToList();
}
```

### 7.2 Member Dependency Analysis

```csharp
private IReadOnlyList<ISymbol> GetMemberDependencies(ISymbol member)
{
    var dependencies = new List<ISymbol>();

    if (member is IMethodSymbol method)
    {
        var body = method.DeclaringSyntaxReferences
            .FirstOrDefault()?.GetSyntax() as MethodDeclarationSyntax;

        if (body?.Body != null)
        {
            var model = await document.GetSemanticModelAsync();
            var collector = new DependencyCollector(model);
            collector.Visit(body.Body);
            dependencies.AddRange(collector.Dependencies);
        }
    }

    return dependencies;
}
```

---

## 8. Testing Strategy

### 8.1 Test Categories

| Category | Scenarios |
|----------|-----------|
| Pull Up | To base class, to interface, with dependencies |
| Push Down | Single derived, multiple derived, copy vs move |
| Static | Can make static, has instance access, update calls |
| Safe Delete | No usages, has usages, force delete |
| Introduce | Parameter, field, with initialization |

### 8.2 Test Fixtures

```
tests/RoslynMcp.Core.Tests/Fixtures/Hierarchy/
├── BaseAndDerived.cs
├── InterfaceHierarchy.cs
├── DeepHierarchy.cs
└── DiamondInheritance.cs

tests/RoslynMcp.Core.Tests/Fixtures/Additional/
├── MethodsToMakeStatic.cs
├── SymbolsToDelete.cs
└── ExpressionsToIntroduce.cs
```

---

## 9. Performance Considerations

| Operation | Expected Time | Notes |
|-----------|---------------|-------|
| pull_members_up | <500ms | Dependency analysis |
| push_members_down | <700ms | Multiple type updates |
| use_base_type | <400ms | Reference finding |
| introduce_parameter | <400ms | Call site updates |
| introduce_field | <200ms | Single type change |
| make_static | <300ms | Call site updates |
| make_non_static | <300ms | Call site updates |
| safe_delete | <500ms | Usage analysis |

---

## 10. Build Sequence

**Phase 1: Utilities**
- HierarchyAnalyzer
- UsageAnalyzer
- MemberMover

**Phase 2: Simple Operations**
- IntroduceFieldOperation
- SafeDeleteOperation

**Phase 3: Static Operations**
- MakeStaticOperation
- MakeNonStaticOperation
- IntroduceParameterOperation

**Phase 4: Hierarchy Operations**
- PullMembersUpOperation
- PushMembersDownOperation
- UseBaseTypeOperation

**Phase 5: Integration**
- Cross-operation testing
- Preview mode validation
- Performance optimization

---

## Revision History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-01-28 | Architect | Initial design |
