# Unsafe Method Pointer Parameter Conversion Design

## Overview

Support conversion of C# `unsafe` methods with pointer type parameters to Java using the FFM (Foreign Function & Memory) API. When a method has pointer parameters like `char* pChars` or `byte* pBytes`, they map to `MemorySegment` in Java, and the method body's pointer operations correctly generate FFM API calls.

## Current State

- `unsafe` method modifier is silently stripped (maps to `JavaModifiers.None`)
- Pointer type parameters (`char*`, `byte*`, etc.) fall through `MapType()` / `MapTypeFromSyntax()` with no handling for `IPointerTypeSymbol` / `PointerTypeSyntax`, producing incorrect type mappings
- Method body pointer operations (`*p`, `p[i]`, `p++`) depend on `FindPointerInfo()` which only works within `fixed` scope — pointer parameters are never registered, so body operations emit warnings/comments instead of FFM calls

## Design

### 1. Pointer Type Mapping in TypeMappingService

Add `IPointerTypeSymbol` branch in `MapTypeInternal()`:

```csharp
if (typeSymbol is IPointerTypeSymbol)
{
    AddImport("java.lang.foreign.MemorySegment");
    return "MemorySegment";
}
```

Add `PointerTypeSyntax` branch in `MapTypeFromSyntax()`:

```csharp
if (typeSyntax is PointerTypeSyntax)
{
    AddImport("java.lang.foreign.MemorySegment");
    return "MemorySegment";
}
```

### 2. Method-Level Fixed Scope Injection

In `MethodTransformer.Transform()`, before method body conversion:

1. Scan parameters for pointer types (`PointerTypeSyntax`)
2. Create `FixedPointerInfo` for each pointer parameter via `FfmHelper.CreatePointerInfo()`
3. Push all pointer infos onto `FixedScopeStack` via `context.PushFixedScope()`
4. Add FFM imports via `FfmHelper.GetRequiredImports(false)`
5. After method body conversion, pop the scope via `context.PopFixedScope()`

This enables `FindPointerInfo()` to resolve pointer parameters, making all existing FFM expression transformers (deref, index access, arithmetic, assignment) work automatically.

### 3. Shared Utility: GetPointerElementTypeName

Extract `GetPointerElementTypeName()` from `StatementTransformer.SwitchAndResource.cs` to `FfmHelper` so both `MethodTransformer` and `StatementTransformer` can use it.

### 4. Conversion Example

C#:
```csharp
private unsafe void Decode(char* pChars, char* pCharsEndPos,
                           byte* pBytes, byte* pBytesEndPos,
                           out int charsDecoded, out int bytesDecoded)
```

Java:
```java
private void Decode(MemorySegment pChars, MemorySegment pCharsEndPos,
                    MemorySegment pBytes, MemorySegment pBytesEndPos,
                    IntHolder charsDecoded, IntHolder bytesDecoded)
```

## Modified Files

1. `src/CSharpToJava.Core/Context/TypeMappingService.cs` — Add `IPointerTypeSymbol` and `PointerTypeSyntax` handling
2. `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs` — Inject fixed scope for pointer parameters
3. `src/CSharpToJava.Core/Transformers/Expression/Utilities/FfmHelper.cs` — Add shared `GetPointerElementTypeName()`
4. `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs` — Use shared `GetPointerElementTypeName()`
5. `tests/CSharpToJava.Tests/UnsafeMethodPointerTests.cs` — New test file

## Test Cases

1. Pointer parameter type mapping (byte*, char*, int*, etc.)
2. Method body pointer dereference (*p)
3. Method body pointer index access (p[i])
4. Method body pointer arithmetic (p++, p + n)
5. Method body pointer assignment (*p = val, p[i] = val)
6. Mixed pointer and non-pointer parameters
7. Pointer parameters with out/ref parameters
8. Import verification
9. Unsafe method without pointer parameters (no scope injection)
10. Multiple pointer parameters of different element types
