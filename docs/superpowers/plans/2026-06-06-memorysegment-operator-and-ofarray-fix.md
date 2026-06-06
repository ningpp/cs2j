# MemorySegment Operator and ofArray Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix C# to Java conversion errors where (1) `MemorySegment.ofArray(byte)` is generated for scalar address-of initializers, and (2) binary operators `<`, `>`, `-`, `+`, `+=` on pointer types produce invalid Java operator expressions.

**Architecture:** Extend the existing FFM (Foreign Function & Memory) API transformation pipeline in `BinaryExpressionTransformer`, `AssignmentTransformer`, and `StatementTransformer.SwitchAndResource` to handle pointer arithmetic, pointer comparison, and scalar address-of in fixed statement initializers.

**Tech Stack:** C# 12, .NET 8, Roslyn, Java 22 FFM API, xUnit

---

## File Structure

| File | Responsibility |
|------|---------------|
| `src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs` | Handles binary expressions; needs pointer `-`, `<`, `>`, `<=`, `>=` support |
| `src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs` | Handles assignments; needs pointer `+=`, `-=` support |
| `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs` | Handles fixed statements; needs scalar `&x` initializer support |
| `src/CSharpToJava.Core/Transformers/Expression/Utilities/FfmHelper.cs` | Helper for MemorySegment code generation; may need new helper methods |
| `tests/CSharpToJava.Tests/FixedStatementFfmTests.cs` | Existing FFM tests; add new test cases for the fixes |

---

## Task 1: Fix `fixed (T* p = &scalar)` generating `ofArray(scalar)`

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs:742-759`
- Test: `tests/CSharpToJava.Tests/FixedStatementFfmTests.cs`

**Root cause:** When a fixed statement initializer is `&scalar` (address-of a scalar local/parameter), the expression transformer either (a) returns the bare scalar value when already inside a fixed scope (nested fixed), or (b) returns a pre-created `_addr_` MemorySegment variable. In both cases `GenerateMemorySegmentInit` wraps it with `ofArray(...)`, producing `ofArray(byte)` or `ofArray(MemorySegment)`.

**Fix:** In `TransformFixedStatement`, detect `PrefixUnaryExpressionSyntax` with `AmpersandToken` and emit `MemorySegment.ofArray(new {type}[] { {operand} })` directly.

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public void FixedBytePointer_AddressOfScalar()
{
    var result = Convert(@"
unsafe class Test {
    void M() {
        byte b = 42;
        fixed (byte* p = &b) { }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("MemorySegment p = MemorySegment.ofArray(new byte[] { b })", result.GeneratedCode);
}

[Fact]
public void FixedBytePointer_NestedFixed_AddressOfScalar()
{
    var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        byte b = 42;
        fixed (byte* p = arr) {
            fixed (byte* q = &b) { }
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    Assert.Contains("MemorySegment q = MemorySegment.ofArray(new byte[] { b })", result.GeneratedCode);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CSharpToJava.Tests --filter "FullyQualifiedName~FixedBytePointer_AddressOfScalar" --no-build`
Expected: FAIL with generated code containing `ofArray(b)` instead of `ofArray(new byte[] { b })`.

- [ ] **Step 3: Implement fix in `TransformFixedStatement`**

In `StatementTransformer.SwitchAndResource.cs`, modify the loop body around line 742:

```csharp
foreach (var declarator in stmt.Declaration.Variables)
{
    var varName = declarator.Identifier.Text;
    var info = FfmHelper.CreatePointerInfo(varName, elementTypeName);
    pointerInfos.Add(info);

    bool isNull = declarator.Initializer?.Value is LiteralExpressionSyntax lit && lit.Token.IsKind(SyntaxKind.NullKeyword);
    bool isString = false;
    string initExpr = "";

    if (!isNull && declarator.Initializer != null)
    {
        var initValue = declarator.Initializer.Value;

        // Handle address-of scalar: fixed (byte* p = &b)
        if (initValue is PrefixUnaryExpressionSyntax addrOf && addrOf.OperatorToken.IsKind(SyntaxKind.AmpersandToken))
        {
            var operandExpr = ExpressionTransformerFacade.Instance.Transform(addrOf.Operand, context);
            var arrayType = FfmHelper.GetScratchArrayType(info.CSharpElementTypeName);
            sb.AppendLine($"MemorySegment {varName} = MemorySegment.ofArray(new {arrayType}[] {{ {operandExpr} }});");
        }
        else
        {
            initExpr = ExpressionTransformerFacade.Instance.Transform(initValue, context);
            var initType = context.GetTypeInfo(initValue).Type;
            isString = initType?.SpecialType == SpecialType.System_String;
            sb.AppendLine(FfmHelper.GenerateMemorySegmentInit(varName, initExpr, info, isString, isNull));
        }
    }
    else
    {
        sb.AppendLine(FfmHelper.GenerateMemorySegmentInit(varName, initExpr, info, isString, isNull));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CSharpToJava.Tests --filter "FullyQualifiedName~FixedBytePointer_AddressOfScalar" --no-build`
Expected: PASS

---

## Task 2: Fix pointer subtraction (`-`) and comparison (`<`, `>`, `<=`, `>=`) in binary expressions

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs:216-352`
- Test: `tests/CSharpToJava.Tests/FixedStatementFfmTests.cs`

**Root cause:** `TransformBinaryExpression` only handles `+` for pointer arithmetic. `-` on pointers and pointer comparisons are emitted as raw Java operators, which are illegal on `MemorySegment`.

**Java mapping:**
- `p - n` → `p.asSlice(-(long)n * elementSize)`
- `p - q` → `(p.address() - q.address()) / elementSize`
- `p < q` → `p.address() < q.address()`
- `p > q` → `p.address() > q.address()`
- `p <= q` → `p.address() <= q.address()`
- `p >= q` → `p.address() >= q.address()`

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public void PointerSubtractOffset_BytePointer()
{
    var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) {
            byte* q = p - 4;
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("p.asSlice(-4)", result.GeneratedCode);
}

[Fact]
public void PointerSubtractPointers_BytePointer()
{
    var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) {
            fixed (byte* q = arr) {
                long diff = p - q;
            }
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("(p.address() - q.address())", result.GeneratedCode);
}

[Fact]
public void PointerComparison_LessThan()
{
    var result = Convert(@"
unsafe class Test {
    void M(byte[] a, byte[] b) {
        fixed (byte* p = a) {
            fixed (byte* q = b) {
                bool v = p < q;
            }
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("p.address() < q.address()", result.GeneratedCode);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CSharpToJava.Tests --filter "FullyQualifiedName~PointerSubtract|PointerComparison" --no-build`
Expected: FAIL with Java compilation errors (operators not supported on MemorySegment).

- [ ] **Step 3: Implement pointer `-` handling**

In `BinaryExpressionTransformer.TransformBinaryExpression`, after the existing `if (op == "+")` block (around line 243), add:

```csharp
if (op == "-")
{
    var leftType = context.GetTypeInfo(node.Left).Type;
    var rightType = context.GetTypeInfo(node.Right).Type;

    // Pointer - integer
    if (leftType is IPointerTypeSymbol leftPtrType)
    {
        var facade2 = ExpressionTransformerFacade.Instance;
        var leftExpr = facade2.Transform(node.Left, context);
        var rightExpr = facade2.Transform(node.Right, context);

        var pointerInfo = context.FindPointerInfo(leftExpr.Trim());
        if (pointerInfo == null && leftPtrType.PointedAtType != null)
        {
            var elementTypeName = leftPtrType.PointedAtType.ToDisplayString();
            pointerInfo = FfmHelper.CreatePointerInfo("", elementTypeName);
        }

        if (pointerInfo != null)
        {
            if (pointerInfo.ElementSize == 1)
                return $"{leftExpr}.asSlice(-{rightExpr})";
            return $"{leftExpr}.asSlice(-(long){rightExpr} * {pointerInfo.ElementSize})";
        }
    }

    // Pointer - pointer
    if (leftType is IPointerTypeSymbol && rightType is IPointerTypeSymbol)
    {
        var facade2 = ExpressionTransformerFacade.Instance;
        var leftExpr = facade2.Transform(node.Left, context);
        var rightExpr = facade2.Transform(node.Right, context);

        var pointerInfo = context.FindPointerInfo(leftExpr.Trim());
        if (pointerInfo == null && leftType is IPointerTypeSymbol leftPtr)
        {
            var elementTypeName = leftPtr.PointedAtType?.ToDisplayString() ?? "byte";
            pointerInfo = FfmHelper.CreatePointerInfo("", elementTypeName);
        }

        var elementSize = pointerInfo?.ElementSize ?? 1;
        if (elementSize == 1)
            return $"({leftExpr}.address() - {rightExpr}.address())";
        return $"({leftExpr}.address() - {rightExpr}.address()) / {elementSize}";
    }
}
```

- [ ] **Step 4: Implement pointer comparison handling**

In `TransformBinaryExpression`, inside the standard operator path but before the `var facade = ...` line at ~245, add a guard for comparison operators on pointers. Place it right after the `op == "+"` block and before `var facade = ...`:

```csharp
// Handle pointer comparison operators (<, >, <=, >=)
if (IsComparisonOp(op) && context.SemanticModel != null)
{
    var leftType = context.GetTypeInfo(node.Left).Type;
    var rightType = context.GetTypeInfo(node.Right).Type;
    if (leftType is IPointerTypeSymbol && rightType is IPointerTypeSymbol)
    {
        var facade2 = ExpressionTransformerFacade.Instance;
        var leftExpr = facade2.Transform(node.Left, context);
        var rightExpr = facade2.Transform(node.Right, context);
        return $"{leftExpr}.address() {op} {rightExpr}.address()";
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/CSharpToJava.Tests --filter "FullyQualifiedName~PointerSubtract|PointerComparison" --no-build`
Expected: PASS

---

## Task 3: Fix pointer `+=` and `-=` compound assignments

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs:680-734`
- Test: `tests/CSharpToJava.Tests/FixedStatementFfmTests.cs`

**Root cause:** `AssignmentTransformer.TransformAssignment` falls through to `return $"{left} {op} {rightStr}";` for pointer compound assignments, generating `p += n` which is invalid on `MemorySegment`.

**Java mapping:**
- `p += n` → `p = p.asSlice((long)n * elementSize)`
- `p -= n` → `p = p.asSlice(-(long)n * elementSize)`

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public void PointerAddAssign_BytePointer()
{
    var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) {
            p += 4;
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("p = p.asSlice(4)", result.GeneratedCode);
}

[Fact]
public void PointerSubtractAssign_IntPointer()
{
    var result = Convert(@"
unsafe class Test {
    void M(int[] arr) {
        fixed (int* p = arr) {
            p -= 2;
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("p = p.asSlice(-(long)2 * 4)", result.GeneratedCode);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CSharpToJava.Tests --filter "FullyQualifiedName~PointerAddAssign|PointerSubtractAssign" --no-build`
Expected: FAIL with Java compilation error (operator += not supported on MemorySegment).

- [ ] **Step 3: Implement fix in `AssignmentTransformer`**

In `AssignmentTransformer.TransformAssignment`, just before the final `return $"{left} {op} {rightStr}";` (around line 733), add:

```csharp
// Handle pointer += / -=
if ((op == "+=" || op == "-=") && context.SemanticModel != null)
{
    var leftType = context.GetTypeInfo(leftNode).Type;
    if (leftType is IPointerTypeSymbol ptrType)
    {
        var leftExpr = facade.Transform(leftNode, context);
        var rightExpr = facade.Transform(rightNode, context);

        var pointerInfo = context.FindPointerInfo(leftExpr.Trim());
        if (pointerInfo == null && ptrType.PointedAtType != null)
        {
            var elementTypeName = ptrType.PointedAtType.ToDisplayString();
            pointerInfo = FfmHelper.CreatePointerInfo("", elementTypeName);
        }

        if (pointerInfo != null)
        {
            if (pointerInfo.ElementSize == 1)
            {
                var sliceExpr = op == "+="
                    ? $"{leftExpr}.asSlice({rightExpr})"
                    : $"{leftExpr}.asSlice(-{rightExpr})";
                return $"{leftExpr} = {sliceExpr}";
            }
            else
            {
                var sign = op == "+=" ? "" : "-";
                return $"{leftExpr} = {leftExpr}.asSlice({sign}(long){rightExpr} * {pointerInfo.ElementSize})";
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CSharpToJava.Tests --filter "FullyQualifiedName~PointerAddAssign|PointerSubtractAssign" --no-build`
Expected: PASS

---

## Task 4: Full test suite verification

- [ ] **Step 1: Run all FixedStatementFfmTests**

Run: `dotnet test tests/CSharpToJava.Tests --filter "FullyQualifiedName~FixedStatementFfmTests"`
Expected: All tests PASS

- [ ] **Step 2: Run full test suite**

Run: `dotnet test tests/CSharpToJava.Tests`
Expected: All tests PASS (or no new failures introduced)

---

## Self-Review

1. **Spec coverage:**
   - `ofArray(byte)` scalar issue → Task 1
   - Binary operator `-` on pointers → Task 2
   - Binary operators `<`, `>`, `<=`, `>=` on pointers → Task 2
   - Compound assignment `+=` on pointers → Task 3
   - Compound assignment `-=` on pointers → Task 3

2. **Placeholder scan:** No placeholders found; all steps contain exact code.

3. **Type consistency:** `FfmHelper.GetScratchArrayType` and `FfmHelper.CreatePointerInfo` are used consistently across tasks. `MemorySegment.address()` and `MemorySegment.asSlice()` are standard Java FFM API methods documented in `MemorySegment.json`.
