# C# byte/sbyte Conversion Fix — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix C# `byte` (unsigned 0-255) → Java `int` mapping and C# `sbyte` (signed) → Java `byte` mapping with `& 0xFF` masking on narrowing.

**Architecture:** Root cause is that C# `byte` (unsigned) is mapped identically to `sbyte` (signed) → both become Java `byte` (signed). Fix: C# `byte` → Java `int` across all pipeline stages, with `& 0xFF` masking when narrowing from wider types. C# `sbyte` → Java `byte` stays unchanged. `byte[]` arrays stay as Java `byte[]` for API compat.

**Tech Stack:** C#, Roslyn (Microsoft.CodeAnalysis), dotnet build/test

---

### Task 1: Type Mapping Foundation — TypeMappings.json + TypeMappingService

**Files:**
- Modify: `config/TypeMappings.json:32-33`
- Modify: `src/CSharpToJava.Core/Context/TypeMappingService.cs:664,736`

- [ ] **Step 1: Update TypeMappings.json**

In `config/TypeMappings.json`, change line 32-33:
```json
"csharp":  "System.Byte",
"java":  "int",
```

- [ ] **Step 2: Update MapSimpleTypeName in TypeMappingService.cs**

In `TypeMappingService.cs` line 664, change:
```csharp
"Byte" => "byte",
```
to:
```csharp
"Byte" => "int",
```

- [ ] **Step 3: Update MapTypeFromSyntaxString in TypeMappingService.cs**

In `TypeMappingService.cs` line 736, change:
```csharp
"byte"    => "byte",
```
to:
```csharp
"byte"    => "int",
```

- [ ] **Step 4: Verify BoxPrimitive and nullable already handle int**

BoxPrimitive (line 691) already has `"int" => "Integer"`. Nullable handling (line 279) already has `"int" => "Integer"`. Both are correct — no changes needed since C# byte now routes through `int`.

- [ ] **Step 5: Build and verify**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

---

### Task 2: ExpressionTransformerHelpers — Wrapper Type + MaskByte

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs`

- [ ] **Step 1: Fix GetJavaWrapperType**

Line 397: change `"byte" => "short"` to `"byte" => "Integer"`:
```csharp
"byte" => "Integer",
```

- [ ] **Step 2: Add MaskByte helper method**

After line 424 (after `BoxJavaPrimitiveType`), add:
```csharp
/// <summary>
/// Wraps an expression in &amp; 0xFF when the target is a C# byte (now Java int)
/// to preserve C# byte's wrap-at-256 semantics.
/// </summary>
public static string MaskByte(string expr, bool isByteTarget)
    => isByteTarget ? $"({expr}) & 0xFF" : expr;
```

- [ ] **Step 3: Update AdaptExpressionToTargetTypeCore for byte→int**

At line 168-177, the cast keyword table currently maps both `System_Byte` and `System_SByte` → `"byte"`. Since C# byte now maps to Java int, only `System_SByte` should produce `"byte"` cast. `System_Byte` should NOT produce a byte cast — it maps to int, and instead may need `& 0xFF` masking.

Change lines 168-182:
```csharp
var castKeyword = targetSpecial switch
{
    SpecialType.System_SByte => "byte",
    SpecialType.System_Int16 or SpecialType.System_UInt16 => "short",
    SpecialType.System_Int32 or SpecialType.System_UInt32 => "int",
    SpecialType.System_Int64 or SpecialType.System_UInt64 => "long",
    SpecialType.System_Single => "float",
    SpecialType.System_Double => "double",
    SpecialType.System_Char => "char",
    _ => null
};
```

When `targetSpecial` is `System_Byte`, return the expression unchanged (no cast needed since it maps to int). But when the source is wider than int (e.g. long→byte), add masking.

Add before the `castKeyword` block:
```csharp
// C# byte (unsigned) → Java int: add & 0xFF masking when narrowing from wider types
if (targetSpecial == SpecialType.System_Byte)
{
    var sourceSpecialValue = (int)sourceSpecial;
    // Wider-than-int source: long, ulong, float, double
    if (sourceSpecial is SpecialType.System_Int64 or SpecialType.System_UInt64
        or SpecialType.System_Single or SpecialType.System_Double)
    {
        return $"({GetCastKeyword(sourceSpecial)}) ({transformedExpression}) & 0xFF";
    }
    return $"{transformedExpression} & 0xFF";
}
```

Where `GetCastKeyword` is a helper that maps `SpecialType` → Java cast keyword (long→"long", float→"float", double→"double").

Wait — simpler approach. When the target is `System_Byte` (C# byte → Java int) and source is a wider type, just wrap with `((int)(expr)) & 0xFF`:

```csharp
if (targetSpecial == SpecialType.System_Byte)
{
    // C# byte target → Java int + & 0xFF mask
    if (!IsNumericOrCharType(sourceSpecial))
        return transformedExpression;
    
    bool needsNarrowingCast = sourceSpecial is SpecialType.System_Int64 
        or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double;
    
    if (needsNarrowingCast)
    {
        var narrowKeyword = sourceSpecial switch
        {
            SpecialType.System_Int64 or SpecialType.System_UInt64 => "int",
            SpecialType.System_Single => "int",
            SpecialType.System_Double => "int",
            _ => null
        };
        return $"({narrowKeyword}) ({transformedExpression}) & 0xFF";
    }
    return $"{transformedExpression} & 0xFF";
}
```

- [ ] **Step 4: Update IsPrimitiveSpecialTypeForArrayStream**

At line 974, `SpecialType.System_Byte` is listed. Since byte now maps to int, and int is already handled by `Arrays.stream()`, add `System_Byte` to the supported primitives list at line 887.

Change line 887 from:
```csharp
return st is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Double;
```
to:
```csharp
return st is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Double or SpecialType.System_Byte;
```

And at line 915, add `System_Byte`:
```csharp
if (elemSt is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Double or SpecialType.System_Byte)
```

And remove `System_Byte` from the unsupported comment at lines 893-895:
Change:
```csharp
/// <c>short[]</c>, <c>byte[]</c>, <c>char[]</c>, <c>float[]</c>, <c>boolean[]</c> are NOT supported.
```
to:
```csharp
/// <c>short[]</c>, <c>char[]</c>, <c>float[]</c>, <c>boolean[]</c> are NOT supported.
```

- [ ] **Step 5: Build**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

---

### Task 3: IdentifierExpressionTransformer — Boxed Class + PredefinedType

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`

- [ ] **Step 1: Update boxed class name mapping**

Line 46: change:
```csharp
["Byte"]     = ("byte",    "Byte"),
```
to:
```csharp
["Byte"]     = ("int",     "Integer"),
```

- [ ] **Step 2: Update PredefinedType keyword mapping**

Find the `TransformPredefinedType` method (around line 428-455). Change the keyword mapping for `"byte"`:
```csharp
"byte"  => "int",
```
(was `"byte"  => "byte"`)

- [ ] **Step 3: Build**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

---

### Task 4: TypeOperationTransformer — default, sizeof, cast

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs`

- [ ] **Step 1: Fix default(byte)**

Line 772: change from:
```csharp
"byte" => "(byte)0",
```
to:
```csharp
"byte" => "0",
```

- [ ] **Step 2: Fix sizeof(byte)**

Line 817: change from:
```csharp
"byte" or "sbyte" or "bool" => "1",
```
to:
```csharp
"byte" => "4",
"sbyte" or "bool" => "1",
```

- [ ] **Step 3: Build**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

---

### Task 5: LowerRefOut + HolderTypeResolver — ByteHolder → IntHolder

**Files:**
- Modify: `src/CSharpToJava.Core/Lowering/LowerRefOut.cs:72`
- Modify: `src/CSharpToJava.Core/Transformers/HolderTypeResolver.cs:26`

- [ ] **Step 1: Fix LowerRefOut.cs**

Line 72: change from:
```csharp
"byte" or "Byte" => "ByteHolder",
```
to:
```csharp
"int" or "Integer" => "IntHolder",
```

**Important**: Keep `"byte" or "Byte"` entry for sbyte type — but wait, after the type mapping change, C# byte maps to Java int, so `ref byte` becomes `IntHolder`. But `ref sbyte` maps to Java byte and should use `ByteHolder`. The existing `"int" or "Integer" => "IntHolder"` is at line 66. The `"byte" or "Byte" => "ByteHolder"` should now only fire for sbyte-ref scenarios.

Check the existing entries at lines 65-76. They key on `refOut.Inner.JavaType`. After the type mapping change:
- C# `ref byte` → Java type `int` → matches `"int" or "Integer" => "IntHolder"` ✓
- C# `ref sbyte` → Java type `byte` → matches `"byte" or "Byte" => "ByteHolder"` ✓

No change needed in LowerRefOut.cs! The existing entries already handle this correctly since the type mapping change at the source (MapType returns "int" for byte) will make the JavaType "int" which routes to IntHolder.

- [ ] **Step 2: Fix HolderTypeResolver.cs**

Check line 26. If it has `"byte" => "ByteHolder"`, it also doesn't need changing since the input to HolderTypeResolver is the already-mapped Java type name. C# byte → Java int → IntHolder. SByte → Java byte → ByteHolder.

Read the file to confirm:
```bash
grep -n "byte" src/CSharpToJava.Core/Transformers/HolderTypeResolver.cs
```

If it maps `"byte" => "ByteHolder"` and `"int" => "IntHolder"` already exists, no change needed.

- [ ] **Step 3: Build**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

---

### Task 6: ImplicitCastCompletionRewriter — Update Narrowing Pairs

**Files:**
- Modify: `src/CSharpToJava.Core/Java/Rewriters/ImplicitCastCompletionRewriter.cs`

- [ ] **Step 1: Understand what changes are needed**

The narrowing pairs are Java-type to Java-type. Since C# `byte` now maps to Java `int`, the pairs involving `byte` as target should only apply to sbyte→byte scenarios (Java byte IS the target for sbyte). No changes needed to the narrowing pairs themselves.

However, there is a subtlety: when Java `int` is assigned to Java `byte` (from sbyte), the existing `("int", "byte")` pair handles it. When C# byte (now Java int) is assigned, there's no narrowing — both are int.

The masking is handled by `AdaptExpressionToTargetTypeCore` in Task 2, not here.

**Verify**: no changes needed in this file for the narrowing pairs. But we should verify the raw statement regex path doesn't incorrectly add casts.

Read the VarDecl heuristic path (lines 86-130+). It checks declaredType against initType using the narrowing pairs. If C# `byte x = expr` becomes `int x = expr` in Java, and `expr` resolves to an int type, no narrowing cast is needed — they match. Correct.

- [ ] **Step 2: Build and verify no regressions**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

---

### Task 7: AssignmentTransformer — GetJavaFieldDefault + Compound Assignment Masking

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs`

- [ ] **Step 1: Fix GetJavaFieldDefault**

Line 1075: change from:
```csharp
"byte" => "(byte)0",
```
to:
```csharp
"byte" => "(byte)0",   // sbyte → Java byte — keep for Java byte type
```

Wait — this is called with Java type names. `"byte"` here means Java `byte` (from sbyte). So `(byte)0` is still correct for sbyte defaults! No change needed.

But `"int"` already has `"int" => "0"` at line 1077. Since C# byte → Java int, defaults become `0`. This is already handled by the existing `"int"` entry.

**Verify**: no change needed.

- [ ] **Step 2: Add compound assignment masking for byte targets**

In the compound assignment path (around lines 560-626), when the target variable is C# `byte` (Java `int`), compound assignments like `b += 1` need `& 0xFF`:

Find the return at line 625:
```csharp
return $"{left} {op} {rightStr}";
```

For simple assignments (`=`) with C# byte target, `AdaptExpressionToTargetType` (called at line 608) will add masking. But for compound assignments (`+=`, `-=`, etc.), the expansion is `b = b + 1` which produces an int result. We need `b = (b + 1) & 0xFF` for byte targets.

Add after line 608 (after the AdaptExpressionToTargetType call for the simple-assignment path), before line 625:

Near the compound assignment expansion, check if the LHS type is C# byte and wrap the result:
```csharp
// For compound assignments on C# byte (Java int) targets, add & 0xFF masking
if (op != "=" && context.SemanticModel != null)
{
    var lhsType = context.SemanticModel.GetTypeInfo(leftNode).Type;
    if (lhsType?.SpecialType == SpecialType.System_Byte)
    {
        rightStr = $"({rightStr}) & 0xFF";
        // The compound op is expanded manually: left op rightStr → left = (left op rightStr) & 0xFF
        return $"{left} = ({left} {op[..^1]} {rightStr.Substring(1, rightStr.Length - 1 - 8)}) & 0xFF";
    }
}
```

Actually, this is getting complex. The compound assignment for non-property, non-indexer already falls through to line 625: `$"{left} {op} {rightStr}"`. Java handles `+=` natively. For C# byte→Java int, `b += 1` in Java is `b = b + 1` — both int, correct. But we need `b = (b + 1) & 0xFF`.

Simpler approach: detect byte target and rewrite compound assignment:
```csharp
// After all the special cases (property, indexer, event, user-defined operator),
// if the target type is C# byte (Java int), compound assignments need & 0xFF
if (op != "=" && leftNode is IdentifierNameSyntax)
{
    var lhsTypeForCompound = context.SemanticModel?.GetTypeInfo(leftNode).Type;
    if (lhsTypeForCompound?.SpecialType == SpecialType.System_Byte)
    {
        var leftForCompound = facade.Transform(leftNode, context);
        var rightForCompound = facade.Transform(rightNode, context);
        return $"{leftForCompound} = ({leftForCompound} {op[..^1]} {rightForCompound}) & 0xFF";
    }
}
```

Insert this before line 563 (before `var left = facade.Transform(leftNode, context);` — actually before the final fallback path).

Let me find the right location. After the fix-6 block (~line 539) and before the compound-operator-overload block (~line 544), insert:

```csharp
// When the LHS is a C# byte-typed simple identifier (Java int), compound assignments
// need & 0xFF masking to preserve wrap-at-256 semantics.
if (op != "=" && leftNode is IdentifierNameSyntax
    && !(context.SemanticModel?.GetSymbolInfo(node).Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator }))
{
    var lhsTypeForByte = context.SemanticModel?.GetTypeInfo(leftNode).Type;
    if (lhsTypeForByte?.SpecialType == SpecialType.System_Byte)
    {
        var leftByte = facade.Transform(leftNode, context);
        var rightByte = facade.Transform(rightNode, context);
        string baseOpByte = op[..^1]; // "+=" → "+"
        return $"{leftByte} = ({leftByte} {baseOpByte} {rightByte}) & 0xFF";
    }
}
```

- [ ] **Step 3: Build**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

---

### Task 8: ArgumentTransformer — Parameter Passing with & 0xFF

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.cs`

- [ ] **Step 1: Add masking in AdaptExpressionToTargetType for byte→int**

The `CoerceArgumentType` method at line 454 calls `ExpressionTransformerHelpers.AdaptExpressionToTargetType` at line 655. This already goes through our updated `AdaptExpressionToTargetTypeCore` which handles `System_Byte` target → `& 0xFF` masking. 

- [ ] **Step 2: Add byte→int narrowing when passing to Java byte parameter**

When a C# byte (Java int) is passed to a method expecting Java byte (from sbyte parameter or Java library), the converter needs to detect this and add `(byte)(expr & 0xFF)`.

In `CoerceArgumentType`, after the AdaptExpressionToTargetType call, add:

After line 655 (the `return ExpressionTransformerHelpers.AdaptExpressionToTargetType(...)` call at the end of `CoerceArgumentType`), add:

```csharp
// When C# byte (Java int) is passed to Java byte (sbyte/library) parameter,
// narrow with (byte)(expr & 0xFF)
if (context.SemanticModel != null)
{
    var argTypeForByte = context.SemanticModel.GetTypeInfo(arg.Expression).Type;
    var paramTypeForByte = targetParam.Type;
    if (argTypeForByte?.SpecialType == SpecialType.System_Byte
        && paramTypeForByte.SpecialType == SpecialType.System_SByte)
    {
        transformedExpr = $"(byte)({transformedExpr})";
        // No & 0xFF needed since System_SByte expects signed byte range
    }
}
```

- [ ] **Step 3: Build**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

---

### Task 9: BinaryExpressionTransformer — Arithmetic Masking

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs`

- [ ] **Step 1: Add byte arithmetic masking**

When the result of a binary expression is assigned to a byte-typed variable, the `AdaptExpressionToTargetType` in `AssignmentTransformer` handles it. The binary expression itself doesn't need modification — it produces the correct arithmetic on int values. The masking is applied by the assignment side.

**No changes needed** in BinaryExpressionTransformer. The `AdaptExpressionToTargetType` path already handles byte→int masking.

- [ ] **Step 2: Verify build passes**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

---

### Task 10: Remaining Transformer Files

**Files:**
- Verify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs`
- Verify: `src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs`
- Verify: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs`
- Verify: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs`

- [ ] **Step 1: Check UnaryExpressionTransformer for byte handling**

Search for `"byte"` in the file:
```bash
grep -n "byte" src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs
```

The BuiltInTypeNames set includes `"byte"` and `"sbyte"`. These are used to determine if a unary operator is built-in vs user-defined. Since C# byte maps to Java int and int is already in the set, no change needed. The `"byte"` entry now refers to sbyte (since sbyte→byte).

- [ ] **Step 2: Check FieldTransformer for byte handling**

Search for `"byte"`:
```bash
grep -n "byte" src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs
```

FieldTransformer uses `context.MapType()` for type resolution, which already maps byte→int. No explicit byte handling found — no change needed.

- [ ] **Step 3: Check StatementTransformer files**

```bash
grep -n "byte" src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs
grep -n "byte" src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs
```

These files use `context.MapType()` and `AdaptExpressionToTargetType` which already handle the byte→int mapping and masking.

- [ ] **Step 4: Build the full solution**

```bash
dotnet build
```

---

### Task 11: Add Test File

**Files:**
- Create: `tests/CSharpToJava.Tests/ByteSByteConversionTests.cs`

- [ ] **Step 1: Create the test file**

```csharp
using Xunit;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Tests;

public class ByteSByteConversionTests
{
    [Fact]
    public void ByteDeclaration_MapsToInt()
    {
        var csharp = @"
class Test {
    byte x = 200;
}
";
        var java = Convert(csharp);
        Assert.Contains("int x = 200", java);
    }

    [Fact]
    public void SByteDeclaration_StaysByte()
    {
        var csharp = @"
class Test {
    sbyte y = -100;
}
";
        var java = Convert(csharp);
        Assert.Contains("byte y = -100", java);
    }

    [Fact]
    public void ByteArithmeticWithCast_AddsMask()
    {
        var csharp = @"
class Test {
    byte Add(byte a, byte b) {
        byte c = (byte)(a + b);
        return c;
    }
}
";
        var java = Convert(csharp);
        Assert.Contains("& 0xFF", java);
    }

    [Fact]
    public void ByteArithmeticToInt_NoMask()
    {
        var csharp = @"
class Test {
    int Add(byte a, byte b) {
        int c = a + b;
        return c;
    }
}
";
        var java = Convert(csharp);
        Assert.Contains("int c = a + b", java);
    }

    [Fact]
    public void BytePlainLiteral_NoMask()
    {
        var csharp = @"
class Test {
    byte x = 42;
}
";
        var java = Convert(csharp);
        Assert.Contains("int x = 42", java);
    }

    [Fact]
    public void ByteLiteralWithCast_HasMask()
    {
        var csharp = @"
class Test {
    byte x = (byte)300;
}
";
        var java = Convert(csharp);
        Assert.Contains("& 0xFF", java);
    }

    [Fact]
    public void ByteArrayRead_AddsMask()
    {
        var csharp = @"
class Test {
    int Read(byte[] buf) {
        byte b = buf[0];
        return b;
    }
}
";
        var java = Convert(csharp);
        Assert.Contains("& 0xFF", java);
    }

    [Fact]
    public void ByteArrayWrite_AddsMask()
    {
        var csharp = @"
class Test {
    void Write(byte[] buf, int v) {
        buf[0] = (byte)v;
    }
}
";
        var java = Convert(csharp);
        Assert.Contains("& 0xFF", java);
    }

    [Fact]
    public void ByteArrayDeclaration_StaysByteArray()
    {
        var csharp = @"
class Test {
    byte[] buf = new byte[1024];
}
";
        var java = Convert(csharp);
        Assert.Contains("byte[] buf = new byte[1024]", java);
    }

    [Fact]
    public void ByteMethodParameter_BecomesInt()
    {
        var csharp = @"
class Test {
    void Foo(byte b) { }
}
";
        var java = Convert(csharp);
        Assert.Contains("void foo(int b)", java);
    }

    [Fact]
    public void DefaultByte_ReturnsZero()
    {
        var csharp = @"
class Test {
    byte x = default;
}
";
        var java = Convert(csharp);
        Assert.Contains("int x = 0", java);
    }

    [Fact]
    public void ByteMaxValue_MapsTo255()
    {
        var csharp = @"
class Test {
    int x = byte.MaxValue;
}
";
        var java = Convert(csharp);
        Assert.Contains("int x = 255", java);
    }

    [Fact]
    public void ByteMinValue_MapsTo0()
    {
        var csharp = @"
class Test {
    int x = byte.MinValue;
}
";
        var java = Convert(csharp);
        Assert.Contains("int x = 0", java);
    }

    [Fact]
    public void SizeOfByte_Returns4()
    {
        var csharp = @"
class Test {
    int s = sizeof(byte);
}
";
        var java = Convert(csharp);
        Assert.Contains("int s = 4", java);
    }

    [Fact]
    public void ByteCompoundAssignment_HasMask()
    {
        var csharp = @"
class Test {
    void Inc(byte b) {
        b += 1;
    }
}
";
        var java = Convert(csharp);
        Assert.Contains("& 0xFF", java);
    }

    [Fact]
    public void NullableByte_MapsToInteger()
    {
        var csharp = @"
class Test {
    byte? x = null;
}
";
        var java = Convert(csharp);
        Assert.Contains("Integer x = null", java);
    }

    [Fact]
    public void RefByte_PassesThrough()
    {
        var csharp = @"
class Test {
    void Modify(ref byte b) { b = 255; }
}
";
        var java = Convert(csharp);
        // Should use IntHolder, not ByteHolder
        Assert.DoesNotContain("ByteHolder", java);
    }

    private static string Convert(string csharpCode)
    {
        var options = new ConversionOptions { JavaVersion = "Java25" };
        var pipeline = new ConversionPipeline(options);
        var result = pipeline.ConvertText(csharpCode, "test.cs");
        // Return the first output file's content
        return result.OutputFiles.Values.FirstOrDefault() ?? "";
    }
}
```

- [ ] **Step 2: Run tests to verify failures**

```bash
dotnet test --filter "FullyQualifiedName~ByteSByteConversionTests"
```

Expected: most tests FAIL because the byte→int mapping isn't fully implemented yet.

- [ ] **Step 3: Address test failures one by one**

After each fix in the implementation tasks (Tasks 1-10), re-run the tests. Each task should make more tests pass.

---

### Task 12: Final Compilation and Test Run

- [ ] **Step 1: Build the full solution**

```bash
dotnet build
```

- [ ] **Step 2: Run all byte/sbyte tests**

```bash
dotnet test --filter "FullyQualifiedName~ByteSByteConversionTests"
```

Expected: all 17 tests PASS.

- [ ] **Step 3: Run the full test suite to check for regressions**

```bash
dotnet test
```

- [ ] **Step 4: Fix any regressions found**
