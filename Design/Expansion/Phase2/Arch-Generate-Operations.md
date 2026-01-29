# Architecture Design: Generate Operations

## Document Overview

| Property | Value |
|----------|-------|
| Document ID | ARCH-GEN-001 |
| Version | 1.0 |
| Status | Draft |
| Author | Software Architect |
| Created | 2026-01-28 |
| BA Reference | BA-Generate-Operations.md |

---

## 1. Operations Covered

| Operation | Class | Priority | Complexity |
|-----------|-------|----------|------------|
| generate_constructor | GenerateConstructorOperation | Tier 1 | Medium |
| generate_equals_hashcode | GenerateEqualsHashCodeOperation | Tier 2 | Medium |
| generate_property | GeneratePropertyOperation | Tier 3 | Low |
| generate_method_stub | GenerateMethodStubOperation | Tier 3 | Low |
| implement_interface | ImplementInterfaceOperation | Tier 2 | Medium |
| implement_abstract | ImplementAbstractOperation | Tier 2 | Medium |

---

## 2. File Structure

### 2.1 Parameter Models (src/RoslynMcp.Contracts/Models/)

```
GenerateConstructorParams.cs      (existing - extend)
GenerateEqualsHashCodeParams.cs
GeneratePropertyParams.cs
GenerateMethodStubParams.cs
ImplementInterfaceParams.cs
ImplementAbstractParams.cs
```

### 2.2 Operation Classes (src/RoslynMcp.Core/Refactoring/Generate/)

```
GenerateConstructorOperation.cs          (existing - extend)
GenerateEqualsHashCodeOperation.cs       (~500 lines)
GeneratePropertyOperation.cs             (~300 lines)
GenerateMethodStubOperation.cs           (~350 lines)
ImplementInterfaceOperation.cs           (~450 lines)
ImplementAbstractOperation.cs            (~400 lines)
```

### 2.3 Utilities (src/RoslynMcp.Core/Refactoring/Generate/)

```
MemberGenerator.cs                - Common syntax generation
EqualityCodeGenerator.cs          - Equals/GetHashCode patterns
InterfaceMemberMapper.cs          - Interface to implementation mapping
```

---

## 3. Class Designs

### 3.1 GenerateEqualsHashCodeOperation

```csharp
namespace RoslynMcp.Core.Refactoring.Generate;

public sealed class GenerateEqualsHashCodeOperation : RefactoringOperationBase<GenerateEqualsHashCodeParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private MethodDeclarationSyntax GenerateEquals(INamedTypeSymbol type, IReadOnlyList<ISymbol> members);
    private MethodDeclarationSyntax GenerateGetHashCode(INamedTypeSymbol type, IReadOnlyList<ISymbol> members);
    private MethodDeclarationSyntax GenerateOperatorEquals(INamedTypeSymbol type);
    private MethodDeclarationSyntax GenerateOperatorNotEquals(INamedTypeSymbol type);
}
```

**Key Algorithms:**
- Member selection: All fields/properties or specified subset
- Null checking pattern based on nullability settings
- HashCode.Combine() for modern or manual combination for legacy

### 3.2 GeneratePropertyOperation

```csharp
public sealed class GeneratePropertyOperation : RefactoringOperationBase<GeneratePropertyParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private PropertyDeclarationSyntax CreateAutoProperty(string name, string type, string visibility);
    private PropertyDeclarationSyntax CreatePropertyWithBackingField(string name, string type, IFieldSymbol? backingField);
}
```

**Key Algorithms:**
- Auto-property generation: `{ get; set; }` pattern
- Backing field property: `{ get => _field; set => _field = value; }`
- Init-only property: `{ get; init; }`

### 3.3 ImplementInterfaceOperation

```csharp
public sealed class ImplementInterfaceOperation : RefactoringOperationBase<ImplementInterfaceParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private IReadOnlyList<ISymbol> GetUnimplementedMembers(INamedTypeSymbol type, INamedTypeSymbol iface);
    private MemberDeclarationSyntax GenerateImplementation(ISymbol member, bool explicitImpl);
}
```

**Key Algorithms:**
- Unimplemented member discovery via `FindImplementationForInterfaceMember()`
- Explicit vs implicit implementation based on parameter
- throw NotImplementedException pattern for bodies

---

## 4. Equals/GetHashCode Generation

### 4.1 Equals Pattern (Reference Type)

```csharp
public override bool Equals(object? obj)
{
    return Equals(obj as MyClass);
}

public bool Equals(MyClass? other)
{
    if (other is null) return false;
    if (ReferenceEquals(this, other)) return true;
    return Field1 == other.Field1
        && Field2 == other.Field2
        && EqualityComparer<T>.Default.Equals(Field3, other.Field3);
}
```

### 4.2 GetHashCode Pattern (Modern)

```csharp
public override int GetHashCode()
{
    return HashCode.Combine(Field1, Field2, Field3);
}
```

### 4.3 GetHashCode Pattern (Legacy)

```csharp
public override int GetHashCode()
{
    unchecked
    {
        int hash = 17;
        hash = hash * 31 + Field1?.GetHashCode() ?? 0;
        hash = hash * 31 + Field2?.GetHashCode() ?? 0;
        return hash;
    }
}
```

### 4.4 Value Type Pattern

```csharp
public bool Equals(MyStruct other)
{
    return Field1 == other.Field1
        && Field2 == other.Field2;
}

public override bool Equals(object? obj)
{
    return obj is MyStruct other && Equals(other);
}
```

---

## 5. Interface Implementation

### 5.1 Unimplemented Member Discovery

```csharp
private IReadOnlyList<ISymbol> GetUnimplementedMembers(
    INamedTypeSymbol type,
    INamedTypeSymbol iface)
{
    var unimplemented = new List<ISymbol>();

    foreach (var member in iface.GetMembers())
    {
        if (member.Kind == SymbolKind.Method && ((IMethodSymbol)member).MethodKind != MethodKind.Ordinary)
            continue; // Skip property accessors, etc.

        var impl = type.FindImplementationForInterfaceMember(member);
        if (impl == null)
            unimplemented.Add(member);
    }

    return unimplemented;
}
```

### 5.2 Implementation Generation

```csharp
private MethodDeclarationSyntax GenerateMethodImplementation(
    IMethodSymbol method,
    bool explicitImpl)
{
    var returnType = method.ReturnsVoid
        ? SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword))
        : SyntaxFactory.ParseTypeName(method.ReturnType.ToDisplayString());

    var parameters = method.Parameters.Select(p =>
        SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.Name))
            .WithType(SyntaxFactory.ParseTypeName(p.Type.ToDisplayString())));

    var body = SyntaxFactory.Block(
        SyntaxFactory.ThrowStatement(
            SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.ParseTypeName("NotImplementedException"))
                .WithArgumentList(SyntaxFactory.ArgumentList())));

    var decl = SyntaxFactory.MethodDeclaration(returnType, method.Name)
        .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
        .WithBody(body);

    if (explicitImpl)
    {
        decl = decl.WithExplicitInterfaceSpecifier(
            SyntaxFactory.ExplicitInterfaceSpecifier(
                SyntaxFactory.ParseName(method.ContainingType.ToDisplayString())));
    }
    else
    {
        decl = decl.WithModifiers(SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PublicKeyword)));
    }

    return decl;
}
```

---

## 6. Error Handling

### 6.1 Error Code Mapping

| Error Code | Constant | Trigger |
|------------|----------|---------|
| 3070 | TYPE_ALREADY_HAS_EQUALS | Equals override exists |
| 3071 | TYPE_ALREADY_HAS_HASHCODE | GetHashCode override exists |
| 3072 | INTERFACE_NOT_IMPLEMENTED | Class doesn't implement interface |
| 3073 | NO_UNIMPLEMENTED_MEMBERS | All members already implemented |
| 3074 | PROPERTY_NAME_EXISTS | Property with name already exists |
| 3075 | MEMBER_NAME_EXISTS | Member with name already exists |
| 3076 | ABSTRACT_MEMBER_NOT_FOUND | Abstract member doesn't exist |
| 3077 | NOT_ABSTRACT_CLASS | Class is not abstract |

---

## 7. Testing Strategy

### 7.1 Test Categories

| Category | Scenarios |
|----------|-----------|
| Equals/Hash | Reference types, value types, nullable handling |
| Property | Auto, backing field, init-only |
| Interface | Single, multiple, explicit, generic |
| Abstract | Single method, all methods, property |

### 7.2 Test Fixtures

```
tests/RoslynMcp.Core.Tests/Fixtures/Generate/
├── SimpleClass.cs
├── RecordType.cs
├── StructType.cs
├── InterfaceImplementor.cs
├── AbstractDerived.cs
└── GenericClass.cs
```

---

## 8. Performance Considerations

| Operation | Expected Time | Notes |
|-----------|---------------|-------|
| generate_constructor | <100ms | Existing implementation |
| generate_equals_hashcode | <150ms | Syntax generation |
| generate_property | <50ms | Single member |
| generate_method_stub | <50ms | Single member |
| implement_interface | <200ms | Multi-member generation |
| implement_abstract | <150ms | Multi-member generation |

---

## 9. Build Sequence

**Phase 1: Extend Existing**
- Extend GenerateConstructorParams
- Add new parameter models

**Phase 2: Simple Generators**
- GeneratePropertyOperation
- GenerateMethodStubOperation

**Phase 3: Complex Generators**
- GenerateEqualsHashCodeOperation
- ImplementInterfaceOperation
- ImplementAbstractOperation

**Phase 4: Integration**
- Cross-operation testing
- Preview mode validation

---

## Revision History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-01-28 | Architect | Initial design |
