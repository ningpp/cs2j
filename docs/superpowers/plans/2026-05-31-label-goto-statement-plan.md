# LabelStatement & GotoStatement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Systematically support C# LabelStatement and GotoStatement in the old pipeline, converting them to Java's labeled break/continue pattern with comprehensive unit tests.

**Architecture:** Add a `LabelRegistry` to track labels and their kinds (Loop/Block/Other) during method conversion. Pre-scan labels before statement processing, then use the registry to convert `goto label` → `break label;` or `continue label;` depending on the label's target type. Label statements are directly mapped to Java labels.

**Tech Stack:** C# / Roslyn / XUnit / existing ConversionPipeline test pattern

---

## File Structure

| Action | Path | Responsibility |
|--------|------|----------------|
| Create | `src/CSharpToJava.Core/Context/LabelRegistry.cs` | LabelInfo record and LabelRegistry class |
| Create | `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.LabelAndGoto.cs` | PrescanLabels, TransformLabelStatement, TransformGotoStatement, IsInLabelScope |
| Modify | `src/CSharpToJava.Core/Context/MethodConversionState.cs` | Add LabelRegistry property, reset in Reset() |
| Modify | `src/CSharpToJava.Core/Context/ConversionContext.cs` | Add LabelRegistry facade property |
| Modify | `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs` | Add switch cases, add PrescanLabels call in TransformBlock, add GotoStatement to IsUnconditionalJump |
| Create | `tests/CSharpToJava.Tests/LabelGotoStatementTests.cs` | All unit tests |

---

### Task 1: Create LabelRegistry

**Files:**
- Create: `src/CSharpToJava.Core/Context/LabelRegistry.cs`

- [ ] **Step 1: Write the LabelRegistry class**

```csharp
namespace CSharpToJava.Core.Context;

/// <summary>
/// The kind of statement a label annotates, determining how goto-label
/// is translated to Java (loop → continue, block/other → break).
/// </summary>
public enum LabelKind
{
    Loop,
    Block,
    Other
}

/// <summary>
/// Information about a labeled statement registered during pre-scan.
/// </summary>
public record LabelInfo(string Name, LabelKind Kind);

/// <summary>
/// Method-level registry of labels encountered during conversion.
/// Used to determine how goto-label should be translated:
/// <list type="bullet">
/// <item>Loop labels → <c>continue label;</c></item>
/// <item>Block/Other labels → <c>break label;</c></item>
/// </list>
/// Cleared on entering each new method via <see cref="MethodConversionState.Reset"/>.
/// </summary>
public class LabelRegistry
{
    private readonly Dictionary<string, LabelInfo> _labels = new(StringComparer.Ordinal);

    public void Register(string name, LabelKind kind)
    {
        _labels[name] = new LabelInfo(name, kind);
    }

    public bool Contains(string name) => _labels.ContainsKey(name);

    public bool TryGetLabel(string name, out LabelInfo? info)
        => _labels.TryGetValue(name, out info);

    public void Clear() => _labels.Clear();
}
```

- [ ] **Step 2: Commit**

```bash
git add src/CSharpToJava.Core/Context/LabelRegistry.cs
git commit -m "feat: add LabelRegistry for label/goto conversion support"
```

---

### Task 2: Integrate LabelRegistry into MethodConversionState and ConversionContext

**Files:**
- Modify: `src/CSharpToJava.Core/Context/MethodConversionState.cs`
- Modify: `src/CSharpToJava.Core/Context/ConversionContext.cs`

- [ ] **Step 1: Add LabelRegistry property to MethodConversionState**

In `MethodConversionState.cs`, add after the `LambdaCapturePreScanDone` property (line 224):

```csharp
    // ─── Label Registry ────────────────────────────────────────────

    /// <summary>
    /// Labels registered during pre-scan for goto-label conversion.
    /// Maps label names to their kind (Loop/Block/Other) so that
    /// <c>goto label</c> can be translated to <c>break label;</c> or
    /// <c>continue label;</c> appropriately.
    /// </summary>
    public LabelRegistry Labels { get; } = new();
```

In the `Reset()` method, add after line 343 (`LambdaCapturePreScanDone = false;`):

```csharp
        Labels.Clear();
```

- [ ] **Step 2: Add LabelRegistry facade property to ConversionContext**

In `ConversionContext.cs`, add after the `HasPendingPostStatements` property (line 259):

```csharp
    /// <summary>
    /// Method-level label registry for goto-label conversion.
    /// Delegates to <see cref="MethodState.Labels"/>.
    /// </summary>
    public LabelRegistry Labels => MethodState.Labels;
```

- [ ] **Step 3: Build and verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --no-restore`
Expected: Build succeeds with no errors

- [ ] **Step 4: Commit**

```bash
git add src/CSharpToJava.Core/Context/MethodConversionState.cs src/CSharpToJava.Core/Context/ConversionContext.cs
git commit -m "feat: integrate LabelRegistry into MethodConversionState and ConversionContext"
```

---

### Task 3: Create StatementTransformer.LabelAndGoto.cs

**Files:**
- Create: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.LabelAndGoto.cs`

- [ ] **Step 1: Write the label and goto transformer partial class**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Statement;

public partial class StatementTransformer
{
    /// <summary>
    /// Pre-scans a list of statements to register all labels and their kinds
    /// in the <see cref="ConversionContext.Labels"/> registry. Recursively
    /// descends into nested blocks (if/for/while/switch/try etc.) but does NOT
    /// descend into lambda/anonymous-method bodies (they have their own scope).
    /// </summary>
    internal static void PrescanLabels(SyntaxList<StatementSyntax> statements, ConversionContext context)
    {
        foreach (var stmt in statements)
        {
            if (stmt is LabeledStatementSyntax labeled)
            {
                var kind = GetLabelKind(labeled.Statement);
                context.Labels.Register(labeled.Identifier.Text, kind);
                // Also prescan the labeled statement's body
                PrescanLabelsInStatement(labeled.Statement, context);
            }
            else
            {
                PrescanLabelsInStatement(stmt, context);
            }
        }
    }

    /// <summary>
    /// Recursively prescan labels inside a single statement's nested blocks.
    /// Skips lambda/anonymous-method bodies.
    /// </summary>
    private static void PrescanLabelsInStatement(StatementSyntax stmt, ConversionContext context)
    {
        switch (stmt)
        {
            case BlockSyntax block:
                PrescanLabels(block.Statements, context);
                break;
            case IfStatementSyntax ifStmt:
                PrescanLabelsInStatement(ifStmt.Statement, context);
                if (ifStmt.Else != null)
                    PrescanLabelsInStatement(ifStmt.Else.Statement, context);
                break;
            case WhileStatementSyntax whileStmt:
                PrescanLabelsInStatement(whileStmt.Statement, context);
                break;
            case ForStatementSyntax forStmt:
                PrescanLabelsInStatement(forStmt.Statement, context);
                break;
            case ForEachStatementSyntax foreachStmt:
                PrescanLabelsInStatement(foreachStmt.Statement, context);
                break;
            case DoStatementSyntax doStmt:
                PrescanLabelsInStatement(doStmt.Statement, context);
                break;
            case SwitchStatementSyntax switchStmt:
                foreach (var section in switchStmt.Sections)
                    PrescanLabels(section.Statements, context);
                break;
            case TryStatementSyntax tryStmt:
                PrescanLabelsInStatement(tryStmt.Block, context);
                foreach (var catchClause in tryStmt.Catches)
                    PrescanLabelsInStatement(catchClause.Block, context);
                if (tryStmt.Finally != null)
                    PrescanLabelsInStatement(tryStmt.Finally.Block, context);
                break;
            case UsingStatementSyntax usingStmt:
                PrescanLabelsInStatement(usingStmt.Statement, context);
                break;
            case LockStatementSyntax lockStmt:
                PrescanLabelsInStatement(lockStmt.Statement, context);
                break;
            case FixedStatementSyntax fixedStmt:
                PrescanLabelsInStatement(fixedStmt.Statement, context);
                break;
            case UnsafeStatementSyntax unsafeStmt:
                if (unsafeStmt.Block != null)
                    PrescanLabelsInStatement(unsafeStmt.Block, context);
                break;
            case CheckedStatementSyntax checkedStmt:
                PrescanLabelsInStatement(checkedStmt.Block, context);
                break;
            case LabeledStatementSyntax labeled:
                // Already handled in PrescanLabels, but handle nested labeled
                PrescanLabelsInStatement(labeled.Statement, context);
                break;
            // Intentionally skip: LambdaExpressionSyntax, AnonymousMethodExpressionSyntax
            // (they have their own label scope)
        }
    }

    private static LabelKind GetLabelKind(StatementSyntax labeledStatement)
    {
        return labeledStatement switch
        {
            ForStatementSyntax => LabelKind.Loop,
            WhileStatementSyntax => LabelKind.Loop,
            DoStatementSyntax => LabelKind.Loop,
            ForEachStatementSyntax => LabelKind.Loop,
            BlockSyntax => LabelKind.Block,
            _ => LabelKind.Other
        };
    }

    private JavaSyntaxNode TransformLabelStatement(LabeledStatementSyntax stmt, ConversionContext context)
    {
        var labelName = stmt.Identifier.Text;
        var innerResult = Transform(stmt.Statement, context).ToString("");

        // If the labeled statement is a loop or block, output directly;
        // otherwise wrap in a block so break-label works.
        if (stmt.Statement is ForStatementSyntax
            or WhileStatementSyntax
            or DoStatementSyntax
            or ForEachStatementSyntax
            or BlockSyntax)
        {
            return new JavaStatementNode($"{labelName}: {innerResult}");
        }
        else
        {
            return new JavaStatementNode($"{labelName}: {{ {innerResult}; }}");
        }
    }

    private JavaSyntaxNode TransformGotoStatement(GotoStatementSyntax stmt, ConversionContext context)
    {
        // goto case / goto default are not supported
        if (stmt.Kind() == SyntaxKind.GotoCaseStatement)
        {
            return new JavaStatementNode("/* TODO: goto case - unsupported */");
        }
        if (stmt.Kind() == SyntaxKind.GotoDefaultStatement)
        {
            return new JavaStatementNode("/* TODO: goto default - unsupported */");
        }

        // goto label
        var targetLabel = (stmt.Expression as IdentifierNameSyntax)?.Identifier.Text;
        if (targetLabel == null)
        {
            return new JavaStatementNode("/* TODO: goto - unsupported pattern */");
        }

        if (!context.Labels.Contains(targetLabel))
        {
            return new JavaStatementNode($"/* TODO: goto {targetLabel} - label not found in scope */");
        }

        if (!IsInLabelScope(stmt, targetLabel))
        {
            return new JavaStatementNode($"/* TODO: goto {targetLabel} - cross-scope goto unsupported */");
        }

        context.Labels.TryGetLabel(targetLabel, out var labelInfo);
        if (labelInfo!.Kind == LabelKind.Loop)
        {
            return new JavaStatementNode($"continue {targetLabel};");
        }
        else
        {
            return new JavaStatementNode($"break {targetLabel};");
        }
    }

    /// <summary>
    /// Checks whether a GotoStatement is within the scope of the target label
    /// by walking up the AST parent chain looking for a LabeledStatementSyntax
    /// with the matching name.
    /// </summary>
    private static bool IsInLabelScope(GotoStatementSyntax gotoStmt, string targetLabel)
    {
        var current = gotoStmt.Parent;
        while (current != null)
        {
            if (current is LabeledStatementSyntax labeled
                && labeled.Identifier.Text == targetLabel)
            {
                return true;
            }
            // Stop at method body boundary — labels don't cross method boundaries
            if (current is BlockSyntax block && block.Parent is MethodDeclarationSyntax
                or ConstructorDeclarationSyntax
                or OperatorDeclarationSyntax
                or ConversionOperatorDeclarationSyntax
                or ArrowExpressionClauseSyntax)
            {
                break;
            }
            current = current.Parent;
        }
        return false;
    }
}
```

- [ ] **Step 2: Build and verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --no-restore`
Expected: Build succeeds (methods are not yet called, but class compiles)

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.LabelAndGoto.cs
git commit -m "feat: add StatementTransformer.LabelAndGoto partial class"
```

---

### Task 4: Wire up LabelAndGoto in StatementTransformer.cs

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs`

- [ ] **Step 1: Add switch cases for LabelStatement, GotoStatement, GotoCaseStatement, GotoDefaultStatement**

In the `Transform` method's switch expression (line 21-47), add these cases before the `_ =>` default:

After line 36 (`SyntaxKind.ContinueStatement => new JavaStatementNode("continue;"),`), add:

```csharp
            SyntaxKind.LabeledStatement => TransformLabelStatement(node as LabeledStatementSyntax, context),
            SyntaxKind.GotoStatement => TransformGotoStatement(node as GotoStatementSyntax, context),
            SyntaxKind.GotoCaseStatement => TransformGotoStatement(node as GotoStatementSyntax, context),
            SyntaxKind.GotoDefaultStatement => TransformGotoStatement(node as GotoStatementSyntax, context),
```

- [ ] **Step 2: Add PrescanLabels call in TransformBlock**

In the `TransformBlock` method (line 50-67), add the label pre-scan after the lambda capture pre-scan block. After line 61 (`context.MethodState.LambdaCapturePreScanDone = true;`), add:

```csharp
        // Pre-scan labels at method body level (scope depth 0).
        // This registers label names and their kinds so that goto-label
        // can be correctly translated to break/continue-label.
        if (context.MethodState.ScopeDepth == 0)
        {
            PrescanLabels(block.Statements, context);
        }
```

- [ ] **Step 3: Add GotoStatement to IsUnconditionalJump**

In the `IsUnconditionalJump` method (line 221-225), change to:

```csharp
    private static bool IsUnconditionalJump(StatementSyntax statement)
        => statement is ReturnStatementSyntax
            or ThrowStatementSyntax
            or BreakStatementSyntax
            or ContinueStatementSyntax
            or GotoStatementSyntax;
```

- [ ] **Step 4: Build and verify compilation**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --no-restore`
Expected: Build succeeds

- [ ] **Step 5: Run existing tests to check for regressions**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --no-restore --filter "FullyQualifiedName~Statement" -v q`
Expected: All existing tests pass

- [ ] **Step 6: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs
git commit -m "feat: wire up LabelStatement and GotoStatement in StatementTransformer"
```

---

### Task 5: Write unit tests for LabelStatement

**Files:**
- Create: `tests/CSharpToJava.Tests/LabelGotoStatementTests.cs`

- [ ] **Step 1: Write the test file with LabelStatement tests**

```csharp
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class LabelGotoStatementTests
{
    // ─── LabelStatement Tests ──────────────────────────────────────────

    [Fact]
    public void Label_ForLoop()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) { }
    }
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        Assert.Contains("outer: for", result.GeneratedCode);
    }

    [Fact]
    public void Label_WhileLoop()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: while (true) { }
    }
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        Assert.Contains("outer: while", result.GeneratedCode);
    }

    [Fact]
    public void Label_DoWhileLoop()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: do { } while (true);
    }
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        Assert.Contains("outer: do", result.GeneratedCode);
    }

    [Fact]
    public void Label_ForeachLoop()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(List<int> list) {
        outer: foreach (var x in list) { }
    }
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        Assert.Contains("outer: for", result.GeneratedCode);
    }

    [Fact]
    public void Label_Block()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: { int x = 1; }
    }
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        Assert.Contains("target: {", result.GeneratedCode);
    }

    [Fact]
    public void Label_SimpleStatement_WrappedInBlock()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: x = 1;
    }
    int x;
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        Assert.Contains("target: { x = 1; }", result.GeneratedCode);
    }

    [Fact]
    public void Label_NestedLabels()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) {
            inner: for (int j = 0; j < 10; j++) { }
        }
    }
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        Assert.Contains("outer: for", result.GeneratedCode);
        Assert.Contains("inner: for", result.GeneratedCode);
    }

    // ─── GotoStatement Tests ──────────────────────────────────────────

    [Fact]
    public void Goto_LoopLabel_Continue()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) {
            if (i == 5) goto outer;
        }
    }
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        Assert.Contains("continue outer;", result.GeneratedCode);
    }

    [Fact]
    public void Goto_BlockLabel_Break()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: {
            if (true) goto target;
        }
    }
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        Assert.Contains("break target;", result.GeneratedCode);
    }

    [Fact]
    public void Goto_OtherLabel_Break()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: x = 1;
        if (true) goto target;
    }
    int x;
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        Assert.Contains("break target;", result.GeneratedCode);
    }

    [Fact]
    public void GotoCase_Unsupported()
    {
        var result = Convert(@"
class Test {
    void M(int x) {
        switch (x) {
            case 1:
                goto case 2;
            case 2:
                break;
        }
    }
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        Assert.Contains("/* TODO: goto case - unsupported */", result.GeneratedCode);
    }

    [Fact]
    public void GotoDefault_Unsupported()
    {
        var result = Convert(@"
class Test {
    void M(int x) {
        switch (x) {
            case 1:
                goto default;
            default:
                break;
        }
    }
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        Assert.Contains("/* TODO: goto default - unsupported */", result.GeneratedCode);
    }

    [Fact]
    public void Goto_CrossScope_Unsupported()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: { }
        goto target;
    }
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        Assert.Contains("/* TODO: goto target - cross-scope goto unsupported */", result.GeneratedCode);
    }

    [Fact]
    public void Goto_UnknownLabel_Unsupported()
    {
        var result = Convert(@"
class Test {
    void M() {
        goto nonexistent;
    }
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        Assert.Contains("/* TODO: goto nonexistent - label not found in scope */", result.GeneratedCode);
    }

    // ─── Break/Continue Label Tests ────────────────────────────────────

    [Fact]
    public void Break_Label_Preserved()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) {
            for (int j = 0; j < 10; j++) {
                if (j == 5) break outer;
            }
        }
    }
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        // C# break does not support labels — it's just a plain break.
        // If the code compiles, the label is preserved on the for loop.
        Assert.Contains("outer: for", result.GeneratedCode);
    }

    [Fact]
    public void Goto_WhileLoop_Continue()
    {
        var result = Convert(@"
class Test {
    void M() {
        restart: while (true) {
            if (System.DateTime.Now.Ticks > 0) goto restart;
        }
    }
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        Assert.Contains("continue restart;", result.GeneratedCode);
    }

    // ─── Registry Isolation Test ───────────────────────────────────────

    [Fact]
    public void Label_Registry_ClearedBetweenMethods()
    {
        var result = Convert(@"
class Test {
    void M1() {
        outer: for (int i = 0; i < 10; i++) { }
    }
    void M2() {
        goto outer;
    }
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        // The label 'outer' from M1 should NOT be visible in M2
        Assert.Contains("/* TODO: goto outer - label not found in scope */", result.GeneratedCode);
    }

    // ─── Goto inside nested structures ────────────────────────────────

    [Fact]
    public void Goto_LabelInsideIf_WithinScope()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: for (int i = 0; i < 10; i++) {
            if (i == 3) {
                goto target;
            }
        }
    }
}");
        Assert.True(result.Success, result.ErrorMessage ?? "Conversion failed");
        Assert.Contains("continue target;", result.GeneratedCode);
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

- [ ] **Step 2: Run the tests — expect all to pass**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --no-restore --filter "FullyQualifiedName~LabelGotoStatementTests" -v normal`
Expected: All tests pass

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/LabelGotoStatementTests.cs
git commit -m "test: add LabelStatement and GotoStatement unit tests"
```

---

### Task 6: Run full test suite and fix any regressions

**Files:** None (verification only)

- [ ] **Step 1: Run full test suite**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --no-restore -v q`
Expected: All tests pass

- [ ] **Step 2: If any test fails, investigate and fix**

Common issues:
- PrescanLabels may need to skip more node types (e.g., local function bodies)
- IsInLabelScope boundary detection may need adjustment

- [ ] **Step 3: Final commit if any fixes were needed**

```bash
git add -u
git commit -m "fix: address label/goto test regressions"
```
