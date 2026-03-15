# ControlFlowTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs`

## 1. C# Language Feature Converted

`ControlFlowTransformer` converts C# control-flow and special-form expressions that are part of expressions (not statements). It handles:

| C# Expression | Java Equivalent |
|---|---|
| Ternary `a ? b : c` | `a ? b : c` |
| `null`-conditional `a?.b`, `a?[i]` | `a != null ? a.b : null` |
| `await expr` | `expr.join()` / CompletableFuture chain |
| `throw expr` (C# 7 throw expression) | `throw` in conditional context |
| Switch expression `x switch { ... }` | Java 14+ `switch` expression |
| `this` / `base` literals | `this` / `super` |
| `with` expression (record copy-and-update) | `/* TODO */` |
| Index/range `a[1..3]`, `^n` | `/* TODO */` (Range) |
| `SuppressNullableWarningExpression` `x!` | stripped (pass-through) |

## 2. Deep Analysis of Current Code Issues

### Issue 1 — `WithExpression` record copy is unimplemented

C# 9 `with` creates a modified copy of a record:
```csharp
var p2 = p1 with { X = 5 };
```

The transformer emits `/* TODO: with expression */`. In Java this requires a clone + setter pattern:
```java
// var p2 = p1.clone(); p2.setX(5); — emitted as multiple statements
```

Since `with` is an expression, generating the multi-statement Java requires a pre-statement injection before the expression site, which `ConversionContext` does not currently support.

### Issue 2 — `RangeExpression` is unimplemented

C# 8 ranges:
```csharp
var slice = arr[1..3];
var last3 = arr[^3..];
```

These emit `/* TODO: range */`. Java (21+) has `Arrays.copyOfRange` but no language-level range syntax. At minimum, for `arr[a..b]`, the transformer should emit `Arrays.copyOfRange(arr, a, b)`.

### Issue 3 — `SuppressNullableWarningExpression` (`x!`) is not registered

The `!` null-forgiving operator in C# is a no-op at runtime (purely a null-safety analysis hint). It should be stripped and the inner expression emitted. However, `SyntaxKind.SuppressNullableWarningExpression` is not registered in `ExpressionTransformerRegistry`, so encountering `x!` throws `NotSupportedException`.

### Issue 4 — Null-conditional with primitive result type produces invalid Java

```csharp
int? len = str?.Length;   // nullable int in C#
```

The transformer emits:
```java
Integer len = str != null ? str.length() : null;
```

This is correct because `Integer` (boxed) can be `null`. However, if the context expects a primitive `int` (e.g., as an argument to a method that takes `int`):
```java
someMethod(str != null ? str.length() : null)  // compile error: null cannot be unboxed
```

The transformer does not check the consuming context.

### Issue 5 — Switch expression arms with pattern guards may not translate completely

```csharp
int result = x switch {
    > 0 and < 10 => 1,
    { Length: > 5 } => 2,  // property pattern
    _ => 0
};
```

Relational patterns (`> 0`), combined patterns (`and`, `or`), and property patterns (`{ Prop: val }`) emit `/* TODO: pattern */` or fall through to a basic string representation. Java 21+ supports enhanced-form patterns in switch, but the transformer's switch-arm translator handles only `ConstantPatternSyntax` and `DiscardPatternSyntax`.

## 3. Proposed Fixes

### Fix 1 — Implement `with` expression via pre-statement injection

Add `PreStatements` injection to `ConversionContext`:
```csharp
string tmpVar = context.GenerateTempVarName();
context.PreStatements.Add($"var {tmpVar} = {TransformExpression(withExpr.Expression, context)}.clone();");
foreach (var init in withExpr.Initializer.Expressions)
{
    var name = ((AssignmentExpressionSyntax)init).Left.ToString();
    var val  = TransformExpression(((AssignmentExpressionSyntax)init).Right, context);
    context.PreStatements.Add($"{tmpVar}.set{char.ToUpperInvariant(name[0])}{name[1..]}({val});");
}
return tmpVar;
```

### Fix 2 — Implement `RangeExpression` for common array-slice case

```csharp
if (rangeExpr.LeftOperand == null && rangeExpr.RightOperand == null)
    return "/* full range — use as-is */";
string lo = rangeExpr.LeftOperand != null ? TransformExpression(rangeExpr.LeftOperand, context) : "0";
string hi = rangeExpr.RightOperand != null ? TransformExpression(rangeExpr.RightOperand, context) : "/* length */";
return $"Arrays.copyOfRange(_arr, {lo}, {hi})";  // context must know the array name
```

### Fix 3 — Register `SuppressNullableWarningExpression` as a pass-through

```csharp
ExpressionTransformerRegistry.Instance.Register(
    SyntaxKind.SuppressNullableWarningExpression,
    new PassThroughTransformer(n => ((PostfixUnaryExpressionSyntax)n).Operand));
```

### Fix 4 — Detect primitive-consuming context for null-conditional results

After generating the null-conditional expression, check whether the result type is a primitive via the semantic model. If so, emit `0` (or the type's default) instead of `null` as the false branch:
```java
str != null ? str.length() : 0   // safe for int context
```

### Fix 5 — Extend switch-arm pattern translation to relational/combined patterns

```csharp
case RelationalPatternSyntax rel:
    return $"case {TransformRelational(rel, switchVar, context)}:";
case BinaryPatternSyntax bin when bin.IsKind(SyntaxKind.AndPattern):
    // Java 21+: case GuardedPattern
```

## 4. Commit Changes

```
fix(controlflow): implement with expression via clone+setters, map RangeExpression to Arrays.copyOfRange, register SuppressNullableWarning as passthrough, guard primitive context in null-conditional, and extend switch pattern translation
```
