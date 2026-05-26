# Design: Fix Collection-Constraint Fallback Diamond Inference

## Problem

`ObjectCreationTransformer.cs:217` emits `new ArrayList<>()` as the fallback for `new TC()` when `TC : ICollection<TS>, new()`. The Java diamond operator `<>` infers type arguments from the assignment target type, but `TC extends Collection<TS>` is a type variable, not a concrete type — so inference fails:

```
不兼容的类型: 无法推断java.util.ArrayList<>的类型参数
原因: 不存在类型变量E的实例, 以使java.util.ArrayList<E>与TC一致
```

## Root Cause

When the converter encounters `new TC()` where `TC` has both a `new()` constraint and a collection constraint (`ICollection<TS>` / `IEnumerable<TS>`), it emits `new ArrayList<>()` as a best-effort fallback. But the diamond operator cannot resolve its type parameter against a type-variable target.

The call-site inliner covers many cases by inlining the method body with concrete types, but the standalone method definition must still compile — callers that don't get inlined will link against it.

## Fix

In `ObjectCreationTransformer.TransformObjectCreation`, extract the collection element type from `TC`'s constraint types and emit an explicit type argument for `ArrayList`:

```
Before:  new ArrayList<>()
After:   new ArrayList<TS>()
```

### New helper: `GetCollectionElementType`

- Iterate `typeParameter.ConstraintTypes`
- Find the first `ICollection<T>` or `IEnumerable<T>` constraint
- Return `T` (the element type)
- If no collection constraint with type args found, return null → fallback to `"Object"`

### Files changed

1. `src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs`
   - Modify `TransformObjectCreation` (line 217): use explicit type arg
   - Add `GetCollectionElementType` helper (~15 lines)
2. `tests/CSharpToJava.Tests/GenericNewConstraintInliningTests.cs`
   - Update `AddToMap_StandaloneMethod_HasFallback` assertion
   - Add new tests for edge cases

## Verification

1. `dotnet test` — all tests pass
2. Re-convert MSAGL GraphLayout project to `E:\z5`
3. Confirm `CollectionUtilities.java` compiles (no diamond inference error)
