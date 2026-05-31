# Fixed Statement FFM Conversion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Systematically convert C# `fixed` statements and related pointer operations to Java FFM (Foreign Function & Memory) API calls in the old pipeline, with full unit test coverage.

**Architecture:** Add a `FfmHelper` utility class for FFM code generation, extend `ConversionContext` with fixed-scope tracking, and modify the statement/expression transformers to generate `MemorySegment`-based Java code instead of TODO comments. All primitive type pointers are supported, with special byte unsigned masking and char string-to-charArray handling.

**Tech Stack:** C# (.NET 10), Roslyn (Microsoft.CodeAnalysis.CSharp 4.12.0), XUnit 2.9.3, Java FFM API (java.lang.foreign.*)

---

## File Structure

| Action | File | Responsibility |
|--------|------|----------------|
| Create | `src/CSharpToJava.Core/Transformers/Expression/Utilities/FfmHelper.cs` | FFM code generation utilities (layout names, sizes, masks, read/write) |
| Create | `tests/CSharpToJava.Tests/FixedStatementFfmTests.cs` | 31 unit tests for fixed statement FFM conversion |
| Modify | `src/CSharpToJava.Core/Context/ConversionContext.cs` | Add `FixedPointerInfo` class and fixed-scope tracking stack |
| Modify | `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs` | Replace `TransformFixedStatement` and `TransformUnsafeStatement` stubs |
| Modify | `src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs` | Update `TransformPointerIndirection` and `TransformAddressOf` for fixed scope |
| Modify | `src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs` | Handle pointer index access `p[i]` in fixed scope |
| Modify | `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs` | Update `TransformPointerMemberAccess` for fixed scope |
| Modify | `src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs` | Convert fixed-size buffer fields to MemorySegment |

---

### Task 1: Create FfmHelper utility class

**Files:**
- Create: `src/CSharpToJava.Core/Transformers/Expression/Utilities/FfmHelper.cs`

- [ ] **Step 1: Write FfmHelper.cs**

```csharp
using System;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Transformers.Expression.Utilities;

public class FixedPointerInfo
{
    public string VariableName { get; set; } = "";
    public string CSharpElementTypeName { get; set; } = "";
    public string ValueLayoutName { get; set; } = "";
    public int ElementSize { get; set; }
    public bool NeedsUnsignedMask { get; set; }
    public string MaskSuffix { get; set; } = "";
    public string WriteCast { get; set; } = "";
}

public static class FfmHelper
{
    public static string GetValueLayoutName(string csharpElementType) => csharpElementType switch
    {
        "byte" or "Byte" or "System.Byte" => "JAVA_BYTE",
        "sbyte" or "SByte" or "System.SByte" => "JAVA_BYTE",
        "char" or "Char" or "System.Char" => "JAVA_CHAR",
        "short" or "Short" or "System.Int16" => "JAVA_SHORT",
        "ushort" or "UInt16" or "System.UInt16" => "JAVA_CHAR",
        "int" or "Int32" or "System.Int32" => "JAVA_INT",
        "uint" or "UInt32" or "System.UInt32" => "JAVA_INT",
        "long" or "Int64" or "System.Int64" => "JAVA_LONG",
        "ulong" or "UInt64" or "System.UInt64" => "JAVA_LONG",
        "float" or "Single" or "System.Single" => "JAVA_FLOAT",
        "double" or "Double" or "System.Double" => "JAVA_DOUBLE",
        "bool" or "Boolean" or "System.Boolean" => "JAVA_BOOLEAN",
        _ => "JAVA_BYTE"
    };

    public static int GetElementSize(string csharpElementType) => csharpElementType switch
    {
        "byte" or "Byte" or "System.Byte" => 1,
        "sbyte" or "SByte" or "System.SByte" => 1,
        "char" or "Char" or "System.Char" => 2,
        "short" or "Short" or "System.Int16" => 2,
        "ushort" or "UInt16" or "System.UInt16" => 2,
        "int" or "Int32" or "System.Int32" => 4,
        "uint" or "UInt32" or "System.UInt32" => 4,
        "long" or "Int64" or "System.Int64" => 8,
        "ulong" or "UInt64" or "System.UInt64" => 8,
        "float" or "Single" or "System.Single" => 4,
        "double" or "Double" or "System.Double" => 8,
        "bool" or "Boolean" or "System.Boolean" => 1,
        _ => 1
    };

    public static bool NeedsUnsignedMask(string csharpElementType) => csharpElementType switch
    {
        "byte" or "Byte" or "System.Byte" => true,
        "ushort" or "UInt16" or "System.UInt16" => true,
        "uint" or "UInt32" or "System.UInt32" => true,
        _ => false
    };

    public static string GetMaskSuffix(string csharpElementType) => csharpElementType switch
    {
        "byte" or "Byte" or "System.Byte" => "& 0xFF",
        "ushort" or "UInt16" or "System.UInt16" => "& 0xFFFF",
        "uint" or "UInt32" or "System.UInt32" => "& 0xFFFFFFFFL",
        _ => ""
    };

    public static string GetWriteCast(string csharpElementType) => csharpElementType switch
    {
        "byte" or "Byte" or "System.Byte" => "(byte)",
        "ushort" or "UInt16" or "System.UInt16" => "(char)",
        "uint" or "UInt32" or "System.UInt32" => "(int)",
        _ => ""
    };

    public static FixedPointerInfo CreatePointerInfo(string variableName, string csharpElementType)
    {
        return new FixedPointerInfo
        {
            VariableName = variableName,
            CSharpElementTypeName = csharpElementType,
            ValueLayoutName = GetValueLayoutName(csharpElementType),
            ElementSize = GetElementSize(csharpElementType),
            NeedsUnsignedMask = NeedsUnsignedMask(csharpElementType),
            MaskSuffix = GetMaskSuffix(csharpElementType),
            WriteCast = GetWriteCast(csharpElementType)
        };
    }

    public static string GeneratePointerRead(string segmentExpr, FixedPointerInfo info, string offsetExpr)
    {
        string read = $"{segmentExpr}.get(ValueLayout.{info.ValueLayoutName}, {offsetExpr})";
        return info.NeedsUnsignedMask ? $"({read} {info.MaskSuffix})" : read;
    }

    public static string GeneratePointerWrite(string segmentExpr, FixedPointerInfo info, string offsetExpr, string valueExpr)
    {
        string castValue = info.WriteCast.Length > 0 ? $"{info.WriteCast} {valueExpr}" : valueExpr;
        return $"{segmentExpr}.set(ValueLayout.{info.ValueLayoutName}, {offsetExpr}, {castValue})";
    }

    public static string GeneratePointerArithmetic(string segmentExpr, FixedPointerInfo info, string offsetExpr)
    {
        if (info.ElementSize == 1)
            return $"{segmentExpr}.asSlice({offsetExpr})";
        return $"{segmentExpr}.asSlice((long){offsetExpr} * {info.ElementSize})";
    }

    public static string GenerateMemorySegmentInit(string variableName, string initializerExpr, FixedPointerInfo info, bool isString, bool isNull)
    {
        if (isNull)
            return $"MemorySegment {variableName} = MemorySegment.NULL;";
        if (isString)
            return $"MemorySegment {variableName} = MemorySegment.ofArray({initializerExpr}.toCharArray());";
        return $"MemorySegment {variableName} = MemorySegment.ofArray({initializerExpr});";
    }

    public static string[] GetRequiredImports(bool usesArena)
    {
        if (usesArena)
            return ["java.lang.foreign.MemorySegment", "java.lang.foreign.ValueLayout", "java.lang.foreign.Arena"];
        return ["java.lang.foreign.MemorySegment", "java.lang.foreign.ValueLayout"];
    }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --no-restore`
Expected: Build succeeds with no errors.

---

### Task 2: Add fixed-scope tracking to ConversionContext

**Files:**
- Modify: `src/CSharpToJava.Core/Context/ConversionContext.cs`

- [ ] **Step 1: Add FixedPointerInfo import and fixed-scope stack fields**

Add after line 8 (`using CSharpToJava.Core.Transformers;`):
```csharp
using CSharpToJava.Core.Transformers.Expression.Utilities;
```

Add after line 20 (`private readonly Stack<Dictionary<string, string>> _runtimeClassFieldsStack = new();`):
```csharp
private readonly Stack<List<FixedPointerInfo>> _fixedScopeStack = new();
public bool IsInFixedScope => _fixedScopeStack.Count > 0;
public void PushFixedScope(List<FixedPointerInfo> pointers) => _fixedScopeStack.Push(pointers);
public void PopFixedScope() => _fixedScopeStack.Pop();
public FixedPointerInfo? FindPointerInfo(string varName)
{
    foreach (var scope in _fixedScopeStack)
    {
        var info = scope.FirstOrDefault(p => p.VariableName == varName);
        if (info != null) return info;
    }
    return null;
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --no-restore`
Expected: Build succeeds.

---

### Task 3: Implement TransformFixedStatement

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs`

- [ ] **Step 1: Add using directive**

Add at the top of the file with the other usings:
```csharp
using CSharpToJava.Core.Transformers.Expression.Utilities;
```

- [ ] **Step 2: Replace TransformFixedStatement stub**

Replace the existing `TransformFixedStatement` method (lines 465-472):

```csharp
private JavaSyntaxNode TransformFixedStatement(FixedStatementSyntax stmt, ConversionContext context)
{
    var sb = new StringBuilder();
    var pointerInfos = new List<FixedPointerInfo>();

    var pointerType = stmt.Declaration.Type as PointerTypeSyntax;
    if (pointerType == null)
    {
        context.Diagnostics.Error("Fixed statement requires a pointer type declaration.", stmt.GetLocation());
        return new JavaStatementNode("/* TODO: Fixed statement - unsupported declaration */");
    }

    var elementTypeName = GetPointerElementTypeName(pointerType.ElementType);

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
            initExpr = ExpressionTransformerFacade.Instance.Transform(declarator.Initializer.Value, context);
            var initType = context.SemanticModel?.GetTypeInfo(declarator.Initializer.Value).Type;
            isString = initType?.SpecialType == SpecialType.System_String;
        }

        sb.AppendLine(FfmHelper.GenerateMemorySegmentInit(varName, initExpr, info, isString, isNull));
    }

    var imports = FfmHelper.GetRequiredImports(false);
    foreach (var imp in imports)
        context.AddImport(imp);

    context.PushFixedScope(pointerInfos);

    var body = stmt.Statement is BlockSyntax block
        ? TransformBlock(block, context)
        : Transform(stmt.Statement, context).ToString("");

    context.PopFixedScope();

    return new JavaStatementNode(sb.ToString() + body);
}

private static string GetPointerElementTypeName(TypeSyntax elementType)
{
    if (elementType is PredefinedTypeSyntax predefined)
        return predefined.Keyword.Text;
    if (elementType is IdentifierNameSyntax identifier)
        return identifier.Identifier.Text;
    return elementType.ToString();
}
```

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --no-restore`
Expected: Build succeeds.

---

### Task 4: Implement TransformUnsafeStatement

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs`

- [ ] **Step 1: Replace TransformUnsafeStatement stub**

Replace the existing `TransformUnsafeStatement` method (lines 474-481):

```csharp
private JavaSyntaxNode TransformUnsafeStatement(UnsafeStatementSyntax stmt, ConversionContext context)
{
    return stmt.Block != null
        ? new JavaStatementNode(TransformBlock(stmt.Block, context))
        : new JavaStatementNode("");
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --no-restore`
Expected: Build succeeds.

---

### Task 5: Update TransformPointerIndirection for fixed scope

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs`

- [ ] **Step 1: Add using directive**

Add at the top of the file with the other usings:
```csharp
using CSharpToJava.Core.Transformers.Expression.Utilities;
```

- [ ] **Step 2: Replace TransformPointerIndirection method**

Replace the existing `TransformPointerIndirection` method (lines 333-340):

```csharp
private string TransformPointerIndirection(PrefixUnaryExpressionSyntax node, ConversionContext context)
{
    var facade = ExpressionTransformerFacade.Instance;
    var operand = facade.Transform(node.Operand, context);

    if (context.IsInFixedScope)
    {
        var operandText = operand.Trim();
        var pointerInfo = context.FindPointerInfo(operandText);
        if (pointerInfo != null)
        {
            return FfmHelper.GeneratePointerRead(operandText, pointerInfo, "0");
        }
    }

    context.Diagnostics.Warning("Pointer indirection operator (*) has no Java equivalent - unsafe code not supported", node.GetLocation());
    return $"/* unsafe: pointer deref */ {operand}";
}
```

- [ ] **Step 3: Replace TransformAddressOf method**

Replace the existing `TransformAddressOf` method (lines 324-331):

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

    context.Diagnostics.Warning("Address-of operator (&) has no Java equivalent - converting to unsafe memory access", node.GetLocation());
    return $"/* C# addressof — no Java equivalent: {operand} */";
}
```

- [ ] **Step 4: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --no-restore`
Expected: Build succeeds.

---

### Task 6: Handle pointer index access p[i] in ElementAccessTransformer

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs`

- [ ] **Step 1: Add using directive**

Add at the top of the file with the other usings:
```csharp
using CSharpToJava.Core.Transformers.Expression.Utilities;
```

- [ ] **Step 2: Add pointer index access detection at the start of TransformElementAccess**

Insert at the beginning of the `TransformElementAccess` method (after line 112 `var facade = ...`), before the existing `var expr = ...` line:

```csharp
if (context.IsInFixedScope && node.ArgumentList.Arguments.Count == 1)
{
    var targetExpr = facade.Transform(node.Expression, context);
    var targetType = context.SemanticModel?.GetTypeInfo(node.Expression).Type;
    if (targetType is IPointerTypeSymbol pointerType)
    {
        var pointeeType = pointerType.PointedAtType;
        var elementTypeName = GetPointeeTypeName(pointeeType);
        var pointerInfo = context.FindPointerInfo(targetExpr.Trim());
        if (pointerInfo != null)
        {
            var idxExpr = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            string offsetExpr = pointerInfo.ElementSize == 1
                ? idxExpr
                : $"(long){idxExpr} * {pointerInfo.ElementSize}";
            return FfmHelper.GeneratePointerRead(targetExpr.Trim(), pointerInfo, offsetExpr);
        }
        var idxExprFallback = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
        return $"/* pointer index access */ {targetExpr}.get(ValueLayout.{FfmHelper.GetValueLayoutName(elementTypeName)}, {idxExprFallback})";
    }
}
```

- [ ] **Step 3: Add helper method GetPointeeTypeName**

Add at the bottom of the `ElementAccessTransformer` class (before the closing `}`):

```csharp
private static string GetPointeeTypeName(ITypeSymbol type)
{
    if (type.SpecialType != SpecialType.None)
        return type.SpecialType switch
        {
            SpecialType.System_Byte => "byte",
            SpecialType.System_SByte => "sbyte",
            SpecialType.System_Char => "char",
            SpecialType.System_Int16 => "short",
            SpecialType.System_UInt16 => "ushort",
            SpecialType.System_Int32 => "int",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_Int64 => "long",
            SpecialType.System_UInt64 => "ulong",
            SpecialType.System_Single => "float",
            SpecialType.System_Double => "double",
            SpecialType.System_Boolean => "bool",
            _ => type.Name
        };
    return type.Name;
}
```

- [ ] **Step 4: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --no-restore`
Expected: Build succeeds.

---

### Task 7: Update TransformPointerMemberAccess for fixed scope

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`

- [ ] **Step 1: Add using directive**

Add at the top of the file with the other usings (if not already present):
```csharp
using CSharpToJava.Core.Transformers.Expression.Utilities;
```

- [ ] **Step 2: Update TransformPointerMemberAccess method**

Replace the existing `TransformPointerMemberAccess` method (lines 1527-1536):

```csharp
private string TransformPointerMemberAccess(MemberAccessExpressionSyntax node, ConversionContext context)
{
    var facade = ExpressionTransformerFacade.Instance;
    var target = facade.Transform(node.Expression, context);
    var member = ConversionContext.EscapeJavaKeyword(node.Name.Identifier.Text);

    if (context.IsInFixedScope)
    {
        context.Diagnostics.Warning("Pointer member access (->) requires struct layout info - converting to field access", node.GetLocation());
        return $"{target}.{member}";
    }

    context.Diagnostics.Warning("Pointer member access (->) has no Java equivalent - unsafe code not supported", node.GetLocation());
    return $"/* WARNING: C# unsafe pointer dereference — Java does not support pointer arithmetic. */ {target}.{member}";
}
```

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --no-restore`
Expected: Build succeeds.

---

### Task 8: Convert fixed-size buffer fields to MemorySegment

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs`

- [ ] **Step 1: Add using directive**

Add at the top of the file with the other usings:
```csharp
using CSharpToJava.Core.Transformers.Expression.Utilities;
```

- [ ] **Step 2: Replace fixed-size buffer error with MemorySegment conversion**

Replace lines 58-59:
```csharp
if (fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.FixedKeyword)))
    context.Diagnostics.Error("Java doesn't support fixed-size buffers. Field needs manual conversion.", fieldDecl.GetLocation());
```

With:
```csharp
if (fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.FixedKeyword)))
{
    var fixedArray = (ArrayTypeSyntax)fieldDecl.Declaration.Type;
    var elementTypeName = fixedArray.ElementType is PredefinedTypeSyntax pre
        ? pre.Keyword.Text
        : fixedArray.ElementType.ToString();
    var sizeValue = fieldDecl.Declaration.Variables.FirstOrDefault()?.ArgumentList?.Arguments.Count > 0
        ? fieldDecl.Declaration.Variables[0].ArgumentList.Arguments[0].ToString()
        : "0";

    foreach (var variable in fieldDecl.Declaration.Variables)
    {
        var varName = variable.Identifier.Text;
        var arraySizeStr = variable.ArgumentList?.Arguments.FirstOrDefault()?.ToString() ?? "0";
        var info = FfmHelper.CreatePointerInfo(varName, elementTypeName);
        long byteSize = info.ElementSize * (int.TryParse(arraySizeStr, out var n) ? n : 0);

        var javaField = new JavaFieldDeclaration
        {
            Name = varName,
            Type = "MemorySegment",
            Modifiers = modifiers,
            Initializer = $"Arena.ofAuto().allocate({byteSize}, ValueLayout.{info.ValueLayoutName})",
            Comment = sharedComment
        };
        context.CurrentType?.Fields.Add(javaField);
        commentAssigned = true;
    }

    var imports = FfmHelper.GetRequiredImports(true);
    foreach (var imp in imports)
        context.AddImport(imp);

    return;
}
```

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --no-restore`
Expected: Build succeeds.

---

### Task 9: Write unit tests — Fixed statement basic conversion (8 tests)

**Files:**
- Create: `tests/CSharpToJava.Tests/FixedStatementFfmTests.cs`

- [ ] **Step 1: Create test file with basic conversion tests**

```csharp
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class FixedStatementFfmTests
{
    [Fact]
    public void FixedBytePointer_Array()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    }

    [Fact]
    public void FixedSBytePointer_Array()
    {
        var result = Convert(@"
unsafe class Test {
    void M(sbyte[] arr) {
        fixed (sbyte* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    }

    [Fact]
    public void FixedCharPointer_Array()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr) {
        fixed (char* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    }

    [Fact]
    public void FixedShortPointer_Array()
    {
        var result = Convert(@"
unsafe class Test {
    void M(short[] arr) {
        fixed (short* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    }

    [Fact]
    public void FixedIntPointer_Array()
    {
        var result = Convert(@"
unsafe class Test {
    void M(int[] arr) {
        fixed (int* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    }

    [Fact]
    public void FixedLongPointer_Array()
    {
        var result = Convert(@"
unsafe class Test {
    void M(long[] arr) {
        fixed (long* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    }

    [Fact]
    public void FixedFloatPointer_Array()
    {
        var result = Convert(@"
unsafe class Test {
    void M(float[] arr) {
        fixed (float* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    }

    [Fact]
    public void FixedDoublePointer_Array()
    {
        var result = Convert(@"
unsafe class Test {
    void M(double[] arr) {
        fixed (double* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
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
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FixedStatementFfmTests" --no-build -v n`
Expected: All 8 tests PASS.

---

### Task 10: Write unit tests — Byte special handling (6 tests)

**Files:**
- Modify: `tests/CSharpToJava.Tests/FixedStatementFfmTests.cs`

- [ ] **Step 1: Add byte special handling tests**

Add the following tests to the `FixedStatementFfmTests` class (before the `Convert` helper):

```csharp
[Fact]
public void FixedBytePointer_DerefRead()
{
    var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) {
            byte v = *p;
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("p.get(ValueLayout.JAVA_BYTE, 0) & 0xFF", result.GeneratedCode);
}

[Fact]
public void FixedBytePointer_DerefWrite()
{
    var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) {
            *p = 42;
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("p.set(ValueLayout.JAVA_BYTE, 0, (byte) 42)", result.GeneratedCode);
}

[Fact]
public void FixedBytePointer_IndexRead()
{
    var result = Convert(@"
unsafe class Test {
    void M(byte[] arr, int i) {
        fixed (byte* p = arr) {
            byte v = p[i];
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("p.get(ValueLayout.JAVA_BYTE, i) & 0xFF", result.GeneratedCode);
}

[Fact]
public void FixedBytePointer_IndexWrite()
{
    var result = Convert(@"
unsafe class Test {
    void M(byte[] arr, int i) {
        fixed (byte* p = arr) {
            p[i] = 42;
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("p.set(ValueLayout.JAVA_BYTE, i, (byte) 42)", result.GeneratedCode);
}

[Fact]
public void FixedBytePointer_ArithmeticResult()
{
    var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) {
            byte v = (byte)(p[0] + p[1]);
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("& 0xFF", result.GeneratedCode);
}

[Fact]
public void FixedSBytePointer_NoMask()
{
    var result = Convert(@"
unsafe class Test {
    void M(sbyte[] arr) {
        fixed (sbyte* p = arr) {
            sbyte v = *p;
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("p.get(ValueLayout.JAVA_BYTE, 0)", result.GeneratedCode);
    Assert.DoesNotContain("& 0xFF", result.GeneratedCode);
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FixedStatementFfmTests" --no-build -v n`
Expected: All 14 tests PASS.

---

### Task 11: Write unit tests — Char special handling (3 tests)

**Files:**
- Modify: `tests/CSharpToJava.Tests/FixedStatementFfmTests.cs`

- [ ] **Step 1: Add char special handling tests**

```csharp
[Fact]
public void FixedCharPointer_DerefRead()
{
    var result = Convert(@"
unsafe class Test {
    void M(char[] arr) {
        fixed (char* p = arr) {
            char v = *p;
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("p.get(ValueLayout.JAVA_CHAR, 0)", result.GeneratedCode);
}

[Fact]
public void FixedCharPointer_DerefWrite()
{
    var result = Convert(@"
unsafe class Test {
    void M(char[] arr) {
        fixed (char* p = arr) {
            *p = 'a';
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("p.set(ValueLayout.JAVA_CHAR, 0, 'a')", result.GeneratedCode);
}

[Fact]
public void FixedCharPointer_FromString()
{
    var result = Convert(@"
unsafe class Test {
    void M(string str) {
        fixed (char* p = str) { }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("MemorySegment.ofArray(str.toCharArray())", result.GeneratedCode);
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FixedStatementFfmTests" --no-build -v n`
Expected: All 17 tests PASS.

---

### Task 12: Write unit tests — Pointer arithmetic (4 tests)

**Files:**
- Modify: `tests/CSharpToJava.Tests/FixedStatementFfmTests.cs`

- [ ] **Step 1: Add pointer arithmetic tests**

```csharp
[Fact]
public void PointerAddOffset_BytePointer()
{
    var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) {
            byte* q = p + 4;
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("p.asSlice(4)", result.GeneratedCode);
}

[Fact]
public void PointerIncrement_BytePointer()
{
    var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) {
            p++;
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("p = p.asSlice(1)", result.GeneratedCode);
}

[Fact]
public void IntPointerAddOffset()
{
    var result = Convert(@"
unsafe class Test {
    void M(int[] arr) {
        fixed (int* p = arr) {
            int* q = p + 4;
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("asSlice((long)4 * 4)", result.GeneratedCode);
}

[Fact]
public void PointerIndexAccess_IntPointer()
{
    var result = Convert(@"
unsafe class Test {
    void M(int[] arr) {
        fixed (int* p = arr) {
            int v = p[2];
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("p.get(ValueLayout.JAVA_INT, (long)2 * 4)", result.GeneratedCode);
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FixedStatementFfmTests" --no-build -v n`
Expected: All 21 tests PASS.

---

### Task 13: Write unit tests — Unsafe block handling (2 tests)

**Files:**
- Modify: `tests/CSharpToJava.Tests/FixedStatementFfmTests.cs`

- [ ] **Step 1: Add unsafe block tests**

```csharp
[Fact]
public void UnsafeBlock_Stripped()
{
    var result = Convert(@"
unsafe class Test {
    void M() {
        unsafe { int x = 1; }
    }
}");
    Assert.True(result.Success);
    Assert.DoesNotContain("unsafe", result.GeneratedCode);
    Assert.Contains("int x = 1", result.GeneratedCode);
}

[Fact]
public void UnsafeBlock_WithFixedInside()
{
    var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        unsafe {
            fixed (byte* p = arr) { }
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    Assert.DoesNotContain("/* TODO: Unsafe", result.GeneratedCode);
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FixedStatementFfmTests" --no-build -v n`
Expected: All 23 tests PASS.

---

### Task 14: Write unit tests — Fixed-size buffer fields (3 tests)

**Files:**
- Modify: `tests/CSharpToJava.Tests/FixedStatementFfmTests.cs`

- [ ] **Step 1: Add fixed-size buffer field tests**

```csharp
[Fact]
public void FixedBufferField_Byte()
{
    var result = Convert(@"
unsafe class Test {
    fixed byte buffer[256];
}");
    Assert.True(result.Success);
    Assert.Contains("MemorySegment buffer", result.GeneratedCode);
    Assert.Contains("Arena.ofAuto().allocate(256, ValueLayout.JAVA_BYTE)", result.GeneratedCode);
}

[Fact]
public void FixedBufferField_Char()
{
    var result = Convert(@"
unsafe class Test {
    fixed char buffer[128];
}");
    Assert.True(result.Success);
    Assert.Contains("MemorySegment buffer", result.GeneratedCode);
    Assert.Contains("Arena.ofAuto().allocate(256, ValueLayout.JAVA_CHAR)", result.GeneratedCode);
}

[Fact]
public void FixedBufferField_Int()
{
    var result = Convert(@"
unsafe class Test {
    fixed int buffer[64];
}");
    Assert.True(result.Success);
    Assert.Contains("MemorySegment buffer", result.GeneratedCode);
    Assert.Contains("Arena.ofAuto().allocate(256, ValueLayout.JAVA_INT)", result.GeneratedCode);
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FixedStatementFfmTests" --no-build -v n`
Expected: All 26 tests PASS.

---

### Task 15: Write unit tests — Import verification and edge cases (5 tests)

**Files:**
- Modify: `tests/CSharpToJava.Tests/FixedStatementFfmTests.cs`

- [ ] **Step 1: Add import and edge case tests**

```csharp
[Fact]
public void FixedStatement_Imports()
{
    var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) { }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("import java.lang.foreign.MemorySegment", result.GeneratedCode);
    Assert.Contains("import java.lang.foreign.ValueLayout", result.GeneratedCode);
}

[Fact]
public void FixedBufferField_Imports()
{
    var result = Convert(@"
unsafe class Test {
    fixed byte buffer[256];
}");
    Assert.True(result.Success);
    Assert.Contains("import java.lang.foreign.Arena", result.GeneratedCode);
}

[Fact]
public void FixedNullPointer()
{
    var result = Convert(@"
unsafe class Test {
    void M() {
        fixed (byte* p = null) { }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("MemorySegment.NULL", result.GeneratedCode);
}

[Fact]
public void FixedMultipleDeclarators()
{
    var result = Convert(@"
unsafe class Test {
    void M(byte[] a, byte[] b) {
        fixed (byte* p1 = a, p2 = b) { }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("MemorySegment p1 = MemorySegment.ofArray(a)", result.GeneratedCode);
    Assert.Contains("MemorySegment p2 = MemorySegment.ofArray(b)", result.GeneratedCode);
}

[Fact]
public void NestedFixedStatements()
{
    var result = Convert(@"
unsafe class Test {
    void M(byte[] a, int[] b) {
        fixed (byte* p = a) {
            fixed (int* q = b) { }
        }
    }
}");
    Assert.True(result.Success);
    Assert.Contains("MemorySegment p = MemorySegment.ofArray(a)", result.GeneratedCode);
    Assert.Contains("MemorySegment q = MemorySegment.ofArray(b)", result.GeneratedCode);
}
```

- [ ] **Step 2: Run all tests**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FixedStatementFfmTests" --no-build -v n`
Expected: All 31 tests PASS.

---

### Task 16: Run full test suite and verify no regressions

- [ ] **Step 1: Build the entire solution**

Run: `dotnet build CSharpToJavaConverter.slnx`
Expected: Build succeeds with no errors.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -v n`
Expected: All existing tests still pass, plus the 31 new FixedStatementFfmTests pass.

- [ ] **Step 3: Commit all changes**

```bash
git add -A
git commit -m "feat: implement fixed statement FFM conversion with byte/char support

- Convert C# fixed statements to Java FFM MemorySegment API
- Support all primitive type pointers (byte/sbyte/char/short/ushort/int/uint/long/ulong/float/double/bool)
- Handle byte unsigned masking (& 0xFF) for FFM MemorySegment reads
- Handle char string-to-charArray conversion for fixed (char* p = str)
- Strip unsafe modifier from unsafe blocks, convert inner code normally
- Convert fixed-size buffer fields to MemorySegment with Arena.ofAuto()
- Add FfmHelper utility class for FFM code generation
- Add FixedPointerInfo and fixed-scope tracking to ConversionContext
- Update pointer indirection, address-of, and element access transformers
- Add 31 unit tests covering all conversion scenarios"
```
