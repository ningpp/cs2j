# Line Continuation Trim Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `LineContinuationTrimRewriter` that fixes the GPLEX-generated Scanner's `trimString()` to handle CR-only (`\r`) line continuations in DOT files.

**Architecture:** Follows the existing `MapEntryTypeRewriter` pattern — a `JavaSyntaxRewriter` subclass that overrides `VisitMethodDeclaration` and uses `string.Contains`/`string.Replace` on raw `node.Body` strings. Uses C# verbatim string literals to represent Java source patterns without regex escaping issues. Runs alongside the 14 built-in rewriters in the D4 lowering phase.

**Tech Stack:** C#, xUnit, Roslyn (Java IR model classes)

---

### Task 1: Create the rewriter class skeleton

**Files:**
- Create: `src/CSharpToJava.Core/Java/Rewriters/LineContinuationTrimRewriter.cs`

- [ ] **Step 1: Write the skeleton class**

```csharp
namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that fixes GPLEX-generated Scanner classes to handle
/// CR-only (<c>\r</c>) line continuations in DOT quoted strings.
///
/// <para>The original C# lexer's <c>TrimString()</c> only strips
/// <c>\&lt;CR&gt;&lt;LF&gt;</c> and <c>\&lt;LF&gt;</c> from accumulated
/// string values. When a DOT file uses CR-only line endings, the backslash
/// leaks into parsed coordinate data, causing
/// <c>NumberFormatException</c>.</para>
///
/// <para>This rewriter makes two changes:
/// 1. Separates DFA state 35 from the grouped case and adds a
///    <c>trimString()</c> call.
/// 2. Adds an <c>else if</c> branch to <c>trimString()</c> for
///    <c>\&lt;CR&gt;</c>.</para>
/// </summary>
public sealed class LineContinuationTrimRewriter : JavaSyntaxRewriter
{
    private int _rewriteCount;
    private bool _trimStringFixed;
    private bool _scanCaseFixed;

    /// <summary>Number of rewrites performed during traversal.</summary>
    public int RewriteCount => _rewriteCount;

    // Verbatim strings matching the literal text in the generated Java source.
    // The Java file on disk contains characters like: endsWith("\<backslash>r\<backslash>n").
    // C# verbatim strings treat \r, \n as literal backslash+letter, not escapes.
    private const string CrlfCheckPattern = @"endsWith(""\\\r\n"")";
    private const string LfCheckPattern   = @"endsWith(""\\\n"")";
    private const string CrCheckPattern   = @"endsWith(""\\\r"")";

    // The switch-case block that groups state 35 without trimString().
    private const string Case35GroupedPattern =
        @"case 30, 31, 33, 35:" + "\n" +
        @"            stringId += getYytext();" + "\n" +
        @"            break;";

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _rewriteCount = 0;
        _trimStringFixed = false;
        _scanCaseFixed = false;
        return base.VisitCompilationUnit(node);
    }
}
```

- [ ] **Step 2: Build and verify skeleton compiles**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: Build succeeds

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Java/Rewriters/LineContinuationTrimRewriter.cs
git commit -m "feat: add LineContinuationTrimRewriter skeleton"
```

---

### Task 2: Implement trimString() and scan() detection and transformation

**Files:**
- Modify: `src/CSharpToJava.Core/Java/Rewriters/LineContinuationTrimRewriter.cs`

- [ ] **Step 1: Replace the skeleton with the full implementation**

```csharp
namespace CSharpToJava.Core.Java.Rewriters;

public sealed class LineContinuationTrimRewriter : JavaSyntaxRewriter
{
    private int _rewriteCount;
    private bool _trimStringFixed;
    private bool _scanCaseFixed;

    public int RewriteCount => _rewriteCount;

    private const string CrlfCheckPattern = @"endsWith(""\\\r\n"")";
    private const string LfCheckPattern   = @"endsWith(""\\\n"")";
    private const string CrCheckPattern   = @"endsWith(""\\\r"")";

    private const string Case35GroupedPattern =
        @"case 30, 31, 33, 35:" + "\n" +
        @"            stringId += getYytext();" + "\n" +
        @"            break;";

    private static readonly string Case35FixedBlock =
        @"case 30, 31, 33:" + "\n" +
        @"            stringId += getYytext();" + "\n" +
        @"            break;" + "\n" +
        @"        case 35:" + "\n" +
        @"            stringId += getYytext();" + "\n" +
        @"            trimString();" + "\n" +
        @"            break;";

    private const string LfBranchEndMarker = @"stringId.length() - 2);";

    private static readonly string CrOnlyBranch =
        @"        } else if (stringId.endsWith(""\\\r"")) {" + "\n" +
        @"            stringId = stringId.substring(0, stringId.length() - 2);";

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _rewriteCount = 0;
        _trimStringFixed = false;
        _scanCaseFixed = false;
        return base.VisitCompilationUnit(node);
    }

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        var result = base.VisitMethodDeclaration(node);

        if (string.IsNullOrWhiteSpace(node.Body))
            return result;

        if (!_trimStringFixed && IsTrimStringMethod(node))
        {
            if (NeedsTrimStringFix(node.Body))
            {
                node.Body = FixTrimString(node.Body);
                _trimStringFixed = true;
                _rewriteCount++;
            }
        }

        if (!_scanCaseFixed && IsScanMethod(node))
        {
            if (NeedsScanCaseFix(node.Body))
            {
                node.Body = FixScanCase(node.Body);
                _scanCaseFixed = true;
                _rewriteCount++;
            }
        }

        return result;
    }

    private static bool IsTrimStringMethod(JavaMethodDeclaration node)
        => node.Name.Equals("trimString", StringComparison.OrdinalIgnoreCase);

    private static bool IsScanMethod(JavaMethodDeclaration node)
        => node.Name.Equals("scan", StringComparison.OrdinalIgnoreCase);

    private static bool NeedsTrimStringFix(string body)
        => body.Contains(CrlfCheckPattern)
        && body.Contains(LfCheckPattern)
        && !body.Contains(CrCheckPattern);

    private static bool NeedsScanCaseFix(string body)
        => body.Contains(Case35GroupedPattern);

    private static string FixTrimString(string body)
    {
        // The LF branch ends with "stringId.length() - 2);".
        // Find the last occurrence (the \LF branch specifically) and insert
        // the CR-only branch before the closing brace of that else-if.
        int idx = body.LastIndexOf(LfBranchEndMarker, StringComparison.Ordinal);
        if (idx < 0)
            return body;

        // Find the closing '}' of the LF-only branch, after the marker.
        int closeBrace = body.IndexOf('}', idx + LfBranchEndMarker.Length);
        if (closeBrace < 0)
            return body;

        // Insert the new CR-only branch before the closing brace.
        return body.Insert(closeBrace, CrOnlyBranch);
    }

    private static string FixScanCase(string body)
        => body.Replace(Case35GroupedPattern, Case35FixedBlock);
}
```

- [ ] **Step 2: Build and verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj`
Expected: Build succeeds

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Java/Rewriters/LineContinuationTrimRewriter.cs
git commit -m "feat: implement trimString and scan case detection and transformation"
```

---

### Task 3: Write unit tests

**Files:**
- Create: `tests/CSharpToJava.Tests/Rewriters/LineContinuationTrimRewriterTests.cs`

- [ ] **Step 1: Create the test class with helper methods**

```csharp
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Java.Rewriters;

namespace CSharpToJava.Tests.Rewriters;

public class LineContinuationTrimRewriterTests
{
    private static JavaCompilationUnit WrapMethod(string methodName, string body)
    {
        var method = new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.None,
            ReturnType = "void",
            Name = methodName,
            Body = body,
        };
        var classDecl = new JavaClassDeclaration { Name = "Scanner" };
        classDecl.Methods.Add(method);
        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(classDecl);
        return cu;
    }

    private static JavaCompilationUnit BuildScannerWithBothMethods(
        string trimBody, string scanBody)
    {
        var classDecl = new JavaClassDeclaration { Name = "Scanner" };

        classDecl.Methods.Add(new JavaMethodDeclaration
        {
            ReturnType = "void", Name = "trimString", Body = trimBody,
        });
        classDecl.Methods.Add(new JavaMethodDeclaration
        {
            ReturnType = "int", Name = "scan", Body = scanBody,
        });

        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(classDecl);
        return cu;
    }
}
```

- [ ] **Step 2: Add test — trimString gets CR-only branch added**

```csharp
[Fact]
public void TrimString_AddsCrOnlyBranch_WhenMissing()
{
    var body = @"
void trimString() {
    if (stringId.endsWith(""\\\r\n"")) {
        stringId = stringId.substring(0, stringId.length() - 3);
    } else {
        if (stringId.endsWith(""\\\n"")) {
            stringId = stringId.substring(0, stringId.length() - 2);
        }
    }
}";

    var cu = WrapMethod("trimString", body);
    var rewriter = new LineContinuationTrimRewriter();

    rewriter.VisitCompilationUnit(cu);

    Assert.Equal(1, rewriter.RewriteCount);
    Assert.Contains(@"endsWith(""\\\r"")", cu.TypeDeclarations[0].Methods[0].Body);
}
```

- [ ] **Step 3: Add test — scan method case 35 is separated with trimString call**

```csharp
[Fact]
public void ScanMethod_SeparatesCase35_WhenGrouped()
{
    var body = @"
int scan() {
    switch (state) {
    case 30, 31, 33, 35:
            stringId += getYytext();
            break;
    }
}";

    var cu = WrapMethod("scan", body);
    var rewriter = new LineContinuationTrimRewriter();

    rewriter.VisitCompilationUnit(cu);

    Assert.Equal(1, rewriter.RewriteCount);
    var result = cu.TypeDeclarations[0].Methods[0].Body;
    Assert.Contains("case 30, 31, 33:", result);
    Assert.Contains("case 35:", result);
    Assert.Contains("trimString();", result);
    Assert.DoesNotContain("case 30, 31, 33, 35:", result);
}
```

- [ ] **Step 4: Add test — already-fixed code is unchanged**

```csharp
[Fact]
public void TrimString_NoChange_WhenCrBranchAlreadyPresent()
{
    var body = @"
void trimString() {
    if (stringId.endsWith(""\\\r\n"")) {
        stringId = stringId.substring(0, stringId.length() - 3);
    } else if (stringId.endsWith(""\\\n"")) {
        stringId = stringId.substring(0, stringId.length() - 2);
    } else if (stringId.endsWith(""\\\r"")) {
        stringId = stringId.substring(0, stringId.length() - 2);
    }
}";

    var cu = WrapMethod("trimString", body);
    var rewriter = new LineContinuationTrimRewriter();

    rewriter.VisitCompilationUnit(cu);

    Assert.Equal(0, rewriter.RewriteCount);
}
```

- [ ] **Step 5: Add test — unrelated method is untouched**

```csharp
[Fact]
public void UnrelatedMethod_NoChange()
{
    var body = "System.out.println(\"hello\");";
    var cu = WrapMethod("otherMethod", body);
    var rewriter = new LineContinuationTrimRewriter();

    rewriter.VisitCompilationUnit(cu);

    Assert.Equal(0, rewriter.RewriteCount);
    Assert.Equal(body, cu.TypeDeclarations[0].Methods[0].Body);
}
```

- [ ] **Step 6: Add test — both fixes applied in same class**

```csharp
[Fact]
public void BothFixesApplied_WhenBothPatternsFound()
{
    var trimBody = @"
void trimString() {
    if (stringId.endsWith(""\\\r\n"")) {
        stringId = stringId.substring(0, stringId.length() - 3);
    } else {
        if (stringId.endsWith(""\\\n"")) {
            stringId = stringId.substring(0, stringId.length() - 2);
        }
    }
}";
    var scanBody = @"
int scan() {
    switch (state) {
    case 30, 31, 33, 35:
            stringId += getYytext();
            break;
    }
}";

    var cu = BuildScannerWithBothMethods(trimBody, scanBody);
    var rewriter = new LineContinuationTrimRewriter();

    rewriter.VisitCompilationUnit(cu);

    Assert.Equal(2, rewriter.RewriteCount);
}
```

- [ ] **Step 7: Run tests and verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~LineContinuationTrimRewriter"`
Expected: All 6 tests pass

- [ ] **Step 8: Commit**

```bash
git add tests/CSharpToJava.Tests/Rewriters/LineContinuationTrimRewriterTests.cs
git commit -m "test: add unit tests for LineContinuationTrimRewriter"
```

---

### Task 4: Register rewriter in both pipeline paths

**Files:**
- Modify: `src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs`
- Modify: `src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs`

- [ ] **Step 1: Register in ProjectPasses.cs**

In `BuildEffectiveRewriters()` (near line 574), add after `StopwatchApiRewriter`:

```csharp
rewriters.Add(new Java.Rewriters.StopwatchApiRewriter());
rewriters.Add(new Java.Rewriters.LineContinuationTrimRewriter());  // <-- ADD
rewriters.Add(new Java.Rewriters.StringConcatRewriter());
```

- [ ] **Step 2: Register in SingleFilePasses.cs**

In `RunBuiltInRewriters()` (near line 305), add after `StopwatchApiRewriter`:

```csharp
rewriters.Add(new Java.Rewriters.LineContinuationTrimRewriter());  // <-- ADD
```

- [ ] **Step 3: Build to verify registration compiles**

Run: `dotnet build`
Expected: Build succeeds

- [ ] **Step 4: Commit**

```bash
git add src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs src/CSharpToJava.Core/Pipeline/Passes/SingleFilePasses.cs
git commit -m "feat: register LineContinuationTrimRewriter in both pipeline paths"
```

---

### Task 5: Re-convert and verify the fix

**Files:**
- (None — verification only)

- [ ] **Step 1: Re-convert the GraphLayout project**

Run:
```bash
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s "E:\agl-master\GraphLayout" -d "E:\z5"
```

- [ ] **Step 2: Verify generated Scanner.java has both fixes**

Check the CR-only branch in trimString:

Run: `grep -c "endsWith.*r\"" "E:\z5\Dot2Graph\src\main\java\Dot2Graph\Scanner.java"`
Expected: Output is `1` (the new CR-only branch)

Check case 35 is separated:

Run: `grep -c "case 35:" "E:\z5\Dot2Graph\src\main\java\Dot2Graph\Scanner.java"`
Expected: Output is `1` (case 35 is now standalone with trimString call)

Verify old grouped pattern is gone:

Run: `grep -c "case 30, 31, 33, 35:" "E:\z5\Dot2Graph\src\main\java\Dot2Graph\Scanner.java"`
Expected: Output is `0`

- [ ] **Step 3: Run the Java test that previously failed**

```bash
cd "E:\z5" && mvn test -pl MSAGLTests -Dtest=SugiyamaLayoutTests#randomDotFileTests -DfailIfNoTests=false 2>&1 | tail -30
```

Expected: Test passes without `NumberFormatException: For input string: "54\"`

- [ ] **Step 4: Commit**

```bash
git status
git add -A
git commit -m "verify: confirm LineContinuationTrimRewriter fixes bug1.dot CR-only issue"
```

---

### Task 6: Run full cs2j test suite

**Files:**
- (None — verification only)

- [ ] **Step 1: Run all cs2j tests**

```bash
dotnet test
```
Expected: All tests pass (including new `LineContinuationTrimRewriter` tests)

- [ ] **Step 2: Verify no regressions and commit**

If any tests fail, investigate and fix. Otherwise:

```bash
git add -A
git commit -m "test: full suite passes with LineContinuationTrimRewriter"
```
