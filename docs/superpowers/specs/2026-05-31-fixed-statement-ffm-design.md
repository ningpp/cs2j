# Fixed Statement FFM Conversion Design

## Overview

Systematically convert C# `fixed` statements and related pointer operations to Java FFM (Foreign Function & Memory) API calls in the old pipeline. Support all primitive type pointers (byte/sbyte/char/short/ushort/int/uint/long/ulong/float/double/bool), with special attention to byte (unsigned vs signed) and char semantics.

## Current State

All `fixed`/`unsafe` constructs produce error diagnostics and TODO comment placeholders:

- `fixed` statement → `/* TODO: Fixed statement - manual conversion required */` (error)
- `unsafe` statement → `/* TODO: Unsafe statement - manual conversion required */` (error)
- `fixed byte buffer[256]` field → error diagnostic
- `&` (address-of) → comment placeholder (warning)
- `*p` (pointer deref) → comment + operand (warning)
- `p->m` (pointer member access) → `p.m` + warning

## Design Decisions

1. **Approach**: Direct FFM translation (no compat library wrapper classes)
2. **Scope**: All primitive type pointers
3. **Unsafe blocks**: Strip `unsafe` modifier only, convert inner code normally
4. **Fixed-size buffer fields**: Convert to `MemorySegment` fields with `Arena.ofAuto()` allocation

## Core Mapping: C# fixed → Java FFM

### Fixed Statement Patterns

| C# Pattern | Java FFM Equivalent |
|---|---|
| `fixed (byte* p = array)` | `MemorySegment p = MemorySegment.ofArray(array)` |
| `fixed (char* p = str)` | `MemorySegment p = MemorySegment.ofArray(str.toCharArray())` |
| `fixed (int* p = &array[0])` | `MemorySegment p = MemorySegment.ofArray(array)` |
| `fixed (byte* p = null)` | `MemorySegment p = MemorySegment.NULL` |
| `fixed (byte* p1 = a, p2 = b)` | Two `MemorySegment` variable declarations |

### Pointer Operations

| C# Operation | Java FFM | Notes |
|---|---|---|
| `*p` (deref read) | `p.get(LAYOUT, 0)` | Unsigned types need mask suffix |
| `*p = val` (deref write) | `p.set(LAYOUT, 0, val)` | Unsigned types need cast |
| `p[i]` (index read) | `p.get(LAYOUT, i * SIZE)` | Unsigned types need mask suffix |
| `p[i] = val` (index write) | `p.set(LAYOUT, i * SIZE, val)` | Unsigned types need cast |
| `p + n` (pointer add) | `p.asSlice(n * SIZE)` | Returns new MemorySegment |
| `p - n` (pointer sub) | `p.asSlice(-n * SIZE)` | Returns new MemorySegment |
| `p++` / `++p` | `p = p.asSlice(SIZE)` | Reassignment |
| `p--` / `--p` | `p = p.asSlice(-SIZE)` | Reassignment |
| `p1 - p2` (pointer diff) | `(p1.address() - p2.address()) / SIZE` | |
| `p == null` | `p.equals(MemorySegment.NULL)` | |
| `p != null` | `!p.equals(MemorySegment.NULL)` | |

### Type Layout Mapping

| C# Type | ValueLayout Constant | sizeof | Read Mask | Write Cast |
|---|---|---|---|---|
| `byte` | `JAVA_BYTE` | 1 | `& 0xFF` | `(byte)` |
| `sbyte` | `JAVA_BYTE` | 1 | none | none |
| `char` | `JAVA_CHAR` | 2 | none | none |
| `short` | `JAVA_SHORT` | 2 | none | none |
| `ushort` | `JAVA_CHAR` | 2 | `& 0xFFFF` | `(char)` |
| `int` | `JAVA_INT` | 4 | none | none |
| `uint` | `JAVA_INT` | 4 | `& 0xFFFFFFFFL` | `(int)` |
| `long` | `JAVA_LONG` | 8 | none | none |
| `ulong` | `JAVA_LONG` | 8 | none | none |
| `float` | `JAVA_FLOAT` | 4 | none | none |
| `double` | `JAVA_DOUBLE` | 8 | none | none |
| `bool` | `JAVA_BOOLEAN` | 1 | none | none |

### Byte Special Handling

C# `byte` is unsigned (0-255) and maps to Java `int` in the existing type system. However, FFM `MemorySegment.get(JAVA_BYTE)` returns Java `byte` (signed, -128 to 127). The conversion must bridge this gap:

```java
// C#: byte val = *p;
// Java: int val = p.get(ValueLayout.JAVA_BYTE, 0) & 0xFF;

// C#: *p = val;  (val is C# byte → Java int)
// Java: p.set(ValueLayout.JAVA_BYTE, 0, (byte) val);

// C#: byte val = p[i];
// Java: int val = p.get(ValueLayout.JAVA_BYTE, i) & 0xFF;
// Note: byte offset for JAVA_BYTE is just i (1 byte per element)

// C#: p[i] = val;
// Java: p.set(ValueLayout.JAVA_BYTE, i, (byte) val);
```

For `sbyte*`, no masking is needed because C# `sbyte` maps to Java `byte` directly:

```java
// C#: sbyte val = *p;
// Java: byte val = p.get(ValueLayout.JAVA_BYTE, 0);

// C#: *p = val;
// Java: p.set(ValueLayout.JAVA_BYTE, 0, val);
```

### Char Handling

C# `char` and Java `char` are both unsigned 16-bit UTF-16. Direct mapping:

```java
// C#: char val = *p;
// Java: char val = p.get(ValueLayout.JAVA_CHAR, 0);

// C#: fixed (char* p = str) { ... }
// Java: MemorySegment p = MemorySegment.ofArray(str.toCharArray());
// Note: Java String is immutable; toCharArray() creates a copy.
// This differs from C# which pins the actual string buffer.
```

### Fixed-Size Buffer Fields

C# `fixed byte buffer[256]` in a struct/class becomes:

```java
private MemorySegment buffer;

// In constructor or instance initializer:
buffer = Arena.ofAuto().allocate(256, ValueLayout.JAVA_BYTE);
```

`Arena.ofAuto()` provides automatic cleanup via Cleaner when the arena becomes unreachable. This avoids requiring the class to implement `AutoCloseable`.

For multi-element types:
- `fixed char buffer[128]` → `Arena.ofAuto().allocate(128 * 2, ValueLayout.JAVA_CHAR)` (128 chars × 2 bytes each)
- `fixed int buffer[64]` → `Arena.ofAuto().allocate(64 * 4, ValueLayout.JAVA_INT)` (64 ints × 4 bytes each)

## Implementation Architecture (Old Pipeline)

### New Files

**`src/CSharpToJava.Core/Transformers/Expression/Utilities/FfmHelper.cs`**

Central utility for FFM code generation:

```
FfmHelper
├── GetValueLayoutName(string csharpElementType) → string
├── GetElementSize(string csharpElementType) → int
├── NeedsUnsignedMask(string csharpElementType) → bool
├── GetMaskSuffix(string csharpElementType) → string
├── GetWriteCast(string csharpElementType) → string
├── GenerateMemorySegmentInit(pointerInfo, initializer, context) → string
├── GeneratePointerRead(segmentExpr, layout, offset, elementType) → string
├── GeneratePointerWrite(segmentExpr, layout, offset, value, elementType) → string
├── GeneratePointerArithmetic(segmentExpr, offset, elementType) → string
└── GetRequiredImports() → string[]
```

### Modified Files

**`src/CSharpToJava.Core/Context/ConversionContext.cs`**

Add fixed scope tracking:

```
+ public class FixedPointerInfo
+ {
+     public string VariableName;
+     public string CSharpElementTypeName;
+     public string ValueLayoutName;
+     public int ElementSize;
+     public bool NeedsUnsignedMask;
+     public string MaskSuffix;
+     public string WriteCast;
+ }
+
+ public Stack<List<FixedPointerInfo>> FixedScopeStack = new();
+ public bool IsInFixedScope => FixedScopeStack.Count > 0;
+ public FixedPointerInfo? FindPointerInfo(string varName);
```

**`src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs`**

- `TransformFixedStatement()`: Replace stub with full FFM conversion
- `TransformUnsafeStatement()`: Replace stub with inner block conversion (strip unsafe modifier)

**`src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs`**

- `TransformPointerIndirection()`: Generate `segment.get(layout, offset)` with mask when in fixed scope
- `TransformAddressOf()`: Generate `MemorySegment.ofArray().asSlice()` when in fixed scope

**`src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`**

- `TransformPointerMemberAccess()`: Improved warning when in fixed scope (struct layout not yet supported)

**`src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs`** (if pointer arithmetic exists)

- Handle pointer arithmetic in binary expressions when in fixed scope

**`src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs`**

- Fixed-size buffer field: Generate `MemorySegment` field + Arena initialization

**`src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerRegistry.cs`** or relevant dispatcher

- Pointer index access (`p[i]`): Detect pointer-typed element access and generate `segment.get(layout, offset)`

### Import Management

When any FFM code is generated, add required imports:

```java
import java.lang.foreign.MemorySegment;
import java.lang.foreign.ValueLayout;
import java.lang.foreign.Arena;  // only when Arena is used (fixed-size buffer fields)
```

Track these through the existing import collection mechanism in the old pipeline.

### Pointer Index Access Detection

C# pointer index access `p[i]` appears as `ElementAccessExpressionSyntax` in Roslyn. Distinguish from array access by checking the expression type via semantic model:

- If the expression type is a pointer type → generate `segment.get(layout, offset)`
- If the expression type is an array type → generate `arr[i]` (existing behavior)

## Unit Tests

### Test File

`tests/CSharpToJava.Tests/FixedStatementFfmTests.cs`

### Test Cases (31 total)

#### A. Fixed Statement Basic Conversion (8 tests)

1. `FixedBytePointer_Array` — `fixed (byte* p = arr)` → `MemorySegment.ofArray(arr)`
2. `FixedSBytePointer_Array` — `fixed (sbyte* p = arr)` → `MemorySegment.ofArray(arr)`
3. `FixedCharPointer_Array` — `fixed (char* p = arr)` → `MemorySegment.ofArray(arr)`
4. `FixedShortPointer_Array` — `fixed (short* p = arr)` → `MemorySegment.ofArray(arr)`
5. `FixedIntPointer_Array` — `fixed (int* p = arr)` → `MemorySegment.ofArray(arr)`
6. `FixedLongPointer_Array` — `fixed (long* p = arr)` → `MemorySegment.ofArray(arr)`
7. `FixedFloatPointer_Array` — `fixed (float* p = arr)` → `MemorySegment.ofArray(arr)`
8. `FixedDoublePointer_Array` — `fixed (double* p = arr)` → `MemorySegment.ofArray(arr)`

#### B. Byte Special Handling (6 tests)

9. `FixedBytePointer_DerefRead` — `byte v = *p` → `p.get(ValueLayout.JAVA_BYTE, 0) & 0xFF`
10. `FixedBytePointer_DerefWrite` — `*p = 42` → `p.set(ValueLayout.JAVA_BYTE, 0, (byte) 42)`
11. `FixedBytePointer_IndexRead` — `byte v = p[i]` → `p.get(ValueLayout.JAVA_BYTE, i) & 0xFF`
12. `FixedBytePointer_IndexWrite` — `p[i] = 42` → `p.set(ValueLayout.JAVA_BYTE, i, (byte) 42)`
13. `FixedBytePointer_ArithmeticResult` — `byte v = (byte)(p[0] + p[1])` → correct `& 0xFF`
14. `FixedSBytePointer_NoMask` — `sbyte v = *p` → `p.get(ValueLayout.JAVA_BYTE, 0)` (no mask)

#### C. Char Special Handling (3 tests)

15. `FixedCharPointer_DerefRead` — `char v = *p` → `p.get(ValueLayout.JAVA_CHAR, 0)`
16. `FixedCharPointer_DerefWrite` — `*p = 'a'` → `p.set(ValueLayout.JAVA_CHAR, 0, 'a')`
17. `FixedCharPointer_FromString` — `fixed (char* p = str)` → `MemorySegment.ofArray(str.toCharArray())`

#### D. Pointer Arithmetic (4 tests)

18. `PointerAddOffset` — `byte* q = p + 4` → `p.asSlice(4)`
19. `PointerIncrement` — `p++` → `p = p.asSlice(1)`
20. `IntPointerAddOffset` — `int* q = p + 4` → `p.asSlice(16)` (4 × sizeof(int) = 16)
21. `PointerIndexAccess_WithOffset` — `int v = p[2]` (int*) → `p.get(ValueLayout.JAVA_INT, 8)` (2 × 4 = 8)

#### E. Unsafe Block Handling (2 tests)

22. `UnsafeBlock_Stripped` — `unsafe { int x = 1; }` → `{ int x = 1; }`
23. `UnsafeBlock_WithFixedInside` — `unsafe { fixed (byte* p = arr) { } }` → correct fixed conversion

#### F. Fixed-Size Buffer Fields (3 tests)

24. `FixedBufferField_Byte` — `fixed byte buffer[256]` → `MemorySegment buffer` + Arena init
25. `FixedBufferField_Char` — `fixed char buffer[128]` → `MemorySegment buffer` + Arena init
26. `FixedBufferField_Int` — `fixed int buffer[64]` → `MemorySegment buffer` + Arena init

#### G. Import Verification (2 tests)

27. `FixedStatement_Imports` — class with fixed → includes `import java.lang.foreign.MemorySegment` and `import java.lang.foreign.ValueLayout`
28. `FixedBufferField_Imports` — class with fixed field → additionally includes `import java.lang.foreign.Arena`

#### H. Edge Cases (3 tests)

29. `FixedNullPointer` — `fixed (byte* p = null)` → `MemorySegment.NULL`
30. `FixedMultipleDeclarators` — `fixed (byte* p1 = a, p2 = b)` → two MemorySegment variables
31. `NestedFixedStatements` — nested fixed → correct scope stack tracking

### Test Pattern

All tests follow the existing end-to-end pattern:

```csharp
[Fact]
public void TestName()
{
    var result = Convert("unsafe class Test { ... }");
    Assert.True(result.Success);
    Assert.Contains("expected Java code", result.GeneratedCode);
}

private static ConversionResult Convert(string sourceCode)
{
    var pipeline = new ConversionPipeline();
    return pipeline.Convert(new ConversionRequest
    {
        SourceCode = sourceCode,
        FileName = "Sample.cs",
        Options = new ConversionOptions(),
    });
}
```

## Limitations and Future Work

1. **Struct pointer dereference** (`p->field`): Requires struct layout knowledge; not supported in initial implementation. Emits warning.
2. **Pointer casting** (`(int*)bytePtr`): Not supported initially; would require layout reinterpretation.
3. **`ulong` unsigned masking**: Java has no unsigned long; `& 0xFFFFFFFFFFFFFFFFL` doesn't fully solve this. Left as future work.
4. **String mutability**: `fixed (char* p = str)` creates a copy via `toCharArray()`. Writes to the pointer don't affect the original string. This differs from C# semantics.
5. **`stackalloc` in fixed context**: Currently converts to heap allocation; could be optimized to use `Arena.allocate()` for true stack-like semantics.
