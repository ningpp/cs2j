# ExpressionTransformerFacade

**Source:** `src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs`

## 1. C# Language Feature Converted

`ExpressionTransformerFacade` is the **dispatch gateway for all C# expression syntax nodes**. It implements `IExpressionTransformer` and receives any `ExpressionSyntax` node, looks up the responsible specialized transformer in `ExpressionTransformerRegistry`, and delegates transformation to it. In addition, it provides `TransformWhenNotNull`, a recursive helper for handling the `WhenNotNull` branch of `ConditionalAccessExpressionSyntax` (i.e., chains like `obj?.a.b.Method(args)`).

C# expression syntax kinds covered (via the registry): arithmetic, logical, bitwise, assignment, invocation, member access, object/array creation, lambda, LINQ query, string interpolation, type operations, literal values, control flow expressions, and element access.

## 2. Deep Analysis of Current Code Issues

### Issue 1 — `Transform()` throws at runtime for unregistered expression kinds

```csharp
public string Transform(ExpressionSyntax node, ConversionContext context)
{
    var transformer = ExpressionTransformerRegistry.GetTransformer(node.Kind())
        ?? throw new NotSupportedException($"Expression kind {node.Kind()} is not supported.");
    return transformer.Transform(node, context);
}
```

If any C# syntax node with an unregistered `SyntaxKind` is encountered (e.g., `SuppressNullableWarningExpression` (`x!`), `TupleExpression`, `DeclarationExpression`), the entire conversion crashes with an unhandled exception instead of emitting a `/* TODO: kind */` placeholder and continuing.

### Issue 2 — `TransformWhenNotNull` falls back to `Transform()` which can throw

```csharp
default:
    // Unknown structure; fall back to transformed C# text (best effort)
    return Transform(expr, context);
```

The comment says "best effort" but the fallback itself will throw `NotSupportedException` for any unregistered kind in the WhenNotNull chain, making the "best effort" label misleading. Only registered kinds survive.

### Issue 3 — `TransformWhenNotNull` missing `ConditionalElementAccessExpression`

The method handles `MemberBindingExpression`, `InvocationExpression`, `MemberAccessExpression`, and `ElementAccessExpression` but does not explicitly handle the case where `WhenNotNull` is itself a `ConditionalAccessExpressionSyntax` (e.g., `obj?.a?.b()`). The outer null-check would be emitted correctly, but the inner chained `?.` would fall to the `default` branch and trigger another `Transform()` call which could crash.

### Issue 4 — Singleton pattern via `Lazy<T>` is correct but `TransformWhenNotNull` is `internal`

`TransformWhenNotNull` is `internal` (assembly-level), which means only `StatementTransformer` (same assembly) can call it directly. External test assemblies cannot verify its behavior without reflection.

### Issue 5 — Null-conditional result type may be a primitive

In `TransformConditionalAccess` (called through the registry's `ControlFlowTransformer`):
```
(objExpr != null ? whenNotNull : null)
```
If `whenNotNull` evaluates to a Java primitive type (e.g., `int`, `double`), the ternary `... : null` produces a compile error in Java because primitives cannot be null. The facade should detect this and emit a box type or use `0`/`false` as the null-branch default value.

## 3. Proposed Fixes

### Fix 1 — Add a safe fallback in `Transform()` instead of throwing

```csharp
public string Transform(ExpressionSyntax node, ConversionContext context)
{
    var transformer = ExpressionTransformerRegistry.GetTransformer(node.Kind());
    if (transformer == null)
    {
        context.Diagnostics.Warning(
            $"Expression kind {node.Kind()} is not yet supported. Emitting TODO comment.",
            node.GetLocation());
        return $"/* TODO: {node.Kind()} – {node.ToFullString().Trim()} */";
    }
    return transformer.Transform(node, context);
}
```

### Fix 2 — Make `TransformWhenNotNull` handle nested conditional access

Add a `ConditionalAccessExpressionSyntax` case:
```csharp
case ConditionalAccessExpressionSyntax nested:
    var nestedObj = TransformWhenNotNull(nested.Expression, objExpr, context);
    return $"({nestedObj} != null ? {TransformWhenNotNull(nested.WhenNotNull, nestedObj, context)} : null)";
```

### Fix 3 — Fix null-conditional for primitive result types

Before emitting `... : null`, check the result type via `context.SemanticModel.GetTypeInfo()` and substitute an appropriate default:
```csharp
var resultType = context.SemanticModel?.GetTypeInfo(node).Type;
var nullBranch = (resultType?.IsValueType == true) ? GetDefaultValue(resultType) : "null";
return $"({objExpr} != null ? {whenNotNull} : {nullBranch})";
```

### Fix 4 — Make `TransformWhenNotNull` `public` or `internal` with `InternalsVisibleTo` for tests

Expose the method as `public` (or add `InternalsVisibleTo` attribute) so test projects can cover all code paths.

## 4. Commit Changes

```
fix(expression-facade): add safe TODO fallback for unregistered expression kinds and handle nested null-conditional access
```
