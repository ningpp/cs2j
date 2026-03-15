# UnaryExpressionTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs`

## 1. C# Language Feature Converted

`UnaryExpressionTransformer` converts C# unary expressions to Java equivalents. It handles:

| C# expression | Java equivalent |
|---|---|
| `+x` | `+x` |
| `-x` | `-x` |
| `!x` | `!x` |
| `~x` | `~x` |
| `++x` (prefix) | `++x` |
| `--x` (prefix) | `--x` |
| `x++` (postfix) | `x++` |
| `x--` (postfix) | `x--` |
| `&x` (addressof, unsafe) | `/* addressof */` |
| `*x` (pointer indirection) | `/* pointer */` |
| `^n` (from-end index) | `arr.length - n` |
| `x!` (null-forgiving) | `x` (stripped) |

```csharp
// C#
int neg   = -x;
bool inv  = !flag;
int pre   = ++counter;
int post  = counter++;

// Java
int neg   = -x;
boolean inv = !flag;
int pre   = ++counter;
int post  = counter++;
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — Postfix `++`/`--` skip user-defined operator check

For prefix `++x`, the transformer first checks whether the type has a user-defined `operator++` and, if so, emits the named Java method call (`MyType.increment(x)` etc.). For postfix `x++`, this check is missing — the transformer blindly emits `x++` even for types that have a custom increment operator. The postfix semantics (return the pre-increment value) would also need special handling in the user-defined case.

### Issue 2 — `SuppressNullableWarningExpression` (`x!`) not registered

The C# null-forgiving operator `x!` should be stripped (it is a no-op at runtime). However, `SyntaxKind.SuppressNullableWarningExpression` is not registered in `ExpressionTransformerRegistry`. When `x!` is encountered, `ExpressionTransformerFacade` throws `NotSupportedException` and the entire expression fails to convert.

### Issue 3 — `addressof` comment says "pointer member access" incorrectly

The comment emitted for `&x` reads:
```java
/* pointer member access: x */
```

"Pointer member access" is the description for `x->y` (pointer dereference + member selection). The comment for `&x` (address-of) should read:
```java
/* C# addressof expression: Java has no pointers */
```

This incorrect comment makes it harder to identify and remediate unsafe-code translations.

### Issue 4 — `^n` from-end index without known array name produces incomplete code

```csharp
Index idx = ^3;
arr[idx]         // consuming context — should produce arr[arr.length - 3]
```

When `^n` appears inside an `ElementAccessExpressionSyntax`, the transformer knows the array name and can produce `arr.length - n`. But when `^n` is a standalone expression (assigned to an `Index` variable or passed as an argument), there is no array name in scope and the transformer emits `(-3)` — which is semantically incorrect (it's a negative integer, not an index).

### Issue 5 — Unary `+` on strings is allowed in C# but not in Java

```csharp
string s = +"hello";   // valid C# (returns the string unchanged)
```

The transformer emits `+"hello"` which is a Java compile error (unary `+` is not defined for `String`). The transformer does not check operand type and should strip unary `+` on reference types.

## 3. Proposed Fixes

### Fix 1 — Apply user-defined operator check to postfix `++`/`--`

```csharp
if (IsUserDefinedIncrement(operandType, context))
{
    // Postfix semantics: save value, then call increment
    string tmp = context.GenerateTempVarName();
    context.PreStatements.Add($"var {tmp} = {operandText};");
    context.PostStatements.Add($"{operandText} = {typeName}.increment({operandText});");
    return tmp;  // return the saved (pre-increment) value
}
return $"{operandText}++";
```

### Fix 2 — Register `SuppressNullableWarningExpression` as a pass-through

```csharp
ExpressionTransformerRegistry.Instance.Register(
    SyntaxKind.SuppressNullableWarningExpression,
    new PassThroughTransformer(node =>
        ((PostfixUnaryExpressionSyntax)node).Operand));
```

Or handle within `UnaryExpressionTransformer`:
```csharp
case SyntaxKind.SuppressNullableWarningExpression:
    return TransformExpression(((PostfixUnaryExpressionSyntax)node).Operand, context);
```

### Fix 3 — Fix the `addressof` comment

```csharp
case SyntaxKind.AddressOfExpression:
    return $"/* C# addressof — no Java equivalent: {operandText} */";
```

### Fix 4 — Emit a placeholder for standalone `^n` index

```csharp
case SyntaxKind.IndexExpression:
    if (context.CurrentArrayName != null)
        return $"{context.CurrentArrayName}.length - {n}";
    return $"/* C# from-end index ^{n} — requires array name to resolve */";
```

### Fix 5 — Strip unary `+` from non-numeric operands

```csharp
case SyntaxKind.UnaryPlusExpression:
    var operandType = context.SemanticModel.GetTypeInfo(node.Operand).Type;
    if (operandType?.SpecialType is not (SpecialType.System_Int32 or SpecialType.System_Double or ...))
        return TransformExpression(node.Operand, context);  // strip '+'
    return $"+{TransformExpression(node.Operand, context)}";
```

## 4. Commit Changes

```
fix(unary): apply user-defined operator check to postfix ++/--, register SuppressNullableWarning as passthrough, fix addressof comment, annotate standalone ^n index, and strip unary + on non-numeric types
```
