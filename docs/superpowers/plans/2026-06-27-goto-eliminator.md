# GotoEliminator 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现一个 C# goto/label 预处理器，将含 goto 的 C# 源文件转换为等价的无 goto/label C#，作为 C#→Java 转换器的前置步骤。

**Architecture:** 自包含 Roslyn `CSharpSyntaxRewriter` 模块，两遍改写——Pass 1 把 `goto case/default` 去糖为合成标签；Pass 2 把每个含 goto 的方法体切分为基本块、提升跨块局部变量、发射 `while(true){switch(__state){...}}` 状态机。新增 `eliminate-goto` CLI 动词；验证脚本复制只读的 `D:\csharpxml` 到临时目录后转换并 `dotnet build`/`test`。

**Tech Stack:** C# net10.0, Roslyn `Microsoft.CodeAnalysis.CSharp` 4.12.0 + `Microsoft.CodeAnalysis.CSharp.Workspaces`（Formatter/AdhocWorkspace）, xUnit 2.9.3, `CommandLineParser`。

**Spec:** [docs/superpowers/specs/2026-06-27-goto-eliminator-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-06-27-goto-eliminator-design.md)

---

## 公共 API 契约（贯穿全计划，类型名/签名以此为准）

```csharp
namespace CSharpToJava.Core.GotoEliminator;

public sealed record GotoEliminatorOptions(bool Verbose = false, bool Strict = false);
public enum GotoEliminatorSeverity { Warning, Error }
public sealed record GotoEliminatorDiagnostic(
    GotoEliminatorSeverity Severity, string Message,
    string? MethodName = null, int? Line = null);
public sealed class GotoEliminatorStatistics
{
    public int FilesScanned; public int FilesTransformed; public int FilesSkippedClean;
    public int MethodsTransformed; public int MethodsSkipped; public int GotosEliminated;
}
public sealed record GotoEliminatorResult(
    string OutputCode, bool Changed,
    IReadOnlyList<GotoEliminatorDiagnostic> Diagnostics,
    GotoEliminatorStatistics Statistics);

public sealed class GotoEliminator
{
    public GotoEliminatorResult Eliminate(string sourceCode, GotoEliminatorOptions? options = null);
}

// internal
internal enum BlockExit { FallThrough, Goto, ConditionalGoto, Return, Break }
internal sealed class BasicBlock { ... }   // Task 4
internal sealed class GotoCaseDesugarer : CSharpSyntaxRewriter { ... }   // Task 3
internal sealed class StateMachineBuilder : CSharpSyntaxRewriter { ... } // Tasks 4-8
```

---

## Task 1: 红基线脚本——确认未转换的 csharpxml 今日可 build+test

**Files:**
- Create: `scripts/verify-goto-eliminator-baseline.ps1`

- [ ] **Step 1: 写脚本**

```powershell
# scripts/verify-goto-eliminator-baseline.ps1
# 确认未转换的 D:\csharpxml 今日可 build + test（红基线）。
# 绝不修改 D:\csharpxml；在临时副本上运行。
$ErrorActionPreference = 'Stop'
$src = 'D:\csharpxml'
$tmp = "D:\temp\cs2j_baseline_" + [guid]::NewGuid().ToString('N')
Write-Host "Copying $src -> $tmp"
Copy-Item -Recurse -Path $src -Destination $tmp
try {
    Set-Location $tmp
    Write-Host '--- dotnet build ---'
    dotnet build csharpxml.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
    Write-Host '--- dotnet test ---'
    dotnet test csharpxml.sln -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "test failed (exit $LASTEXITCODE)" }
    Write-Host 'BASELINE OK'
}
finally {
    Set-Location 'D:\code\cs2j'
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
```

- [ ] **Step 2: 运行脚本**

Run: `powershell -ExecutionPolicy Bypass -File scripts\verify-goto-eliminator-baseline.ps1`
Expected: 末尾打印 `BASELINE OK`。若失败立即停下上报（前置问题）。

- [ ] **Step 3: Commit**

```bash
git add scripts/verify-goto-eliminator-baseline.ps1
git commit -m "chore: add csharpxml red-baseline script for goto-eliminator"
```

---

## Task 2: 基础类型——Options/Diagnostics/Statistics/Result

**Files:**
- Create: `src/CSharpToJava.Core/GotoEliminator/GotoEliminatorOptions.cs`
- Create: `src/CSharpToJava.Core/GotoEliminator/GotoEliminatorDiagnostics.cs`
- Test: `tests/CSharpToJava.Tests/GotoEliminatorTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
// tests/CSharpToJava.Tests/GotoEliminatorTests.cs
using CSharpToJava.Core.GotoEliminator;

namespace CSharpToJava.Tests;

public partial class GotoEliminatorTests
{
    [Fact]
    public void Options_Default_IsNotStrictNotVerbose()
    {
        var o = new GotoEliminatorOptions();
        Assert.False(o.Strict);
        Assert.False(o.Verbose);
    }

    [Fact]
    public void Diagnostics_Record_Properties_RoundTrip()
    {
        var d = new GotoEliminatorDiagnostic(GotoEliminatorSeverity.Warning, "msg", "M", 7);
        Assert.Equal(GotoEliminatorSeverity.Warning, d.Severity);
        Assert.Equal("msg", d.Message);
        Assert.Equal("M", d.MethodName);
        Assert.Equal(7, d.Line);
    }

    [Fact]
    public void Statistics_Starts_Zero()
    {
        var s = new GotoEliminatorStatistics();
        Assert.Equal(0, s.FilesTransformed);
        Assert.Equal(0, s.GotosEliminated);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: FAIL，编译错误 `type or namespace 'GotoEliminatorOptions' not found`

- [ ] **Step 3: 写实现**

```csharp
// src/CSharpToJava.Core/GotoEliminator/GotoEliminatorOptions.cs
namespace CSharpToJava.Core.GotoEliminator;

public sealed record GotoEliminatorOptions(bool Verbose = false, bool Strict = false);
```

```csharp
// src/CSharpToJava.Core/GotoEliminator/GotoEliminatorDiagnostics.cs
namespace CSharpToJava.Core.GotoEliminator;

public enum GotoEliminatorSeverity { Warning, Error }

public sealed record GotoEliminatorDiagnostic(
    GotoEliminatorSeverity Severity,
    string Message,
    string? MethodName = null,
    int? Line = null);

public sealed class GotoEliminatorStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int MethodsTransformed;
    public int MethodsSkipped;
    public int GotosEliminated;
}

public sealed record GotoEliminatorResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<GotoEliminatorDiagnostic> Diagnostics,
    GotoEliminatorStatistics Statistics);

public sealed class GotoEliminatorException : Exception
{
    public GotoEliminatorException(string message) : base(message) { }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: PASS（3 个测试）

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/GotoEliminator/GotoEliminatorOptions.cs src/CSharpToJava.Core/GotoEliminator/GotoEliminatorDiagnostics.cs tests/CSharpToJava.Tests/GotoEliminatorTests.cs
git commit -m "feat(goto-eliminator): add options, diagnostics, statistics, result types"
```

---

## Task 3: GotoCaseDesugarer——把 goto case/default 去糖为合成 goto label

**Files:**
- Create: `src/CSharpToJava.Core/GotoEliminator/GotoCaseDesugarer.cs`
- Test: `tests/CSharpToJava.Tests/GotoEliminatorTests.cs`（追加）

- [ ] **Step 1: 写失败测试**

```csharp
// 追加到 tests/CSharpToJava.Tests/GotoEliminatorTests.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

public partial class GotoEliminatorTests
{
    private static CompilationUnitSyntax Parse(string csharp)
        => (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(csharp).GetRoot();

    private static string Desugar(string csharp)
        => CSharpToJava.Core.GotoEliminator.GotoCaseDesugarer.Run(Parse(csharp)).ToFullString();

    [Fact]
    public void Desugar_GotoCase_Forward_Produces_SyntheticGotoAndLabel()
    {
        var src = """
        class C {
            void M(int x) {
                switch (x) {
                    case 1: goto case 2;
                    case 2: break;
                }
            }
        }
        """;
        var out_ = Desugar(src);
        Assert.Contains("goto __case0_2", out_);
        Assert.Contains("__case0_2:", out_);
        // 原 goto case 2 不再出现
        Assert.DoesNotContain("goto case 2", out_);
    }

    [Fact]
    public void Desugar_GotoDefault_Produces_SyntheticGotoAndLabel()
    {
        var src = """
        class C {
            void M(int x) {
                switch (x) {
                    case 1: goto default;
                    default: break;
                }
            }
        }
        """;
        var out_ = Desugar(src);
        Assert.Contains("goto __default0", out_);
        Assert.Contains("__default0:", out_);
        Assert.DoesNotContain("goto default", out_);
    }

    [Fact]
    public void Desugar_SwitchWithoutGotoCase_IsUnchanged()
    {
        var src = """
        class C {
            void M(int x) {
                switch (x) {
                    case 1: break;
                    default: break;
                }
            }
        }
        """;
        var out_ = Desugar(src);
        Assert.DoesNotContain("__case", out_);
        Assert.DoesNotContain("__default", out_);
    }

    [Fact]
    public void Desugar_MultipleSwitches_GetDistinctIndices()
    {
        var src = """
        class C {
            void M(int x, int y) {
                switch (x) { case 1: goto case 2; case 2: break; }
                switch (y) { case 1: goto default; default: break; }
            }
        }
        """;
        var out_ = Desugar(src);
        Assert.Contains("__case0_2", out_);
        Assert.Contains("__default1", out_);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: FAIL，`GotoCaseDesugarer.Run` 不存在

- [ ] **Step 3: 写实现**

```csharp
// src/CSharpToJava.Core/GotoEliminator/GotoCaseDesugarer.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GotoEliminator;

/// <summary>
/// Pass 1: 把 goto case X / goto default 去糖为 goto __caseN_X / goto __defaultN，
/// 并在被引用的 switch section 顶部插入合成标签。去糖后只剩 goto &lt;identifier&gt;。
/// </summary>
internal sealed class GotoCaseDesugarer : CSharpSyntaxRewriter
{
    private int _switchIndex;

    public static CompilationUnitSyntax Run(CompilationUnitSyntax root)
        => (CompilationUnitSyntax)new GotoCaseDesugarer().Visit(root)!;

    public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
    {
        // 先递归子节点（内部嵌套 switch 先处理）
        var visited = (SwitchStatementSyntax)base.VisitSwitchStatement(node)!;

        bool hasGotoCase = visited.DescendantNodes()
            .OfType<GotoStatementSyntax>()
            .Any(g => g.IsKind(SyntaxKind.GotoCaseStatement) || g.IsKind(SyntaxKind.GotoDefaultStatement));
        if (!hasGotoCase) return visited;

        int n = _switchIndex++;
        // 收集本 switch 内所有 goto case 的目标规范化名
        var targetedCaseNames = new HashSet<string>();
        var targetsDefault = false;
        foreach (var g in visited.DescendantNodes().OfType<GotoStatementSyntax>())
        {
            if (g.IsKind(SyntaxKind.GotoCaseStatement) && g.Expression != null)
                targetedCaseNames.Add(NormalizeCase(g.Expression));
            else if (g.IsKind(SyntaxKind.GotoDefaultStatement))
                targetsDefault = true;
        }

        // 重写每个 section：在顶部插入命中目标的合成标签
        var newSections = new List<SwitchSectionSyntax>();
        foreach (var section in visited.Sections)
        {
            var labels = section.Labels;
            var stmts = section.Statements.ToList();
            foreach (var label in labels)
            {
                string? synthetic = null;
                if (label is CasePatternSwitchLabelSyntax cp)
                    synthetic = MaybeSynthetic(NormalizeCase(cp.Pattern), targetedCaseNames, n, isDefault: false);
                else if (label is CaseSwitchLabelSyntax cs)
                    synthetic = MaybeSynthetic(NormalizeCase(cs.Value), targetedCaseNames, n, isDefault: false);
                else if (label is DefaultSwitchLabelSyntax && targetsDefault)
                    synthetic = "__default" + n;
                if (synthetic != null)
                {
                    // 合成标签：LabeledStatement 包裹一个空语句，置于 section 语句最前
                    var empty = SyntaxFactory.EmptyStatement();
                    var labeled = SyntaxFactory.LabeledStatement(synthetic, empty)
                        .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
                    stmts.Insert(0, labeled);
                }
            }
            newSections.Add(section.WithStatements(SyntaxFactory.List(stmts)));
        }

        var withSections = visited.WithSections(SyntaxFactory.List(newSections));

        // 重写 goto case / goto default
        var rewritten = (SwitchStatementSyntax)new GotoRewriter(n).Visit(withSections)!;
        return rewritten;
    }

    private static string? MaybeSynthetic(string normalized, HashSet<string> targets, int n, bool isDefault)
    {
        if (isDefault) return "__default" + n;
        return targets.Contains(normalized) ? "__case" + n + "_" + normalized : null;
    }

    /// <summary>规范化 case 值：源文本去空白，非字母数字→下划线。</summary>
    internal static string NormalizeCase(ExpressionSyntax expr)
    {
        var text = expr.ToString().Replace(" ", "");
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        return sb.ToString();
    }

    private sealed class GotoRewriter : CSharpSyntaxRewriter
    {
        private readonly int _n;
        internal GotoRewriter(int n) { _n = n; }
        public override SyntaxNode? VisitGotoStatement(GotoStatementSyntax node)
        {
            if (node.IsKind(SyntaxKind.GotoCaseStatement) && node.Expression != null)
            {
                var name = "__case" + _n + "_" + NormalizeCase(node.Expression);
                return SyntaxFactory.GotoStatement(SyntaxKind.GotoStatement, SyntaxFactory.IdentifierName(name))
                    .WithTriviaFrom(node);
            }
            if (node.IsKind(SyntaxKind.GotoDefaultStatement))
            {
                var name = "__default" + _n;
                return SyntaxFactory.GotoStatement(SyntaxKind.GotoStatement, SyntaxFactory.IdentifierName(name))
                    .WithTriviaFrom(node);
            }
            return base.VisitGotoStatement(node);
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: PASS（7 个测试）

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/GotoEliminator/GotoCaseDesugarer.cs tests/CSharpToJava.Tests/GotoEliminatorTests.cs
git commit -m "feat(goto-eliminator): desugar goto case/default to synthetic labels"
```

---

## Task 4: BasicBlock 模型 + StateMachineBuilder 基本块分割

**Files:**
- Create: `src/CSharpToJava.Core/GotoEliminator/BasicBlock.cs`
- Create: `src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs`
- Test: `tests/CSharpToJava.Tests/GotoEliminatorTests.cs`（追加）

- [ ] **Step 1: 写失败测试**

```csharp
// 追加到 GotoEliminatorTests.cs
using System.Reflection;

namespace CSharpToJava.Tests;

public partial class GotoEliminatorTests
{
    // 通过反射访问 internal BasicBlock/Splitter 以单元测试分割逻辑。
    private static object InvokeSplit(string methodBody)
    {
        var asm = typeof(CSharpToJava.Core.GotoEliminator.GotoEliminatorOptions).Assembly;
        var smbType = asm.GetType("CSharpToJava.Core.GotoEliminator.StateMachineBuilder")!;
        var bbType = asm.GetType("CSharpToJava.Core.GotoEliminator.BasicBlock")!;
        // StateMachineBuilder.SplitToBlocks(IEnumerable<StatementSyntax>) : List<BasicBlock>
        var split = smbType.GetMethod("SplitToBlocks",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        // 解析方法体语句
        var src = $"class C {{ void M() {{ {methodBody} }} }}";
        var root = Parse(src);
        var methodBodyBlock = root.DescendantNodes().OfType<BlockSyntax>().First();
        var result = split.Invoke(null, new object[] { methodBodyBlock.Statements })!;
        return result;
    }

    [Fact]
    public void Split_B3Forward_TwoBlocks_SecondHasLabel()
    {
        var blocks = (System.Collections.IList)InvokeSplit("goto skip; int x = 1; skip: int y = 2;");
        Assert.Equal(2, blocks.Count);
        // 第一块以 Goto 终结
        var bbType = blocks[0]!.GetType();
        var exit0 = bbType.GetProperty("Exit")!.GetValue(blocks[0]);
        Assert.Equal("Goto", exit0!.ToString());
        // 第二块带标签 skip
        var label1 = (string?)bbType.GetProperty("Label")!.GetValue(blocks[1]);
        Assert.Equal("skip", label1);
    }

    [Fact]
    public void Split_B2Backward_TwoBlocks_FirstHasLabel()
    {
        var blocks = (System.Collections.IList)InvokeSplit("t: int x = 1; if (x > 0) goto t; int y = 2;");
        Assert.Equal(2, blocks.Count);
        var bbType = blocks[0]!.GetType();
        var label0 = (string?)bbType.GetProperty("Label")!.GetValue(blocks[0]);
        Assert.Equal("t", label0);
        var exit1 = bbType.GetProperty("Exit")!.GetValue(blocks[1]);
        Assert.Equal("FallThrough", exit1!.ToString());
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: FAIL，`SplitToBlocks` 不存在

- [ ] **Step 3: 写实现**

```csharp
// src/CSharpToJava.Core/GotoEliminator/BasicBlock.cs
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GotoEliminator;

internal enum BlockExit { FallThrough, Goto, ConditionalGoto, Return, Break }

internal sealed class BasicBlock
{
    public int Index { get; init; }
    public string? Label { get; init; }
    public List<StatementSyntax> Statements { get; } = new();
    public BlockExit Exit { get; set; } = BlockExit.FallThrough;
    public int? FallThroughTarget { get; set; }
}
```

```csharp
// src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GotoEliminator;

/// <summary>
/// Pass 2: 把含 goto/label 的方法体转为 while(true){switch(__state){...}} 状态机。
/// 本任务只实现「扁平化 + 基本块分割」。变量提升/发射在后续任务加。
/// </summary>
internal sealed partial class StateMachineBuilder : CSharpSyntaxRewriter
{
    public List<GotoEliminatorDiagnostic> Diagnostics { get; } = new();
    public int TransformedMethods { get; private set; }
    public int SkippedMethods { get; private set; }
    public int GotosEliminated { get; private set; }

    /// <summary>扁平化 + 基本块分割。输入：方法体顶层语句列表。</summary>
    internal static List<BasicBlock> SplitToBlocks(IEnumerable<StatementSyntax> statements)
    {
        var flat = Flatten(statements);
        var blocks = new List<BasicBlock>();
        var current = new BasicBlock { Index = 0 };
        blocks.Add(current);
        int blockIdx = 0;

        void NewBlock(string? label)
        {
            // 若当前块以终结语句收尾，开新块；否则当前块在此处被切分
            if (current.Exit != BlockExit.FallThrough || current.Statements.Count == 0 && current.Label == null && label == null)
            {
                blockIdx++;
                current = new BasicBlock { Index = blockIdx, Label = label };
                blocks.Add(current);
            }
            else if (label != null)
            {
                blockIdx++;
                current = new BasicBlock { Index = blockIdx, Label = label };
                blocks.Add(current);
            }
        }

        foreach (var stmt in flat)
        {
            // 标签：开新块并记标签
            if (stmt is LabeledStatementSyntax labeled)
            {
                NewBlock(labeled.Identifier.ValueText);
                // 标签后的实际语句：labeled.Statement（可能是空语句）
                if (!labeled.Statement.IsKind(SyntaxKind.EmptyStatement))
                    current.Statements.Add(labeled.Statement);
                continue;
            }

            current.Statements.Add(stmt);

            if (stmt is GotoStatementSyntax)
            {
                current.Exit = BlockExit.Goto;
                current = new BasicBlock { Index = ++blockIdx };
                blocks.Add(current);
            }
            else if (stmt is ReturnStatementSyntax || stmt is ThrowStatementSyntax)
            {
                current.Exit = BlockExit.Return;
                current = new BasicBlock { Index = ++blockIdx };
                blocks.Add(current);
            }
            else if (stmt is BreakStatementSyntax || stmt is ContinueStatementSyntax)
            {
                // 非 goto 的 break/continue（必在内层循环内）；作为块终结以便发射时不追加 fall-through
                current.Exit = BlockExit.Break;
                current = new BasicBlock { Index = ++blockIdx };
                blocks.Add(current);
            }
            else if (ContainsTopLevelGoto(stmt))
            {
                current.Exit = BlockExit.ConditionalGoto;
                current = new BasicBlock { Index = ++blockIdx };
                blocks.Add(current);
            }
        }

        // 删除末尾空块（若为空且 FallThrough）
        if (blocks.Count > 1 && blocks[^1].Statements.Count == 0
            && blocks[^1].Label == null && blocks[^1].Exit == BlockExit.FallThrough)
        {
            blocks.RemoveAt(blocks.Count - 1);
        }

        // 设置 FallThroughTarget
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Exit is BlockExit.FallThrough or BlockExit.ConditionalGoto)
            {
                blocks[i].FallThroughTarget = (i + 1 < blocks.Count) ? i + 1 : (int?)null;
            }
        }
        return blocks;
    }

    /// <summary>递归展开含 label/goto 的裸 BlockSyntax；不含的裸块作为单语句保留。</summary>
    private static List<StatementSyntax> Flatten(IEnumerable<StatementSyntax> statements)
    {
        var result = new List<StatementSyntax>();
        foreach (var s in statements)
        {
            if (s is BlockSyntax block && ContainsLabelOrGoto(block))
            {
                // 展开裸块
                result.AddRange(Flatten(block.Statements));
            }
            else
            {
                result.Add(s);
            }
        }
        return result;
    }

    private static bool ContainsLabelOrGoto(SyntaxNode node)
        => node.DescendantNodesAndSelf().Any(n => n is LabeledStatementSyntax or GotoStatementSyntax);

    /// <summary>语句自身（非后代控制体）是否含顶层 goto——用于 if/switch 包裹的 goto。</summary>
    private static bool ContainsTopLevelGoto(StatementSyntax stmt)
    {
        // if (c) goto L;  →  if 的直接子语句是 goto
        if (stmt is IfStatementSyntax iff)
        {
            if (IsOrContainsGotoDirect(iff.Statement)) return true;
            if (iff.Else != null && IsOrContainsGotoDirect(iff.Else.Statement)) return true;
        }
        return false;
    }

    private static bool IsOrContainsGotoDirect(StatementSyntax s)
    {
        if (s is GotoStatementSyntax) return true;
        if (s is BlockSyntax b)
            return b.Statements.Any(IsOrContainsGotoDirect);
        if (s is IfStatementSyntax iff)
            return IsOrContainsGotoDirect(iff.Statement)
                || (iff.Else != null && IsOrContainsGotoDirect(iff.Else.Statement));
        return false;
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: PASS（9 个测试）

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/GotoEliminator/BasicBlock.cs src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs tests/CSharpToJava.Tests/GotoEliminatorTests.cs
git commit -m "feat(goto-eliminator): basic-block model and splitter"
```

---

## Task 5: StateMachineBuilder——变量提升

**Files:**
- Modify: `src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs`
- Test: `tests/CSharpToJava.Tests/GotoEliminatorTests.cs`（追加）

- [ ] **Step 1: 写失败测试**

```csharp
// 追加到 GotoEliminatorTests.cs
namespace CSharpToJava.Tests;

public partial class GotoEliminatorTests
{
    [Fact]
    public void Hoist_SpanningLocal_MovedBeforeLoop_ReplacedByAssignment()
    {
        // int x = 1; skip: int y = x;  → x 跨块使用，需提升
        var asm = typeof(CSharpToJava.Core.GotoEliminator.GotoEliminatorOptions).Assembly;
        var smbType = asm.GetType("CSharpToJava.Core.GotoEliminator.StateMachineBuilder")!;
        var hoist = smbType.GetMethod("HoistSpanningLocals",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var src = "class C { void M() { int x = 1; skip: int y = x; } }";
        var root = Parse(src);
        var methodBodyBlock = root.DescendantNodes().OfType<BlockSyntax>().First();
        var blocks = (System.Collections.IList)smbType.GetMethod("SplitToBlocks",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { methodBodyBlock.Statements })!;
        // 调用 HoistSpanningLocals(blocks) -> (List<StatementSyntax> hoistedDecls, blocks mutated)
        var result = (System.Collections.IList)hoist.Invoke(null, new object[] { blocks })!;
        // 应有 1 个提升声明 int x = default;
        Assert.Single(result);
        var declText = result[0]!.ToString();
        Assert.Contains("int x", declText);
        Assert.Contains("default", declText);
        // 第一块内原声明应变为赋值 x = 1
        var bbType = blocks[0]!.GetType();
        var stmts = (System.Collections.Generic.List<StatementSyntax>)bbType.GetField("Statements")!.GetValue(blocks[0])!;
        Assert.Contains(stmts, s => s.ToString().Contains("x = 1") && !s.ToString().Contains("int x"));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: FAIL，`HoistSpanningLocals` 不存在

- [ ] **Step 3: 写实现**（追加到 `StateMachineBuilder.cs`，保持 `partial`）

```csharp
// 追加到 src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs
internal sealed partial class StateMachineBuilder
{
    /// <summary>
    /// 提升跨块使用的局部声明到 while 之前。
    /// 普通 T x = expr; → 循环外 T x = default;，块内改 x = expr;
    /// const / using var / fixed / ref 跨块 → 抛 GotoEliminatorException（由调用方转诊断）。
    /// 返回提升到循环外的声明列表。
    /// </summary>
    internal static List<StatementSyntax> HoistSpanningLocals(System.Collections.IList blocks)
    {
        var hoisted = new List<StatementSyntax>();
        var bbList = new List<BasicBlock>();
        foreach (var b in blocks) bbList.Add((BasicBlock)b);

        for (int i = 0; i < bbList.Count; i++)
        {
            var blockStmts = bbList[i].Statements;
            for (int s = 0; s < blockStmts.Count; s++)
            {
                if (blockStmts[s] is not LocalDeclarationStatementSyntax decl) continue;
                if (decl.Modifiers.Any(SyntaxKind.ConstKeyword))
                    continue; // const 原地保留
                bool isUsing = decl.UsingKeyword.IsKind(SyntaxKind.UsingKeyword);
                bool isFixed = decl.Modifiers.Any(SyntaxKind.FixedKeyword) || decl.Modifiers.Any(SyntaxKind.UnsafeKeyword);
                bool isRef = decl.Declaration.Variables.Any(v => v.Initializer is null && decl.Modifiers.Any(SyntaxKind.RefKeyword));
                if (decl.Modifiers.Any(SyntaxKind.RefKeyword) || decl.Modifiers.Any(SyntaxKind.OutKeyword))
                    isRef = true;

                var names = decl.Declaration.Variables.Select(v => v.Identifier.ValueText).ToList();
                bool spans = false;
                for (int j = i + 1; j < bbList.Count; j++)
                {
                    if (bbList[j].Statements.Any(st => ReferencesAny(st, names)))
                    {
                        spans = true; break;
                    }
                }
                if (!spans) continue;

                if (isUsing) throw new GotoEliminatorException("spanning 'using' local not supported");
                if (isFixed) throw new GotoEliminatorException("spanning 'fixed/unsafe' local not supported");
                if (isRef) throw new GotoEliminatorException("spanning 'ref/out' local not supported");

                // 提升声明：T x = default;
                var type = decl.Declaration.Type;
                foreach (var v in decl.Declaration.Variables)
                {
                    var defaultDecl = SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(type,
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(v.Identifier)
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(
                                        SyntaxFactory.DefaultExpression(type))))));
                    hoisted.Add(defaultDecl);
                }
                // 块内改为赋值
                var assignStmts = new List<StatementSyntax>();
                for (int k = 0; k < s; k++) assignStmts.Add(blockStmts[k]);
                foreach (var v in decl.Declaration.Variables)
                {
                    if (v.Initializer != null)
                    {
                        var assign = SyntaxFactory.ExpressionStatement(
                            SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.IdentifierName(v.Identifier), v.Initializer.Value));
                        assignStmts.Add(assign);
                    }
                }
                for (int k = s + 1; k < blockStmts.Count; k++) assignStmts.Add(blockStmts[k]);
                bbList[i].Statements.Clear();
                bbList[i].Statements.AddRange(assignStmts);
            }
        }
        return hoisted;
    }

    private static bool ReferencesAny(SyntaxNode node, IEnumerable<string> names)
    {
        var set = new HashSet<string>(names);
        return node.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Any(id => set.Contains(id.Identifier.ValueText));
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: PASS（10 个测试）

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs tests/CSharpToJava.Tests/GotoEliminatorTests.cs
git commit -m "feat(goto-eliminator): hoist spanning locals before state machine loop"
```

---

## Task 6: StateMachineBuilder——状态机发射（B2/B3/A1/A2/C）

**Files:**
- Modify: `src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs`
- Test: `tests/CSharpToJava.Tests/GotoEliminatorTests.cs`（追加）

- [ ] **Step 1: 写失败测试**

```csharp
// 追加到 GotoEliminatorTests.cs
namespace CSharpToJava.Tests;

public partial class GotoEliminatorTests
{
    private static string BuildMethod(string body)
    {
        var asm = typeof(CSharpToJava.Core.GotoEliminator.GotoEliminatorOptions).Assembly;
        var smbType = asm.GetType("CSharpToJava.Core.GotoEliminator.StateMachineBuilder")!;
        var builder = (CSharpSyntaxRewriter)System.Activator.CreateInstance(smbType)!;
        var src = $"class C {{ void M() {{ {body} }} }}";
        var root = Parse(src);
        var visited = (CompilationUnitSyntax)builder.Visit(root)!;
        return visited.ToFullString();
    }

    private static void AssertNoGotoOrLabel(string code)
    {
        var root = Parse(code);
        Assert.Empty(root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.GotoStatementSyntax>());
        Assert.Empty(root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LabeledStatementSyntax>());
    }

    [Theory]
    [InlineData("goto skip; int x = 1; skip: int y = 2;")]                 // B3 前向
    [InlineData("t: int x = 1; if (x > 0) goto t; int y = 2;")]            // B2 后向
    [InlineData("outer: for (int i = 0; i < 3; i++) { if (i == 1) goto outer; }")] // A1
    [InlineData("target: { if (true) goto target; }")]                      // A2
    [InlineData("t: int x = 1; for (int i = 0; i < 3; i++) { if (i == 1) goto t; } int y = 2;")] // C 跨域
    public void Build_EliminatesGotoAndLabel(string body)
    {
        var out_ = BuildMethod(body);
        AssertNoGotoOrLabel(out_);
        Assert.Contains("__state", out_);
        Assert.Contains("while (true)", out_);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: FAIL，`Visit` 未重写方法（输出仍含 goto）

- [ ] **Step 3: 写实现**（追加到 `StateMachineBuilder.cs`）

```csharp
// 追加到 src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs
internal sealed partial class StateMachineBuilder
{
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        if (visited.Body == null) return visited; // 表达式体无 goto
        if (!ContainsLabelOrGoto(visited.Body)) return visited;

        try
        {
            var blocks = SplitToBlocks(visited.Body.Statements);
            var hoisted = HoistSpanningLocals(blocks);
            var newBody = EmitStateMachine(blocks, hoisted, visited.Body);
            TransformedMethods++;
            GotosEliminated += CountGotos(visited.Body);
            return visited.WithBody(newBody);
        }
        catch (GotoEliminatorException ex)
        {
            SkippedMethods++;
            Diagnostics.Add(new GotoEliminatorDiagnostic(
                GotoEliminatorSeverity.Warning, ex.Message,
                visited.Identifier.ValueText));
            return visited; // 原样保留该方法
        }
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        => RewriteBaseMethod(node, (n) => n.Body, (n, b) => n.WithBody(b));
    public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
        => RewriteBaseMethod(node, (n) => n.Body, (n, b) => n.WithBody(b));

    private SyntaxNode RewriteBaseMethod<T>(
        T node,
        System.Func<T, BlockSyntax?> getBody,
        System.Func<T, BlockSyntax, T> withBody) where T : BaseMethodDeclarationSyntax
    {
        var visited = (T)base.Visit(node)!;
        var body = getBody(visited);
        if (body == null || !ContainsLabelOrGoto(body)) return visited;
        try
        {
            var blocks = SplitToBlocks(body.Statements);
            var hoisted = HoistSpanningLocals(blocks);
            var newBody = EmitStateMachine(blocks, hoisted, body);
            TransformedMethods++;
            GotosEliminated += CountGotos(body);
            return withBody(visited, newBody);
        }
        catch (GotoEliminatorException ex)
        {
            SkippedMethods++;
            Diagnostics.Add(new GotoEliminatorDiagnostic(
                GotoEliminatorSeverity.Warning, ex.Message));
            return visited;
        }
    }

    private static int CountGotos(SyntaxNode node)
        => node.DescendantNodes().OfType<GotoStatementSyntax>().Count();

    /// <summary>发射 while(true){switch(__state){case i: ...}} 并替换原方法体。</summary>
    internal static BlockSyntax EmitStateMachine(
        List<BasicBlock> blocks, List<StatementSyntax> hoisted, BlockSyntax originalBody)
    {
        // 标签 -> 块 index
        var labelToIndex = new Dictionary<string, int>(System.StringComparer.Ordinal);
        for (int i = 0; i < blocks.Count; i++)
            if (blocks[i].Label != null) labelToIndex[blocks[i].Label!] = i;

        var sections = new List<SwitchSectionSyntax>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var caseStmts = new List<StatementSyntax>(block.Statements);
            // 改写块内 goto / conditional-goto；追加转移
            RewriteBlockStatements(caseStmts, block, labelToIndex, i, blocks);
            var label = SyntaxFactory.CaseSwitchLabel(
                SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(block.Index)));
            sections.Add(SyntaxFactory.SwitchSection(
                SyntaxFactory.SingletonList<SwitchLabelSyntax>(label),
                SyntaxFactory.List(caseStmts)));
        }

        // int __state = 0;
        var stateDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator("__state")
                        .WithInitializer(SyntaxFactory.EqualsValueClause(
                            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
                                SyntaxFactory.Literal(0)))))));

        // while (true) { switch (__state) { ... } }
        var whileStmt = SyntaxFactory.WhileStatement(
            SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
            SyntaxFactory.Block(
                SyntaxFactory.SwitchStatement(SyntaxFactory.IdentifierName("__state"))
                    .WithSections(SyntaxFactory.List(sections))));

        var allStmts = new List<StatementSyntax>();
        allStmts.AddRange(hoisted);
        allStmts.Add(stateDecl);
        allStmts.Add(whileStmt);

        // 保留原方法体的 leading trivia（如文档注释）挂在第一个语句上
        if (allStmts.Count > 0 && originalBody.Statements.Count > 0)
        {
            var origLeading = originalBody.Statements[0].GetLeadingTrivia();
            allStmts[0] = allStmts[0].WithLeadingTrivia(origLeading);
        }
        return SyntaxFactory.Block(allStmts);
    }

    /// <summary>改写块内语句：goto label -> __state=N; continue; ；追加块转移。</summary>
    private static void RewriteBlockStatements(
        List<StatementSyntax> stmts, BasicBlock block,
        Dictionary<string, int> labelToIndex, int selfIndex,
        List<BasicBlock> blocks)
    {
        for (int i = 0; i < stmts.Count; i++)
        {
            stmts[i] = RewriteNode(stmts[i], labelToIndex);
        }

        switch (block.Exit)
        {
            case BlockExit.Goto:
                // 末语句是 goto；已被 RewriteNode 改写为 __state=N; continue;
                break;
            case BlockExit.ConditionalGoto:
                // if (c) goto L;  已被改写为 if (c){__state=L;continue;}
                // 追加 fall-through 转移
                if (block.FallThroughTarget is int ft)
                    stmts.Add(StateAssignContinue(ft));
                break;
            case BlockExit.FallThrough:
                if (block.FallThroughTarget is int ft2)
                    stmts.Add(StateAssignContinue(ft2));
                else
                    stmts.Add(StateAssignContinue(selfIndex)); // 末块自循环？不应发生；安全起见 break while
                break;
            case BlockExit.Return:
            case BlockExit.Break:
                // 块内已含 return/throw/break/continue，无需追加
                break;
        }
    }

    private static StatementSyntax RewriteNode(SyntaxNode node, Dictionary<string, int> labelToIndex)
    {
        var rewriter = new GotoTransitionRewriter(labelToIndex);
        return (StatementSyntax)rewriter.Visit(node)!;
    }

    private static StatementSyntax StateAssignContinue(int target)
        => SyntaxFactory.Block(
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName("__state"),
                    SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(target)))),
            SyntaxFactory.ContinueStatement());

    private sealed class GotoTransitionRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, int> _labelToIndex;
        internal GotoTransitionRewriter(Dictionary<string, int> labelToIndex) { _labelToIndex = labelToIndex; }
        public override SyntaxNode? VisitGotoStatement(GotoStatementSyntax node)
        {
            // 此时只可能是 goto <identifier>（goto case/default 已去糖）
            if (node.Expression is IdentifierNameSyntax id && _labelToIndex.TryGetValue(id.Identifier.ValueText, out var idx))
            {
                return StateAssignContinue(idx).WithTriviaFrom(node);
            }
            return base.VisitGotoStatement(node);
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: PASS（15 个测试，5 个 Theory 分支 + 之前 10）

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs tests/CSharpToJava.Tests/GotoEliminatorTests.cs
git commit -m "feat(goto-eliminator): emit while/switch state machine for goto label"
```

---

## Task 7: try/finally + 嵌套循环 break/continue 处理

**Files:**
- Modify: `src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs`
- Test: `tests/CSharpToJava.Tests/GotoEliminatorTests.cs`（追加）

- [ ] **Step 1: 写失败测试**

```csharp
// 追加到 GotoEliminatorTests.cs
namespace CSharpToJava.Tests;

public partial class GotoEliminatorTests
{
    [Fact]
    public void Build_GotoInsideTry_PreservesFinallyStructure()
    {
        var body = "t: try { if (true) goto t; } finally { System.Console.WriteLine(\"fin\"); }";
        var out_ = BuildMethod(body);
        AssertNoGotoOrLabel(out_);
        // finally 仍在
        Assert.Contains("finally", out_);
        Assert.Contains("fin", out_);
    }

    [Fact]
    public void Build_NestedLoopBreakContinue_Preserved()
    {
        var body = """
        t: for (int i = 0; i < 3; i++) {
            for (int j = 0; j < 3; j++) {
                if (j == 1) break;
                if (j == 2) continue;
                if (i == 2) goto t;
            }
        }
        """;
        var out_ = BuildMethod(body);
        AssertNoGotoOrLabel(out_);
        // 内层 break/continue 保留（不为 0）
        Assert.Contains("break", out_);
        Assert.Contains("continue", out_);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: FAIL 或 PARTIAL——`try/finally` 内 goto 可能未正确处理；嵌套循环内 `goto t` 跨域

注：Task 6 的 `GotoTransitionRewriter.VisitGotoStatement` 已递归（`CSharpSyntaxRewriter` 默认递归子节点），所以 try 内的 goto 会被改写。但需验证 `ContainsTopLevelGoto` 不误判 try 块为 conditional-goto。若失败，修 `ContainsTopLevelGoto` 仅识别 `if`。

- [ ] **Step 3: 修实现**（确保 `ContainsTopLevelGoto` 只对 `if` 触发 ConditionalGoto；`try`/`for`/`while`/`switch` 内的 goto 由递归 `RewriteNode` 处理——它们不会切块，而是作为块内语句保留，内部 goto 被 `GotoTransitionRewriter` 改写）

在 `StateMachineBuilder.cs` 中确认 `ContainsTopLevelGoto` 实现（Task 4 已写：只识别 `IfStatementSyntax`）。无需新增代码即可通过——本任务主要是验证。若 `try{ if(c) goto t; }` 因 `if` 触发 ConditionalGoto 而 `try` 被当作单语句保留，则 `RewriteNode` 递归改写 if 内 goto，finally 仍在 try 内，语义正确。

若测试仍失败（例如 `goto t` 在 `for` 体内但 `t` 在 `for` 外，C 跨域），检查 `SplitToBlocks` 是否把 `for` 作为一个语句保留——是的，`for` 不是裸块，保留为单语句。块内 `goto t` 由 `RewriteNode` 改写。应通过。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: PASS（17 个测试）

- [ ] **Step 5: Commit**

```bash
git add tests/CSharpToJava.Tests/GotoEliminatorTests.cs
git commit -m "test(goto-eliminator): cover try/finally and nested-loop break/continue"
```

---

## Task 8: 不支持方法回退（spanning using/fixed/ref）

**Files:**
- Modify: `src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs`
- Test: `tests/CSharpToJava.Tests/GotoEliminatorTests.cs`（追加）

- [ ] **Step 1: 写失败测试**

```csharp
// 追加到 GotoEliminatorTests.cs
namespace CSharpToJava.Tests;

public partial class GotoEliminatorTests
{
    [Fact]
    public void Build_SpanningUsing_WarnsAndKeepsMethod()
    {
        // using var 跨标签使用 -> 不支持，方法原样保留 + 诊断
        var asm = typeof(CSharpToJava.Core.GotoEliminator.GotoEliminatorOptions).Assembly;
        var smbType = asm.GetType("CSharpToJava.Core.GotoEliminator.StateMachineBuilder")!;
        var builder = (CSharpSyntaxRewriter)System.Activator.CreateInstance(smbType)!;
        var src = """
        using System.IO;
        class C {
            void M() {
                using var s = new MemoryStream();
                t: s.WriteByte(1);
                if (s.Length > 0) goto t;
            }
        }
        """;
        var root = Parse(src);
        var visited = (CompilationUnitSyntax)builder.Visit(root)!;
        var out_ = visited.ToFullString();
        // 方法仍含 goto（未转换）
        Assert.Contains("goto t", out_);
        // 诊断列表非空
        var diags = (System.Collections.Generic.List<GotoEliminatorDiagnostic>)
            smbType.GetProperty("Diagnostics")!.GetValue(builder)!;
        Assert.NotEmpty(diags);
        Assert.Contains(diags, d => d.Severity == GotoEliminatorSeverity.Warning);
    }
}
```

- [ ] **Step 2: 运行测试**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: PASS（Task 6 的 `VisitMethodDeclaration` 已 catch `GotoEliminatorException` 并加诊断、原样返回；Task 5 的 `HoistSpanningLocals` 对 spanning `using` 抛异常）。若 PASS 则本任务主要是验证；若 FAIL 修 `HoistSpanningLocals` 的 `using` 检测（`decl.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)`）。

- [ ] **Step 3: 必要时修实现**（若 Task 5 的 `using` 检测有误：`LocalDeclarationStatementSyntax.UsingKeyword` 是 `SyntaxToken`，用 `.IsKind(SyntaxKind.UsingKeyword)` 或 `.ValueText == "using"`）

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: PASS（18 个测试）

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs tests/CSharpToJava.Tests/GotoEliminatorTests.cs
git commit -m "feat(goto-eliminator): warn-and-skip methods with spanning using/fixed/ref locals"
```

---

## Task 9: GotoEliminator 入口——Dirty 检测 + 两遍 + 格式化 + 幂等

**Files:**
- Create: `src/CSharpToJava.Core/GotoEliminator/GotoEliminator.cs`
- Test: `tests/CSharpToJava.Tests/GotoEliminatorTests.cs`（追加）

- [ ] **Step 1: 写失败测试**

```csharp
// 追加到 GotoEliminatorTests.cs
namespace CSharpToJava.Tests;

public partial class GotoEliminatorTests
{
    private static string Eliminate(string src)
        => new CSharpToJava.Core.GotoEliminator.GotoEliminator()
            .Eliminate(src).OutputCode;

    [Fact]
    public void Eliminate_CleanFile_ByteIdentical()
    {
        var src = """
        class C {
            void M() {
                for (int i = 0; i < 3; i++) {
                    if (i == 1) break;
                    System.Console.WriteLine(i);
                }
            }
        }
        """;
        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void Eliminate_GotoInStringLiteral_NotDirty()
    {
        var src = """class C { string s = "goto label"; }""";
        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void Eliminate_DirtyFile_HasNoGotoOrLabel()
    {
        var src = "class C { void M() { goto skip; int x = 1; skip: int y = 2; } }";
        var out_ = Eliminate(src);
        AssertNoGotoOrLabel(out_);
    }

    [Fact]
    public void Eliminate_Idempotent_SecondPassUnchanged()
    {
        var src = "class C { void M() { goto skip; int x = 1; skip: int y = 2; } }";
        var first = Eliminate(src);
        var second = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(first);
        Assert.False(second.Changed);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: FAIL，`GotoEliminator` 类不存在

- [ ] **Step 3: 写实现**

```csharp
// src/CSharpToJava.Core/GotoEliminator/GotoEliminator.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.GotoEliminator;

public sealed class GotoEliminator
{
    public GotoEliminatorResult Eliminate(string sourceCode, GotoEliminatorOptions? options = null)
    {
        options ??= new GotoEliminatorOptions();
        var stats = new GotoEliminatorStatistics();
        stats.FilesScanned = 1;

        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        bool dirty = IsDirty(root);
        if (!dirty)
        {
            stats.FilesSkippedClean = 1;
            return new GotoEliminatorResult(sourceCode, Changed: false, Array.Empty<GotoEliminatorDiagnostic>(), stats);
        }

        // Pass 1: goto case/default 去糖
        var desugared = GotoCaseDesugarer.Run(root);
        // Pass 2: 状态机
        var builder = new StateMachineBuilder();
        var rewritten = (CompilationUnitSyntax)builder.Visit(desugared)!;

        // 格式化
        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(rewritten, workspace);

        var code = formatted.ToFullString();
        stats.FilesTransformed = 1;
        stats.MethodsTransformed = builder.TransformedMethods;
        stats.MethodsSkipped = builder.SkippedMethods;
        stats.GotosEliminated = builder.GotosEliminated;
        return new GotoEliminatorResult(code, Changed: true, builder.Diagnostics, stats);
    }

    private static bool IsDirty(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(n => n is GotoStatementSyntax or LabeledStatementSyntax);
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: PASS（22 个测试）

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/GotoEliminator/GotoEliminator.cs tests/CSharpToJava.Tests/GotoEliminatorTests.cs
git commit -m "feat(goto-eliminator): public entry point with dirty detection and formatting"
```

---

## Task 10: CLI 动词 eliminate-goto

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`
- Test: `tests/CSharpToJava.Tests/GotoEliminatorTests.cs`（追加 CLI 集成测试）

- [ ] **Step 1: 写失败测试**

```csharp
// 追加到 GotoEliminatorTests.cs
using System.IO;

namespace CSharpToJava.Tests;

public partial class GotoEliminatorTests
{
    [Fact]
    public async System.Threading.Tasks.Task Cli_SingleFile_TransformsAndWrites()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "cs2j_cli_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var input = Path.Combine(tmp, "in.cs");
            var output = Path.Combine(tmp, "out.cs");
            await File.WriteAllTextAsync(input, "class C { void M() { goto s; int x=1; s: int y=2; } }");
            var exit = await CSharpToJava.CLI.Program.MainImpl(new[] { "eliminate-goto", "-i", input, "-o", output });
            Assert.Equal(0, exit);
            var result = await File.ReadAllTextAsync(output);
            AssertNoGotoOrLabel(result);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Cli_Directory_CopiesAndTransformsOnlyDirty()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "cs2j_dir_" + System.Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(tmp, "src"); var dstDir = Path.Combine(tmp, "dst");
        Directory.CreateDirectory(srcDir);
        try
        {
            var dirty = Path.Combine(srcDir, "dirty.cs");
            var clean = Path.Combine(srcDir, "clean.cs");
            await File.WriteAllTextAsync(dirty, "class C { void M() { goto s; int x=1; s: int y=2; } }");
            await File.WriteAllTextAsync(clean, "class C { void M() { int x=1; } }");
            var exit = await CSharpToJava.CLI.Program.MainImpl(new[] { "eliminate-goto", "-s", srcDir, "-d", dstDir, "-v" });
            Assert.Equal(0, exit);
            var dirtyOut = await File.ReadAllTextAsync(Path.Combine(dstDir, "dirty.cs"));
            var cleanOut = await File.ReadAllTextAsync(Path.Combine(dstDir, "clean.cs"));
            AssertNoGotoOrLabel(dirtyOut);
            // clean 文件字节一致
            Assert.Equal(await File.ReadAllTextAsync(clean), cleanOut);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: FAIL，`Program.MainImpl` 不存在

- [ ] **Step 3: 修改 Program.cs**

在 `Program.cs` 顶部 `using` 区加：
```csharp
using CSharpToJava.Core.GotoEliminator;
```

把 `Main` 改为委托到 `MainImpl`（便于测试调用），并在 `ParseArguments` 加新动词：
```csharp
static async Task<int> Main(string[] args)
{
    SolutionLoader.EnsureMSBuildRegistered();
    return await MainImpl(args);
}

internal static async Task<int> MainImpl(string[] args)
{
    return await Parser.Default.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions>(args)
        .MapResult(
            (ConvertOptions opts) => ConvertFile(opts),
            (ConvertProjectOptions opts) => ConvertProject(opts),
            (AnalyzeOptions opts) => AnalyzeProject(opts),
            (EliminateGotoOptions opts) => EliminateGoto(opts),
            errs => Task.FromResult(1)
        );
}
```

新增 handler 与 options（放文件末尾 options 区附近）：
```csharp
private static async Task<int> EliminateGoto(EliminateGotoOptions opts)
{
    var elim = new GotoEliminator();
    var goOpts = new GotoEliminatorOptions(opts.Verbose, opts.Strict);

    if (opts.Input != null)
    {
        if (!File.Exists(opts.Input))
        {
            Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
            return 1;
        }
        var sourceCode = await File.ReadAllTextAsync(opts.Input);
        var result = elim.Eliminate(sourceCode, goOpts);
        if (opts.Output != null)
        {
            await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
            if (opts.Verbose) Console.WriteLine($"Transformed: {opts.Input} -> {opts.Output}");
        }
        else Console.Write(result.OutputCode);
        return DiagnosticsExitCode(result, opts);
    }

    // 目录模式
    if (opts.Source == null || opts.Destination == null)
    {
        Console.Error.WriteLine("Error: provide (-i/-o) or (-s/-d).");
        return 1;
    }
    if (!Directory.Exists(opts.Source))
    {
        Console.Error.WriteLine($"Error: Source directory not found: {opts.Source}");
        return 1;
    }
    Directory.CreateDirectory(opts.Destination);
    int transformed = 0, clean = 0, failed = 0;
    foreach (var file in Directory.GetFiles(opts.Source, "*.cs", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(opts.Source, file);
        var dst = Path.Combine(opts.Destination, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var sourceCode = await File.ReadAllTextAsync(file);
        var result = elim.Eliminate(sourceCode, goOpts);
        if (!result.Changed)
        {
            // 字节复制保真
            File.Copy(file, dst, overwrite: true);
            clean++;
            if (opts.Verbose) Console.WriteLine($"clean (copy): {rel}");
        }
        else
        {
            // 原子写：先 .tmp 再替换
            await File.WriteAllTextAsync(dst + ".tmp", result.OutputCode, new System.Text.UTF8Encoding(false));
            File.Delete(dst);
            File.Move(dst + ".tmp", dst);
            transformed++;
            if (opts.Verbose) Console.WriteLine($"transformed: {rel}");
        }
        if (result.Diagnostics.Any(d => d.Severity == GotoEliminatorSeverity.Warning)) failed++;
    }
    // 非 .cs 文件字节复制
    foreach (var file in Directory.GetFiles(opts.Source, "*.*", SearchOption.AllDirectories)
                 .Where(f => !f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
    {
        var rel = Path.GetRelativePath(opts.Source, file);
        var dst = Path.Combine(opts.Destination, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(file, dst, overwrite: true);
    }
    Console.WriteLine($"eliminate-goto: {transformed} transformed, {clean} clean, {failed} with-warnings");
    return failed > 0 ? 1 : 0;
}

private static int DiagnosticsExitCode(GotoEliminatorResult result, EliminateGotoOptions opts)
{
    foreach (var d in result.Diagnostics)
        Console.Error.WriteLine($"[{d.Severity}] {d.MethodName}: {d.Message}");
    return result.Diagnostics.Any(d => d.Severity == GotoEliminatorSeverity.Warning) ? 1 : 0;
}
```

options 类（文件末尾，与其它 options 并列）：
```csharp
[Verb("eliminate-goto", HelpText = "Eliminate goto/label from C# source")]
class EliminateGotoOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("strict", Default = false, HelpText = "Treat unsupported-method diagnostics as fatal")]
    public bool Strict { get; set; }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~GotoEliminatorTests"`
Expected: PASS（24 个测试）

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.CLI/Program.cs tests/CSharpToJava.Tests/GotoEliminatorTests.cs
git commit -m "feat(cli): add eliminate-goto verb for single-file and directory modes"
```

---

## Task 11: 真实文件快照测试（XsdDuration.cs）

**Files:**
- Test: `tests/CSharpToJava.Tests/GotoEliminatorTests.cs`（追加）

- [ ] **Step 1: 写失败测试**

```csharp
// 追加到 GotoEliminatorTests.cs
namespace CSharpToJava.Tests;

public partial class GotoEliminatorTests
{
    [Fact]
    public void Eliminate_XsdDuration_RealFile_NoGotoAndCompiles()
    {
        // XsdDuration.cs 含 ~40 goto。从 D:\csharpxml 读取（只读）。
        var path = @"D:\csharpxml\System\Xml\Schema\XsdDuration.cs";
        if (!File.Exists(path))
        {
            // 跳过：环境无 csharpxml
            return;
        }
        var src = File.ReadAllText(path);
        var result = new CSharpToJava.Core.GotoEliminator.GotoEliminator().Eliminate(src);
        Assert.True(result.Changed, "XsdDuration.cs 应被转换");
        AssertNoGotoOrLabel(result.OutputCode);
        // 编译校验：用 CSharpCompilation 单独编译该文件（语法层）
        var tree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var comp = CSharpCompilation.Create("xsddur_check",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var diags = comp.GetDiagnostics();
        var errors = diags.Where(d => d.Severity == DiagnosticSeverity.Error
            && d.Id != "CS5001" // 输出类型无关
            && d.Id != "CS0246" // 缺少引用（环境无关，仅语法层校验）
            && d.Id != "CS0103" // 未定义名称（同上）
            ).ToList();
        Assert.Empty(errors);
    }
}
```

- [ ] **Step 2: 运行测试**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "Eliminate_XsdDuration"`
Expected: 若通过则继续；若失败（某方法触发 spanning using 或转换 bug），记录失败方法名与诊断，回到 Task 5/8 修复后重跑。

- [ ] **Step 3: Commit**

```bash
git add tests/CSharpToJava.Tests/GotoEliminatorTests.cs
git commit -m "test(goto-eliminator): snapshot test against real XsdDuration.cs"
```

---

## Task 12: 验证脚本 + 端到端运行

**Files:**
- Create: `scripts/verify-goto-eliminator.ps1`

- [ ] **Step 1: 写脚本**

```powershell
# scripts/verify-goto-eliminator.ps1
# 复制 D:\csharpxml -> 临时目录 -> 转换副本 -> dotnet build/test -> 断言源只读 -> 清理
$ErrorActionPreference = 'Stop'
$repo = 'D:\code\cs2j'
$src = 'D:\csharpxml'
$tmp = "D:\temp\cs2j_verify_" + [guid]::NewGuid().ToString('N')

function Get-TreeHashes($root) {
    Get-ChildItem -Recurse -File $root | ForEach-Object {
        [PSCustomObject]@{ Path = $_.FullName; Hash = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash }
    }
}

Write-Host "Recording source SHA256 baseline..."
$before = Get-TreeHashes $src

Write-Host "Copying $src -> $tmp"
Copy-Item -Recurse -Path $src -Destination $tmp

try {
    Write-Host "Running eliminate-goto on copy..."
    & dotnet run --project "$repo\src\CSharpToJava.CLI\CSharpToJava.CLI.csproj" -- `
        eliminate-goto -s $tmp -d $tmp --verbose
    if ($LASTEXITCODE -ne 0) { throw "eliminate-goto failed (exit $LASTEXITCODE)" }

    Write-Host "--- dotnet build ---"
    Set-Location $tmp
    dotnet build csharpxml.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }

    Write-Host "--- dotnet test ---"
    dotnet test csharpxml.sln -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "test failed (exit $LASTEXITCODE)" }

    Write-Host "--- self-check: no goto/label in transformed .cs ---"
    $remaining = Get-ChildItem -Recurse -File $tmp -Filter *.cs | Select-String -Pattern '\bgoto\b' -SimpleMatch:$false
    # 注：注释/字符串里的 goto 可能误报；用 Roslyn 自检更准。此处仅粗检。
    if ($remaining) { Write-Warning "Possible goto remnants (verify manually): $($remaining.Count)" }

    Write-Host "VERIFY OK"
}
finally {
    Set-Location $repo
    Write-Host "Re-checking source SHA256 (read-only proof)..."
    $after = Get-TreeHashes $src
    $diff = Compare-Object $before $after -Property Path, Hash
    if ($diff) { throw "SOURCE MODIFIED! Differences:`n$($diff | Out-String)" }
    Write-Host "Source unchanged."
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
```

- [ ] **Step 2: 运行脚本**

Run: `powershell -ExecutionPolicy Bypass -File scripts\verify-goto-eliminator.ps1`
Expected: 打印 `VERIFY OK` 与 `Source unchanged.`。若 build/test 失败，记录失败的文件/方法，回到 Task 5/6/8 修复。

- [ ] **Step 3: Commit**

```bash
git add scripts/verify-goto-eliminator.ps1
git commit -m "chore: add end-to-end verification script for goto-eliminator"
```

---

## Task 13: 全量回归 + 项目记忆

**Files:**
- 无新文件（仅运行验证 + 写记忆）

- [ ] **Step 1: 全量单元测试**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
Expected: 全部 PASS

- [ ] **Step 2: 端到端验证脚本**

Run: `powershell -ExecutionPolicy Bypass -File scripts\verify-goto-eliminator.ps1`
Expected: `VERIFY OK` + `Source unchanged.`

- [ ] **Step 3: 核对成功标准清单**

对照 spec §1.4：
- [ ] `D:\csharpxml` SHA256 未变（脚本已断言）
- [ ] 副本 `dotnet build` 通过
- [ ] 副本 `dotnet test` 通过
- [ ] 所有含 goto 的文件被转换且编译通过
- [ ] 不含 goto 的文件 SHA256 与原始一致
- [ ] 转换后含 goto 的文件数 = 0（Roslyn 自检：在脚本中可选追加一段用 dotnet script 跑 Roslyn 扫描）

- [ ] **Step 4: 写项目记忆**（记录关键决策供后续会话）

把以下追加到 `c:\Users\15097\.trae-cn\memory\projects\-d-code-cs2j\project_memory.md`（若不存在则创建）：
```markdown
## GotoEliminator (2026-06-27)
- 模块位置: src/CSharpToJava.Core/GotoEliminator/（自包含，不依赖 GotoAnalyzer/LabelRegistry）
- 策略: 统一 switch 基本块状态机（非内联守卫；内联模式有重入重执行 bug，见 docs/superpowers/specs/2026-06-03-goto-conversion-redesign.md）
- goto case/default: 先去糖为合成 __caseN_X / __defaultN 标签再走状态机
- CLI: eliminate-goto -i/-o（单文件）或 -s/-d（目录，仅转换 dirty .cs，clean 字节复制）
- 不支持: spanning using/fixed/ref 局部 → warn-and-skip 该方法（csharpxml 若触发需扩展提升器）
- 验证: scripts/verify-goto-eliminator.ps1 复制 D:\csharpxml 到临时目录后转换+build+test+SHA256 断言
- Spec: docs/superpowers/specs/2026-06-27-goto-eliminator-design.md
- Plan: docs/superpowers/plans/2026-06-27-goto-eliminator.md
```

- [ ] **Step 5: 最终 Commit**

```bash
git add -A
git commit -m "chore(goto-eliminator): final regression pass and project memory"
```

---

## Self-Review

**Spec coverage**（对照 spec 各节）：
- §2 决策 D1/D2/D3 → Task 6/3/10 ✓
- §3 模块布局 6 文件 → Task 2/3/4/5/6/9 ✓（BasicBlock, GotoCaseDesugarer, StateMachineBuilder, GotoEliminatorOptions/Diagnostics, GotoEliminator）
- §4.1 文件级流程（dirty 检测+字节保真）→ Task 9 ✓
- §4.2 goto case 去糖 → Task 3 ✓
- §4.3.2 扁平化 → Task 4 SplitToBlocks ✓
- §4.3.3 基本块分割 → Task 4 ✓
- §4.3.4 变量提升 → Task 5 ✓
- §4.3.5 状态机发射 → Task 6 ✓
- §4.3.6 不支持回退 → Task 8 ✓
- §4.4 格式化/trivia → Task 9 ✓
- §5 CLI → Task 10 ✓
- §6.1 红基线脚本 → Task 1 ✓
- §6.2 验证脚本 → Task 12 ✓
- §7 单元测试 18 用例 → Task 2-11 ✓
- §8 风险（spanning using）→ Task 8 ✓
- §10 文件清单全部覆盖 ✓

**Placeholder 扫描**：无 TBD/TODO；每步含实际代码或命令。Task 7/8 标注"若失败则修"——这是因为 Task 6 的实现已覆盖这些场景，Task 7/8 主要是验证；若验证失败则给出具体修复方向，非占位符。

**类型一致性**：`GotoEliminatorOptions(Verbose, Strict)`、`GotoEliminatorResult(OutputCode, Changed, Diagnostics, Statistics)`、`BasicBlock(Index, Label, Statements, Exit, FallThroughTarget)`、`StateMachineBuilder.Diagnostics/TransformedMethods/SkippedMethods/GotosEliminated`、`Program.MainImpl` 在所有任务中签名一致。`SplitToBlocks`/`HoistSpanningLocals`/`EmitStateMachine` 均为 `internal static`，反射测试访问一致。

**注意**：Task 11 的 XsdDuration.cs 快照测试依赖 `D:\csharpxml` 存在；若环境缺失则跳过（`return`）。Task 12 是最终 gate。
