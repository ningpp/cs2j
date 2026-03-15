# OperatorTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Member/OperatorTransformer.cs`

## 1. C# Language Feature Converted

`OperatorTransformer` converts C# user-defined operator declarations to static Java methods. C# allows classes to define operators such as `+`, `-`, `*`, `/`, `==`, `!=`, `<`, `>`, and so on. Java has no operator-overloading mechanism, so they are mapped to named static methods.

The transformer also handles conversion operators (`implicit operator` and `explicit operator`).

```csharp
// C#
public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.X + b.X, a.Y + b.Y);
public static bool    operator ==(Vector2 a, Vector2 b) => a.X == b.X && a.Y == b.Y;

// Java
public static Vector2 add(Vector2 a, Vector2 b)        { return new Vector2(a.X + b.X, a.Y + b.Y); }
public static boolean equals_(Vector2 a, Vector2 b)    { return a.X == b.X && a.Y == b.Y; }
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — `ConversionOperatorDeclarationSyntax` (`implicit`/`explicit operator`) not handled

C# conversion operators are declared with `ConversionOperatorDeclarationSyntax`:
```csharp
public static implicit operator double(Temperature t) => t.Celsius;
public static explicit operator int(Temperature t)    => (int)t.Celsius;
```

`OperatorTransformer` dispatches on `OperatorDeclarationSyntax` only. Conversion operators use a different syntax node type and are never dispatched to this transformer: they fall through to a `/* TODO */` comment or are silently dropped. The fix is to handle `ConversionOperatorDeclarationSyntax` in its own branch and emit a correctly-named `toDouble()` / `toInt()` static method.

### Issue 2 — `operator ==` renamed to `equals` conflicts with `Object.equals(Object)`

The name mapping `operator ==` → `equals` produces:
```java
public static boolean equals(Vector2 a, Vector2 b) { ... }
```

This creates two problems:
1. It shadows `Object.equals(Object obj)` (instance method) without satisfying the override contract (wrong signature — two params, both typed, vs one `Object` param).
2. The static `equals` breaks `Map`, `Set`, and other JDK data structures that rely on the instance `equals`/`hashCode` contract.

The operator should be renamed to `equalTo` or `valueEquals` to avoid the collision, and the class should additionally override the instance `equals` and `hashCode` methods.

### Issue 3 — `>>>` (unsigned right-shift, C# 11) not in the operator-name map

C# 11 added an unsigned right-shift operator `>>>`. The operator-name map contains entries for `>>` → `rightShift` and `<<` → `leftShift` but not for `>>>`. If the source uses `operator>>>`, the transformer falls through to a default case that may emit `op_UnsignedRightShift` (the CIL name) which is not a valid Java identifier.

### Issue 4 — `checked` operator variants (C# 11) not handled

C# 11 introduced `checked` arithmetic operators:
```csharp
public static Vector2 operator checked +(Vector2 a, Vector2 b) { ... }
```

The `CheckedKeyword` modifier on an operator has no equivalent in Java (where checked arithmetic requires `Math.addExact` etc.). The transformer does not detect or handle this modifier; unchecked and checked variants with the same symbol generate two methods with the same Java name, causing a compile error.

### Issue 5 — Unary `operator !` maps to `not` but Java reserves `!` for booleans only

The C# `operator !` on a non-boolean type (e.g., a custom bit-vector class) maps to a Java method named `not`. This is fine. However, the transformer does not verify that the return type is consistent (e.g., C# `bool operator !(MyFlags f)` should return `boolean` in Java). If the type-mapping for the return type diverges, the operator may emit an incorrect return type.

## 3. Proposed Fixes

### Fix 1 — Handle `ConversionOperatorDeclarationSyntax`

```csharp
if (memberDecl is ConversionOperatorDeclarationSyntax conv)
{
    string targetType  = MapType(conv.Type, context);
    string methodName  = $"to{char.ToUpper(targetType[0])}{targetType[1..]}"; // e.g., toDouble
    var javaMethod     = BuildStaticMethod(methodName, targetType, conv.ParameterList, conv.Body, context);
    javaMethod.LeadingComment = conv.ImplicitOrExplicitKeyword.IsKind(SyntaxKind.ExplicitKeyword)
        ? "// C# explicit conversion operator"
        : "// C# implicit conversion operator";
    return javaMethod;
}
```

### Fix 2 — Rename `operator ==` to `valueEquals` and emit instance `equals`/`hashCode`

```csharp
"==" => "valueEquals",
"!=" => "notEquals",
```

After generating the static `valueEquals` method, also emit:
```java
@Override public boolean equals(Object obj) {
    if (!(obj instanceof MyClass)) return false;
    return valueEquals(this, (MyClass) obj);
}
@Override public int hashCode() { return Objects.hash(/* fields */); }
```

### Fix 3 — Add `>>>` to the operator-name map

```csharp
">>>" => "unsignedRightShift",
```

### Fix 4 — Suffix `checked` operator names to avoid duplicate

```csharp
if (operatorDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.CheckedKeyword)))
    javaName += "Checked";  // addChecked, subtractChecked, etc.
```

### Fix 5 — Verify unary operator return type consistency

After building the method, check that the declared return type matches the mapped C# return type:
```csharp
Debug.Assert(javaMethod.ReturnType == MapType(operatorDecl.ReturnType, context),
    $"Operator return type mismatch: {operatorDecl}");
```

## 4. Commit Changes

```
fix(operator): handle implicit/explicit conversion operators, rename == to valueEquals with instance equals/hashCode, add >>> to name map, suffix checked variants, and verify return type consistency
```
