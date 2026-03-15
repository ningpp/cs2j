# BinaryExpressionTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs`

## 1. C# Language Feature Converted

`BinaryExpressionTransformer` converts C# binary expressions to Java binary expressions. It covers:

- Arithmetic: `+`, `-`, `*`, `/`, `%`
- Bitwise: `&`, `|`, `^`, `<<`, `>>`, `>>>` (C# 11)
- Comparison: `==`, `!=`, `<`, `>`, `<=`, `>=`
- Logical: `&&`, `||`
- Null-coalescing: `??`
- String concatenation (special case of `+`)
- User-defined operator dispatch (calls `OperatorTransformer`-named static methods)
- `as` expression (type coerce, separate from `is`)

```csharp
// C# binary expressions
int sum     = a + b;
bool same   = str1 == str2;    // needs .equals() in Java
object val  = x ?? fallback;
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — String `==`/`!=` not converted to `.equals()`

In C#, `str1 == str2` compares string **values** (overloaded). In Java, `str1 == str2` compares **references**, which almost always produces incorrect results. The transformer emits `str1 == str2` without converting to `str1.equals(str2)`. The fix requires a type-check on both operands via the semantic model:
```java
// Correct:
str1.equals(str2)   // for ==
!str1.equals(str2)  // for !=
```

### Issue 2 — `UnsignedRightShiftExpression` (`>>>`, C# 11) not registered

The expression kind `SyntaxKind.UnsignedRightShiftExpression` is not in `ExpressionTransformerRegistry`. The Java `>>>` operator (unsigned right shift) exists and is the correct mapping, but the transformer never encounters this node.

### Issue 3 — Null-coalescing `??` may evaluate the left operand twice

The generated Java for `a ?? b` is typically:
```java
a != null ? a : b
```

When `a` is a non-trivial expression (method call, property access, etc.), it is evaluated twice: once for the null check and once for the value. This is semantically incorrect when `a` has side effects. The correct Java equivalent either:
1. Uses a local variable: `(var _t = a) != null ? _t : b` (Java 14+ can do this with a local), or
2. If Java 8 compatible: extracts a temp variable in a pre-statement.

### Issue 4 — User-defined operator dispatch does not verify the method exists

When a binary expression uses a user-defined operator (detected via semantic model `GetSymbolInfo`), the transformer emits:
```java
MyClass.add(a, b)   // assumes OperatorTransformer named it 'add'
```

The name `add` is chosen by `OperatorTransformer`'s internal mapping table. If the mapping diverges (e.g., a custom `operator+` on a non-numeric type), the method may not exist in the generated Java. There is no validation that the dispatched method name matches what `OperatorTransformer` actually emits.

### Issue 5 — `as` expression is in this transformer despite being a type operation

`BinaryExpressionTransformer` handles `AsExpression` (`x as T`). Logically this belongs in `TypeOperationTransformer`. The duplication makes it hard to find and maintain the `as` handling, and may produce inconsistent results if `TypeOperationTransformer` is later updated to handle patterns.

## 3. Proposed Fixes

### Fix 1 — Convert `==`/`!=` on string operands to `.equals()`

```csharp
bool leftIsString  = IsStringType(left,  context.SemanticModel);
bool rightIsString = IsStringType(right, context.SemanticModel);
if ((leftIsString || rightIsString) && op is "==" or "!=")
{
    string eq = $"{TransformExpression(left, context)}.equals({TransformExpression(right, context)})";
    return op == "!=" ? $"!({eq})" : eq;
}
```

Handle null operands specially: `null == str` → `str == null`.

### Fix 2 — Register `UnsignedRightShiftExpression`

```csharp
// In BinaryExpressionTransformer static constructor:
ExpressionTransformerRegistry.Instance.Register(
    SyntaxKind.UnsignedRightShiftExpression, new BinaryExpressionTransformer());
```

And in the operator switch:
```csharp
SyntaxKind.UnsignedRightShiftExpression => ">>>",
```

### Fix 3 — Avoid double evaluation in `??`

```csharp
if (left is IdentifierNameSyntax or MemberAccessExpressionSyntax { ... })
    return $"{lhs} != null ? {lhs} : {rhs}";  // safe to repeat identifier
else
{
    string tmpVar = context.GenerateTempVarName();
    context.PreStatements.Add($"var {tmpVar} = {lhs};");
    return $"{tmpVar} != null ? {tmpVar} : {rhs}";
}
```

### Fix 4 — Cross-reference operator method name with `OperatorTransformer`

Extract the operator-name map from `OperatorTransformer` into a shared static dictionary, and use it from both `OperatorTransformer` and `BinaryExpressionTransformer`.

### Fix 5 — Move `as` handling to `TypeOperationTransformer`

Delete the `AsExpression` branch from `BinaryExpressionTransformer` and handle it in `TypeOperationTransformer.Transform`.

## 4. Commit Changes

```
fix(binary): convert string ==/!= to .equals(), register >>> kind, avoid double-eval in ??, share operator-name map with OperatorTransformer, and move 'as' to TypeOperationTransformer
```
