# Fix Project-Level Conversion Errors — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix 6 root causes causing 76 Java compile errors after MSAGL project conversion

**Architecture:** Each RC gets a converter-level unit test (C# input → ConversionPipeline → assert Java output), then a minimal fix in the converter's transformer/lowering code. After each fix, verify the corresponding errors disappear from a full MSAGL project conversion.

**Tech Stack:** C#, Roslyn (Microsoft.CodeAnalysis), xUnit, dotnet CLI

---

## Shared Test Helper

All tests use this pattern:

```csharp
private static ConversionResult Convert(string sourceCode)
{
    var pipeline = new ConversionPipeline();
    return pipeline.Convert(new ConversionRequest
    {
        SourceCode = sourceCode,
        FileName = "Test.cs",
        Options = new ConversionOptions(),
    });
}
```

---

### Task 1: RC1 — Fix `!int` unary operator in IR pipeline

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs`
- Create: `tests/CSharpToJava.Tests/LogicalNotOnIntIrTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/CSharpToJava.Tests/LogicalNotOnIntIrTests.cs`:

```csharp
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class LogicalNotOnIntIrTests
{
    [Fact]
    public void LogicalNot_OnInt_EmitsEqualToZero()
    {
        var result = Convert(@"
class Test
{
    bool M(int x)
    {
        if (!x)
            return false;
        return true;
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain("!x", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("(x == 0)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LogicalNot_OnBool_KeepsExclamation()
    {
        var result = Convert(@"
class Test
{
    bool M(bool flag)
    {
        return !flag;
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("!flag", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~LogicalNotOnIntIrTests" -v n`
Expected: FAIL — `!x` appears in output instead of `(x == 0)`

- [ ] **Step 3: Fix `TransformToIR()` to check boolean type for `!` operator**

In `UnaryExpressionTransformer.cs`, in `TransformToIR()`, after the `isPropertyTarget` check (line 96) and before `var operandIR = ...` (line 98), add:

```csharp
                // For LogicalNotExpression (!), verify the operand is boolean.
                // C# allows ! on int (0→true, non-zero→false) but Java requires
                // boolean. Non-boolean operands must fall back to the raw string path
                // which calls TransformLogicalNot() → rewrites to (operand == 0).
                if (node.Kind() == SyntaxKind.LogicalNotExpression
                    && context.SemanticModel != null
                    && operandSyntax != null)
                {
                    var opTypeInfo = context.SemanticModel.GetTypeInfo(operandSyntax);
                    if (opTypeInfo.Type == null
                        || opTypeInfo.Type.SpecialType != SpecialType.System_Boolean)
                    {
                        return new JavaRawExpression(Transform(node, context));
                    }
                }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~LogicalNotOnIntIrTests" -v n`
Expected: PASS

- [ ] **Step 5: Run all tests**

Run: `dotnet test -v n`
Expected: All tests pass

- [ ] **Step 6: Verify against MSAGL**

Run: `dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s D:\MSAGL -d d:\aj31648`
Compile the Java project. Verify LgInteractor.java and OverlapRemovalFixedSegmentsMst.java `!int` errors are gone.

- [ ] **Step 7: Commit**

```bash
git add tests/CSharpToJava.Tests/LogicalNotOnIntIrTests.cs src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs
git commit -m "fix: check boolean type for ! operator in TransformToIR path"
```

---

### Task 2: RC2 — Fix `Stream.size()` to emit `.count()` for Stream receivers

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`
- Create: `tests/CSharpToJava.Tests/CountOnStreamTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/CSharpToJava.Tests/CountOnStreamTests.cs`:

```csharp
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class CountOnStreamTests
{
    [Fact]
    public void Count_AfterUnion_EmitsCountNotSize()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Test
{
    int M(List<int> a, List<int> b)
    {
        return a.Union(b).Count();
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain(".size()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".count()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Count_AfterDistinct_EmitsCountNotSize()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Test
{
    int M(List<int> a)
    {
        return a.Distinct().Count();
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain(".size()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".count()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CountAsProperty_OnList_EmitsSize()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test
{
    int M(List<int> a) { return a.Count; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains(".size()", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~CountOnStreamTests" -v n`
Expected: FAIL — `.size()` appears for Union+Count instead of `.count()`

- [ ] **Step 3: Fix `TransformMemberInvocation` — detect Stream receivers**

In `InvocationExpressionTransformer.cs`, modify lines 422-426:

Change:
```csharp
            var cReceiver = facade.Transform(memberAccess.Expression, context);
            if (memberAccess.Name.Identifier.Text == "Count")
                return $"{cReceiver}.size()";
```

To:
```csharp
            var cReceiver = facade.Transform(memberAccess.Expression, context);
            if (memberAccess.Name.Identifier.Text == "Count")
            {
                // If the receiver is a Stream pipeline (from Union/Intersect/Distinct/etc.),
                // emit .count() — Java Stream uses count(), not size().
                if (ReceiverLooksLikeStream(cReceiver))
                    return $"{cReceiver}.count()";
                return $"{cReceiver}.size()";
            }
```

Add helper at the bottom of the class:

```csharp
    private static bool ReceiverLooksLikeStream(string receiver)
    {
        return receiver.Contains(".distinct()")
            || receiver.Contains(".filter(")
            || receiver.Contains(".map(")
            || receiver.Contains(".flatMap(")
            || receiver.Contains(".sorted(")
            || receiver.Contains(".limit(")
            || receiver.Contains(".skip(")
            || receiver.Contains(".dropWhile(")
            || receiver.Contains(".takeWhile(")
            || receiver.Contains("Stream.concat(");
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~CountOnStreamTests" -v n`
Expected: All 3 tests PASS

- [ ] **Step 5: Run all tests**

Run: `dotnet test -v n`
Expected: All tests pass

- [ ] **Step 6: Verify against MSAGL**

Run full conversion. Verify LayoutAlgorithmHelpers.java errors are gone.

- [ ] **Step 7: Remove CLI patches in Program.cs**

Remove lines 1212-1217 (LayoutAlgorithmHelpers `.distinct().size()` → `.distinct().count()` patch).

- [ ] **Step 8: Commit**

```bash
git add tests/CSharpToJava.Tests/CountOnStreamTests.cs src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs src/CSharpToJava.CLI/Program.cs
git commit -m "fix: emit .count() instead of .size() for LINQ Count() on Stream receivers"
```

---

### Task 3: RC5 — Expand `IsEnumeratorLikeType` to match Java Iterator types

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`
- Modify: `src/CSharpToJava.Core/Lowering/LowerProperty.cs`
- Create: `tests/CSharpToJava.Tests/EnumeratorCurrentToNextTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/CSharpToJava.Tests/EnumeratorCurrentToNextTests.cs`:

```csharp
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class EnumeratorCurrentToNextTests
{
    [Fact]
    public void CustomEnumerator_Current_EmitsNext()
    {
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;

class MyEnumerator : IEnumerator<int>
{
    int[] data;
    int pos = -1;
    public int Current => data[pos];
    object IEnumerator.Current => data[pos];
    public bool MoveNext() { pos++; return pos < data.Length; }
    public void Reset() { pos = -1; }
    public void Dispose() { }
}

class User
{
    int M(int[] arr)
    {
        var en = new MyEnumerator(arr);
        en.MoveNext();
        return en.Current;
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain("getCurrent()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".next()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IEnumeratorParameter_Current_EmitsNext()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Test
{
    int M(IEnumerator<int> en)
    {
        en.MoveNext();
        return en.Current;
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain("getCurrent()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".next()", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~EnumeratorCurrentToNextTests" -v n`
Expected: FAIL — may contain `getCurrent()` instead of `next()`

- [ ] **Step 3: Expand `IsEnumeratorLikeType()` in IdentifierExpressionTransformer.cs**

In `IdentifierExpressionTransformer.cs`, modify `IsEnumeratorLikeType()` (lines 1150-1169). After the existing `IsEnumerator` checks and interface walk, add:

```csharp
        // Also match types whose Java mapping is Iterator<>
        // (e.g. a C# class implementing IEnumerator<T> mapped to Iterator<T>)
        var fullName = named.ToDisplayString();
        if (fullName.StartsWith("java.util.Iterator") || named.Name == "Iterator")
            return true;
```

So the full method becomes:

```csharp
    private static bool IsEnumeratorLikeType(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        static bool IsEnumerator(INamedTypeSymbol t)
            => (t.ContainingNamespace?.ToDisplayString() == "System.Collections" && t.Name == "IEnumerator")
               || (t.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic" && t.Name == "IEnumerator");

        if (IsEnumerator(named))
            return true;

        foreach (var iface in named.AllInterfaces)
        {
            if (IsEnumerator(iface))
                return true;
        }

        // Also match types mapped to Java Iterator (post type-mapping)
        var fullName = named.ToDisplayString();
        if (fullName.StartsWith("java.util.Iterator") || named.Name == "Iterator")
            return true;

        return false;
    }
```

- [ ] **Step 4: Add `Current → next()` mapping in `LowerProperty.cs` (IR pipeline)**

In `LowerProperty.cs`, in `LowerPropertyAccess()`, add before line 142 ("Default: getter/setter pattern"):

```csharp
        // Special case: IEnumerator.Current → next() (Java Iterator convention)
        if (prop.PropertyName == "Current")
        {
            var containingType = propSymbol?.ContainingType;
            if (containingType != null && IsEnumeratorLikeType(containingType))
            {
                return new IrInvocationExpression
                {
                    Target = loweredTarget,
                    MethodName = "next",
                    Symbol = prop.Symbol,
                    JavaType = prop.JavaType,
                };
            }
        }
```

Add the helper to `LowerProperty` class:

```csharp
    private static bool IsEnumeratorLikeType(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
            return false;
        static bool IsEnumerator(INamedTypeSymbol t)
            => (t.ContainingNamespace?.ToDisplayString() == "System.Collections" && t.Name == "IEnumerator")
               || (t.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic" && t.Name == "IEnumerator");
        if (IsEnumerator(named)) return true;
        foreach (var iface in named.AllInterfaces)
            if (IsEnumerator(iface)) return true;
        return false;
    }
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~EnumeratorCurrentToNextTests" -v n`
Expected: All tests PASS

- [ ] **Step 6: Run all tests**

Run: `dotnet test -v n`
Expected: All tests pass

- [ ] **Step 7: Verify against MSAGL**

Run full conversion. Verify NetworkSimplex.java `getCurrent()` errors are gone.

- [ ] **Step 8: Remove CLI patches in Program.cs**

Remove lines 1196-1203 (NetworkSimplex `getCurrent()` → `next()` patches).

- [ ] **Step 9: Commit**

```bash
git add tests/CSharpToJava.Tests/EnumeratorCurrentToNextTests.cs src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs src/CSharpToJava.Core/Lowering/LowerProperty.cs src/CSharpToJava.CLI/Program.cs
git commit -m "fix: expand IsEnumeratorLikeType to match Iterator-mapped types"
```

---

### Task 4: RC3 — Fix type/property name collision (static context errors)

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`
- Create: `tests/CSharpToJava.Tests/PropertyTypeNameCollisionTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/CSharpToJava.Tests/PropertyTypeNameCollisionTests.cs`:

```csharp
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class PropertyTypeNameCollisionTests
{
    [Fact]
    public void PropertyWithSameNameAsType_MemberAccess_UsesGetter()
    {
        // Edge.Label where Edge is a property and Edge is a type in the same namespace
        var result = Convert(@"
namespace Microsoft.Msagl.Core.Layout
{
    public class Edge
    {
        public string Label { get; set; }
    }
    public class PolyIntEdge
    {
        public Edge Edge { get; set; }
        string M()
        {
            return Edge.Label;
        }
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain("Microsoft.Msagl.Core.Layout.Edge.getLabel()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getEdge()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PropertyWithSameNameAsType_CtorArg_UsesGetter()
    {
        // new PolyIntEdge(target, source, Edge) where Edge is a property
        var result = Convert(@"
namespace Microsoft.Msagl.Core.Layout
{
    public class Edge { }
    public class PolyIntEdge
    {
        public Edge Edge { get; set; }
        public PolyIntEdge(int s, int t, Edge e)
        {
            Edge = e;
        }
        PolyIntEdge Clone()
        {
            return new PolyIntEdge(1, 2, Edge);
        }
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain("Microsoft.Msagl.Core.Layout.Edge)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getEdge()", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~PropertyTypeNameCollisionTests" -v n`
Expected: FAIL — emits `Microsoft.Msagl.Core.Layout.Edge.getLabel()` instead of `getEdge().getLabel()`

- [ ] **Step 3: Add 9th receiver resolution strategy in `TransformMemberAccess()`**

In `IdentifierExpressionTransformer.cs`, in `TransformMemberAccess()`, after Strategy 8 (line 572), add before the `TryGetStaticTypeReceiverJavaReference` call (line 574):

```csharp
            // Strategy 9: When the resolved receiver is a type symbol and the expression
            // is a simple IdentifierNameSyntax, check if the enclosing type has a member
            // (property/field) with the same name. If so, this is an instance property
            // access shadowed by a same-named type — rewrite to this.get{Name}().
            // E.g. Edge.getLabel() where Edge is both a property (instance member)
            // and a type (Microsoft.Msagl.Core.Layout.Edge) →
            //      getEdge().getLabel()
            if (receiverType is INamedTypeSymbol typeSym
                && node.Expression is IdentifierNameSyntax idName
                && char.IsUpper(idName.Identifier.Text[0]))
            {
                var enclosingSym = context.SemanticModel?.GetEnclosingSymbol(node.SpanStart);
                var enclosingType = enclosingSym?.ContainingType;
                if (enclosingType != null)
                {
                    var propName = idName.Identifier.Text;
                    if (HasInstanceMemberNamed(enclosingType, propName))
                    {
                        // Use this.get{Name}() as the receiver
                        var getter = "get" + char.ToUpperInvariant(propName[0]) + propName[1..];
                        var effectiveTarget = $"this.{getter}()";
                        // Re-resolve with the corrected receiver
                        var innerGetter = "get" + char.ToUpperInvariant(memberName[0]) + memberName[1..];
                        return $"{effectiveTarget}.{innerGetter}()";
                    }
                }
            }
```

Add helper method:

```csharp
    private static bool HasInstanceMemberNamed(INamedTypeSymbol type, string name)
    {
        foreach (var m in type.GetMembers(name))
        {
            if (m is IPropertySymbol { IsStatic: false } or IFieldSymbol { IsStatic: false })
                return true;
        }
        // Also check base types
        var baseType = type.BaseType;
        while (baseType != null)
        {
            foreach (var m in baseType.GetMembers(name))
            {
                if (m is IPropertySymbol { IsStatic: false } or IFieldSymbol { IsStatic: false })
                    return true;
            }
            baseType = baseType.BaseType;
        }
        return false;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~PropertyTypeNameCollisionTests" -v n`
Expected: PASS

- [ ] **Step 5: Run all tests**

Run: `dotnet test -v n`
Expected: All tests pass

- [ ] **Step 6: Verify against MSAGL**

Run full conversion. Verify errors in PolyIntEdge.java, Anchor.java, LinkedPoint.java, CdtFrontElement.java, PerimeterEdge.java are gone.

- [ ] **Step 7: Remove CLI patches in Program.cs**

Remove lines 1170-1185 (PolyIntEdge), lines 1187-1194 (LinkedPoint), lines 1205-1210 (general Edge regex — or keep if legitimate static Edge methods remain).

- [ ] **Step 8: Commit**

```bash
git add tests/CSharpToJava.Tests/PropertyTypeNameCollisionTests.cs src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs src/CSharpToJava.CLI/Program.cs
git commit -m "fix: resolve type/property name collision with 9th receiver strategy"
```

---

### Task 5: RC6 — Fix `var` type loss for array-returning methods

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs`
- Create: `tests/CSharpToJava.Tests/VarArrayElementAccessTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/CSharpToJava.Tests/VarArrayElementAccessTests.cs`:

```csharp
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class VarArrayElementAccessTests
{
    [Fact]
    public void VarFromRegexSplit_ElementAccess_UsesArrayIndexer()
    {
        // Reproduces SteinerCdt pattern: Regex.Split returns string[]
        var result = Convert(@"
using System.Text.RegularExpressions;
class Test
{
    string M(string line)
    {
        var parsed = Regex.Split(line, ""\s+"");
        return parsed[0];
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain(".get(0)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("[0]", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void VarFromListMethod_ElementAccess_UsesGet()
    {
        // List access should still use .get()
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Test
{
    int M(List<int> nums)
    {
        var lst = nums.Select(x => x * 2).ToList();
        return lst[0];
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains(".get(0)", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~VarArrayElementAccessTests" -v n`
Expected: FAIL — may produce `.get(0)` for array

- [ ] **Step 3: Fix `ElementAccessTransformer` — improve fallback for array types**

In `ElementAccessTransformer.cs`, modify the VarTypeMap fallback (lines 111-118). After the existing VarTypeMap fallback, add a second check that tries to recover the original C# array type from the mapped type:

```csharp
        // VarTypeMap fallback: when var locals can't be resolved via semantic model
        if ((exprType == null || exprType.TypeKind == TypeKind.Error)
            && node.Expression is IdentifierNameSyntax idExpr
            && context.VarTypeMap.TryGetValue(idExpr.Identifier.Text, out var mappedType)
            && mappedType.TypeKind != TypeKind.Error)
        {
            exprType = mappedType;
        }

        // Second fallback: if VarTypeMap gave us a non-array type (e.g. List mapped from
        // string[]), but the original variable was initialized from a method returning
        // an array, try to recover the array type. Check if the mapped type is List
        // and see if the original initializer came from an array source.
        if (exprType is INamedTypeSymbol named && !(exprType is IArrayTypeSymbol)
            && node.Expression is IdentifierNameSyntax idExpr2)
        {
            if (named.Name == "List" && context.VarTypeMap.TryGetValue(idExpr2.Identifier.Text, out var mappedType2))
            {
                // VarTypeMap may store the ORIGINAL C# type for array-sourced vars.
                // If mappedType2 is IArrayTypeSymbol, the source was an array — use [idx].
                if (mappedType2 is IArrayTypeSymbol)
                    exprType = mappedType2;
            }
        }
```

Note: This fix depends on how VarTypeMap is populated. The pre-scan phase should store the ORIGINAL C# type (before Java mapping) for `var` locals. If the current VarTypeMap already stores the mapped type, adjust the approach: instead of checking VarTypeMap for array, check the original C# initializer via the Roslyn model.

If the test still fails after Step 3, alternative approach: trace back to the variable declaration and check the initializer's method return type:

```csharp
        // Alternative: find the var declaration and check its initializer
        if ((exprType == null || exprType.TypeKind == TypeKind.Error || !(exprType is IArrayTypeSymbol))
            && node.Expression is IdentifierNameSyntax idExpr3)
        {
            var varName = idExpr3.Identifier.Text;
            var enclosingBlock = node.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
            if (enclosingBlock != null)
            {
                foreach (var stmt in enclosingBlock.Statements)
                {
                    if (stmt is LocalDeclarationStatementSyntax { Declaration: { Type: { IsVar: true }, Variables: { Count: 1 } vars } }
                        && vars[0].Identifier.Text == varName
                        && vars[0].Initializer?.Value is InvocationExpressionSyntax invoke)
                    {
                        var invokeTypeInfo = context.SemanticModel?.GetTypeInfo(invoke);
                        if (invokeTypeInfo?.Type is IArrayTypeSymbol)
                        {
                            exprType = invokeTypeInfo.Type;
                            break;
                        }
                    }
                }
            }
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~VarArrayElementAccessTests" -v n`
Expected: All tests PASS

- [ ] **Step 5: Run all tests**

Run: `dotnet test -v n`
Expected: All tests pass

- [ ] **Step 6: Verify against MSAGL**

Run full conversion. Verify SteinerCdt.java array/List errors are gone.

- [ ] **Step 7: Remove CLI patches in Program.cs**

Remove lines 1219-1226 (SteinerCdt `lineParsed` patches).

- [ ] **Step 8: Commit**

```bash
git add tests/CSharpToJava.Tests/VarArrayElementAccessTests.cs src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs src/CSharpToJava.CLI/Program.cs
git commit -m "fix: improve var array type detection in ElementAccessTransformer"
```

---

### Task 6: RC4 — Fix `System.Xml` namespace collision with `java.lang.System`

**Files:**
- Modify: `config/TypeMappings.json`
- Create: `tests/CSharpToJava.Tests/SystemXmlNamespaceMappingTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/CSharpToJava.Tests/SystemXmlNamespaceMappingTests.cs`:

```csharp
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class SystemXmlNamespaceMappingTests
{
    [Fact]
    public void SystemXml_TypeUsage_DoesNotEmitJavaLangSystem()
    {
        var result = Convert(@"
using System.Xml;
class Test
{
    void M()
    {
        var settings = new XmlWriterSettings();
        var writer = XmlWriter.Create(null, settings);
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // Must NOT contain "System.Xml.XmlWriter" (Java would interpret as java.lang.System)
        Assert.DoesNotContain("System.Xml.XmlWriter", result.GeneratedCode, StringComparison.Ordinal);
        // Must contain the mapped type "XmlWriter" (not System-prefixed)
        Assert.Contains("XmlWriter", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemXml_TypeUsage_HasCorrectImport()
    {
        var result = Convert(@"
using System.Xml;
class Test
{
    XmlWriter w;
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain("System.Xml", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~SystemXmlNamespaceMappingTests" -v n`
Expected: FAIL — may contain `System.Xml.XmlWriter` in output

- [ ] **Step 3: Add `System.Xml` namespace mapping to TypeMappings.json**

In `config/TypeMappings.json`, locate the `"namespaceMappings"` array (around line 3254). Add:

```json
    {
        "csharp": "System.Xml",
        "java": "System.Xml"
    },
```

This prevents `System.Xml` from being resolved against `java.lang.System`. The individual type mappings already map each type (XmlWriter, XmlWriterSettings, etc.) to their Java equivalents.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~SystemXmlNamespaceMappingTests" -v n`
Expected: All tests PASS

- [ ] **Step 5: Run all tests**

Run: `dotnet test -v n`
Expected: All tests pass

- [ ] **Step 6: Verify against MSAGL**

Run full conversion. Verify GeometryGraphWriter.java and GeometryGraphReader.java errors are gone.

- [ ] **Step 7: Remove CLI patches in Program.cs**

Remove lines 1158-1168 (GeometryGraphWriter and GeometryGraphReader `System.Xml.` prefix removal patches).

- [ ] **Step 8: Commit**

```bash
git add tests/CSharpToJava.Tests/SystemXmlNamespaceMappingTests.cs config/TypeMappings.json src/CSharpToJava.CLI/Program.cs
git commit -m "fix: add System.Xml namespace mapping to prevent java.lang.System collision"
```

---

### Task 7: Final Verification

- [ ] **Step 1: Run all unit tests**

Run: `dotnet test -v n`
Expected: All tests pass (both new and existing)

- [ ] **Step 2: Full MSAGL project conversion**

Run: `dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s D:\MSAGL -d d:\aj31648`

- [ ] **Step 3: Compile the Java project**

Compile the generated Java code. Verify all 76 original errors are resolved.

- [ ] **Step 4: Check for leftover CLI patches in Program.cs**

Search `Program.cs` for any remaining text replacement patches that correspond to now-fixed errors. Remove any that are no longer needed.

```bash
git diff src/CSharpToJava.CLI/Program.cs
```

- [ ] **Step 5: Final commit (if any changes from Step 4)**

```bash
git add -A && git diff --cached --stat
git commit -m "chore: remove obsolete CLI text patches for fixed root causes"
```
