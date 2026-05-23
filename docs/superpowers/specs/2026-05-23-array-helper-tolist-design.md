# Replace Arrays.asList() with ArrayHelper.toList()

## Problem

The C# converter generates `Arrays.asList()` in Java output to wrap arrays as `Iterable<T>` / `List<T>`. This causes runtime issues:

1. **NPE on null array**: `Arrays.asList((T[]) null)` throws `NullPointerException`
2. **Immutable result**: `Arrays.asList()` returns a fixed-size list backed by the array, so `.add()` / `.remove()` / `.clear()` throw `UnsupportedOperationException`

## Solution

Add `ArrayHelper.toList()` to the existing Java compat library (`java/csharptojava-compat`), then replace all `Arrays.asList()` generation points in the C# converter to emit `ArrayHelper.toList()` instead.

### ArrayHelper.toList() behavior

- null array → empty `ArrayList` (no NPE)
- null elements preserved (`ArrayList` supports null elements)
- Always returns mutable `ArrayList` (add/remove/clear work)
- Independent copy (modifications don't affect source array)

### Scope

- **ALL** `Arrays.asList()` generation points (~25 across ~10 files) → `ArrayHelper.toList()`
- Imports: add `io.github.ningpp.compat.ArrayHelper` instead of (or in addition to) `java.util.Arrays` where asList was the only usage
- Tests: update assertions to expect `ArrayHelper.toList`
- `ArrayIterableConversionRewriter` unchanged (handles instance method rewrites, different concern)

## Files to change

### Java compat (done)
- `java/csharptojava-compat/.../ArrayHelper.java` — add `toList()` method

### C# converter (~10 files)
- `ExpressionTransformerHelpers.cs` — `BuildArrayToCollectionExpression()`
- `ObjectCreationTransformer.cs` — multiple generation points
- `StatementTransformer.ExpressionAndReturn.cs`
- `StatementTransformer.Declarations.cs`
- `FieldTransformer.cs`
- `MethodTransformer.cs`
- `AssignmentTransformer.cs`
- `InvocationExpressionTransformer.cs`
- `IdentifierExpressionTransformer.cs`
- `ElementAccessTransformer.cs`
- `ControlFlowTransformer.cs`

### Tests (~6 files)
- `ArrayToIterableTests.cs`
- `ArrayToIterableConversionTests.cs`
- `AsEnumerableArrayWrappingTests.cs`
- `StreamArrayCountSystemicTests.cs`
- `ErrorDocDiagnosticTests.cs`
- `NumericTargetTypeConversionTests.cs`

## Target

Java 25, package `io.github.ningpp.compat`
