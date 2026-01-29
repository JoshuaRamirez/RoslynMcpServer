# Architecture Design: Signature Operations

## Document Overview

| Property | Value |
|----------|-------|
| Document ID | ARCH-SIG-001 |
| Version | 1.0 |
| Status | Draft |
| Author | Software Architect |
| Created | 2026-01-28 |
| BA Reference | BA-Signature-Operations.md |

---

## 1. Operations Covered

| Operation | Class | Priority | Complexity |
|-----------|-------|----------|------------|
| change_signature | ChangeSignatureOperation | Tier 2 | High |
| add_parameter | AddParameterOperation | Tier 2 | Medium |
| remove_parameter | RemoveParameterOperation | Tier 2 | Medium |
| reorder_parameters | ReorderParametersOperation | Tier 3 | Low |

---

## 2. File Structure

### 2.1 Parameter Models (src/RoslynMcp.Contracts/Models/)

```
ChangeSignatureParams.cs
AddParameterParams.cs
RemoveParameterParams.cs
ReorderParametersParams.cs
```

### 2.2 Operation Classes (src/RoslynMcp.Core/Refactoring/Signature/)

```
ChangeSignatureOperation.cs       (~600 lines)
AddParameterOperation.cs          (~400 lines)
RemoveParameterOperation.cs       (~350 lines)
ReorderParametersOperation.cs     (~300 lines)
```

### 2.3 Utilities (src/RoslynMcp.Core/Refactoring/Signature/)

```
SignatureAnalyzer.cs              - Parameter analysis, override chain
CallSiteUpdater.cs                - Argument list transformation
OverrideChainTracker.cs           - Virtual/interface method tracking
```

---

## 3. Class Designs

### 3.1 ChangeSignatureOperation

```csharp
namespace RoslynMcp.Core.Refactoring.Signature;

public sealed class ChangeSignatureOperation : RefactoringOperationBase<ChangeSignatureParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private IReadOnlyList<IMethodSymbol> GetOverrideChain(IMethodSymbol method);
    private ParameterListSyntax ApplySignatureChanges(ParameterListSyntax original, SignatureChange[] changes);
    private ArgumentListSyntax UpdateCallSiteArguments(ArgumentListSyntax original, SignatureChange[] changes);
}
```

**Key Algorithms:**
- Override chain discovery: Find all overrides and interface implementations
- Signature delta computation: Added, removed, reordered, renamed parameters
- Call site argument mapping: Handle named arguments, default values

### 3.2 AddParameterOperation

```csharp
public sealed class AddParameterOperation : RefactoringOperationBase<AddParameterParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private ParameterSyntax CreateParameter(string name, string type, string? defaultValue);
    private int ComputeInsertionIndex(ParameterListSyntax list, int? position);
    private ArgumentSyntax CreateArgument(string paramName, string? defaultValue);
}
```

**Key Algorithms:**
- Position validation: Between 0 and parameter count
- Default value handling: Required for existing call sites
- Named argument insertion when position != end

### 3.3 RemoveParameterOperation

```csharp
public sealed class RemoveParameterOperation : RefactoringOperationBase<RemoveParameterParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private IParameterSymbol FindParameter(IMethodSymbol method, string name, int? index);
    private bool IsParameterUsed(IParameterSymbol param, BlockSyntax body);
    private ArgumentListSyntax RemoveArgument(ArgumentListSyntax args, int index);
}
```

**Key Algorithms:**
- Usage analysis: Check if parameter is referenced in method body
- Named argument handling: Remove by name, adjust positional indices
- Cascade removal through override chain

### 3.4 ReorderParametersOperation

```csharp
public sealed class ReorderParametersOperation : RefactoringOperationBase<ReorderParametersParams>
{
    protected override Task<RefactoringResult> ExecuteCoreAsync(...);

    // Private helpers
    private int[] ValidateNewOrder(int paramCount, int[] newOrder);
    private ParameterListSyntax ReorderParameters(ParameterListSyntax list, int[] newOrder);
    private ArgumentListSyntax ReorderArguments(ArgumentListSyntax args, int[] newOrder);
}
```

**Key Algorithms:**
- Order validation: Permutation of 0..n-1
- Named argument preservation (don't reorder if named)
- Positional argument reordering

---

## 4. Override Chain Handling

### 4.1 Discovery Algorithm

```csharp
private IReadOnlyList<IMethodSymbol> GetOverrideChain(IMethodSymbol method)
{
    var chain = new List<IMethodSymbol> { method };

    // Walk up to base
    var current = method;
    while (current.OverriddenMethod != null)
    {
        chain.Add(current.OverriddenMethod);
        current = current.OverriddenMethod;
    }

    // Find implementations of interface methods
    foreach (var iface in method.ContainingType.AllInterfaces)
    {
        var ifaceMethod = iface.GetMembers(method.Name)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => method.ContainingType.FindImplementationForInterfaceMember(m)?.Equals(method) == true);

        if (ifaceMethod != null)
            chain.Add(ifaceMethod);
    }

    // Find derived overrides via FindReferences
    var derivedOverrides = await SymbolFinder.FindOverridesAsync(method, solution);
    chain.AddRange(derivedOverrides);

    return chain;
}
```

### 4.2 Signature Consistency

All methods in the override chain must be updated together:
- Same parameter types
- Same parameter order
- Same parameter names (recommended, not required)
- Default values can differ

---

## 5. Call Site Update Algorithm

### 5.1 Positional Arguments

```csharp
private ArgumentListSyntax UpdatePositionalArguments(
    ArgumentListSyntax original,
    SignatureChange[] changes)
{
    var args = original.Arguments.ToList();
    var newArgs = new List<ArgumentSyntax>();

    foreach (var change in changes.OrderBy(c => c.NewIndex))
    {
        switch (change.Kind)
        {
            case ChangeKind.Keep:
                newArgs.Add(args[change.OldIndex]);
                break;

            case ChangeKind.Add:
                var expr = change.DefaultValue != null
                    ? SyntaxFactory.ParseExpression(change.DefaultValue)
                    : SyntaxFactory.DefaultExpression(SyntaxFactory.ParseTypeName(change.Type));
                newArgs.Add(SyntaxFactory.Argument(expr));
                break;

            case ChangeKind.Remove:
                // Skip this argument
                break;
        }
    }

    return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs));
}
```

### 5.2 Named Arguments

```csharp
private ArgumentListSyntax UpdateNamedArguments(
    ArgumentListSyntax original,
    SignatureChange[] changes)
{
    var args = original.Arguments.ToList();
    var newArgs = new List<ArgumentSyntax>();

    // Named arguments can stay in any order
    foreach (var arg in args)
    {
        var name = arg.NameColon?.Name.Identifier.Text;
        if (name == null)
        {
            // Positional - handle based on index
            continue;
        }

        var change = changes.FirstOrDefault(c => c.OldName == name);
        if (change?.Kind == ChangeKind.Remove)
            continue;

        if (change?.NewName != name)
        {
            // Rename the argument
            arg = arg.WithNameColon(
                SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(change.NewName)));
        }

        newArgs.Add(arg);
    }

    // Add new required parameters
    foreach (var change in changes.Where(c => c.Kind == ChangeKind.Add))
    {
        var expr = SyntaxFactory.ParseExpression(change.DefaultValue);
        newArgs.Add(SyntaxFactory.Argument(
            SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(change.NewName)),
            default,
            expr));
    }

    return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs));
}
```

---

## 6. Error Handling

### 6.1 Error Code Mapping

| Error Code | Constant | Trigger |
|------------|----------|---------|
| 3060 | PARAMETER_NAME_EXISTS | Duplicate parameter name |
| 3061 | INVALID_PARAMETER_TYPE | Unparseable type string |
| 3062 | PARAMETER_IN_USE | Removing parameter that's used |
| 3063 | INVALID_PARAMETER_ORDER | Invalid reorder permutation |
| 3064 | REQUIRED_AFTER_OPTIONAL | Required param after optional |
| 3065 | OVERRIDE_SIGNATURE_MISMATCH | Can't change signature due to override |
| 3066 | NO_DEFAULT_FOR_NEW_PARAM | New param needs default for existing calls |

### 6.2 Validation Rules

```
1. Parameter name must be valid C# identifier
2. Parameter type must parse as valid type syntax
3. New parameter after optional params must have default
4. Default value must be compile-time constant or default(T)
5. Override chain must all be modifiable
```

---

## 7. Testing Strategy

### 7.1 Test Categories

| Category | Scenarios |
|----------|-----------|
| Simple | Add/remove single parameter |
| Override | Changes propagate to overrides |
| Interface | Changes propagate to implementations |
| Named Args | Named arguments handled correctly |
| Defaults | Default values work at call sites |
| Mixed | Combined add/remove/reorder |

### 7.2 Test Fixtures

```
tests/RoslynMcp.Core.Tests/Fixtures/Signature/
├── SimpleMethod.cs
├── VirtualMethod.cs
├── InterfaceMethod.cs
├── OverrideChain.cs
└── NamedArguments.cs
```

---

## 8. Performance Considerations

| Operation | Expected Time | Notes |
|-----------|---------------|-------|
| change_signature | <1s | Depends on call site count |
| add_parameter | <500ms | Override chain discovery |
| remove_parameter | <500ms | Usage analysis |
| reorder_parameters | <300ms | Simpler transformation |

**Optimization:**
- Cache override chain resolution
- Batch call site updates per document
- Use parallel document editing

---

## 9. Build Sequence

**Phase 1: Foundation**
- Parameter models (4 files)
- Utility classes (3 files)

**Phase 2: Simple Operations**
- AddParameterOperation
- RemoveParameterOperation
- ReorderParametersOperation

**Phase 3: Complex Operation**
- ChangeSignatureOperation (combines all)

**Phase 4: Integration**
- Override chain testing
- Cross-project reference updates
- Performance validation

---

## Revision History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-01-28 | Architect | Initial design |
