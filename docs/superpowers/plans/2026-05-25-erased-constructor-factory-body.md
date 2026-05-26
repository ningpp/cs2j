# Erased Constructor Factory Body Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve the original constructor body when a Java type-erasure conflict forces a C# constructor into a static factory method.

**Architecture:** Keep the existing constructor conflict mechanism in `ClassTransformer.AddCtorIfNotDuplicateInternal`, but change the generated factory body from a no-op allocation into an allocation plus the original constructor body statements. Update `ObjectCreationTransformer` only if the existing `Rectangle.createFrom_Iterable_Rectangle(...)` call-site special case no longer matches the generated factory name.

**Tech Stack:** C#/.NET 10, Roslyn, XUnit, converter Java IR (`JavaClassDeclaration`, `JavaConstructorDeclaration`, `JavaMethodDeclaration`).

---

## File Structure

- Modify `D:\code\cs2j\tests\CSharpToJava.Tests\SameTypeErasureConflictTests.cs`
  - Add a regression test proving static factories created for erased constructor conflicts preserve constructor body behavior.
- Modify `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Type\ClassTransformer.cs`
  - Update static factory generation for one-parameter constructor erasure conflicts.
- Use existing `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Expression\Transformers\ObjectCreationTransformer.cs`
  - Inspect after implementation only if call sites do not target the generated factory.

## Task 1: Add A Failing Regression Test

**Files:**
- Modify: `D:\code\cs2j\tests\CSharpToJava.Tests\SameTypeErasureConflictTests.cs`

- [ ] **Step 1: Add the failing test**

Append this test to `SameTypeErasureConflictTests`:

```csharp
[Fact]
public void ConstructorErasureFactory_PreservesOriginalConstructorBody()
{
    var pipeline = new ConversionPipeline();
    var result = pipeline.Convert(new ConversionRequest
    {
        SourceCode = @"
using System.Collections.Generic;

class Rectangle { }
class Point { }

class Box
{
    public int Count;

    public Box(IEnumerable<Point> points)
    {
        Count = 100;
        foreach (var p in points)
            Count++;
    }

    public Box(IEnumerable<Rectangle> rectangles)
    {
        Count = 200;
        foreach (var r in rectangles)
            Count++;
    }

    public static Box FromRectangles(IEnumerable<Rectangle> rectangles)
    {
        return new Box(rectangles);
    }
}",
        FileName = "Box.cs",
        Options = new ConversionOptions(),
    });

    Assert.True(result.Success,
        "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

    var code = result.GeneratedCode;

    Assert.Contains("public static Box createFrom_Iterable_Rectangle(Iterable<Rectangle> rectangles)", code);
    Assert.Contains("Box __inst = new Box();", code);
    Assert.Contains("__inst.Count = 200;", code);
    Assert.Contains("for (Rectangle r : rectangles)", code);
    Assert.Contains("__inst.Count++;", code);
    Assert.Contains("return __inst;", code);
    Assert.Contains("return Box.createFrom_Iterable_Rectangle(rectangles);", code);
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~SameTypeErasureConflictTests.ConstructorErasureFactory_PreservesOriginalConstructorBody"
```

Expected: FAIL because the generated factory currently contains `Box __inst = new Box();` and `return __inst;` but does not contain the constructor body statements such as `__inst.Count = 200;`.

## Task 2: Preserve The Constructor Body In Generated Factories

**Files:**
- Modify: `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Type\ClassTransformer.cs`

- [ ] **Step 1: Locate the factory generation block**

Open `ClassTransformer.AddCtorIfNotDuplicateInternal`. The relevant block begins near the type-erasure conflict comment and currently creates a `JavaMethodDeclaration` with this body shape:

```csharp
factory.Body = $"var __inst = new {javaClass.Name}();\nreturn __inst;";
```

- [ ] **Step 2: Replace the factory body builder**

Change the factory body construction so it:

1. Allocates a default instance using the containing class name.
2. Replays the original constructor body against `__inst`.
3. Returns `__inst`.

Use this helper in the same class:

```csharp
private static string BuildFactoryBodyFromConstructor(JavaClassDeclaration javaClass, JavaConstructorDeclaration ctor)
{
    var lines = new List<string>
    {
        $"{javaClass.Name} __inst = new {javaClass.Name}();"
    };

    var ctorBody = ctor.Body?.Trim();
    if (!string.IsNullOrWhiteSpace(ctorBody))
    {
        lines.Add(QualifyConstructorBodyForFactory(ctorBody));
    }
    else if (ctor.StructuredBody != null && ctor.StructuredBody.Statements.Count > 0)
    {
        foreach (var statement in ctor.StructuredBody.Statements)
        {
            lines.Add(QualifyConstructorBodyForFactory(statement.ToJava()));
        }
    }

    lines.Add("return __inst;");
    return string.Join("\n", lines);
}

private static string QualifyConstructorBodyForFactory(string body)
{
    var rewritten = body;
    rewritten = Regex.Replace(rewritten, @"(?<![\w.])this\.", "__inst.");
    rewritten = Regex.Replace(
        rewritten,
        @"(?m)^(\s*)([A-Za-z_][A-Za-z0-9_]*)\s*=",
        match => IsLikelyLocalDeclaration(match.Value)
            ? match.Value
            : $"{match.Groups[1].Value}__inst.{match.Groups[2].Value} =");
    rewritten = Regex.Replace(
        rewritten,
        @"(?m)^(\s*)([A-Za-z_][A-Za-z0-9_]*)\+\+;",
        "$1__inst.$2++;");
    rewritten = Regex.Replace(
        rewritten,
        @"(?m)^(\s*)([A-Za-z_][A-Za-z0-9_]*)--;",
        "$1__inst.$2--;");
    return rewritten;
}

private static bool IsLikelyLocalDeclaration(string assignmentPrefix)
{
    var trimmed = assignmentPrefix.TrimStart();
    return trimmed.StartsWith("var ", StringComparison.Ordinal)
        || trimmed.StartsWith("int ", StringComparison.Ordinal)
        || trimmed.StartsWith("double ", StringComparison.Ordinal)
        || trimmed.StartsWith("boolean ", StringComparison.Ordinal)
        || trimmed.StartsWith("String ", StringComparison.Ordinal)
        || trimmed.StartsWith("long ", StringComparison.Ordinal)
        || trimmed.StartsWith("float ", StringComparison.Ordinal)
        || trimmed.StartsWith("short ", StringComparison.Ordinal)
        || trimmed.StartsWith("byte ", StringComparison.Ordinal)
        || trimmed.StartsWith("char ", StringComparison.Ordinal);
}
```

Add `using System.Text.RegularExpressions;` at the top of `ClassTransformer.cs` if it is not already present.

- [ ] **Step 3: Wire the helper into factory generation**

Set:

```csharp
factory.Body = BuildFactoryBodyFromConstructor(javaClass, ctor);
```

instead of the existing no-op factory body.

- [ ] **Step 4: Keep the implementation minimal**

Do not change general method-erasure naming, constructor duplicate detection, or object-creation call-site mapping in this task unless the focused regression test proves the call site is wrong.

## Task 3: Verify Converter Tests

**Files:**
- Test: `D:\code\cs2j\tests\CSharpToJava.Tests\SameTypeErasureConflictTests.cs`

- [ ] **Step 1: Run the focused test and verify GREEN**

Run:

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~SameTypeErasureConflictTests.ConstructorErasureFactory_PreservesOriginalConstructorBody"
```

Expected: PASS.

- [ ] **Step 2: Run related erasure and object creation tests**

Run:

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~SameTypeErasureConflictTests|FullyQualifiedName~StreamToIterableAssignmentTests|FullyQualifiedName~ArrayToIterableConversionTests"
```

Expected: PASS for the selected tests.

- [ ] **Step 3: Run full converter tests if selected tests pass**

Run:

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj
```

Expected: PASS. If unrelated existing tests fail, record exact failures and do not hide them.

## Task 4: Reconvert MSAGL And Verify The Root Cause

**Files:**
- Generated output: `E:\z5` is recreated by the converter, never manually edited.
- Logs: `D:\code\cs2j\iteration-logs\<run-label>`

- [ ] **Step 1: Re-run conversion and Maven package**

Run:

```powershell
.\scripts\convert-and-compile.ps1 `
  -RunLabel "rectangle-factory-body" `
  -SourcePath "E:\agl-master\GraphLayout\GraphLayout.sln" `
  -DestinationPath "E:\z5"
```

Expected: conversion completes and Maven runs. Maven may still fail on unrelated remaining root causes.

- [ ] **Step 2: Inspect generated Rectangle factory**

Run:

```powershell
Select-String -LiteralPath "E:\z5\AutomaticGraphLayout\src\main\java\Microsoft\Msagl\Core\Geometry\Rectangle.java" -Pattern "createFrom_Iterable_Rectangle" -Context 0,20
```

Expected: `createFrom_Iterable_Rectangle` contains calls equivalent to initializing an empty rectangle and adding each input rectangle, not just allocation and return.

- [ ] **Step 3: Run targeted cluster tests**

Run from `E:\z5`:

```powershell
mvn -pl MSAGLTests -Dtest=Microsoft.Msagl.UnitTests.ClusterTests#simpleDeepTranslationTest,Microsoft.Msagl.UnitTests.ClusterTests#nestedDeepTranslationTest test
```

Expected: The previous width assertion failures are gone. If the tests fail later for a different known root cause, record the new failure.

- [ ] **Step 4: Optionally run the initial layout target**

Run from `E:\z5`:

```powershell
mvn -pl MSAGLTests -Dtest=Microsoft.Msagl.UnitTests.InitialLayoutTests#denseClusteringNoEdges test
```

Expected: The previous height assertion attributable to empty child bounds is gone, or the next failure is documented separately.

## Task 5: Commit The Verified Root Cause Fix

**Files:**
- Stage only files changed for this root cause:
  - `D:\code\cs2j\src\CSharpToJava.Core\Transformers\Type\ClassTransformer.cs`
  - `D:\code\cs2j\tests\CSharpToJava.Tests\SameTypeErasureConflictTests.cs`

- [ ] **Step 1: Check status**

Run:

```powershell
git status --short
```

Expected: only the intended source/test files are modified, plus unrelated pre-existing untracked files that must not be staged.

- [ ] **Step 2: Stage intended files**

Run:

```powershell
git add -- src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs tests/CSharpToJava.Tests/SameTypeErasureConflictTests.cs
```

- [ ] **Step 3: Commit**

Run:

```powershell
git commit -m "fix: preserve erased constructor factory bodies"
```

Expected: a new commit in `D:\code\cs2j` containing only this root cause fix.
