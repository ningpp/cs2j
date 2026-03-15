# ExpressionTransformerHelpers

**Source:** `src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs`

## 1. C# Language Feature Converted

`ExpressionTransformerHelpers` is a **static utility class** that does not directly handle any C# syntax node. It provides shared helper methods used across multiple specialized expression transformers:

- `IsNumericLiteral(string expr)` — detects whether a string is a numeric literal (used to decide widening for `Double`/`Float` field initializers).
- `IsSystemPrimitiveType(string typeName)` — checks if a fully-qualified or short type name is a .NET/Java system primitive.
- `IsCSharpPrimitiveType(string typeName)` — checks C# primitive keywords.
- `BoxedTypeName(PredefinedTypeSyntax)` — maps C# predefined type keywords to Java boxed class names (`bool` → `Boolean`, `int` → `Integer`, etc.).
- `GetJavaWrapperType(string csharpPrimitive)` — maps a primitive short name to its Java wrapper class name.
- `IsJavaWrapperType(string typeName)` — checks whether a name is a Java wrapper type.

## 2. Deep Analysis of Current Code Issues

### Issue 1 — `IsNumericLiteral` regex misses hex and binary literals

The current regex:
```csharp
@"^-?\d+(\.\d+)?[LlfFdD]?$"
```

This pattern does not match:
- **Hex literals**: `0xFF`, `0xDEAD_BEEF` — `\d` only matches `[0-9]`, not `a-f/A-F` or the `x` prefix.
- **Binary literals**: `0b1010_1100` — `0b` prefix and `_` separators are not covered.
- **Unsigned suffixes**: `42UL`, `100U` — `U` and `UL` suffixes are not in the character class.

As a result, `FieldTransformer` will fail to detect that a hex/binary literal initializer on a `Double` or `Float` field needs integer widening (e.g., `double x = 0xFF;` should emit `0xFF.0` or `(double) 0xFF`).

### Issue 2 — `BoxedTypeName` logic is duplicated in `IdentifierExpressionTransformer`

`IdentifierExpressionTransformer.BoxedTypeName(PredefinedTypeSyntax)` contains nearly identical logic to `ExpressionTransformerHelpers.BoxedTypeName`. Two copies means any correction must be applied in two places.

### Issue 3 — `GetJavaWrapperType` maps `sbyte` → `Byte`, losing signedness information

```csharp
"sbyte" => "Byte",
```

Java's `byte` is **signed** (range -128..127) while C#'s `byte` is **unsigned** (range 0..255). C#'s `sbyte` is signed like Java's `byte`. The mapping `sbyte → Byte` is technically correct for signed 8-bit values, but the reverse mapping `byte → Byte` (C# unsigned → Java signed) silently changes value semantics for values > 127. The issue should be documented and a diagnostic should be emitted when an unsigned `byte` value > 127 is detected.

### Issue 4 — `IsJavaWrapperType` implementation not shown in the read excerpt

The method signature is public but its body was cut off in the source read. If this method lists wrapper types by string comparison (like the others), it likely has the same problem as `GetJavaWrapperType` — `"Byte"` appears but the documentation around signed/unsigned semantics is missing.

### Issue 5 — No null/empty guard in `IsNumericLiteral`

```csharp
public static bool IsNumericLiteral(string expr)
{
    return System.Text.RegularExpressions.Regex.IsMatch(expr.Trim(), @"^-?\d+...");
}
```

If `expr` is `null`, `expr.Trim()` throws `NullReferenceException`. A null check or `string.IsNullOrWhiteSpace` guard is missing.

## 3. Proposed Fixes

### Fix 1 — Extend `IsNumericLiteral` to cover hex, binary, and unsigned literals

```csharp
private static readonly Regex _numericLiteralPattern = new Regex(
    @"^[+-]?(0[xX][0-9a-fA-F_]+|0[bB][01_]+|\d[\d_]*)([uU][lL]?|[lL][uU]?|[fF]|[dD]|[mM])?$",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);

public static bool IsNumericLiteral(string? expr)
{
    if (string.IsNullOrWhiteSpace(expr)) return false;
    return _numericLiteralPattern.IsMatch(expr.Trim());
}
```

Also compile the regex once as a static `readonly` field to avoid recompilation on every call.

### Fix 2 — Remove the duplicate `BoxedTypeName` in `IdentifierExpressionTransformer`

Replace the local implementation in `IdentifierExpressionTransformer` with a call to `ExpressionTransformerHelpers.BoxedTypeName(...)`:

```csharp
// IdentifierExpressionTransformer.BoxedTypeName — DELETE
// Use instead:
private string BoxedTypeName(PredefinedTypeSyntax node)
    => ExpressionTransformerHelpers.BoxedTypeName(node);
```

### Fix 3 — Add diagnostic for `byte` (unsigned) values in `GetJavaWrapperType`

```csharp
"byte" => "short",  // Java byte is signed; use short to preserve 0-255 range in Java
"sbyte" => "byte",  // Java byte is signed, same as C# sbyte
```

Alternatively, emit a compile-time warning via `context.Diagnostics` whenever a `byte` field exceeds 127.

### Fix 4 — Add null safety guards to all public helpers

Apply `string? expr` parameter types and null/whitespace checks at the top of each method.

## 4. Commit Changes

```
fix(helpers): fix IsNumericLiteral to cover hex/binary/unsigned literals, deduplicate BoxedTypeName, and add null guards
```
