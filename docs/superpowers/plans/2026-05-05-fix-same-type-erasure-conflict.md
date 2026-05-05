# Fix Same-Type Erasure Conflict Detection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix `HasTypeErasureConflict` so it detects erasure conflicts between overloads with the same type parameter count, and rename conflicted methods with `_erasure_N` suffix.

**Architecture:** Three-layer change: (1) `JavaNaming.cs` gets new enum + conflict-kind detection + suffix method, (2) `ConversionContext.cs` facade is simplified, (3) all 6 call sites in `MethodTransformer.cs` and `InvocationExpressionTransformer.cs` replace the two-line check+append with a single call.

**Tech Stack:** C#, Roslyn (Microsoft.CodeAnalysis), XUnit

---

## File Map

| File | Responsibility |
|------|---------------|
| `src/CSharpToJava.Core/Context/JavaNaming.cs` | Core logic: enum, detection, suffix computation, index assignment |
| `src/CSharpToJava.Core/Context/ConversionContext.cs` | Facade: replaced old two methods with single `GetErasureConflictSuffix` |
| `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs` | Declaration site rename (1 call site) |
| `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs` | Call site rename (5 call sites) |
| `tests/CSharpToJava.Tests/SameTypeErasureConflictTests.cs` | New tests |
| `tests/CSharpToJava.Tests/DebugErasureConflictTests.cs` | Delete (temp debug file) |

---

### Task 1: Write failing tests

**Files:**
- Overwrite: `tests/CSharpToJava.Tests/SameTypeErasureConflictTests.cs`
- Delete: `tests/CSharpToJava.Tests/DebugErasureConflictTests.cs`

- [ ] **Step 1: Delete the temp debug test file and rewrite the real test file**

```csharp
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SameTypeErasureConflictTests
{
    [Fact]
    public void NonGenericOverloads_SameErasedParams_RenamesSecond()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

class Sample
{
    void MkEdgeStmt(IList<string> src, IList<string> dst)
    {
    }

    void MkEdgeStmt(IList<string> src, IList<IList<string>> edges)
    {
    }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode;

        // Both methods must appear
        Assert.Contains("mkEdgeStmt(List<String> src, List<String> dst)", code);
        Assert.Contains("mkEdgeStmt_erasure_2(List<String> src, List<List<String>> edges)", code);

        // Nested generic correctly mapped
        Assert.Contains("List<List<String>>", code);
    }

    [Fact]
    public void NonGenericOverloads_DifferentErasedParameterTypes_NoConflict()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

class Sample
{
    void MkEdgeStmt(IList<string> src, IList<string> dst)
    {
    }

    void MkEdgeStmt(IList<string> src, string name)
    {
    }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);

        var code = result.GeneratedCode;
        // Both should have the same name (no conflict — different erased types)
        Assert.Contains("void mkEdgeStmt(List<String> src, List<String> dst)", code);
        Assert.Contains("void mkEdgeStmt(List<String> src, String name)", code);
        Assert.DoesNotContain("_erasure_", code);
    }

    [Fact]
    public void ThreeWayConflict_SameTypeParamCount_RenamesSequentially()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

class Sample
{
    void Process(IList<string> a) { }
    void Process(IList<int> a) { }
    void Process(IList<IList<string>> a) { }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode;

        // First keeps original name, second gets _erasure_2, third gets _erasure_3
        Assert.Contains("void process(List<String> a)", code);
        Assert.Contains("void process_erasure_2(List<Integer> a)", code);
        Assert.Contains("void process_erasure_3(List<List<String>> a)", code);
    }

    [Fact]
    public void DifferentTypeParamCount_UsesExistingNtpSuffix()
    {
        // Existing behavior must stay unchanged
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
class Sample
{
    void M(object x) { }
    void M<T>(T x) { }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode;
        // Method with 0 type params (fewer) gets _0tp
        Assert.Contains("void m_0tp", code);
        // Method with 1 type param (more) keeps original name
        Assert.Contains("void m<T>", code);
    }
}
```

- [ ] **Step 2: Delete the temp debug file**

```bash
rm tests/CSharpToJava.Tests/DebugErasureConflictTests.cs
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test --filter "FullyQualifiedName~SameTypeErasureConflictTests" --verbosity normal
```

Expected: All 4 tests FAIL. `NonGenericOverloads_SameErasedParams_RenamesSecond` fails because the second method is dropped and no `_erasure_2` suffix exists. `ThreeWayConflict_SameTypeParamCount_RenamesSequentially` fails for the same reason. `DifferentTypeParamCount_UsesExistingNtpSuffix` may fail if the existing behavior is broken by our changes.

- [ ] **Step 4: Commit**

```bash
git add tests/CSharpToJava.Tests/SameTypeErasureConflictTests.cs
git add tests/CSharpToJava.Tests/DebugErasureConflictTests.cs
git commit -m "test: add failing tests for same-type-param-count erasure conflicts"
```

---

### Task 2: Add ErasureConflictKind enum and detection logic to JavaNaming

**Files:**
- Modify: `src/CSharpToJava.Core/Context/JavaNaming.cs`

- [ ] **Step 1: Add the enum above the class**

Replace the file header (everything before `public static class JavaNaming`) with the same content plus the enum:

```csharp
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.Context;

/// <summary>
/// Kind of type-erasure conflict between two method overloads.
/// </summary>
public enum ErasureConflictKind
{
    None,
    DifferentTypeParamCount,
    SameTypeParamCount
}

/// <summary>
/// Static utility methods for Java naming conventions (keyword escaping, type erasure conflict detection).
/// Extracted from ConversionContext.
/// </summary>
public static class JavaNaming
{
    // ... existing IsJavaKeyword and EscapeJavaKeyword unchanged ...
```

- [ ] **Step 2: Add `GetErasureConflictKind` method**

Insert after `EscapeJavaKeyword` (before the existing `HasTypeErasureConflict`):

```csharp
    /// <summary>
    /// Determines whether a C# method has a type-erasure conflict with another overload,
    /// and returns the kind of conflict.
    /// </summary>
    public static (bool HasConflict, ErasureConflictKind Kind) GetErasureConflictKind(IMethodSymbol method)
    {
        if (method.ContainingType == null) return (false, ErasureConflictKind.None);
        if (method.ContainingType.DeclaringSyntaxReferences.Length == 0) return (false, ErasureConflictKind.None);

        foreach (var sibling in method.ContainingType.GetMembers().OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(sibling, method)) continue;
            if (sibling.Name != method.Name) continue;
            if (sibling.Parameters.Length != method.Parameters.Length) continue;
            if (!HaveSameErasedParameters(method, sibling)) continue;

            var kind = sibling.TypeParameters.Length == method.TypeParameters.Length
                ? ErasureConflictKind.SameTypeParamCount
                : ErasureConflictKind.DifferentTypeParamCount;

            // For DifferentTypeParamCount: method with FEWER type params is the conflict.
            // For SameTypeParamCount: method later in source order is the conflict.
            if (kind == ErasureConflictKind.DifferentTypeParamCount)
            {
                if (method.TypeParameters.Length < sibling.TypeParameters.Length)
                    return (true, kind);
            }
            else
            {
                // Both methods are in conflict. Determine which one should be renamed
                // by source order: the first one keeps original name.
                var myLoc = method.Locations.FirstOrDefault(l => l.SourceTree != null);
                var sibLoc = sibling.Locations.FirstOrDefault(l => l.SourceTree != null);
                if (myLoc != null && sibLoc != null)
                {
                    var myLine = myLoc.GetLineSpan().StartLinePosition.Line;
                    var sibLine = sibLoc.GetLineSpan().StartLinePosition.Line;
                    if (myLine > sibLine) return (true, kind);
                }
            }
        }

        return (false, ErasureConflictKind.None);
    }
```

- [ ] **Step 3: Add `GetErasureConflictIndex` method**

Insert after `GetErasureConflictKind`:

```csharp
    /// <summary>
    /// For SameTypeParamCount conflicts, computes a stable 1-based index within the
    /// conflict group (sorted by source line). Returns 1 for the first method (keeps
    /// original name), 2+ for subsequent methods that need _erasure_N suffix.
    /// </summary>
    public static int GetErasureConflictIndex(IMethodSymbol method)
    {
        if (method.ContainingType == null) return 1;

        var conflictGroup = method.ContainingType.GetMembers().OfType<IMethodSymbol>()
            .Where(s => s.Name == method.Name
                && s.Parameters.Length == method.Parameters.Length
                && s.TypeParameters.Length == method.TypeParameters.Length
                && HaveSameErasedParameters(method, s))
            .Select(s => new
            {
                Symbol = s,
                Line = s.Locations.FirstOrDefault(l => l.SourceTree != null)
                    ?.GetLineSpan().StartLinePosition.Line ?? 0
            })
            .OrderBy(x => x.Line)
            .ToList();

        if (conflictGroup.Count <= 1) return 1;

        for (int i = 0; i < conflictGroup.Count; i++)
        {
            if (SymbolEqualityComparer.Default.Equals(conflictGroup[i].Symbol, method))
                return i + 1; // 1-based
        }

        return 1;
    }
```

- [ ] **Step 4: Add `GetErasureConflictSuffix` method**

Insert after `GetErasureConflictIndex`:

```csharp
    /// <summary>
    /// Returns the suffix to append to a method name when it has an erasure conflict,
    /// or an empty string if no conflict exists.
    /// </summary>
    public static string GetErasureConflictSuffix(IMethodSymbol method)
    {
        var (hasConflict, kind) = GetErasureConflictKind(method);
        if (!hasConflict) return "";

        return kind switch
        {
            ErasureConflictKind.DifferentTypeParamCount => $"_{method.TypeParameters.Length}tp",
            ErasureConflictKind.SameTypeParamCount => $"_erasure_{GetErasureConflictIndex(method)}",
            _ => ""
        };
    }
```

- [ ] **Step 5: Run tests to verify new behavior compiles**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

- [ ] **Step 6: Commit**

```bash
git add src/CSharpToJava.Core/Context/JavaNaming.cs
git commit -m "feat: add GetErasureConflictSuffix with SameTypeParamCount support"
```

---

### Task 3: Update ConversionContext facades

**Files:**
- Modify: `src/CSharpToJava.Core/Context/ConversionContext.cs:264-268`

- [ ] **Step 1: Replace the two old facade methods with one new one**

Replace lines 264-268:
```csharp
    // ─── Static facades delegating to JavaNaming ───
    public static bool IsJavaKeyword(string word) => JavaNaming.IsJavaKeyword(word);
    public static string EscapeJavaKeyword(string word) => JavaNaming.EscapeJavaKeyword(word);
    public static bool HasTypeErasureConflict(IMethodSymbol method) => JavaNaming.HasTypeErasureConflict(method);
    public static string GetErasureRenamedSuffix(int typeParameterCount) => JavaNaming.GetErasureRenamedSuffix(typeParameterCount);
}
```

With:
```csharp
    // ─── Static facades delegating to JavaNaming ───
    public static bool IsJavaKeyword(string word) => JavaNaming.IsJavaKeyword(word);
    public static string EscapeJavaKeyword(string word) => JavaNaming.EscapeJavaKeyword(word);
    public static string GetErasureConflictSuffix(IMethodSymbol method) => JavaNaming.GetErasureConflictSuffix(method);
}
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

Expected: FAIL — old call sites still reference `HasTypeErasureConflict` and `GetErasureRenamedSuffix`. That's fixed in the next task.

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Context/ConversionContext.cs
git commit -m "refactor: replace HasTypeErasureConflict/GetErasureRenamedSuffix with GetErasureConflictSuffix"
```

---

### Task 4: Update MethodTransformer declaration site

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs:42-48`

- [ ] **Step 1: Replace the 2-line erasure check with single call**

Replace lines 42-48:
```csharp
        // Type-erasure conflict: if the C# method has an overload with different generic
        // type parameter count but same erased parameter types, rename this overload so that
        // both survive in Java (where type erasure would make them duplicates).
        if (methodInfo != null && ConversionContext.HasTypeErasureConflict(methodInfo))
        {
            javaMethod.Name += ConversionContext.GetErasureRenamedSuffix(methodInfo.TypeParameters.Length);
        }
```

With:
```csharp
        // Type-erasure conflict: rename overload so it survives Java type erasure.
        // Different type param counts get _Ntp suffix; same counts get _erasure_N.
        if (methodInfo != null)
        {
            javaMethod.Name += ConversionContext.GetErasureConflictSuffix(methodInfo);
        }
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs
git commit -m "refactor: use GetErasureConflictSuffix in MethodTransformer"
```

---

### Task 5: Update InvocationExpressionTransformer call sites (5 locations)

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`

Replacements needed at 5 locations:

- [ ] **Step 1: Site 1 — GenericNameSyntax in GenerateIR path (lines 101-102)**

Replace:
```csharp
            if (bareMethodSym != null && ConversionContext.HasTypeErasureConflict(bareMethodSym))
                methodName += ConversionContext.GetErasureRenamedSuffix(bareMethodSym.TypeParameters.Length);
```

With:
```csharp
            if (bareMethodSym != null)
                methodName += ConversionContext.GetErasureConflictSuffix(bareMethodSym);
```

- [ ] **Step 2: Site 2 — IdentifierNameSyntax in GenerateIR path (lines 122-123)**

Replace the same pattern:
```csharp
            if (bareMethodSym2 != null && ConversionContext.HasTypeErasureConflict(bareMethodSym2))
                methodName += ConversionContext.GetErasureRenamedSuffix(bareMethodSym2.TypeParameters.Length);
```

With:
```csharp
            if (bareMethodSym2 != null)
                methodName += ConversionContext.GetErasureConflictSuffix(bareMethodSym2);
```

- [ ] **Step 3: Site 3 — GenericNameSyntax in Transform path (lines 210-211)**

Replace:
```csharp
            if (bareMethodSym != null && ConversionContext.HasTypeErasureConflict(bareMethodSym))
                methodName += ConversionContext.GetErasureRenamedSuffix(bareMethodSym.TypeParameters.Length);
```

With:
```csharp
            if (bareMethodSym != null)
                methodName += ConversionContext.GetErasureConflictSuffix(bareMethodSym);
```

- [ ] **Step 4: Site 4 — IdentifierNameSyntax in Transform path (lines 269-270)**

Replace:
```csharp
            if (bareMethodSym2 != null && ConversionContext.HasTypeErasureConflict(bareMethodSym2))
                methodName += ConversionContext.GetErasureRenamedSuffix(bareMethodSym2.TypeParameters.Length);
```

With:
```csharp
            if (bareMethodSym2 != null)
                methodName += ConversionContext.GetErasureConflictSuffix(bareMethodSym2);
```

- [ ] **Step 5: Site 5 — TransformMemberInvocation (lines 1949-1950)**

Replace:
```csharp
        if (methodSymbol != null && ConversionContext.HasTypeErasureConflict(methodSymbol))
            methodName += ConversionContext.GetErasureRenamedSuffix(methodSymbol.TypeParameters.Length);
```

With:
```csharp
        if (methodSymbol != null)
            methodName += ConversionContext.GetErasureConflictSuffix(methodSymbol);
```

- [ ] **Step 6: Build to verify**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

Expected: PASS (no compile errors).

- [ ] **Step 7: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs
git commit -m "refactor: use GetErasureConflictSuffix in InvocationExpressionTransformer (5 call sites)"
```

---

### Task 6: Run all tests and verify

- [ ] **Step 1: Run the new tests**

```bash
dotnet test --filter "FullyQualifiedName~SameTypeErasureConflictTests" --verbosity normal
```

Expected: All 4 tests PASS.

- [ ] **Step 2: Run the existing erasure-related tests**

```bash
dotnet test --filter "FullyQualifiedName~CrossInheritanceErasure" --verbosity normal
```

Expected: All tests PASS (existing behavior preserved).

- [ ] **Step 3: Run full test suite**

```bash
dotnet test --verbosity normal 2>&1 | grep -E "Passed|Failed|Total"
```

Expected: All tests pass, no regressions.

- [ ] **Step 4: Commit any fixups if needed**

```bash
git add -A
git commit -m "test: verify all tests pass after erasure conflict fix"
```
