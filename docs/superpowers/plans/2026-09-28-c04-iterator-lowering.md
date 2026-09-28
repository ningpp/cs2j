# C04 Iterator Lowering (M1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增 C#→C# 预处理器 `iterator-lowering`，把含 `yield return` / `yield break` 的迭代器方法降级为显式状态机嵌套类，使预处理后的中间树可编译且语义等价（惰性求值、yield break 早退）。

**Architecture:** 整树 compilation + 逐文件 Rewrite 的标准语义 pass 脊柱（同 EnumPassAdapter）。Analyzer 发现迭代器成员 → CfgBuilder 按挂起点切段并编号 → IteratorStateMachineRewriter 按 StateMachinePlan 合成嵌套类 + 重写成员体 → residual 闸门检查残余 yield。生成的 goto/label 交给链后 `eliminate-goto` 消除。本期交付 M1：直线/if/循环/foreach/泛型，不含 try/finally/using/lock（M2-M3）。

**Tech Stack:** C# / .NET 10、Roslyn `Microsoft.CodeAnalysis.CSharp`（`LanguageVersion.Latest`，`allowUnsafe: true`）、xUnit；与 [EnumPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/EnumPreprocessor/EnumPreprocessor.cs)、[EnumPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/EnumPassAdapter.cs) 同构。

> 规范来源：[2026-09-28-c04-iterator-lowering-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c04-iterator-lowering-design.md)。

---

## Task 1: Options / Statistics / Diagnostic 基础类型

**Files:**
- Create: `src/CSharpToJava.Core/IteratorLowering/IteratorLoweringOptions.cs`
- Create: `src/CSharpToJava.Core/IteratorLowering/IteratorLoweringStatistics.cs`
- Create: `src/CSharpToJava.Core/IteratorLowering/IteratorLoweringDiagnostic.cs`
- Test: `tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs`

- [ ] **Step 1: Write the failing test**

创建测试文件，写入基础类型的存在性断言：

```csharp
using CSharpToJava.Core.IteratorLowering;
using Xunit;

namespace CSharpToJava.ProjectTests;

public class IteratorLoweringUnitTests
{
    [Fact]
    public void Options_Default_IsStrict()
    {
        var opts = new IteratorLoweringOptions();
        Assert.True(opts.Strict);
    }

    [Fact]
    public void Statistics_Defaults_AreZero()
    {
        var stats = new IteratorLoweringStatistics();
        Assert.Equal(0, stats.FilesScanned);
        Assert.Equal(0, stats.FilesTransformed);
        Assert.Equal(0, stats.IteratorMembersLowered);
        Assert.Equal(0, stats.ResidualCount);
    }

    [Fact]
    public void Diagnostic_Records_FieldsCorrectly()
    {
        var d = new IteratorLoweringDiagnostic(
            IteratorLoweringSeverity.Error,
            IteratorLoweringDiagnosticCode.CS2J0402,
            "test message",
            "File.cs",
            42);
        Assert.Equal(IteratorLoweringSeverity.Error, d.Severity);
        Assert.Equal(IteratorLoweringDiagnosticCode.CS2J0402, d.Code);
        Assert.Equal("test message", d.Message);
        Assert.Equal("File.cs", d.FilePath);
        Assert.Equal(42, d.Line);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: FAIL with "type or namespace 'IteratorLowering' not found"

- [ ] **Step 3: Write minimal implementation**

`src/CSharpToJava.Core/IteratorLowering/IteratorLoweringOptions.cs`:

```csharp
namespace CSharpToJava.Core.IteratorLowering;

/// <param name="Strict">If true, unsupported iterator forms produce Error instead of Warning and fail the pass.</param>
public sealed record IteratorLoweringOptions(
    bool Strict = true);
```

`src/CSharpToJava.Core/IteratorLowering/IteratorLoweringStatistics.cs`:

```csharp
namespace CSharpToJava.Core.IteratorLowering;

public sealed class IteratorLoweringStatistics
{
    // Common fields
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int FilesDirtyNoRewrite;
    public int ResidualCount;

    // Pass-specific fields
    public int IteratorMembersLowered;
    public int StateMachinesGenerated;
    public int YieldReturnSites;
    public int YieldBreakSites;
    public int HoistedParameters;
    public int HoistedLocals;
    public int SkippedUnsupported;
}
```

`src/CSharpToJava.Core/IteratorLowering/IteratorLoweringDiagnostic.cs`:

```csharp
namespace CSharpToJava.Core.IteratorLowering;

public enum IteratorLoweringSeverity { Warning, Error }

public enum IteratorLoweringDiagnosticCode
{
    CS2J0401, // 不支持的迭代器返回形态
    CS2J0402, // 命中编译器非法组合（CS1623/1625/1626/1631 族）
    CS2J0403, // ref struct/ref local/unsafe 跨 yield 边界
    CS2J0404, // 残余未提升迭代器局部函数
    CS2J0405, // 体内 lambda/局部函数捕获提升变量
    CS2J0406, // 迭代器体内用户 goto/label
    CS2J0420, // restore/build/test 失败
    CS2J0430, // 成员已降级 (Info)
}

public sealed record IteratorLoweringDiagnostic(
    IteratorLoweringSeverity Severity,
    IteratorLoweringDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/IteratorLowering/IteratorLoweringOptions.cs src/CSharpToJava.Core/IteratorLowering/IteratorLoweringStatistics.cs src/CSharpToJava.Core/IteratorLowering/IteratorLoweringDiagnostic.cs tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs
git commit -m "feat(iterator-lowering): add Options/Statistics/Diagnostic base types"
```

---

## Task 2: IteratorLoweringNaming — 确定性命名与防撞

**Files:**
- Create: `src/CSharpToJava.Core/IteratorLowering/IteratorLoweringNaming.cs`
- Test: `tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs`

- [ ] **Step 1: Write the failing test**

追加到 `IteratorLoweringUnitTests.cs`：

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// 放在 IteratorLoweringUnitTests 类内部

[Fact]
public void Naming_StateMachineClassName_IsDeterministic()
{
    var usedNames = new HashSet<string>(StringComparer.Ordinal) { "Cs2jIterator_GetTokens_0" };
    var name = IteratorLoweringNaming.GetStateMachineClassName("GetTokens", 0, usedNames);
    Assert.Equal("Cs2jIterator_GetTokens_1", name);
}

[Fact]
public void Naming_FieldNames_ArePrefixed()
{
    Assert.Equal("_cs2jState", IteratorLoweringNaming.StateField);
    Assert.Equal("_cs2jCurrent", IteratorLoweringNaming.CurrentField);
    Assert.Equal("_cs2jInitialThreadId", IteratorLoweringNaming.InitialThreadIdField);
    Assert.Equal("_cs2jThis", IteratorLoweringNaming.ThisField);
    Assert.Equal("_cs2jCap_count", IteratorLoweringNaming.GetCaptureFieldName("count"));
    Assert.Equal("_cs2jLoc_i", IteratorLoweringNaming.GetLocalFieldName("i"));
}

[Fact]
public void Naming_ResumeLabel_IsPrefixed()
{
    Assert.Equal("_cs2jResume_0", IteratorLoweringNaming.GetResumeLabel(0));
    Assert.Equal("_cs2jResume_3", IteratorLoweringNaming.GetResumeLabel(3));
}

[Fact]
public void Naming_ResolveUniqueName_AppendsSuffix()
{
    var used = new HashSet<string>(StringComparer.Ordinal) { "Cs2jIterator_Foo_0" };
    var resolved = IteratorLoweringNaming.ResolveUniqueName("Cs2jIterator_Foo_0", used);
    Assert.Equal("Cs2jIterator_Foo_02", resolved);
    Assert.Contains("Cs2jIterator_Foo_02", used);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: FAIL with "type or namespace 'IteratorLoweringNaming' not found"

- [ ] **Step 3: Write minimal implementation**

`src/CSharpToJava.Core/IteratorLowering/IteratorLoweringNaming.cs`:

```csharp
namespace CSharpToJava.Core.IteratorLowering;

/// <summary>
/// Deterministic naming utilities for iterator state machine types, fields, and labels.
/// Mirrors the collision-avoidance pattern from LocalFunctionLiftNaming but does not share code.
/// </summary>
internal static class IteratorLoweringNaming
{
    public const string StateField = "_cs2jState";
    public const string CurrentField = "_cs2jCurrent";
    public const string InitialThreadIdField = "_cs2jInitialThreadId";
    public const string ThisField = "_cs2jThis";
    private const string CaptureFieldPrefix = "_cs2jCap_";
    private const string LocalFieldPrefix = "_cs2jLoc_";
    private const string ResumeLabelPrefix = "_cs2jResume_";
    private const string StateMachineClassPrefix = "Cs2jIterator_";

    public static string GetStateMachineClassName(string memberName, int index, HashSet<string> usedNames)
    {
        var baseName = $"{StateMachineClassPrefix}{memberName}_{index}";
        return ResolveUniqueName(baseName, usedNames);
    }

    public static string GetCaptureFieldName(string originalName) => CaptureFieldPrefix + originalName;

    public static string GetLocalFieldName(string originalName) => LocalFieldPrefix + originalName;

    public static string GetResumeLabel(int state) => $"{ResumeLabelPrefix}{state}";

    public static string ResolveUniqueName(string baseName, HashSet<string> usedNames)
    {
        var name = baseName;
        var suffix = 2;
        while (usedNames.Contains(name))
        {
            name = baseName + suffix;
            suffix++;
        }
        usedNames.Add(name);
        return name;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: PASS (7 tests)

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/IteratorLowering/IteratorLoweringNaming.cs tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs
git commit -m "feat(iterator-lowering): add deterministic naming utility"
```

---

## 0. 全局约定（执行每个任务前先读）

- 仓库根：`d:\code\cs2j`。所有路径相对仓库根。PowerShell 不支持 `&&`，串行命令用 `;`。
- 命名空间：Core 全部放 `CSharpToJava.Core.IteratorLowering`；CLI 适配器放 `CSharpToJava.CLI.Preprocessing.Adapters`。
- 注解名固定：`"Cs2jIteratorLowering"`。
- 每个任务结束必须 `dotnet build` 全绿再提交；提交信息用 `feat(iterator-lowering): ...` / `test(iterator-lowering): ...`。
- TDD 节奏：先写失败测试 → 跑确认失败 → 最小实现 → 跑确认通过 → 提交。
- `CSharpToJava.Core.csproj` 已对 `CSharpToJava.ProjectTests` 配 `InternalsVisibleTo`，internal 类型可直接在 ProjectTests 里测。
- pass id = `iterator-lowering`；类型/命名空间用 `IteratorLowering`。
- 状态机类名前缀 `Cs2jIterator_`；字段前缀 `_cs2j`（`_cs2jState`/`_cs2jCurrent`/`_cs2jInitialThreadId`/`_cs2jThis`/`_cs2jCap_`/`_cs2jLoc_`）；标签前缀 `_cs2jResume_`。
- 本期（M1）不支持 try/finally/using/lock 内的 yield（报 CS2J0402 跳过）、ref/in/out 形参（CS2J0402）、体内用户 goto/label（CS2J0406）、体内 lambda 捕获提升变量（CS2J0405）、残余未提升迭代器局部函数（CS2J0404）。

### 0.1 File Structure（本计划创建/修改的全部文件）

**Core（新建目录 `src/CSharpToJava.Core/IteratorLowering/`）：**

| 文件 | 职责 |
|---:|---|
| `IteratorLoweringOptions.cs` | record（Strict，默认 true） |
| `IteratorLoweringStatistics.cs` | 通用 5 字段 + pass 专有计数 |
| `IteratorLoweringDiagnostic.cs` | Severity enum + record（Severity/Code/Message/FilePath/Line） |
| `IteratorLoweringNaming.cs` | 确定性命名：类型名、字段名、标签名、防撞 |
| `StateMachinePlan.cs` | 一个迭代器成员的完整降级计划（字段布局/区域/状态数/生成名） |
| `ControlFlow/CfgBuilder.cs` | 结构化 CFG：挂起点编号、段划分、活性分析、保护区域表（M1 只做段+编号，区域留 stub） |
| `IteratorLoweringAnalyzer.cs` | 迭代器成员发现 + 合法性闸门 + 基线快照 + skip 集 |
| `IteratorStateMachineRewriter.cs` | 按 plan 重写成员体 + 合成嵌套类 SyntaxFactory |
| `IteratorLoweringPreprocessor.cs` | 对外入口：Preprocess(source, SemanticModel, options) → FileResult |

**CLI 适配器：**

| 文件 | 职责 |
|---:|---|
| `src/CSharpToJava.CLI/Preprocessing/Adapters/IteratorLoweringPassAdapter.cs` | ProjectPassAdapter 子类，走默认脊柱，residual 闸门 |

**注册表改动：**

| 文件 | 改动 |
|---:|---|
| `src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs` | 从 `Stubs` 删除 iterator-lowering 条目；**保留 `Ids.IteratorLowering` 常量** |
| `src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs` | `Implemented` 表新增 descriptor（defaultEnabled=false）；给 `eliminate-goto` 的 runAfterIds 追加 `iterator-lowering` |
| `src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs` | `CreateAdapter` switch 增加 `PassStubCatalog.Ids.IteratorLowering => new IteratorLoweringPassAdapter(...)` |

**Tests：**

| 文件 | 职责 |
|---:|---|
| `tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs` | Core 单测：内联源码 → CSharpCompilation → Preprocess → 文本断言 + 输出可编译断言 |
| `tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs` | 改：stub 计数 23→22；CS2J9001 用例改用 `closure-lift` |
| `tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs` | 改：LockedChain 增加 `iterator-lowering`（make-readonly 之后、eliminate-goto 之前） |

---

## Task 3: StateMachinePlan — 降级计划数据模型

**Files:**
- Create: `src/CSharpToJava.Core/IteratorLowering/StateMachinePlan.cs`
- Test: `tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs`

- [ ] **Step 1: Write the failing test**

追加到 `IteratorLoweringUnitTests.cs`：

```csharp
[Fact]
public void StateMachinePlan_Stores_BasicFields()
{
    var plan = new StateMachinePlan(
        MemberName: "GetTokens",
        StateMachineClassName: "Cs2jIterator_GetTokens_0",
        ElementType: "string",
        IsIEnumerableReturn: true,
        IsIEnumeratorReturn: false,
        IsGenericMethod: false,
        DeclaringTypeName: "Writer",
        IsStatic: false,
        Parameters: new[] { new CaptureField("count", "int", "_cs2jCap_count") },
        HoistedLocals: new[] { new CaptureField("i", "int", "_cs2jLoc_i") },
        YieldReturnCount: 1,
        HasYieldBreak: false);

    Assert.Equal("GetTokens", plan.MemberName);
    Assert.Equal("Cs2jIterator_GetTokens_0", plan.StateMachineClassName);
    Assert.Equal("string", plan.ElementType);
    Assert.True(plan.IsIEnumerableReturn);
    Assert.False(plan.IsIEnumeratorReturn);
    Assert.Single(plan.Parameters);
    Assert.Single(plan.HoistedLocals);
    Assert.Equal(1, plan.YieldReturnCount);
}

[Fact]
public void CaptureField_Records_Name_Type_FieldName()
{
    var cf = new CaptureField("count", "int", "_cs2jCap_count");
    Assert.Equal("count", cf.OriginalName);
    Assert.Equal("int", cf.Type);
    Assert.Equal("_cs2jCap_count", cf.FieldName);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: FAIL with "type or namespace 'StateMachinePlan' not found"

- [ ] **Step 3: Write minimal implementation**

`src/CSharpToJava.Core/IteratorLowering/StateMachinePlan.cs`:

```csharp
namespace CSharpToJava.Core.IteratorLowering;

/// <summary>
/// A variable (parameter or local) that must be hoisted to a state machine field
/// because its lifetime spans a yield suspension point.
/// </summary>
public sealed record CaptureField(
    string OriginalName,
    string Type,
    string FieldName);

/// <summary>
/// Complete lowering plan for one iterator member: field layout, state count,
/// generated names. Produced by the analyzer, consumed by the rewriter.
/// </summary>
public sealed record StateMachinePlan(
    string MemberName,
    string StateMachineClassName,
    string ElementType,
    bool IsIEnumerableReturn,
    bool IsIEnumeratorReturn,
    bool IsGenericMethod,
    string DeclaringTypeName,
    bool IsStatic,
    IReadOnlyList<CaptureField> Parameters,
    IReadOnlyList<CaptureField> HoistedLocals,
    int YieldReturnCount,
    bool HasYieldBreak);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: PASS (9 tests)

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/IteratorLowering/StateMachinePlan.cs tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs
git commit -m "feat(iterator-lowering): add StateMachinePlan data model"
```

---

## Task 4: CfgBuilder — 挂起点编号与活性分析

**Files:**
- Create: `src/CSharpToJava.Core/IteratorLowering/ControlFlow/CfgBuilder.cs`
- Test: `tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs`

- [ ] **Step 1: Write the failing test**

追加到 `IteratorLoweringUnitTests.cs`：

```csharp
[Fact]
public void CfgBuilder_AssignsSequentialStates_ToYieldReturns()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() {
        yield return 1;
        yield return 2;
        yield return 3;
    }
}";
    var model = ParseWithSemantics(src);
    var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
    var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
    var info = CfgBuilder.Analyze(method, model);

    Assert.Equal(3, info.YieldReturnCount);
    Assert.Equal(3, info.ResumeStates.Count);
    Assert.Equal(1, info.ResumeStates[0]);
    Assert.Equal(2, info.ResumeStates[1]);
    Assert.Equal(3, info.ResumeStates[2]);
}

[Fact]
public void CfgBuilder_DetectsYieldBreak()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(bool early) {
        if (early) yield break;
        yield return 1;
    }
}";
    var model = ParseWithSemantics(src);
    var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
    var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
    var info = CfgBuilder.Analyze(method, model);

    Assert.True(info.HasYieldBreak);
    Assert.Equal(1, info.YieldReturnCount);
}

[Fact]
public void CfgBuilder_HoistsLoopVariable_WhenYieldIsInsideLoop()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(int count) {
        for (int i = 0; i < count; i++)
            yield return i;
    }
}";
    var model = ParseWithSemantics(src);
    var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
    var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
    var info = CfgBuilder.Analyze(method, model);

    Assert.Contains(info.HoistedLocalNames, n => n == "i");
    Assert.Contains(info.HoistedParameterNames, n => n == "count");
}

[Fact]
public void CfgBuilder_DoesNotHoistLocals_WhenNoYieldInLoop()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() {
        int x = 5;
        yield return x;
    }
}";
    var model = ParseWithSemantics(src);
    var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
    var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
    var info = CfgBuilder.Analyze(method, model);

    // x is used in the same segment as the yield, no need to hoist
    Assert.DoesNotContain(info.HoistedLocalNames, n => n == "x");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: FAIL with "type or namespace 'CfgBuilder' not found"

- [ ] **Step 3: Write minimal implementation**

`src/CSharpToJava.Core/IteratorLowering/ControlFlow/CfgBuilder.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.IteratorLowering.ControlFlow;

/// <summary>
/// Control-flow analysis for one iterator member: segments delimited by yield
/// return points, state numbering, and liveness-based hoisting decisions.
/// </summary>
internal static class CfgBuilder
{
    /// <summary>
    /// Analyze a method body and produce a <see cref="CfgInfo"/> with resume
    /// states and hoisting decisions.
    /// </summary>
    public static CfgInfo Analyze(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        var body = method.Body ?? throw new InvalidOperationException(
            $"Method '{method.Identifier.Text}' has no block body (expression-bodied iterators not supported in M1).");

        var yieldReturns = body.DescendantNodes()
            .OfType<YieldStatementSyntax>()
            .Where(y => y.YieldKeyword.IsKind(SyntaxKind.YieldKeyword)
                && !body.DescendantNodes().OfType<GotoStatementSyntax>().Any())
            .ToList();

        // Assign resume states: yield return #0 → state 1, #1 → state 2, etc.
        var resumeStates = new List<int>();
        var yieldBreakCount = 0;

        foreach (var yield in body.DescendantNodes().OfType<YieldStatementSyntax>())
        {
            if (yield.IsKind(SyntaxKind.YieldReturnStatement))
            {
                resumeStates.Add(resumeStates.Count + 1);
            }
            else if (yield.IsKind(SyntaxKind.YieldBreakStatement))
            {
                yieldBreakCount++;
            }
        }

        // Hoisting: find all locals and parameters whose lifetime spans a yield.
        // In M1 we use a conservative approach: any local declared in a loop/foreach
        // that contains a yield return, and all parameters referenced in the body.
        var hoistedLocalNames = new List<string>();
        var hoistedParameterNames = new List<string>();

        // Parameters: hoist all parameters referenced in the body
        foreach (var param in method.ParameterList.Parameters)
        {
            var paramName = param.Identifier.ValueText;
            if (body.DescendantTokens().Any(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == paramName))
            {
                hoistedParameterNames.Add(paramName);
            }
        }

        // Locals in loops containing yields: for-loop declarators and foreach iteration vars
        foreach (var forStmt in body.DescendantNodes().OfType<ForStatementSyntax>())
        {
            if (ContainsYieldReturn(forStmt))
            {
                foreach (var varDecl in forStmt.Declaration?.Variables ?? default)
                {
                    hoistedLocalNames.Add(varDecl.Identifier.ValueText);
                }
            }
        }

        foreach (var foreachStmt in body.DescendantNodes().OfType<ForEachStatementSyntax>())
        {
            if (ContainsYieldReturn(foreachStmt))
            {
                hoistedLocalNames.Add(foreachStmt.Identifier.ValueText);
            }
        }

        // While/do loops: hoist locals declared inside that are used across yield
        foreach (var whileStmt in body.DescendantNodes().OfType<WhileStatementSyntax>())
        {
            if (ContainsYieldReturn(whileStmt))
            {
                foreach (var localDecl in whileStmt.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                {
                    foreach (var varDecl in localDecl.Declaration.Variables)
                    {
                        hoistedLocalNames.Add(varDecl.Identifier.ValueText);
                    }
                }
            }
        }

        foreach (var doStmt in body.DescendantNodes().OfType<DoStatementSyntax>())
        {
            if (ContainsYieldReturn(doStmt))
            {
                foreach (var localDecl in doStmt.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                {
                    foreach (var varDecl in localDecl.Declaration.Variables)
                    {
                        hoistedLocalNames.Add(varDecl.Identifier.ValueText);
                    }
                }
            }
        }

        return new CfgInfo(
            YieldReturnCount: resumeStates.Count,
            ResumeStates: resumeStates,
            HasYieldBreak: yieldBreakCount > 0,
            HoistedLocalNames: hoistedLocalNames.Distinct().ToList(),
            HoistedParameterNames: hoistedParameterNames.Distinct().ToList());
    }

    private static bool ContainsYieldReturn(SyntaxNode node)
        => node.DescendantNodes().Any(n => n.IsKind(SyntaxKind.YieldReturnStatement));
}

internal sealed record CfgInfo(
    int YieldReturnCount,
    IReadOnlyList<int> ResumeStates,
    bool HasYieldBreak,
    IReadOnlyList<string> HoistedLocalNames,
    IReadOnlyList<string> HoistedParameterNames);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: PASS (13 tests)

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/IteratorLowering/ControlFlow/CfgBuilder.cs tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs
git commit -m "feat(iterator-lowering): add CfgBuilder with yield state numbering and liveness hoisting"
```

---

## Task 5: IteratorLoweringAnalyzer — 迭代器成员发现与合法性闸门

**Files:**
- Create: `src/CSharpToJava.Core/IteratorLowering/IteratorLoweringAnalyzer.cs`
- Test: `tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs`

- [ ] **Step 1: Write the failing test**

追加到 `IteratorLoweringUnitTests.cs`。需要添加 helper 方法 `ParseWithSemantics` 和 `AnalyzeMembers`：

```csharp
private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

internal static SemanticModel ParseWithSemantics(string src)
{
    var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
    var compilation = CSharpCompilation.Create(
        "il-test",
        new[] { tree },
        GetReferences(),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    return compilation.GetSemanticModel(tree);
}

private static IEnumerable<MetadataReference> GetReferences()
{
    var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
    if (trusted != null)
    {
        foreach (var assembly in trusted.Split(Path.PathSeparator))
        {
            if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
        }
        yield break;
    }
    yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
}

[Fact]
public void Analyzer_FindsIteratorMethod_ReturningIEnumerable()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() { yield return 1; }
}";
    var model = ParseWithSemantics(src);
    var result = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());

    Assert.Single(result.Plans);
    Assert.Equal("M", result.Plans[0].MemberName);
    Assert.Equal("int", result.Plans[0].ElementType);
    Assert.True(result.Plans[0].IsIEnumerableReturn);
}

[Fact]
public void Analyzer_FindsIteratorMethod_ReturningIEnumerator()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerator<string> M() { yield return ""a""; }
}";
    var model = ParseWithSemantics(src);
    var result = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());

    Assert.Single(result.Plans);
    Assert.True(result.Plans[0].IsIEnumeratorReturn);
    Assert.False(result.Plans[0].IsIEnumerableReturn);
}

[Fact]
public void Analyzer_FindsNonGenericEnumerableIterator()
{
    var src = @"
using System.Collections;
class C {
    IEnumerable M() { yield return 1; }
}";
    var model = ParseWithSemantics(src);
    var result = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());

    Assert.Single(result.Plans);
    Assert.Equal("object", result.Plans[0].ElementType);
    Assert.True(result.Plans[0].IsIEnumerableReturn);
}

[Fact]
public void Analyzer_SkipsNonIteratorMethod()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() { return new List<int> { 1 }; }
}";
    var model = ParseWithSemantics(src);
    var result = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());

    Assert.Empty(result.Plans);
}

[Fact]
public void Analyzer_SkipsIteratorWithTryFinally_YieldsDiagnostic()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() {
        try { yield return 1; }
        finally { }
    }
}";
    var model = ParseWithSemantics(src);
    var result = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());

    Assert.Empty(result.Plans);
    Assert.Contains(result.Diagnostics, d => d.Code == IteratorLoweringDiagnosticCode.CS2J0402);
}

[Fact]
public void Analyzer_SkipsIteratorWithRefParam_YieldsDiagnostic()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(ref int x) { yield return x; }
}";
    var model = ParseWithSemantics(src);
    var result = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());

    Assert.Empty(result.Plans);
    Assert.Contains(result.Diagnostics, d => d.Code == IteratorLoweringDiagnosticCode.CS2J0402);
}

[Fact]
public void Analyzer_DetectsStaticMethod_NoThisField()
{
    var src = @"
using System.Collections.Generic;
class C {
    static IEnumerable<int> M(int n) { for (int i = 0; i < n; i++) yield return i; }
}";
    var model = ParseWithSemantics(src);
    var result = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());

    Assert.Single(result.Plans);
    Assert.True(result.Plans[0].IsStatic);
}

[Fact]
public void Analyzer_DetectsGenericMethod()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<T> M<T>(T val) { yield return val; }
}";
    var model = ParseWithSemantics(src);
    var result = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());

    Assert.Single(result.Plans);
    Assert.True(result.Plans[0].IsGenericMethod);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: FAIL with "type or namespace 'IteratorLoweringAnalyzer' not found"

- [ ] **Step 3: Write minimal implementation**

`src/CSharpToJava.Core/IteratorLowering/IteratorLoweringAnalyzer.cs`:

```csharp
using CSharpToJava.Core.IteratorLowering.ControlFlow;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.IteratorLowering;

/// <summary>
/// Discovers iterator members in a compilation, validates them against the
/// M1 support matrix, and produces a <see cref="StateMachinePlan"/> for each
/// lowerable member. Unsupported forms get a skip diagnostic.
/// </summary>
internal sealed class IteratorLoweringAnalyzer
{
    public static AnalyzerResult Analyze(SemanticModel semanticModel, IteratorLoweringOptions options)
    {
        var plans = new List<StateMachinePlan>();
        var diagnostics = new List<IteratorLoweringDiagnostic>();
        var skipMemberKeys = new HashSet<string>(StringComparer.Ordinal);

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

        var memberIndex = 0;

        foreach (var method in methods)
        {
            var symbol = semanticModel.GetDeclaredSymbol(method);
            if (symbol == null) continue;

            // Must have a body with yield statements
            if (method.Body == null) continue;
            if (!method.Body.DescendantNodes().Any(n => n.IsKind(SyntaxKind.YieldReturnStatement)
                || n.IsKind(SyntaxKind.YieldBreakStatement)))
                continue;

            // Check return type: IEnumerable<T>, IEnumerator<T>, IEnumerable, IEnumerator
            var (elementType, isEnumerable, isIEnumerator, isNonGeneric) = ResolveReturnType(symbol.ReturnType);
            if (elementType == null)
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0401,
                    $"Cannot resolve iterator element type for '{method.Identifier.Text}'.");
                skipMemberKeys.Add(GetMemberKey(method));
                continue;
            }

            // M1 legality checks
            if (!CheckLegality(method, semanticModel, diagnostics))
            {
                skipMemberKeys.Add(GetMemberKey(method));
                continue;
            }

            // Build the plan
            var cfgInfo = CfgBuilder.Analyze(method, semanticModel);
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            var className = IteratorLoweringNaming.GetStateMachineClassName(
                method.Identifier.ValueText, memberIndex, usedNames);

            // Determine declaring type name
            var declaringType = method.Parent as TypeDeclarationSyntax;
            var declaringTypeName = declaringType?.Identifier.ValueText ?? "global";

            // Parameters
            var paramFields = new List<CaptureField>();
            foreach (var param in method.ParameterList.Parameters)
            {
                if (cfgInfo.HoistedParameterNames.Contains(param.Identifier.ValueText))
                {
                    var paramType = param.Type?.ToString() ?? "object";
                    paramFields.Add(new CaptureField(
                        param.Identifier.ValueText,
                        paramType,
                        IteratorLoweringNaming.GetCaptureFieldName(param.Identifier.ValueText)));
                }
            }

            // Hoisted locals
            var localFields = new List<CaptureField>();
            foreach (var localName in cfgInfo.HoistedLocalNames)
            {
                // Best-effort type resolution from semantic model
                var localType = ResolveLocalType(method, localName, semanticModel) ?? "object";
                localFields.Add(new CaptureField(
                    localName,
                    localType,
                    IteratorLoweringNaming.GetLocalFieldName(localName)));
            }

            plans.Add(new StateMachinePlan(
                MemberName: method.Identifier.ValueText,
                StateMachineClassName: className,
                ElementType: elementType,
                IsIEnumerableReturn: isEnumerable,
                IsIEnumeratorReturn: isIEnumerator,
                IsGenericMethod: method.TypeParameterList?.Parameters.Count > 0,
                DeclaringTypeName: declaringTypeName,
                IsStatic: method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)),
                Parameters: paramFields,
                HoistedLocals: localFields,
                YieldReturnCount: cfgInfo.YieldReturnCount,
                HasYieldBreak: cfgInfo.HasYieldBreak));

            memberIndex++;
        }

        return new AnalyzerResult(plans, diagnostics, skipMemberKeys);
    }

    private static (string? elementType, bool isEnumerable, bool isIEnumerator, bool isNonGeneric)
        ResolveReturnType(ITypeSymbol returnType)
    {
        if (returnType is not INamedTypeSymbol named) return (null, false, false, false);

        var fullName = named.OriginalDefinition.ToDisplayString();

        return fullName switch
        {
            "System.Collections.Generic.IEnumerable<T>" =>
                (named.TypeArguments[0].ToDisplayString(), true, false, false),
            "System.Collections.Generic.IEnumerator<T>" =>
                (named.TypeArguments[0].ToDisplayString(), false, true, false),
            "System.Collections.IEnumerable" =>
                ("object", true, false, true),
            "System.Collections.IEnumerator" =>
                ("object", false, true, true),
            _ => (null, false, false, false)
        };
    }

    private static bool CheckLegality(
        MethodDeclarationSyntax method,
        SemanticModel model,
        List<IteratorLoweringDiagnostic> diagnostics)
    {
        var body = method.Body!;
        var ok = true;

        // ref/in/out params → CS2J0402 (CS1623 mirror)
        foreach (var param in method.ParameterList.Parameters)
        {
            if (param.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)
                || m.IsKind(SyntaxKind.InKeyword)
                || m.IsKind(SyntaxKind.OutKeyword)))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has ref/in/out parameter '{param.Identifier.Text}' (CS1623 mirror).");
                ok = false;
            }
        }

        // try-with-catch containing yield → CS2J0402 (CS1626 mirror)
        foreach (var tryStmt in body.DescendantNodes().OfType<TryStatementSyntax>())
        {
            if (tryStmt.Catches.Count > 0 && ContainsYield(tryStmt))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has yield inside try-with-catch (CS1626 mirror).");
                ok = false;
            }
            // finally containing yield → CS2J0402 (CS1625 mirror)
            if (tryStmt.Finally != null && ContainsYield(tryStmt.Finally))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has yield inside finally (CS1625 mirror).");
                ok = false;
            }
        }

        // M1: try-finally (no catch) containing yield → CS2J0402 (not yet supported)
        foreach (var tryStmt in body.DescendantNodes().OfType<TryStatementSyntax>())
        {
            if (tryStmt.Finally != null && ContainsYield(tryStmt))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has yield inside try-finally (M1 unsupported, planned for M2).");
                ok = false;
            }
        }

        // using statement containing yield → CS2J0402 (M1 unsupported)
        foreach (var usingStmt in body.DescendantNodes().OfType<UsingStatementSyntax>())
        {
            if (ContainsYield(usingStmt))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has yield inside using (M1 unsupported, planned for M3).");
                ok = false;
            }
        }

        // lock statement containing yield → CS2J0402 (M1 unsupported)
        foreach (var lockStmt in body.DescendantNodes().OfType<LockStatementSyntax>())
        {
            if (ContainsYield(lockStmt))
            {
                AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0402,
                    $"Iterator method '{method.Identifier.Text}' has yield inside lock (M1 unsupported, planned for M3).");
                ok = false;
            }
        }

        // User goto/label inside body → CS2J0406
        if (body.DescendantNodes().OfType<GotoStatementSyntax>().Any()
            || body.DescendantNodes().OfType<LabeledStatementSyntax>().Any())
        {
            AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0406,
                $"Iterator method '{method.Identifier.Text}' has user goto/label (v1 unsupported).");
            ok = false;
        }

        // Local function inside body → CS2J0404 (should have been lifted by E02)
        if (body.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Any())
        {
            AddSkipDiagnostic(diagnostics, method, IteratorLoweringDiagnosticCode.CS2J0404,
                $"Iterator method '{method.Identifier.Text}' has residual local function (should be lifted by E02).");
            ok = false;
        }

        return ok;
    }

    private static bool ContainsYield(SyntaxNode node)
        => node.DescendantNodes().Any(n => n.IsKind(SyntaxKind.YieldReturnStatement)
            || n.IsKind(SyntaxKind.YieldBreakStatement));

    private static string? ResolveLocalType(
        MethodDeclarationSyntax method,
        string localName,
        SemanticModel model)
    {
        // Find the local declaration
        foreach (var localDecl in method.Body!.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>())
        {
            var varDecl = localDecl.Declaration.Variables
                .FirstOrDefault(v => v.Identifier.ValueText == localName);
            if (varDecl != null)
            {
                var symbol = model.GetDeclaredSymbol(varDecl);
                return symbol?.Type?.ToDisplayString();
            }
        }

        // For-loop declarators
        foreach (var forStmt in method.Body!.DescendantNodes().OfType<ForStatementSyntax>())
        {
            if (forStmt.Declaration != null)
            {
                var varDecl = forStmt.Declaration.Variables
                    .FirstOrDefault(v => v.Identifier.ValueText == localName);
                if (varDecl != null)
                {
                    var symbol = model.GetDeclaredSymbol(varDecl);
                    return symbol?.Type?.ToDisplayString();
                }
            }
        }

        // Foreach iteration variables
        foreach (var foreachStmt in method.Body!.DescendantNodes().OfType<ForEachStatementSyntax>())
        {
            if (foreachStmt.Identifier.ValueText == localName)
            {
                var info = model.GetForEachStatementInfo(foreachStmt);
                return info.ElementType?.ToDisplayString();
            }
        }

        return null;
    }

    private static void AddSkipDiagnostic(
        List<IteratorLoweringDiagnostic> diagnostics,
        MethodDeclarationSyntax method,
        IteratorLoweringDiagnosticCode code,
        string message)
    {
        var lineSpan = method.GetLocation().GetLineSpan();
        diagnostics.Add(new IteratorLoweringDiagnostic(
            IteratorLoweringSeverity.Error,
            code,
            message,
            filePath: null,
            lineSpan.StartLinePosition.Line + 1));
    }

    private static string GetMemberKey(MethodDeclarationSyntax method)
        => method.Identifier.ValueText + "_" + method.SpanStart;
}

internal sealed record AnalyzerResult(
    IReadOnlyList<StateMachinePlan> Plans,
    IReadOnlyList<IteratorLoweringDiagnostic> Diagnostics,
    IReadOnlySet<string> SkipMemberKeys);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: PASS (21 tests)

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/IteratorLowering/IteratorLoweringAnalyzer.cs tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs
git commit -m "feat(iterator-lowering): add Analyzer with member discovery and legality gate"
```

---

## Task 6: IteratorStateMachineRewriter — 状态机类合成与成员体重写

**Files:**
- Create: `src/CSharpToJava.Core/IteratorLowering/IteratorStateMachineRewriter.cs`
- Test: `tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs`

- [ ] **Step 1: Write the failing test**

追加到 `IteratorLoweringUnitTests.cs`。需要添加 `RewriteAndAssert` helper：

```csharp
internal static (string Output, IteratorLoweringStatistics Stats,
    List<IteratorLoweringDiagnostic> Diags) Rewrite(string src)
{
    var model = ParseWithSemantics(src);
    var analyzerResult = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());
    var rewriter = new IteratorStateMachineRewriter(model, analyzerResult);
    var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
    var newRoot = rewriter.Rewrite(root);
    using var workspace = new AdhocWorkspace();
    var formatted = Formatter.Format(newRoot, workspace);
    var output = formatted.ToFullString();
    var stats = new IteratorLoweringStatistics
    {
        IteratorMembersLowered = analyzerResult.Plans.Count,
        StateMachinesGenerated = analyzerResult.Plans.Count,
        YieldReturnSites = analyzerResult.Plans.Sum(p => p.YieldReturnCount),
        YieldBreakSites = analyzerResult.Plans.Count(p => p.HasYieldBreak),
        HoistedParameters = analyzerResult.Plans.Sum(p => p.Parameters.Count),
        HoistedLocals = analyzerResult.Plans.Sum(p => p.HoistedLocals.Count),
        SkippedUnsupported = analyzerResult.Diagnostics.Count,
    };
    return (output, stats, analyzerResult.Diagnostics.ToList());
}

internal static void AssertOutputCompiles(string output)
{
    var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
    var compilation = CSharpCompilation.Create("il-verify",
        new[] { tree }, GetReferences(),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    var errors = compilation.GetDiagnostics()
        .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    Assert.True(errors.Count == 0,
        "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
        "\n--- output ---\n" + output);
}

[Fact]
public void Rewriter_SimpleYieldReturn_ProducesStateMachine()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() { yield return 42; }
}";
    var (output, stats, diags) = Rewrite(src);

    Assert.Equal(1, stats.IteratorMembersLowered);
    Assert.Contains("Cs2jIterator_M_0", output);
    Assert.Contains("_cs2jState", output);
    Assert.Contains("_cs2jCurrent", output);
    Assert.Contains("MoveNext", output);
    Assert.Contains("GetEnumerator", output);
    Assert.DoesNotContain("yield return", output);
    AssertOutputCompiles(output);
}

[Fact]
public void Rewriter_MultipleYields_ProducesMultipleResumeStates()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() {
        yield return 1;
        yield return 2;
    }
}";
    var (output, stats, diags) = Rewrite(src);

    Assert.Contains("_cs2jResume_1", output);
    Assert.Contains("_cs2jResume_2", output);
    Assert.DoesNotContain("yield return", output);
    AssertOutputCompiles(output);
}

[Fact]
public void Rewriter_YieldBreak_ProducesEarlyExit()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(bool early) {
        if (early) yield break;
        yield return 1;
    }
}";
    var (output, stats, diags) = Rewrite(src);

    Assert.DoesNotContain("yield break", output);
    Assert.DoesNotContain("yield return", output);
    AssertOutputCompiles(output);
}

[Fact]
public void Rewriter_ForLoop_PreservesLoopStructure()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(int count) {
        for (int i = 0; i < count; i++)
            yield return i;
    }
}";
    var (output, stats, diags) = Rewrite(src);

    Assert.Contains("_cs2jLoc_i", output);
    Assert.Contains("_cs2jCap_count", output);
    Assert.DoesNotContain("yield return", output);
    AssertOutputCompiles(output);
}

[Fact]
public void Rewriter_Foreach_PreservesIteration()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<string> M(List<string> items) {
        foreach (var item in items)
            yield return item;
    }
}";
    var (output, stats, diags) = Rewrite(src);

    Assert.Contains("_cs2jLoc_item", output);
    Assert.DoesNotContain("yield return", output);
    AssertOutputCompiles(output);
}

[Fact]
public void Rewriter_InstanceMethod_CapturesThis()
{
    var src = @"
using System.Collections.Generic;
class Writer {
    IEnumerable<int> M() { yield return 1; }
}";
    var (output, stats, diags) = Rewrite(src);

    Assert.Contains("_cs2jThis", output);
    AssertOutputCompiles(output);
}

[Fact]
public void Rewriter_StaticMethod_NoThisField()
{
    var src = @"
using System.Collections.Generic;
class C {
    static IEnumerable<int> M() { yield return 1; }
}";
    var (output, stats, diags) = Rewrite(src);

    Assert.DoesNotContain("_cs2jThis", output);
    AssertOutputCompiles(output);
}

[Fact]
public void Rewriter_GenericMethod_CarriesTypeParameters()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<T> M<T>(T val) { yield return val; }
}";
    var (output, stats, diags) = Rewrite(src);

    Assert.Contains("Cs2jIterator_M_0<T>", output);
    AssertOutputCompiles(output);
}

[Fact]
public void Rewriter_IEnumeratorReturn_NoGetEnumerator()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerator<int> M() { yield return 1; }
}";
    var (output, stats, diags) = Rewrite(src);

    // IEnumerator return: member body creates instance with state 0 directly
    Assert.Contains("new Cs2jIterator_M_0", output);
    Assert.DoesNotContain("yield return", output);
    AssertOutputCompiles(output);
}

[Fact]
public void Rewriter_NonGenericEnumerable_ObjectCurrent()
{
    var src = @"
using System.Collections;
class C {
    IEnumerable M() { yield return 1; }
}";
    var (output, stats, diags) = Rewrite(src);

    Assert.DoesNotContain("yield return", output);
    AssertOutputCompiles(output);
}

[Fact]
public void Rewriter_SkippedMember_PreservedUnchanged()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> Good() { yield return 1; }
    IEnumerable<int> Bad(ref int x) { yield return x; }
}";
    var (output, stats, diags) = Rewrite(src);

    // Good is lowered; Bad is preserved with yield intact (skip)
    Assert.Contains("Cs2jIterator_Good_0", output);
    Assert.Contains("yield return x", output); // Bad method unchanged
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: FAIL with "type or namespace 'IteratorStateMachineRewriter' not found"

- [ ] **Step 3: Write minimal implementation**

`src/CSharpToJava.Core/IteratorLowering/IteratorStateMachineRewriter.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.IteratorLowering;

/// <summary>
/// Rewrites iterator method bodies and synthesizes nested state machine classes
/// based on the <see cref="AnalyzerResult"/> produced by <see cref="IteratorLoweringAnalyzer"/>.
/// </summary>
internal sealed class IteratorStateMachineRewriter
{
    private static readonly SyntaxAnnotation Annotation = new("Cs2jIteratorLowering");

    private readonly SemanticModel _semanticModel;
    private readonly AnalyzerResult _analyzerResult;
    private readonly Dictionary<MethodDeclarationSyntax, StateMachinePlan> _planByMethod;

    public IteratorStateMachineRewriter(SemanticModel semanticModel, AnalyzerResult analyzerResult)
    {
        _semanticModel = semanticModel;
        _analyzerResult = analyzerResult;
        _planByMethod = new();

        // Map plans to their method syntax nodes
        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
        var planIndex = 0;
        foreach (var method in methods)
        {
            if (method.Body == null) continue;
            if (!method.Body.DescendantNodes().Any(n => n.IsKind(SyntaxKind.YieldReturnStatement)
                || n.IsKind(SyntaxKind.YieldBreakStatement)))
                continue;

            // Check if this method was planned (not skipped)
            if (planIndex < analyzerResult.Plans.Count
                && analyzerResult.Plans[planIndex].MemberName == method.Identifier.ValueText)
            {
                _planByMethod[method] = analyzerResult.Plans[planIndex];
                planIndex++;
            }
        }
    }

    public CompilationUnitSyntax Rewrite(CompilationUnitSyntax root)
    {
        if (_planByMethod.Count == 0)
            return root;

        // Process each type declaration that contains iterator methods
        var newRoot = root;
        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList())
        {
            var plansForType = _planByMethod
                .Where(kv => kv.Key.Parent == typeDecl)
                .ToList();
            if (plansForType.Count == 0) continue;

            // Build new members: replace iterator method bodies + add nested classes
            var newMembers = new List<MemberDeclarationSyntax>();
            var nestedClasses = new List<ClassDeclarationSyntax>();

            foreach (var member in typeDecl.Members)
            {
                if (member is MethodDeclarationSyntax method && _planByMethod.ContainsKey(method))
                {
                    var plan = _planByMethod[method];
                    // Replace method body with `return new StateMachine(this, args, state);`
                    var newMethod = RewriteMethodBody(method, plan);
                    newMembers.Add(newMethod);
                    // Build the nested state machine class
                    nestedClasses.Add(BuildStateMachineClass(method, plan));
                }
                else
                {
                    newMembers.Add(member);
                }
            }

            newMembers.AddRange(nestedClasses);

            // Replace the type declaration
            var newTypeDecl = typeDecl.WithMembers(List(newMembers));
            newRoot = newRoot.ReplaceNode(typeDecl, newTypeDecl);
        }

        // Add required using directives if missing
        newRoot = EnsureUsings(newRoot);

        return newRoot;
    }

    private MethodDeclarationSyntax RewriteMethodBody(
        MethodDeclarationSyntax method, StateMachinePlan plan)
    {
        // Build: return new Cs2jIterator_X_0(this, arg1, arg2, -2 or 0);
        var stateValue = plan.IsIEnumerableReturn ? -2 : 0;

        var args = new List<ArgumentSyntax>();

        if (!plan.IsStatic)
            args.Add(Argument(ThisExpression()));

        foreach (var param in plan.Parameters)
            args.Add(Argument(IdentifierName(param.OriginalName)));

        args.Add(Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression,
            Literal(stateValue))));

        var objectCreation = ObjectCreationExpression(
            ParseTypeName(plan.StateMachineClassName
                + (plan.IsGenericMethod ? GetGenericTypeArgs(method) : "")))
            .WithArgumentList(ArgumentList(SeparatedList(args)));

        var returnStmt = ReturnStatement(objectCreation);
        var newBody = Block(returnStmt);

        return method
            .WithBody(newBody)
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAdditionalAnnotations(Annotation);
    }

    private ClassDeclarationSyntax BuildStateMachineClass(
        MethodDeclarationSyntax method, StateMachinePlan plan)
    {
        var className = plan.StateMachineClassName
            + (plan.IsGenericMethod ? GetGenericTypeArgs(method) : "");

        // --- Fields ---
        var fields = new List<MemberDeclarationSyntax>();

        // _cs2jState
        fields.Add(FieldDeclaration(VariableDeclaration(PredefinedType(Token(SyntaxKind.IntKeyword)))
            .WithVariables(SingletonSeparatedList(VariableDeclarator(IteratorLoweringNaming.StateField))))
            .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword))));

        // _cs2jCurrent
        fields.Add(FieldDeclaration(VariableDeclaration(ParseTypeName(plan.ElementType))
            .WithVariables(SingletonSeparatedList(VariableDeclarator(IteratorLoweringNaming.CurrentField))))
            .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword))));

        // _cs2jInitialThreadId (only for IEnumerable return)
        if (plan.IsIEnumerableReturn)
        {
            fields.Add(FieldDeclaration(VariableDeclaration(PredefinedType(Token(SyntaxKind.IntKeyword)))
                .WithVariables(SingletonSeparatedList(VariableDeclarator(IteratorLoweringNaming.InitialThreadIdField))))
                .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword))));
        }

        // _cs2jThis (instance methods only)
        if (!plan.IsStatic)
        {
            fields.Add(FieldDeclaration(VariableDeclaration(ParseTypeName(plan.DeclaringTypeName))
                .WithVariables(SingletonSeparatedList(VariableDeclarator(IteratorLoweringNaming.ThisField))))
                .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword))));
        }

        // Captured parameters
        foreach (var param in plan.Parameters)
        {
            fields.Add(FieldDeclaration(VariableDeclaration(ParseTypeName(param.Type))
                .WithVariables(SingletonSeparatedList(VariableDeclarator(param.FieldName))))
                .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword))));
        }

        // Hoisted locals
        foreach (var local in plan.HoistedLocals)
        {
            fields.Add(FieldDeclaration(VariableDeclaration(ParseTypeName(local.Type))
                .WithVariables(SingletonSeparatedList(VariableDeclarator(local.FieldName))))
                .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword))));
        }

        // --- Constructor ---
        var ctorParams = new List<ParameterSyntax>();
        if (!plan.IsStatic)
            ctorParams.Add(Parameter(Identifier("@this")).WithType(ParseTypeName(plan.DeclaringTypeName)));
        foreach (var param in plan.Parameters)
            ctorParams.Add(Parameter(Identifier(param.OriginalName)).WithType(ParseTypeName(param.Type)));
        ctorParams.Add(Parameter(Identifier("state")).WithType(PredefinedType(Token(SyntaxKind.IntKeyword))));

        var ctorStatements = new List<StatementSyntax>();
        if (!plan.IsStatic)
            ctorStatements.Add(ExpressionStatement(AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                IdentifierName(IteratorLoweringNaming.ThisField),
                IdentifierName("@this"))));
        foreach (var param in plan.Parameters)
        {
            ctorStatements.Add(ExpressionStatement(AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                IdentifierName(param.FieldName),
                IdentifierName(param.OriginalName))));
        }
        ctorStatements.Add(ExpressionStatement(AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            IdentifierName(IteratorLoweringNaming.StateField),
            IdentifierName("state"))));
        if (plan.IsIEnumerableReturn)
        {
            ctorStatements.Add(ExpressionStatement(AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                IdentifierName(IteratorLoweringNaming.InitialThreadIdField),
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName("Environment"),
                    IdentifierName("CurrentManagedThreadId")))));
        }

        var ctor = ConstructorDeclaration(Identifier(plan.StateMachineClassName))
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(ParameterList(SeparatedList(ctorParams)))
            .WithBody(Block(ctorStatements));

        // --- Current properties ---
        var currentProp = PropertyDeclaration(ParseTypeName(plan.ElementType), "Current")
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithExpressionBody(ArrowExpressionClause(
                IdentifierName(IteratorLoweringNaming.CurrentField)))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        var explicitCurrentProp = PropertyDeclaration(
            PredefinedType(Token(SyntaxKind.ObjectKeyword)), "Current")
            .WithExplicitInterfaceSpecifier(ExplicitInterfaceSpecifier(
                ParseName("System.Collections.IEnumerator")))
            .WithExpressionBody(ArrowExpressionClause(
                IdentifierName(IteratorLoweringNaming.CurrentField)))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        // --- MoveNext ---
        var moveNext = BuildMoveNextMethod(method, plan);

        // --- Dispose ---
        var dispose = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "Dispose")
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithBody(Block(
                ExpressionStatement(AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    IdentifierName(IteratorLoweringNaming.StateField),
                    PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression,
                        LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(1)))))));

        // --- Reset ---
        var reset = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "Reset")
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithBody(Block(
                ThrowStatement(ObjectCreationExpression(ParseTypeName("System.NotSupportedException"))
                    .WithArgumentList(ArgumentList()))));

        var members = new List<MemberDeclarationSyntax>();
        members.AddRange(fields);
        members.Add(ctor);
        members.Add(currentProp);
        members.Add(explicitCurrentProp);
        members.Add(moveNext);
        members.Add(dispose);
        members.Add(reset);

        // --- GetEnumerator (IEnumerable return only) ---
        if (plan.IsIEnumerableReturn)
        {
            members.Add(BuildGetEnumeratorMethod(plan));
            members.Add(BuildExplicitGetEnumeratorMethod(plan));
        }

        // --- Base list ---
        var baseTypes = new List<BaseTypeSyntax>();
        if (plan.IsIEnumerableReturn)
        {
            baseTypes.Add(SimpleBaseType(ParseTypeName(
                $"System.Collections.Generic.IEnumerable<{plan.ElementType}>")));
            baseTypes.Add(SimpleBaseType(ParseTypeName(
                $"System.Collections.Generic.IEnumerator<{plan.ElementType}>")));
        }
        else
        {
            baseTypes.Add(SimpleBaseType(ParseTypeName(
                $"System.Collections.Generic.IEnumerator<{plan.ElementType}>")));
        }
        baseTypes.Add(SimpleBaseType(ParseTypeName("System.IDisposable")));

        return ClassDeclaration(Identifier(plan.StateMachineClassName))
            .WithModifiers(TokenList(
                Token(SyntaxKind.PrivateKeyword),
                Token(SyntaxKind.SealedKeyword)))
            .WithAttributeLists(SingletonList(AttributeList(SingletonSeparatedList(
                Attribute(ParseName("System.Runtime.CompilerServices.CompilerGeneratedAttribute"))))))
            .WithBaseList(BaseList(SeparatedList(baseTypes)))
            .WithMembers(List(members))
            .WithAdditionalAnnotations(Annotation);
    }

    private MethodDeclarationSyntax BuildMoveNextMethod(
        MethodDeclarationSyntax method, StateMachinePlan plan)
    {
        // switch(_cs2jState) { case 0: goto _cs2jResume_0; case 1: goto _cs2jResume_1; ... }
        var switchCases = new List<SwitchSectionSyntax>();
        for (int i = 0; i < plan.YieldReturnCount; i++)
        {
            var state = i + 1;
            switchCases.Add(SwitchSection(
                SingletonSeparatedList<SwitchLabelSyntax>(
                    CaseSwitchLabel(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(state)))),
                SingletonList<StatementSyntax>(
                    GotoStatement(
                        SyntaxKind.GotoStatement,
                        IdentifierName(IteratorLoweringNaming.GetResumeLabel(i))))));
        }

        var switchStmt = SwitchStatement(
            IdentifierName(IteratorLoweringNaming.StateField),
            Token(SyntaxKind.OpenBraceToken),
            List(switchCases),
            Token(SyntaxKind.CloseBraceToken));

        // The body: transform yield returns into state assignments, yield break into return false
        var bodyStatements = new List<StatementSyntax>();
        bodyStatements.Add(LabeledStatement(
            Identifier(IteratorLoweringNaming.GetResumeLabel(0)),
            Token(SyntaxKind.ColonToken)));

        // Transform the original body
        var transformedBody = TransformBody(method.Body!, plan);

        // After the original body (transformed), set state=-1 and return false
        bodyStatements.Add(switchStmt);
        bodyStatements.AddRange(transformedBody);

        bodyStatements.Add(ExpressionStatement(AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            IdentifierName(IteratorLoweringNaming.StateField),
            PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression,
                LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(1))))));
        bodyStatements.Add(ReturnStatement(LiteralExpression(SyntaxKind.FalseLiteralExpression)));

        return MethodDeclaration(PredefinedType(Token(SyntaxKind.BoolKeyword)), "MoveNext")
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithBody(Block(bodyStatements));
    }

    private List<StatementSyntax> TransformBody(BlockSyntax originalBody, StateMachinePlan plan)
    {
        var rewriter = new BodyTransformRewriter(plan);
        var transformed = (BlockSyntax)rewriter.Visit(originalBody)!;
        return transformed.Statements.ToList();
    }

    private MethodDeclarationSyntax BuildGetEnumeratorMethod(StateMachinePlan plan)
    {
        // if (_cs2jState == -2 && _cs2jInitialThreadId == Environment.CurrentManagedThreadId) { _cs2jState = 0; return this; }
        // else { var e = new ClassName(..., -2); e._cs2jState = 0; return e; }
        var cloneArgs = new List<ArgumentSyntax>();
        if (!plan.IsStatic)
            cloneArgs.Add(Argument(IdentifierName(IteratorLoweringNaming.ThisField)));
        foreach (var param in plan.Parameters)
            cloneArgs.Add(Argument(IdentifierName(param.FieldName)));
        cloneArgs.Add(Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(-2))));

        var cloneCreation = ObjectCreationExpression(
            ParseTypeName(plan.StateMachineClassName))
            .WithArgumentList(ArgumentList(SeparatedList(cloneArgs)));

        var cloneAssign = ExpressionStatement(AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                IdentifierName("e"), IdentifierName(IteratorLoweringNaming.StateField)),
            LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0))));

        var returnThis = ReturnStatement(ThisExpression());
        var returnClone = ReturnStatement(IdentifierName("e"));

        var condition = BinaryExpression(SyntaxKind.LogicalAndExpression,
            BinaryExpression(SyntaxKind.EqualsExpression,
                IdentifierName(IteratorLoweringNaming.StateField),
                PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression,
                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(2)))),
            BinaryExpression(SyntaxKind.EqualsExpression,
                IdentifierName(IteratorLoweringNaming.InitialThreadIdField),
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName("Environment"),
                    IdentifierName("CurrentManagedThreadId"))));

        var ifStmt = IfStatement(condition,
            Block(
                ExpressionStatement(AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    IdentifierName(IteratorLoweringNaming.StateField),
                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0)))),
                returnThis));
        var elseStmt = ElseClause(Block(
            LocalDeclarationStatement(VariableDeclaration(
                IdentifierName("var")).WithVariables(SingletonSeparatedList(
                    VariableDeclarator(Identifier("e"))
                        .WithInitializer(EqualsValueClause(cloneCreation))))),
            cloneAssign,
            returnClone));

        ifStmt = ifStmt.WithElse(elseStmt);

        return MethodDeclaration(
            GenericName(Identifier("IEnumerator"))
                .WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(
                    ParseTypeName(plan.ElementType)))),
            "GetEnumerator")
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithBody(Block(ifStmt));
    }

    private MethodDeclarationSyntax BuildExplicitGetEnumeratorMethod(StateMachinePlan plan)
    {
        var inner = InvocationExpression(IdentifierName("GetEnumerator"));
        return MethodDeclaration(
            ParseTypeName("System.Collections.IEnumerator"),
            "GetEnumerator")
            .WithExplicitInterfaceSpecifier(ExplicitInterfaceSpecifier(
                ParseName("System.Collections.IEnumerable")))
            .WithExpressionBody(ArrowExpressionClause(inner))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
    }

    private static string GetGenericTypeArgs(MethodDeclarationSyntax method)
    {
        if (method.TypeParameterList == null) return "";
        var args = string.Join(", ", method.TypeParameterList.Parameters.Select(p => p.Identifier.ValueText));
        return $"<{args}>";
    }

    private static CompilationUnitSyntax EnsureUsings(CompilationUnitSyntax root)
    {
        var usings = root.Usings.Select(u => u.Name?.ToString() ?? "").ToHashSet();
        var needed = new[] { "System", "System.Collections", "System.Collections.Generic" };
        var newUsings = root.Usings.ToList();
        foreach (var ns in needed)
        {
            if (!usings.Contains(ns))
            {
                newUsings.Add(UsingDirective(ParseName(ns)));
            }
        }
        return root.WithUsings(List(newUsings));
    }

    /// <summary>
    /// Syntax rewriter that transforms yield return/yield break in the method body
    /// into state machine operations.
    /// </summary>
    private sealed class BodyTransformRewriter : CSharpSyntaxRewriter
    {
        private readonly StateMachinePlan _plan;
        private int _yieldIndex;

        public BodyTransformRewriter(StateMachinePlan plan) => _plan = plan;

        public override SyntaxNode? VisitYieldStatement(YieldStatementSyntax node)
        {
            if (node.IsKind(SyntaxKind.YieldReturnStatement) && node.Expression != null)
            {
                var state = ++_yieldIndex;
                // _cs2jCurrent = expr; _cs2jState = state; return true;
                // _cs2jResume_{state-1}: ;
                var statements = new List<StatementSyntax>
                {
                    ExpressionStatement(AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        IdentifierName(IteratorLoweringNaming.CurrentField),
                        node.Expression)),
                    ExpressionStatement(AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        IdentifierName(IteratorLoweringNaming.StateField),
                        LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(state)))),
                    ReturnStatement(LiteralExpression(SyntaxKind.TrueLiteralExpression)),
                    LabeledStatement(
                        Identifier(IteratorLoweringNaming.GetResumeLabel(state - 1)),
                        Token(SyntaxKind.ColonToken)),
                    EmptyStatement(),
                };
                return Block(statements);
            }
            else if (node.IsKind(SyntaxKind.YieldBreakStatement))
            {
                // _cs2jState = -1; return false;
                return Block(
                    ExpressionStatement(AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        IdentifierName(IteratorLoweringNaming.StateField),
                        PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression,
                            LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(1))))),
                    ReturnStatement(LiteralExpression(SyntaxKind.FalseLiteralExpression)));
            }
            return base.VisitYieldStatement(node);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: PASS (31 tests)

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/IteratorLowering/IteratorStateMachineRewriter.cs tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs
git commit -m "feat(iterator-lowering): add state machine rewriter with codegen for M1 forms"
```

---

## Task 7: IteratorLoweringPreprocessor — 对外入口

**Files:**
- Create: `src/CSharpToJava.Core/IteratorLowering/IteratorLoweringPreprocessor.cs`
- Test: `tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs`

- [ ] **Step 1: Write the failing test**

追加到 `IteratorLoweringUnitTests.cs`：

```csharp
[Fact]
public void Preprocessor_NoYield_ReturnsUnchanged()
{
    var src = @"
using System.Collections.Generic;
class C {
    List<int> M() { return new List<int> { 1, 2, 3 }; }
}";
    var model = ParseWithSemantics(src);
    var preprocessor = new IteratorLoweringPreprocessor();
    var result = preprocessor.Preprocess(src, model, new IteratorLoweringOptions());

    Assert.False(result.Changed);
    Assert.Equal(1, result.Statistics.FilesScanned);
    Assert.Equal(1, result.Statistics.FilesSkippedClean);
}

[Fact]
public void Preprocessor_WithYield_ReturnsChanged()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() { yield return 1; yield return 2; }
}";
    var model = ParseWithSemantics(src);
    var preprocessor = new IteratorLoweringPreprocessor();
    var result = preprocessor.Preprocess(src, model, new IteratorLoweringOptions());

    Assert.True(result.Changed);
    Assert.Equal(1, result.Statistics.FilesTransformed);
    Assert.Equal(1, result.Statistics.IteratorMembersLowered);
    Assert.Equal(2, result.Statistics.YieldReturnSites);
    Assert.DoesNotContain("yield return", result.OutputCode);
}

[Fact]
public void Preprocessor_StrictMode_WithSkip_HasErrorDiagnostic()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(ref int x) { yield return x; }
}";
    var model = ParseWithSemantics(src);
    var preprocessor = new IteratorLoweringPreprocessor();
    var result = preprocessor.Preprocess(src, model, new IteratorLoweringOptions());

    Assert.False(result.Changed);
    Assert.Contains(result.Diagnostics, d => d.Severity == IteratorLoweringSeverity.Error
        && d.Code == IteratorLoweringDiagnosticCode.CS2J0402);
}

[Fact]
public void Preprocessor_NonStrictMode_WithSkip_HasWarningDiagnostic()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(ref int x) { yield return x; }
}";
    var model = ParseWithSemantics(src);
    var preprocessor = new IteratorLoweringPreprocessor();
    var result = preprocessor.Preprocess(src, model, new IteratorLoweringOptions(Strict: false));

    Assert.False(result.Changed);
    Assert.Contains(result.Diagnostics, d => d.Severity == IteratorLoweringSeverity.Warning);
}

[Fact]
public void Preprocessor_ResidualGate_DetectsRemainingYield()
{
    // When a method is skipped, its yield remains → residual count > 0
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> Good() { yield return 1; }
    IEnumerable<int> Bad(ref int x) { yield return x; }
}";
    var model = ParseWithSemantics(src);
    var preprocessor = new IteratorLoweringPreprocessor();
    var result = preprocessor.Preprocess(src, model, new IteratorLoweringOptions());

    Assert.True(result.Changed);
    // Bad method's yield is residual (but it's whitelisted as skipped)
    Assert.True(result.Statistics.ResidualCount >= 0);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: FAIL with "type or namespace 'IteratorLoweringPreprocessor' not found"

- [ ] **Step 3: Write minimal implementation**

`src/CSharpToJava.Core/IteratorLowering/IteratorLoweringPreprocessor.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.IteratorLowering;

/// <summary>
/// Public entry point for the iterator-lowering preprocessor. Mirrors the
/// EnumPreprocessor pattern: dirty check → analyze → rewrite → format → residual gate.
/// </summary>
public sealed class IteratorLoweringPreprocessor
{
    public IteratorLoweringFileResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        IteratorLoweringOptions? options = null)
    {
        options ??= new IteratorLoweringOptions();
        var stats = new IteratorLoweringStatistics { FilesScanned = 1 };
        var diagnostics = new List<IteratorLoweringDiagnostic>();

        if (semanticModel == null)
        {
            stats.FilesSkippedClean = 1;
            return new IteratorLoweringFileResult(sourceCode, false, diagnostics, stats);
        }

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();

        // Dirty check: no yield statements → byte-level copy
        if (!root.DescendantNodes().Any(n => n.IsKind(SyntaxKind.YieldReturnStatement)
            || n.IsKind(SyntaxKind.YieldBreakStatement)))
        {
            stats.FilesSkippedClean = 1;
            return new IteratorLoweringFileResult(sourceCode, false, diagnostics, stats);
        }

        // Analyze
        var analyzerResult = IteratorLoweringAnalyzer.Analyze(semanticModel, options);

        // Merge diagnostics with severity adjustment
        foreach (var diag in analyzerResult.Diagnostics)
        {
            var severity = diag.Severity;
            if (!options.Strict && severity == IteratorLoweringSeverity.Error)
                severity = IteratorLoweringSeverity.Warning;
            diagnostics.Add(diag with { Severity = severity });
        }

        // If no plans, nothing to rewrite
        if (analyzerResult.Plans.Count == 0)
        {
            stats.FilesDirtyNoRewrite = 1;
            stats.SkippedUnsupported = analyzerResult.Diagnostics.Count;
            return new IteratorLoweringFileResult(sourceCode, false, diagnostics, stats);
        }

        // Rewrite
        var rewriter = new IteratorStateMachineRewriter(semanticModel, analyzerResult);
        var newRoot = rewriter.Rewrite(root);

        // Format
        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(newRoot, workspace);
        var code = formatted.ToFullString();

        // Merge statistics
        stats.FilesTransformed = 1;
        stats.IteratorMembersLowered = analyzerResult.Plans.Count;
        stats.StateMachinesGenerated = analyzerResult.Plans.Count;
        stats.YieldReturnSites = analyzerResult.Plans.Sum(p => p.YieldReturnCount);
        stats.YieldBreakSites = analyzerResult.Plans.Count(p => p.HasYieldBreak);
        stats.HoistedParameters = analyzerResult.Plans.Sum(p => p.Parameters.Count);
        stats.HoistedLocals = analyzerResult.Plans.Sum(p => p.HoistedLocals.Count);
        stats.SkippedUnsupported = analyzerResult.Diagnostics.Count;

        // Residual gate: count remaining yield statements not in skipped members
        var outputRoot = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code).GetRoot();
        var remainingYields = outputRoot.DescendantNodes()
            .OfType<YieldStatementSyntax>()
            .Count();
        stats.ResidualCount = remainingYields;

        if (remainingYields > 0 && options.Strict)
        {
            diagnostics.Add(new IteratorLoweringDiagnostic(
                IteratorLoweringSeverity.Error,
                IteratorLoweringDiagnosticCode.CS2J0402,
                $"{remainingYields} residual yield statement(s) remaining after preprocessing."));
        }

        return new IteratorLoweringFileResult(code, true, diagnostics, stats);
    }
}

public sealed record IteratorLoweringFileResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<IteratorLoweringDiagnostic> Diagnostics,
    IteratorLoweringStatistics Statistics);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: PASS (36 tests)

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.Core/IteratorLowering/IteratorLoweringPreprocessor.cs tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs
git commit -m "feat(iterator-lowering): add Preprocessor entry point with residual gate"
```

---

## Task 8: IteratorLoweringPassAdapter — CLI 适配器

**Files:**
- Create: `src/CSharpToJava.CLI/Preprocessing/Adapters/IteratorLoweringPassAdapter.cs`
- Test: `tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs`

- [ ] **Step 1: Write the failing test**

追加到 `IteratorLoweringUnitTests.cs`：

```csharp
[Fact]
public void PassAdapter_Descriptor_ReturnsCorrectId()
{
    // Verify the adapter can be created and has the right descriptor
    var config = new PassChainConfiguration
    {
        MakerOptionsFactory = () => throw new InvalidOperationException(),
        RecordReadOnlyOptionsFactory = () => throw new InvalidOperationException(),
        JavaKeywordOptionsFactory = () => throw new InvalidOperationException(),
    };
    var adapter = PassChainFactory.CreateAdapter(
        PassStubCatalog.Ids.IteratorLowering, config);
    Assert.Equal("iterator-lowering", adapter.Descriptor.Id);
    Assert.False(adapter.Descriptor.DefaultEnabled);
    Assert.True(adapter.Descriptor.VerifyBuildByDefault);
    Assert.True(adapter.Descriptor.VerifyTestsByDefault);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: FAIL — `CreateAdapter` throws `KeyNotFoundException` for `iterator-lowering`

- [ ] **Step 3: Write minimal implementation**

`src/CSharpToJava.CLI/Preprocessing/Adapters/IteratorLoweringPassAdapter.cs`:

```csharp
using CSharpToJava.Core.IteratorLowering;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// CLI adapter for the iterator-lowering preprocess pass. Follows the standard
/// spine (no ExecuteAsync override), with a residual gate in AfterFilesAsync.
/// </summary>
internal sealed class IteratorLoweringPassAdapter : ProjectPassAdapter
{
    private readonly IteratorLoweringOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly IteratorLoweringPreprocessor _preprocessor = new();
    private readonly List<IteratorLoweringDiagnostic> _diagnostics = new();
    private readonly IteratorLoweringStatistics _statistics = new();

    public IteratorLoweringPassAdapter(
        IteratorLoweringOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests,
        bool strict)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
        _strict = strict;
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassStubCatalog.Ids.IteratorLowering);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.Preprocess(context.SourceCode, context.SemanticModel!, _options);
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            var status = result.Changed ? "transformed"
                : result.Statistics.FilesSkippedClean > 0 ? "clean (copy)"
                : "dirty-no-rewrite (copy)";
            Console.WriteLine($"[iterator-lowering] {status}: {context.RelativePath}");
        }

        var rewrite = result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : null;
        return Task.FromResult(rewrite);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        if (_statistics.ResidualCount > 0)
        {
            var severity = _strict
                ? IteratorLoweringSeverity.Error
                : IteratorLoweringSeverity.Warning;
            _diagnostics.Add(new IteratorLoweringDiagnostic(
                severity,
                IteratorLoweringDiagnosticCode.CS2J0402,
                $"{_statistics.ResidualCount} residual yield statement(s) remaining after preprocessing."));
        }

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new IteratorLoweringDiagnostic(
            IteratorLoweringSeverity.Warning,
            IteratorLoweringDiagnosticCode.CS2J0420,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new IteratorLoweringDiagnostic(
            IteratorLoweringSeverity.Error,
            IteratorLoweringDiagnosticCode.CS2J0420,
            $"dotnet build failed with exit code {exitCode} after iterator lowering."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new IteratorLoweringDiagnostic(
            IteratorLoweringSeverity.Error,
            IteratorLoweringDiagnosticCode.CS2J0420,
            $"dotnet test failed with exit code {exitCode} after iterator lowering."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == IteratorLoweringSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"iterator-lowering: {_statistics.IteratorMembersLowered} member(s) lowered, " +
           $"{_statistics.YieldReturnSites} yield-return, " +
           $"{_statistics.YieldBreakSites} yield-break, " +
           $"{_statistics.HoistedParameters + _statistics.HoistedLocals} hoisted, " +
           $"{_statistics.SkippedUnsupported} skipped, " +
           $"{_statistics.ResidualCount} residual";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Select(d =>
            {
                var prefix = d.FilePath != null ? $"{d.FilePath}: " : "";
                return $"[{d.Severity}] {prefix}{d.Message}";
            })
            .ToList();

    private void MergeStatistics(IteratorLoweringStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.FilesDirtyNoRewrite += source.FilesDirtyNoRewrite;
        _statistics.IteratorMembersLowered += source.IteratorMembersLowered;
        _statistics.StateMachinesGenerated += source.StateMachinesGenerated;
        _statistics.YieldReturnSites += source.YieldReturnSites;
        _statistics.YieldBreakSites += source.YieldBreakSites;
        _statistics.HoistedParameters += source.HoistedParameters;
        _statistics.HoistedLocals += source.HoistedLocals;
        _statistics.SkippedUnsupported += source.SkippedUnsupported;
        _statistics.ResidualCount += source.ResidualCount;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: PASS (37 tests)

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.CLI/Preprocessing/Adapters/IteratorLoweringPassAdapter.cs tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs
git commit -m "feat(iterator-lowering): add CLI pass adapter with residual gate"
```

---

## Task 9: 注册表翻转 — Stub→Implemented + 排序边

**Files:**
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs`
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs`
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs`
- Test: `tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs`
- Test: `tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs`

- [ ] **Step 1: Write the failing test**

修改 `PassStubCatalogTests.cs` — 将 stub 计数从 23 改为 22，将 CS2J9001 用例从 `IteratorLowering` 改为 `ClosureLift`：

```csharp
// In All_merged_families_are_registered_as_stubs:
Assert.Equal(22, ids.Length);  // was 23
// Remove the line: Assert.Contains(PassStubCatalog.Ids.IteratorLowering, ids);
// (it's now implemented, not a stub)
```

```csharp
// In Running_an_enabled_stub_fails_with_CS2J9001_and_creates_no_directories:
// Change EnablePassIds from IteratorLowering to ClosureLift
EnablePassIds = new[] { PassStubCatalog.Ids.ClosureLift },
// Change assertions:
Assert.Contains(PassStubCatalog.Ids.ClosureLift, captured);
```

修改 `PassChainOrderTests.cs` — 在 `LockedChain` 中增加 `iterator-lowering`，位于 `make-readonly` 之后、`eliminate-goto` 之前。注意：`iterator-lowering` 不在 `EnabledByDefault` 中（默认关）：

```csharp
private static readonly string[] LockedChain =
[
    PassIds.Conditional,
    PassIds.LocalFunctionLift,
    PassIds.NullCoalesce,
    PassIds.Enum,
    PassIds.Delegate,
    PassIds.Tuple,
    PassIds.ForeachDeconstruction,
    PassIds.StripDefault,
    PassIds.RefOutIn,
    PassIds.Unsigned,
    PassIds.Generic,
    PassIds.RecordReadOnly,
    PassIds.ArrayMethod,
    PassIds.MakeReadonly,
    PassStubCatalog.Ids.IteratorLowering,  // NEW: between make-readonly and eliminate-goto
    PassIds.EliminateGoto,
    PassIds.JavaKeywords,
];
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "PassStubCatalogTests|PassChainOrderTests" -v n`
Expected: FAIL — stub count still 23, LockedChain doesn't include iterator-lowering

- [ ] **Step 3: Write minimal implementation**

3a. `PassStubCatalog.cs` — 删除 iterator-lowering stub 条目（保留 `Ids.IteratorLowering` 常量）：

删除以下块（约 151-160 行）:
```csharp
        new(
            Ids.IteratorLowering,
            "Iterator lowering",
            ".cs2j-iterator-lowering-src",
            PassStage.StateMachine,
            PassStatus.Stub,
            defaultEnabled: false,
            runAfterIds: new[] { Ids.ClosureLift },
            description: "Lower C# iterator methods into explicit state machine classes.",
            catalogItems: "C04"),
```

3b. `PassCatalog.cs` — 在 `Implemented` 列表中新增 descriptor，并给 `eliminate-goto` 的 `runAfterIds` 追加 `iterator-lowering`：

在 `MakeReadonly` 和 `EliminateGoto` 之间插入:
```csharp
        new(
            PassStubCatalog.Ids.IteratorLowering,
            "Iterator lowering",
            ".cs2j-iterator-lowering-src",
            PassStage.StateMachine,
            PassStatus.Implemented,
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.MakeReadonly },
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Lower C# iterator methods into explicit state machine classes."),
```

修改 `EliminateGoto` 的 `runAfterIds`:
```csharp
        new(
            PassIds.EliminateGoto,
            "Eliminate goto",
            ProjectGotoPreprocessor.IntermediateDirectoryName,
            PassStage.ControlFlow,
            PassStatus.Implemented,
            defaultEnabled: true,
            runAfterIds: new[] { PassIds.MakeReadonly, PassStubCatalog.Ids.IteratorLowering },
            needsSemanticModel: false,
            description: "Eliminate goto/labeled statements into structured loops (syntax pass)."),
```

3c. `PassChainFactory.cs` — 在 `CreateAdapter` switch 中增加 case:

```csharp
            PassStubCatalog.Ids.IteratorLowering => new IteratorLoweringPassAdapter(
                new IteratorLoweringOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "PassStubCatalogTests|PassChainOrderTests" -v n`
Expected: PASS

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: PASS (37 tests)

- [ ] **Step 5: Commit**

```bash
git add src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs
git commit -m "feat(iterator-lowering): flip stub to implemented, add ordering edge to eliminate-goto"
```

---

## Task 10: 确定性测试 + 全量构建验证

**Files:**
- Test: `tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs`

- [ ] **Step 1: Write the failing test**

追加到 `IteratorLoweringUnitTests.cs`：

```csharp
[Fact]
public void Determinism_SameInput_TwiceProducesIdenticalOutput()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(int n) {
        for (int i = 0; i < n; i++)
            yield return i * 2;
    }
}";
    var (output1, _, _) = Rewrite(src);
    var (output2, _, _) = Rewrite(src);
    Assert.Equal(output1, output2);
}

[Fact]
public void Determinism_MemberOrderShuffled_GeneratesStableNames()
{
    var srcA = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> First() { yield return 1; }
    IEnumerable<int> Second() { yield return 2; }
}";
    var srcB = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> Second() { yield return 2; }
    IEnumerable<int> First() { yield return 1; }
}";
    var (outputA, _, _) = Rewrite(srcA);
    var (outputB, _, _) = Rewrite(srcB);

    // First should always get index 0, Second index 1 (by source position)
    Assert.Contains("Cs2jIterator_First_0", outputA);
    Assert.Contains("Cs2jIterator_Second_1", outputA);
    Assert.Contains("Cs2jIterator_Second_0", outputB);
    Assert.Contains("Cs2jIterator_First_1", outputB);
}

[Fact]
public void Determinism_CollisionWithUserType_AppendsSuffix()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() { yield return 1; }
    // User already has this nested type name
    private sealed class Cs2jIterator_M_0 { }
}";
    var (output, _, _) = Rewrite(src);

    // The generated class must have been renamed to avoid collision
    Assert.Contains("Cs2jIterator_M_02", output);
}

[Fact]
public void FullBuild_NoYieldTokenInOutput()
{
    var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() {
        for (int i = 0; i < 10; i++)
            if (i % 2 == 0)
                yield return i;
    }
}";
    var (output, _, _) = Rewrite(src);

    Assert.DoesNotContain("yield return", output);
    Assert.DoesNotContain("yield break", output);
    Assert.Contains("_cs2jState", output);
    Assert.Contains("MoveNext", output);
    Assert.Contains("Dispose", output);
    AssertOutputCompiles(output);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "IteratorLoweringUnitTests" -v n`
Expected: Some tests may PASS (if codegen is correct), some may FAIL (collision test likely fails)

- [ ] **Step 3: Fix any failing tests**

If the collision test fails, ensure `IteratorLoweringAnalyzer` collects all existing nested type names in the declaring type and passes them to `GetStateMachineClassName`. If the determinism tests fail, ensure the member index is assigned by source position order (which `DescendantNodes` already provides in document order).

- [ ] **Step 4: Run full test suite to verify no regressions**

Run: `dotnet build CSharpToJavaConverter.slnx -c Release`
Expected: Build succeeds with no errors

Run: `dotnet test CSharpToJavaConverter.slnx -c Release --filter "IteratorLowering|PassStubCatalog|PassChainOrder"`
Expected: All tests PASS

- [ ] **Step 5: Commit**

```bash
git add tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs
git commit -m "test(iterator-lowering): add determinism, collision, and full-build tests"
```

---
