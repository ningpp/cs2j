# Address Of Local Pointer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert C# unsafe local-variable address expressions such as `&tmp` into Java FFM `MemorySegment` scratch storage instead of emitting `/* C# addressof -- no Java equivalent: tmp */`.

**Architecture:** Keep existing pointer-parameter and `fixed`-statement FFM behavior intact. Add a focused local-address path in `UnaryExpressionTransformer.TransformAddressOf()` that recognizes `&local` / `&parameter`, emits method-level scratch `MemorySegment` pre-statements, registers pointer metadata with `ConversionContext`, and returns the scratch segment expression for pointer arguments and assignments.

**Tech Stack:** C# / Roslyn / xUnit / Java FFM API (`MemorySegment`, `ValueLayout`, `Arena`)

---

## File Structure

- Modify: `src/CSharpToJava.Core/Context/ConversionContext.cs`
  - Responsibility: store per-method address-of scratch mappings and expose helper methods so expression transformers can allocate/reuse scratch `MemorySegment` variables.
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Utilities/FfmHelper.cs`
  - Responsibility: map scalar C# locals to one-element Java arrays and emit FFM scratch segment initialization/update code.
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs`
  - Responsibility: split `&` into three cases: existing fixed-scope pointer, supported scalar local address, and unsupported fallback.
- Modify: `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs`
  - Responsibility: ensure the method-level fixed pointer scope includes address-of scratch pointers while transforming the body, or make the context lookup include the new address-of map.
- Test: `tests/CSharpToJava.Tests/UnsafeMethodPointerTests.cs`
  - Responsibility: cover `&tmp` conversion, imports, reuse, pointer writes through the callee-visible segment, and unsupported address-of fallback.

---

### Task 1: Add failing coverage for `&tmp` as a pointer argument

**Files:**
- Modify: `tests/CSharpToJava.Tests/UnsafeMethodPointerTests.cs`

- [ ] **Step 1: Add the failing test**

Append these tests before the existing `Convert()` helper:

```csharp
    [Fact]
    public void UnsafeMethod_AddressOfIntLocal_AsPointerArgument_UsesMemorySegmentScratch()
    {
        var result = Convert(@"
unsafe class Test {
    void Fill(int* p) {
        *p = 42;
    }

    void M() {
        int tmp = 0;
        Fill(&tmp);
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment _addr_tmp", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("MemorySegment.ofArray(new int[] { tmp })", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Fill(_addr_tmp)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("C# addressof", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsafeMethod_AddressOfByteLocal_AsPointerArgument_UsesByteArrayScratch()
    {
        var result = Convert(@"
unsafe class Test {
    void Fill(byte* p) {
        *p = 255;
    }

    void M() {
        byte tmp = 0;
        Fill(&tmp);
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment _addr_tmp", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("MemorySegment.ofArray(new byte[] { tmp })", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Fill(_addr_tmp)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("C# addressof", result.GeneratedCode, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~UnsafeMethodPointerTests.UnsafeMethod_AddressOf" -v n
```

Expected: both tests FAIL because generated code contains `/* C# addressof -- no Java equivalent: tmp */` or does not contain `_addr_tmp`.

- [ ] **Step 3: Commit failing tests**

```bash
git add tests/CSharpToJava.Tests/UnsafeMethodPointerTests.cs
git commit -m "test: cover address-of local pointer arguments"
```

---

### Task 2: Add FFM helpers for scalar address-of scratch storage

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Utilities/FfmHelper.cs`

- [ ] **Step 1: Write focused helper tests through conversion tests**

No separate unit test file is needed. Task 1 conversion tests exercise the helper output through public behavior.

- [ ] **Step 2: Add scalar array type mapping helpers**

In `FfmHelper`, after `GetWriteCast()`, add:

```csharp
    public static string GetScratchArrayType(string csharpElementType) => csharpElementType switch
    {
        "byte" or "Byte" or "System.Byte" => "byte",
        "sbyte" or "SByte" or "System.SByte" => "byte",
        "char" or "Char" or "System.Char" => "char",
        "short" or "Short" or "System.Int16" => "short",
        "ushort" or "UInt16" or "System.UInt16" => "char",
        "int" or "Int32" or "System.Int32" => "int",
        "uint" or "UInt32" or "System.UInt32" => "int",
        "long" or "Int64" or "System.Int64" => "long",
        "ulong" or "UInt64" or "System.UInt64" => "long",
        "float" or "Single" or "System.Single" => "float",
        "double" or "Double" or "System.Double" => "double",
        "bool" or "Boolean" or "System.Boolean" => "boolean",
        _ => "byte"
    };

    public static string GenerateAddressOfScratchInit(string segmentName, string sourceExpression, FixedPointerInfo info)
    {
        var arrayType = GetScratchArrayType(info.CSharpElementTypeName);
        var valueExpression = info.WriteCast.Length > 0
            ? $"{info.WriteCast} {sourceExpression}"
            : sourceExpression;
        return $"MemorySegment {segmentName} = MemorySegment.ofArray(new {arrayType}[] {{ {valueExpression} }});";
    }
```

- [ ] **Step 3: Run helper-driven tests**

Run:

```bash
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~UnsafeMethodPointerTests.UnsafeMethod_AddressOf" -v n
```

Expected: still FAIL because `UnaryExpressionTransformer` has not called the helper yet.

- [ ] **Step 4: Commit helper**

```bash
git add src/CSharpToJava.Core/Transformers/Expression/Utilities/FfmHelper.cs
git commit -m "feat: add FFM scratch segment helpers for address-of locals"
```

---

### Task 3: Track address-of scratch pointers in `ConversionContext`

**Files:**
- Modify: `src/CSharpToJava.Core/Context/ConversionContext.cs`

- [ ] **Step 1: Add per-method scratch state**

Add this record near the existing fixed pointer stack fields:

```csharp
    private readonly Dictionary<string, string> _addressOfScratchSegments = new(StringComparer.Ordinal);
```

Add these methods near `FindPointerInfo()`:

```csharp
    public bool TryGetAddressOfScratchSegment(string sourceName, out string segmentName)
        => _addressOfScratchSegments.TryGetValue(sourceName, out segmentName!);

    public void RegisterAddressOfScratchSegment(string sourceName, string segmentName, FixedPointerInfo pointerInfo)
    {
        _addressOfScratchSegments[sourceName] = segmentName;
        if (_fixedScopeStack.Count == 0)
            _fixedScopeStack.Push(new List<FixedPointerInfo>());
        _fixedScopeStack.Peek().Add(pointerInfo);
    }
```

Update `EnterMethod()` after `MethodState.Reset(readOnlyParams);`:

```csharp
        _addressOfScratchSegments.Clear();
```

- [ ] **Step 2: Run tests to ensure no regression**

Run:

```bash
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~UnsafeMethodPointerTests.UnsafeMethod_BytePointerParam_MapsToMemorySegment" -v n
```

Expected: PASS.

- [ ] **Step 3: Commit context state**

```bash
git add src/CSharpToJava.Core/Context/ConversionContext.cs
git commit -m "feat: track address-of scratch pointer segments"
```

---

### Task 4: Convert `&local` to scratch `MemorySegment`

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs`

- [ ] **Step 1: Replace `TransformAddressOf()` with scalar local handling**

Replace the body of `TransformAddressOf()` with:

```csharp
    private string TransformAddressOf(PrefixUnaryExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);

        if (context.IsInFixedScope)
        {
            context.Diagnostics.Warning("Address-of operator (&) in fixed scope - using MemorySegment offset", node.GetLocation());
            return operand;
        }

        if (TryTransformAddressOfScalarLocal(node, context, out var scratchSegment))
            return scratchSegment;

        context.Diagnostics.Warning("Address-of operator (&) has no Java equivalent - converting to unsafe memory access", node.GetLocation());
        return $"/* C# addressof -- no Java equivalent: {operand} */";
    }
```

Add this helper immediately after `TransformAddressOf()`:

```csharp
    private static bool TryTransformAddressOfScalarLocal(
        PrefixUnaryExpressionSyntax node,
        ConversionContext context,
        out string scratchSegment)
    {
        scratchSegment = string.Empty;

        if (node.Operand is not IdentifierNameSyntax identifier)
            return false;

        var symbol = context.GetSymbolInfo(identifier).Symbol;
        ITypeSymbol? type = symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            _ => null
        };

        if (!IsAddressableScalar(type))
            return false;

        var sourceName = ConversionContext.EscapeJavaKeyword(identifier.Identifier.Text);
        if (context.TryGetAddressOfScratchSegment(sourceName, out scratchSegment))
            return true;

        var csharpElementType = type!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", "", StringComparison.Ordinal);
        var pointerInfo = FfmHelper.CreatePointerInfo(sourceName, csharpElementType);
        scratchSegment = context.GenerateSyntheticName($"_addr_{sourceName}");
        var init = FfmHelper.GenerateAddressOfScratchInit(scratchSegment, sourceName, pointerInfo);

        foreach (var imp in FfmHelper.GetRequiredImports(false))
            context.AddImport(imp);

        context.AddPreStatement(init);
        context.RegisterAddressOfScratchSegment(sourceName, scratchSegment, new FixedPointerInfo
        {
            VariableName = scratchSegment,
            CSharpElementTypeName = pointerInfo.CSharpElementTypeName,
            ValueLayoutName = pointerInfo.ValueLayoutName,
            ElementSize = pointerInfo.ElementSize,
            NeedsUnsignedMask = pointerInfo.NeedsUnsignedMask,
            MaskSuffix = pointerInfo.MaskSuffix,
            WriteCast = pointerInfo.WriteCast
        });
        return true;
    }

    private static bool IsAddressableScalar(ITypeSymbol? type)
    {
        if (type == null)
            return false;

        return type.SpecialType is
            SpecialType.System_Byte or
            SpecialType.System_SByte or
            SpecialType.System_Char or
            SpecialType.System_Int16 or
            SpecialType.System_UInt16 or
            SpecialType.System_Int32 or
            SpecialType.System_UInt32 or
            SpecialType.System_Int64 or
            SpecialType.System_UInt64 or
            SpecialType.System_Single or
            SpecialType.System_Double or
            SpecialType.System_Boolean;
    }
```

- [ ] **Step 2: Run address-of tests**

Run:

```bash
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~UnsafeMethodPointerTests.UnsafeMethod_AddressOf" -v n
```

Expected: both tests PASS.

- [ ] **Step 3: Run adjacent unsafe tests**

Run:

```bash
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~UnsafeMethodPointerTests" -v n
```

Expected: all `UnsafeMethodPointerTests` PASS.

- [ ] **Step 4: Commit conversion support**

```bash
git add src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs
git commit -m "feat: convert address-of scalar locals to FFM scratch segments"
```

---

### Task 5: Preserve unsupported address-of diagnostics for non-scalars

**Files:**
- Modify: `tests/CSharpToJava.Tests/UnsafeMethodPointerTests.cs`

- [ ] **Step 1: Add unsupported fallback test**

Append this test before the `Convert()` helper:

```csharp
    [Fact]
    public void UnsafeMethod_AddressOfObjectLocal_RemainsUnsupportedDiagnostic()
    {
        var result = Convert(@"
unsafe class Test {
    void M() {
        object tmp = new object();
        var p = &tmp;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("C# addressof -- no Java equivalent: tmp", result.GeneratedCode, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run unsupported fallback test**

Run:

```bash
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~UnsafeMethodPointerTests.UnsafeMethod_AddressOfObjectLocal_RemainsUnsupportedDiagnostic" -v n
```

Expected: PASS.

- [ ] **Step 3: Commit fallback coverage**

```bash
git add tests/CSharpToJava.Tests/UnsafeMethodPointerTests.cs
git commit -m "test: keep unsupported address-of object fallback explicit"
```

---

### Task 6: Full verification

**Files:**
- No edits.

- [ ] **Step 1: Run focused tests**

Run:

```bash
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~UnsafeMethodPointerTests" -v n
```

Expected: all `UnsafeMethodPointerTests` PASS.

- [ ] **Step 2: Build solution**

Run:

```bash
dotnet build CSharpToJavaConverter.slnx
```

Expected: build succeeds with `0 Error(s)`.

- [ ] **Step 3: Run full test suite**

Run:

```bash
dotnet test CSharpToJavaConverter.slnx
```

Expected: all tests PASS.

- [ ] **Step 4: Commit verification-only updates if needed**

If no files changed during verification, do not create a commit. If formatting or test corrections were required, commit them:

```bash
git add src/CSharpToJava.Core/Context/ConversionContext.cs src/CSharpToJava.Core/Transformers/Expression/Utilities/FfmHelper.cs src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs tests/CSharpToJava.Tests/UnsafeMethodPointerTests.cs
git commit -m "test: verify address-of local pointer conversion"
```

---

## Self-Review

Spec coverage:
- Covers the reported generated output `C# addressof -- no Java equivalent: tmp`.
- Covers scalar locals passed to pointer parameters.
- Covers byte/int layout differences.
- Preserves existing fixed-scope and pointer-parameter FFM behavior.
- Keeps unsupported non-scalar address-of explicit.

Placeholder scan:
- No implementation step uses placeholder instructions.
- All code changes include concrete snippets.
- All verification commands include expected outcomes.

Type consistency:
- `FixedPointerInfo`, `FfmHelper.CreatePointerInfo()`, `ConversionContext.RegisterAddressOfScratchSegment()`, and `UnaryExpressionTransformer.TryTransformAddressOfScalarLocal()` names are consistent across tasks.
- Scratch segment names are generated via `context.GenerateSyntheticName($"_addr_{sourceName}")`, matching the test substring `_addr_tmp`.
