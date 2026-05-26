# C# byte/sbyte Conversion Fix — Design Spec

**Date**: 2026-05-26
**Status**: Approved

## Problem

C# `byte` is unsigned (0-255) and Java `byte` is signed (-128 to 127). The converter currently maps both C# `byte` and `sbyte` to Java `byte`, which is incorrect for C# `byte` — values above 127 are silently corrupted, arithmetic produces wrong results, and method parameters lose the unsigned semantics.

Additionally, C# `sbyte` (-128 to 127) maps correctly to Java `byte` by type, but arithmetic overflow and narrowing behavior may still be incorrect in certain contexts.

## Solution Summary

| C# type | Java type | Rationale |
|---|---|---|
| `byte` (System.Byte, unsigned 8-bit) | `int` (signed 32-bit) | Preserves full 0-255 range. Required by user. |
| `sbyte` (System.SByte, signed 8-bit) | `byte` (signed 8-bit) | Exact range match. Unchanged. |
| `byte[]` | `byte[]` | Stays as Java `byte[]` for API compatibility. Element reads get `& 0xFF` to unsigned-extend to `int`. |

### Correctness constraint

All C# `byte` arithmetic that narrows back to `byte` must preserve wrap-at-256 semantics via `& 0xFF` masking. No exceptions.

---

## Section 1: Type Mapping Changes

Every mapping point in the codebase must be updated:

### 1.1 config/TypeMappings.json

```json
{ "csharp": "System.Byte",  "java": "int",  "imports": [] }
```
(System.SByte unchanged: `"java": "byte"`)

### 1.2 TypeMappingService.cs

| Method | Change |
|---|---|
| `MapSimpleTypeName` | `"Byte"` → `"int"` |
| `MapTypeFromSyntaxString` | C# keyword `"byte"` → `"int"` |
| `BoxPrimitive` | Ensure C# `byte` routes through `int` → `Integer` |
| Nullable handling | `Byte?` → `Integer` |

### 1.3 ExpressionTransformerHelpers.cs

| Method | Change |
|---|---|
| `GetJavaWrapperType` | C# `"byte"` → `"Integer"` (was `"short"`) |
| `IsPrimitiveSpecialTypeForArrayStream` | Add `System_Byte` |

### 1.4 IdentifierExpressionTransformer.cs

Boxed class mapping: `"Byte"` → `("int", "Integer")`

### 1.5 TypeOperationTransformer.cs

| Expression | Was | Now |
|---|---|---|
| `default(byte)` | `(byte)0` | `0` |
| `sizeof(byte)` | `1` | `4` |

### 1.6 LowerRefOut.cs / HolderTypeResolver.cs

`ByteHolder` → `IntHolder` for ref/out `byte` parameters.

### 1.7 AssignmentTransformer.cs

`GetJavaFieldDefault("byte")` → `"0"` (was `"(byte)0"`)

---

## Section 2: Narrowing Masking (`& 0xFF`)

**Rule**: Whenever a wider-type expression is narrowed to a C# `byte` target, emit `(expr) & 0xFF`.

### 2a. Explicit casts

```
C#:  byte c = (byte)(a + b);
Java: int c = (a + b) & 0xFF;
```

### 2b. Assignment from wider expression

```
C#:  byte x = someIntMethod();
Java: int x = someIntMethod() & 0xFF;
```

### 2c. Method parameter passing (narrowing)

```
C#:  TakesByte((byte)someInt);
Java: takesByte(someInt & 0xFF);
```

### 2d. Array element writes

```
C#:  buf[i] = (byte)(a + b);
Java: buf[i] = (byte)((a + b) & 0xFF);   // byte[] stays byte[], so Java byte cast + mask
```

### 2e. Array element reads

```
C#:  byte b = buf[i];
Java: int b = buf[i] & 0xFF;   // unsigned-extend byte→int
```

### 2f. Literals that don't need masking

```
C#:  byte x = 42;     →  int x = 42;       (fits in 0-255, no cast)
C#:  byte x = 0xFF;   →  int x = 0xFF;     (fits, no mask)
C#:  byte x = (byte)300; → int x = 300 & 0xFF;  (explicit narrowing, mask required)
```

### Implementation

New helper method in `ExpressionTransformerHelpers`:
```csharp
public static string MaskByte(string expr, bool isByteTarget)
    => isByteTarget ? $"({expr}) & 0xFF" : expr;
```

Called by: `BinaryExpressionTransformer`, `AssignmentTransformer`, `ArgumentTransformer`, `TypeOperationTransformer`.

---

## Section 3: Parameter Passing & Method Signatures

### 3.1 Method declarations

```
C#:  void Process(byte id, sbyte delta, byte[] data) { }
Java: void process(int id, byte delta, byte[] data) { }
```

### 3.2 Calling converted methods

Both caller and callee use the converted types — no special handling needed.

### 3.3 Calling Java library methods with `byte` parameter

When a C# `byte` value (now Java `int`) is passed to a Java API expecting `byte` (e.g., `OutputStream.write(byte)`), the converter must insert a narrowing cast:

```
C#:  stream.WriteByte(b);
Java: outputStream.write((byte)(b & 0xFF));
```

Uses existing `AdaptExpressionToTargetType` logic. Detection: source expression is C# `byte`-typed (int in Java), target parameter is Java `byte` (from a Java library method or converted sbyte).

### 3.4 ref/out parameters

```
C#:  void Modify(ref byte b) { b = 255; }
Java: void modify(IntHolder b) { b.value = 255; }
```

### 3.5 Return types

```
C#:  byte GetMax() => 255;
Java: int getMax() { return 255; }
```

---

## Section 4: Edge Cases

### 4.1 Enum underlying type

No change needed. `GetEnumValueJavaType` already maps `System_Byte` → `"int"`.

### 4.2 byte.MaxValue / byte.MinValue

```
byte.MaxValue → 255
byte.MinValue → 0
sbyte.MaxValue → Byte.MAX_VALUE
sbyte.MinValue → Byte.MIN_VALUE
```

### 4.3 Nullable byte

```
byte? x = null;  →  Integer x = null;
byte? y = 200;   →  Integer y = 200;
```

### 4.4 Checked context

Not handled. C# `checked` blocks are rare and Java has no direct equivalent. Unchecked overflow is the C# default and matches Java behavior.

### 4.5 sbyte arithmetic

C# `sbyte` → Java `byte`. Arithmetic promotion to `int` is the same in both languages. Narrowing back to `sbyte` in C# wraps at 256 (same as Java `byte`). No special masking needed — Java wrapping matches C# unchecked wrapping for the signed 8-bit range.

---

## Section 5: Test Cases

| # | Scenario | C# Input | Expected Java Output |
|---|---|---|---|
| 1 | byte decl | `byte x = 200;` | `int x = 200;` |
| 2 | sbyte decl | `sbyte y = -100;` | `byte y = -100;` |
| 3 | byte arith + cast | `byte c = (byte)(a + b);` | `int c = (a + b) & 0xFF;` |
| 4 | byte arith → int | `int c = a + b;` | `int c = a + b;` |
| 5 | plain literal | `byte x = 42;` | `int x = 42;` |
| 6 | hex literal | `byte x = 0xFF;` | `int x = 0xFF;` |
| 7 | large literal w/ cast | `byte x = (byte)300;` | `int x = 300 & 0xFF;` |
| 8 | array read | `byte b = buf[0];` | `int b = buf[0] & 0xFF;` |
| 9 | array write | `buf[i] = (byte)v;` | `buf[i] = (byte)(v & 0xFF);` |
| 10 | array decl | `byte[] buf = new byte[1024];` | `byte[] buf = new byte[1024];` |
| 11 | method param | `void Foo(byte b) { }` | `void foo(int b) { }` |
| 12 | method call to Java API | `stream.WriteByte(b);` | `outputStream.write((byte)(b & 0xFF));` |
| 13 | ref byte | `void M(ref byte b)` | `void m(IntHolder b)` |
| 14 | nullable byte | `byte? x = null;` | `Integer x = null;` |
| 15 | sbyte stays | `sbyte a = -1;` | `byte a = -1;` |
| 16 | default(byte) | `byte x = default;` | `int x = 0;` |
| 17 | byte.MaxValue | `var x = byte.MaxValue;` | `int x = 255;` |
| 18 | sizeof(byte) | `int s = sizeof(byte);` | `int s = 4;` |
| 19 | compound assign | `b += 1;` (b is byte) | `b = (b + 1) & 0xFF;` |
| 20 | ternary (same-type branches) | `byte x = cond ? a : b;` | `int x = cond ? a : b;` (both a,b are int, no mask) |
| 21 | ternary (wider branch) | `byte x = cond ? a : someInt;` | `int x = (cond ? a : someInt) & 0xFF;` (wider→byte narrowing) |

---

## Section 6: Files Changed

1. `config/TypeMappings.json`
2. `src/CSharpToJava.Core/Context/TypeMappingService.cs`
3. `src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs`
4. `src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs`
5. `src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs`
6. `src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs`
7. `src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.cs`
8. `src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs`
9. `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`
10. `src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs`
11. `src/CSharpToJava.Core/Java/Rewriters/ImplicitCastCompletionRewriter.cs`
12. `src/CSharpToJava.Core/Lowering/LowerRefOut.cs`
13. `src/CSharpToJava.Core/Transformers/HolderTypeResolver.cs`
14. `src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs`
15. `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs`
16. `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs`
