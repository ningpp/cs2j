# ExpressionTransformer (deprecated)

**Source:** `src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformer.cs`

## 1. C# Language Feature Converted

`ExpressionTransformer` was the original entry point for transforming **all C# expression syntax nodes** into Java code strings. It implements `IExpressionTransformer` and accepts any `ExpressionSyntax` node. The class has since been superseded by the `ExpressionTransformerFacade` + `ExpressionTransformerRegistry` pattern and is now just a thin forwarding wrapper:

```csharp
[Obsolete("Use ExpressionTransformerFacade.Instance instead.")]
public class ExpressionTransformer : IExpressionTransformer
{
    private readonly ExpressionTransformerFacade _facade = ExpressionTransformerFacade.Instance;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => _facade.Transform(node, context);
}
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — `[Obsolete]` attribute present but the class is still widely instantiated

Despite the `[Obsolete]` annotation, multiple production callers continue to use `new ExpressionTransformer()`:

- `MethodTransformer` calls `new ExpressionTransformer()` for expression-body methods.
- `PropertyTransformer` calls `new ExpressionTransformer()` for property initializers and expression-body accessors.
- `FieldTransformer` calls `new ExpressionTransformer()` for field initializers.
- `StatementTransformer` indirectly also instantiates it.

Each `new ExpressionTransformer()` call is a no-op wrapper that immediately creates `ExpressionTransformerFacade.Instance` — a singleton. The callers are therefore allocating garbage on every expression transformation without any benefit.

### Issue 2 — Hidden call-site coupling makes refactoring harder

Because callers use `new ExpressionTransformer()` rather than `ExpressionTransformerFacade.Instance`, a future change to the facade (e.g., adding a context parameter or changing the signature) must also be applied to this wrapper class. There is no compile-time signal that the wrapper is the wrong layer to change.

### Issue 3 — The `[Obsolete]` warning is silent at most call sites

The attribute has no `error: true` argument:

```csharp
[Obsolete("Use ExpressionTransformerFacade.Instance instead.")]
```

This means callers silently suppress the deprecation warning (CS0612) even with `<TreatWarningsAsErrors>` disabled. The intended migration never happened.

### Issue 4 — Unnecessary redundant layer in the expression pipeline

The call stack during expression transformation is:
```
Caller
  → new ExpressionTransformer()
    → ExpressionTransformerFacade.Instance.Transform()
      → ExpressionTransformerRegistry.GetTransformer()
        → SpecializedTransformer.Transform()
```

The `ExpressionTransformer` wrapper adds one extra virtual dispatch per expression node with no value.

## 3. Proposed Fixes

### Fix 1 — Replace all `new ExpressionTransformer()` call sites with `ExpressionTransformerFacade.Instance`

Search for all `new ExpressionTransformer()` usages across the codebase and replace with the static singleton:

```csharp
// Before
var exprTransformer = new ExpressionTransformer();
javaMethod.Body = exprTransformer.Transform(node.ExpressionBody.Expression, context);

// After
javaMethod.Body = ExpressionTransformerFacade.Instance.Transform(node.ExpressionBody.Expression, context);
```

### Fix 2 — Escalate `[Obsolete]` to a compile error

Once all usages are gone, set `error: true` so that any future accidental re-introduction is caught at build time:

```csharp
[Obsolete("Use ExpressionTransformerFacade.Instance instead.", error: true)]
```

### Fix 3 — Delete the class once call sites are cleaned up

After all callers are updated, remove `ExpressionTransformer.cs` entirely to reduce surface area and confusion.

## 4. Commit Changes

```
refactor(expression): replace all new ExpressionTransformer() calls with ExpressionTransformerFacade.Instance and remove deprecated wrapper class
```
