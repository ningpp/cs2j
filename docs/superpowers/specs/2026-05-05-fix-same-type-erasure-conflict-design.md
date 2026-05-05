# Fix Same-Type Erasure Conflict Detection for Non-Generic Method Overloads

## Problem

`HasTypeErasureConflict` in `JavaNaming.cs` skips conflict detection when two overloads have the same `TypeParameters.Length`, but identical erased parameter signatures. When two non-generic methods differ only in generic type arguments (e.g., `IList<string>` vs `IList<IList<string>>`), the erased JVM signature is the same (`List, List`), but no conflict is detected.

```csharp
// Both have 0 type params, same erased signature (List, List)
void MkEdgeStmt(IList<string> src, IList<string> dst) { }
void MkEdgeStmt(IList<string> src, IList<IList<string>> edges) { }
```

Current output silently drops the second method:
```java
public class Sample {
    void mkEdgeStmt(List<String> src, List<String> dst) { }
}
```

## Root Cause

`JavaNaming.HasTypeErasureConflict` line 46:
```csharp
if (sibling.TypeParameters.Length == method.TypeParameters.Length) continue;
```
For two 0-type-param methods with identical erased params, `0 == 0` causes a `continue`, skipping the check.

## Design

### 1. `JavaNaming.cs` — Detection & Suffix Logic

**New enum:**
```csharp
public enum ErasureConflictKind { None, DifferentTypeParamCount, SameTypeParamCount }
```

**Fix detection** — remove the same-count guard, return conflict kind:
```csharp
public static (bool HasConflict, ErasureConflictKind Kind) GetErasureConflictKind(IMethodSymbol method)
{
    // Removes: if (sibling.TypeParameters.Length == method.TypeParameters.Length) continue;
    // Returns (true, SameTypeParamCount) for new cases
    // Returns (true, DifferentTypeParamCount) for existing cases
}
```

**New suffix method:**
```csharp
public static string GetErasureConflictSuffix(IMethodSymbol method) => kind switch
{
    DifferentTypeParamCount => $"_{method.TypeParameters.Length}tp",   // existing scheme
    SameTypeParamCount => $"_erasure_{GetErasureConflictIndex(method)}", // 1-based, source-order
    _ => ""
};
```

**`GetErasureConflictIndex`** — groups by (name, erased sig), sorts by source line, returns 1-based index (1 = keeps original name, 2+ = renamed).

### 2. Call Site Changes

`MethodTransformer.cs` and `InvocationExpressionTransformer.cs` (5 call sites total) — replace two-line check+append with one line:
```csharp
methodName += ConversionContext.GetErasureConflictSuffix(methodSymbol);
```

`ConversionContext.cs` — replace `HasTypeErasureConflict` + `GetErasureRenamedSuffix` facades with `GetErasureConflictSuffix`.

### 3. Backward Compatibility

- Different type param count conflicts: **unchanged** (`_Ntp` suffix preserved)
- Same type param count conflicts: **new** `_erasure_N` suffix
- Callsites simplified: no more conditional — `GetErasureConflictSuffix` returns `""` for no-conflict cases

## Test Plan

- `NonGenericOverloads_SameErasedParams_RenamesSecond`: 2 same-erasure overloads → second gets `_erasure_2`
- `NestedGenericTypes_MappedCorrectly`: `IList<IList<string>>` → `List<List<String>>`
- `ThreeWayConflict_SameTypeParamCount`: 3 same-erasure overloads → sequential `_erasure_2`, `_erasure_3`
- All existing erasure tests (`CrossInheritanceErasureTests`, etc.) must stay green
