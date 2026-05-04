# Design: Map C# Exception Classes to Java RuntimeException

## Problem

C# has no checked/unchecked exception distinction — all exceptions behave like Java's unchecked exceptions. But the converter maps `System.Exception` → `java.lang.Exception`, so user-defined C# exception classes get `extends Exception` (checked) instead of `extends RuntimeException` (unchecked).

Root cause: [TypeMappings.json:445](../../config/TypeMappings.json#L445) maps `System.Exception` to `java.lang.Exception`. `ClassTransformer` resolves the base type via `MapType()` and uses whatever comes back as the `extends` clause — no special handling.

## Design

### Change `System.Exception` mapping globally

In `config/TypeMappings.json`, change:

```diff
 "System.Exception": {
-  "javaType": "Exception",
-  "import": "java.lang.Exception"
+  "javaType": "RuntimeException",
+  "import": "java.lang.RuntimeException"
 }
```

This single change affects all uses: class `extends`, `catch` clauses, type references, `new Exception()` calls.

### Remove redundant special-case code

In [ObjectCreationTransformer.cs](../../src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs):

- Remove the hardcoded `Exception`/`ApplicationException` → `RuntimeException` rewrite (lines ~278-288) — the mapping now handles it.
- Update `NeedsSpecialCreationHandling` (line ~118) to drop `"Exception"` from the list.

### Why safe

- `catch(Exception)` becomes `catch(RuntimeException)`, which misses Java checked exceptions — but `JavaExceptionCheckRewriter` already wraps all Java library checked exceptions in `RuntimeException`, so no checked exceptions leak into C#-origin code.
- `System.SystemException` and `System.ApplicationException` already map to `RuntimeException` — this change makes `System.Exception` consistent.

## Components

| Component | Change |
|---|---|
| `config/TypeMappings.json` | `System.Exception` → `RuntimeException` |
| `ObjectCreationTransformer.cs` | Remove Exception/ApplicationException rewrite |
| Tests | Update assertions expecting `extends Exception` |

## No changes needed

- `ClassTransformer` / `HIRTypeGenerator` — generic base-type mapping, picks up the change automatically
- `JavaExceptionCheckRewriter` — already treats `RuntimeException` as unchecked
- `Debug.Fail` / `Trace.Fail` rewrites — already produce `throw new RuntimeException()`
