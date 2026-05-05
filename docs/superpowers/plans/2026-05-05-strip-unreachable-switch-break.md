# Strip Unreachable Break After Terminal in Switch — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop emitting unreachable `break;` (and other dead code) after terminal statements in plain switch case sections, preventing Java compile errors.

**Architecture:** Replace the blind `.Select()` statement transformation loop in `TransformPlainSwitch` with a guarded `foreach` that stops converting when a terminal statement is reached. Add a small `IsSwitchSectionTerminal` helper.

**Tech Stack:** C#, Roslyn SyntaxKind enum

---

### Task 1: Write the failing test

**Files:**
- Create: `tests/CSharpToJava.Tests/SwitchUnreachableBreakTests.cs`

- [ ] **Step 1: Write the test file**

```csharp
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SwitchUnreachableBreakTests
{
    [Fact]
    public void PlainSwitch_ReturnWithTrailingBreak_StripsBreak()
    {
        var result = Convert(@"
class C {
    int Test(int x) {
        switch (x) {
            case 0:
                return 1;
                break;
            case 1:
                return 2;
                break;
            default:
                return -1;
                break;
        }
    }
}");
        Assert.True(result.Success);
        // Must contain return statements
        Assert.Contains("return 1;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return 2;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return -1;", result.GeneratedCode, StringComparison.Ordinal);
        // Must NOT contain unreachable break after return (Java compile error)
        Assert.DoesNotContain("return 1;\n            break;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return 2;\n            break;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return -1;\n            break;", result.GeneratedCode, StringComparison.Ordinal);
        // Normal break at the end of a non-terminal case should still work
        // (none in this example, but structure is valid)
    }

    [Fact]
    public void PlainSwitch_ThrowWithTrailingBreak_StripsBreak()
    {
        var result = Convert(@"
class C {
    int Test(int x) {
        switch (x) {
            case 0:
                throw new Exception(""zero"");
                break;
            default:
                return 1;
                break;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("throw new Exception", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("throw new Exception(\"zero\");\n            break;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainSwitch_NormalBreakOnly_Kept()
    {
        var result = Convert(@"
class C {
    void Test(int x) {
        int y = 0;
        switch (x) {
            case 0:
                y = 1;
                break;
            default:
                y = -1;
                break;
        }
    }
}");
        Assert.True(result.Success);
        // Normal case: assignment then break — both should be present
        Assert.Contains("y = 1;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("break;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainSwitch_ReturnThenMoreCode_StripsEverythingAfterReturn()
    {
        var result = Convert(@"
class C {
    int Test(int x) {
        switch (x) {
            case 0:
                return 1;
                System.Console.WriteLine(""never"");
                break;
            default:
                return -1;
                break;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("return 1;", result.GeneratedCode, StringComparison.Ordinal);
        // WriteLine should NOT appear — it's after return
        Assert.DoesNotContain("System.out.println(\"never\")", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
```

- [ ] **Step 2: Run the new tests to verify they fail**

```bash
dotnet test --filter "FullyQualifiedName~SwitchUnreachableBreakTests"
```

Expected: at least the first two tests FAIL — `Assert.DoesNotContain` catches `break;` still present after `return`.

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/SwitchUnreachableBreakTests.cs
git commit -m "test: add failing tests for unreachable break after return in switch

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 2: Implement the fix

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs:126-128`
- Modify: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs` (add helper after `TransformPlainSwitch`)

- [ ] **Step 1: Replace the blind `.Select()` with a guarded loop**

In `TransformPlainSwitch`, replace lines 126-128:

```csharp
var stmtTransformer = new StatementTransformer();
var statements = section.Statements.Select(s =>
    stmtTransformer.Transform(s, context).ToString("")).ToList();
```

with:

```csharp
var stmtTransformer = new StatementTransformer();
var statements = new List<string>();
foreach (var s in section.Statements)
{
    statements.Add(stmtTransformer.Transform(s, context).ToString(""));
    if (IsSwitchSectionTerminal(s))
        break;
}
```

- [ ] **Step 2: Add the `IsSwitchSectionTerminal` helper**

Add this static method to the `StatementTransformer` class (after `TransformPlainSwitch`, before `TransformTryStatement`, around line 156):

```csharp
private static bool IsSwitchSectionTerminal(StatementSyntax stmt) =>
    stmt.Kind() switch
    {
        SyntaxKind.ReturnStatement or
        SyntaxKind.ThrowStatement or
        SyntaxKind.ContinueStatement or
        SyntaxKind.GotoStatement or
        SyntaxKind.GotoCaseStatement or
        SyntaxKind.GotoDefaultStatement => true,
        _ => false
    };
```

`BreakStatement` is intentionally excluded — `break;` is the normal section terminator and the statements before it are reachable.

- [ ] **Step 3: Build to verify compilation**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

Expected: BUILD SUCCESS (no compilation errors).

- [ ] **Step 4: Run the new tests to verify they pass**

```bash
dotnet test --filter "FullyQualifiedName~SwitchUnreachableBreakTests"
```

Expected: ALL 4 tests PASS.

- [ ] **Step 5: Run the full test suite to check for regressions**

```bash
dotnet test
```

Expected: ALL tests PASS (no regressions).

- [ ] **Step 6: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs
git commit -m "fix: strip unreachable statements after terminal in switch sections

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```
