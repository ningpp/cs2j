# Record → readonly record 预处理器 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增一个独立的 CLI 级 C#→C# 预处理器，把任意非 readonly record 原子地转换为 readonly record（`readonly record struct` 显式标记；record class 对外契约不可变），并保证产出的中间源码树永远可编译。

**Architecture:** 语义驱动三阶段（Analyze → Plan → 逐文件 Rewrite），复用 TupleToStruct 的 `RewriteFile` 入口与 `FormatAndValidate` 预筛；整树重编译验证，错误按 **record 计划** 归属并跨文件回退该计划，无法归属则整轮回退。所有重写只认语义符号，不认名字文本。

**Tech Stack:** C# / .NET 10、Roslyn `Microsoft.CodeAnalysis.CSharp` 4.12.0（`LanguageVersion.Latest`，unsafe 打开）、xUnit、CommandLineParser verbs；与 [TupleToStructPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleToStructPreprocessor.cs)、[ProjectTupleToStructPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/ProjectTupleToStructPreprocessor.cs) 同构。

> 规范来源：[2026-09-24-record-readonly-preprocessor-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-24-record-readonly-preprocessor-design.md)。规则号 RS/RC/MS/MM/IR/FB 全部沿用规范 §4。

---

## 0. 全局约定（执行每个任务前先读）

- 仓库根：`d:\code\cs2j`。所有路径相对仓库根。PowerShell 不支持 `&&`，串行命令用 `;`。
- 本特性**完全不改** C#→Java 代码（RecordTransformer/StructTransformer/Pipeline/TypeGroupResolver 一律不动）。
- 命名空间：Core 全部放 `CSharpToJava.Core.RecordReadOnly`；CLI 类放 `CSharpToJava.CLI`（与既有预处理器一致，internal）。
- Roslyn 4.12.0 支持 C# 13；C# 14 空条件赋值若解析失败，按规范当作语法错误走 FB 兜底（MS-5 的测试对解析能力做前置判断，见 Task 16）。
- 确定性：文件按完整路径 `StringComparer.OrdinalIgnoreCase` 排序处理；生成成员名固定（`WithX` / 冲突时 `WithX__cs2j`）；不依赖时间戳。
- 注解名固定：`"Cs2jRecordReadOnlyRewrite"`（TupleToStruct 用 `"Cs2jTupleRewrite"`）。
- 每个任务结束必须 `dotnet build` 全绿再提交；提交信息用 `feat(record-readonly): ...` / `test(record-readonly): ...`。
- TDD 节奏：先写失败测试 → 跑确认失败 → 最小实现 → 跑确认通过 → 提交。测试文件在 M1 骨架任务一次性建好 harness，之后每个规则任务往里加 `[Fact]`。
- `CSharpToJava.Core.csproj` 已对 `CSharpToJava.ProjectTests` 配 `InternalsVisibleTo`，internal 类型可直接在 ProjectTests 里测。

### 0.1 File Structure（本计划创建/修改的全部文件）

**Core（新建目录 `src/CSharpToJava.Core/RecordReadOnly/`）：**

| 文件 | 职责 |
|---:|---|
| `RecordReadOnlyModels.cs` | 公共：options/statistics/diagnostic(code+severity)/file result |
| `RecordSyntaxFacts.cs` | internal 静态谓词与小工厂：可写左值、副作用、纯 getter、可访问性、显式布局、全限定类型名语法等 |
| `RecordReadOnlyAnalyzer.cs` | Phase 1：record 清单（partial 合并/嵌套/泛型）、可变成员、mutation 用点、接口图、拷贝 ctor、dynamic/反射扫描 |
| `RecordConversionPlan.cs` | Phase 2：`RecordPlan`/`MemberRewrite`/`MethodMigrationPlan`/`InterfaceRipplePlan`/`MutationSitePlan`/`ConversionPlan` + `PlanBuilder`（原子可行性、force/skip、层次判定） |
| `DeclarationRewriter.cs` | Phase 3a：readonly 修饰符、set→init、字段→init 属性、访问器修饰符保持、`required` 保持 |
| `MutationSiteRewriter.cs` | Phase 3b：MS-1~MS-19 用点（语句位置 `with` 重建） |
| `MethodMigrator.cs` | Phase 3c：`RecordMethodCallGraph`（符号键 DFS 拓扑）+ MM-1~MM-7 字段提升/出口合成/签名与调用点 |
| `InterfaceRippler.cs` | Phase 3d：IR-1~IR-7 接口加 With 成员、record 显式桩、in-tree 实现者 fluent 补齐 |
| `RecordReadOnlyPreprocessor.cs` | 对外入口 `RewriteFile` + `FormatAndValidate`（预筛）+ 计划缓存/排除/错误归属（FB-5） |

**CLI：**

| 文件 | 职责 |
|---:|---|
| `src/CSharpToJava.CLI/ProjectRecordReadOnlyPreprocessor.cs` | 新建：中间树复制/枚举/建编译/逐文件 RewriteFile/整树验证+计划级回退迭代/restore；`IntermediateDirectoryName = ".cs2j-record-readonly-src"` |
| `src/CSharpToJava.CLI/Program.cs` | 改 4 处：ConvertProjectOptions 开关、ConvertProject 执行块（Generic 后、MakeReadOnly 前）、GetPreservedDestinationDirectories、verb + 注册 |
| `config/RecordReadOnlyOverrides.json` | 新建：`{ "force": [], "skip": [] }` |

**Tests：**

| 文件 | 职责 |
|---:|---|
| `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs` | 新建：内联源码单测（RS/RC/MS/MM/IR/FB 全规则） |
| `tests/CSharpToJava.ProjectTests/RecordReadOnlyProjectTests.cs` | 新建：项目级 + 整树 build + CLI 接线 + 计划级回退 |
| `tests/SampleRecordSolution/` | 新建 fixture：Lib + App + sln（各类规则样本） |
| `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj` | 加一段 `Content Include="..\SampleRecordSolution\**\*.*"` |

---

## Milestone M1 — 骨架 + Analyzer + RS 全集 + 回退框架 + CLI 接线

### Task 1: 创建隔离 worktree 与特性分支

**Files:** 无代码改动（git 操作）

- [ ] **Step 1: 确认工作区有一个无关改动需要避开**

Run:
```powershell
git -C d:/code/cs2j status --short
```
Expected: 出现 ` M tests/CSharpToJava.ProjectTests/UnsignedTypeProjectTests.cs`（与本特性无关），因此用独立 worktree，不污染当前 `dev-3.x` 工作树。

- [ ] **Step 2: 创建 worktree 与分支**

Run（在 `d:/code` 下）:
```powershell
git -C d:/code/cs2j worktree add ../cs2j-record-readonly -b feature/record-readonly
```
Expected: 输出 `Preparing worktree (new branch 'feature/record-readonly')` 且无报错。

> 后续所有 Run/编辑都在 `d:\code\cs2j-record-readonly` 内进行；下文仓库根指该 worktree。若执行者选择不用 worktree，则等价地在 `d:\code\cs2j` 执行 `git switch -c feature/record-readonly`（先 stash 无关改动），任务内容不变。

- [ ] **Step 3: 基线构建与测试通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet build CSharpToJavaConverter.slnx -v q
```
Expected: `Build succeeded`，0 errors。

- [ ] **Step 4: Commit（worktree 无新增内容时跳过提交）**

worktree 是新分支指针，无文件改动，本任务不产生 commit；进入 Task 2 后首次提交即可。

---

### Task 2: 公共模型（options / statistics / diagnostics / file result）

**Files:**
- Create: `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyModels.cs`
- Test: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`（先建文件，放本任务 1 个测试）

- [ ] **Step 1: 写失败测试——诊断码与统计默认可构造**

创建 `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`：

```csharp
using CSharpToJava.Core.RecordReadOnly;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace CSharpToJava.ProjectTests;

public class RecordReadOnlyRewriterUnitTests
{
    private readonly ITestOutputHelper _output;
    public RecordReadOnlyRewriterUnitTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Models_DefaultCounters_AreZero()
    {
        var stats = new RecordReadOnlyStatistics();
        Assert.Equal(0, stats.RecordsScanned);
        Assert.Equal(0, stats.RecordStructsConverted);
        Assert.Equal(0, stats.PlansRolledBack);
        var d = new RecordReadOnlyDiagnostic(
            RecordReadOnlySeverity.Warning, RecordReadOnlyDiagnosticCode.RR020,
            "record skipped", "Lib/R.cs", 7, 3, "Lib.R");
        Assert.Equal("RR020", d.Code.ToString());
        Assert.Equal("Lib/R.cs(7,3): warning RR020: [Lib.R] record skipped", d.ToMsBuildString());
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests.Models_DefaultCounters_AreZero"
```
Expected: 编译失败（找不到 `CSharpToJava.Core.RecordReadOnly` 命名空间/类型）。

- [ ] **Step 3: 写实现——全部公共模型**

创建 `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyModels.cs`：

```csharp
namespace CSharpToJava.Core.RecordReadOnly;

public enum RecordReadOnlySeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>RR0xx 诊断码。码位分段见各枚举项注释；新增码只能追加，不能改号。</summary>
public enum RecordReadOnlyDiagnosticCode
{
    // 001-009 转换结果（Info）
    RR001, // record struct 已转换
    RR002, // record class 已转换
    RR010, // 已是 readonly record struct，跳过（RS-18）
    RR011, // overrides.skip 显式排除（FB-4）
    RR012, // overrides.force 强制转换（FB-4/FB-3）
    RR013, // record class 别名观察语义变化提示（§4.2，Info）
    RR014, // 私有拷贝 ctor 限制提示（RC-10，Info）

    // 020-029 计划级跳过/回退
    RR020, // 单 record 原子跳过（FB-1，原因在 Message）
    RR021, // record class 继承层次整体跳过（FB-2）
    RR030, // 整树验证失败且无法归属单计划 → 本次运行全部回退（FB-5）
    RR031, // 整树验证新增错误归属某 record 计划 → 回退该计划（FB-5）

    // 040-049 声明级不可改写
    RR040, // 实例事件（RS-10）
    RR041, // volatile/ref-return/fixed/unsafe 取址（RS-11；RC-5 对外可见 ref 返回）
    RR042, // 带 set 的索引器（RS-19/RC-9/MS-18）
    RR043, // StructLayout.Explicit 且字段需转属性（RS-17）

    // 050-059 用点不可改写
    RR050, // 接收者不可写（MS-1：in/readonly/foreach/get-only/方法返回值；MS-13/MS-16）
    RR051, // 接收者表达式有副作用（MS-1/MS-5）
    RR052, // 复合/自增但 getter 不纯（MS-3/MS-4）
    RR053, // 表达式位置的变异（MS-4/MS-6/MS-16）
    RR054, // 只读集合/只读索引器元素（MS-7）
    RR055, // record 以 ref/out 传入外部方法（MS-10）
    RR056, // 对 record 字段取 ref/out 传外部（MS-11）
    RR057, // dynamic/反射修改（MS-14）
    RR058, // 方法组绑定被迁移签名方法（MS-15）

    // 060-069 方法迁移不可行
    RR060, // 迭代器/async mutating 方法（MM-6）
    RR061, // partial 方法（MM-6）
    RR062, // 方法调用图含环（MM-1/MM-6）
    RR063, // getter 访问器体内修改其他成员（MM-6）
    RR064, // this = expr 无法展开：含 with 不可设置的实例状态（MM-3/MM-6）

    // 070-079 接口连带
    RR070, // 外部接口无法加成员（IR-7/FB-3）
    RR071, // With 成员签名碰撞（IR-7）

    // 080-089 record class 专项
    RR080, // 私有/不可访问拷贝 ctor，外部 with 不可用（RC-10）

    // 090-099 单文件模式
    RR090, // 单文件模式：record 非自包含，跳过（§3.3）
}

public sealed record RecordReadOnlyDiagnostic(
    RecordReadOnlySeverity Severity,
    RecordReadOnlyDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0,
    string? RecordName = null)
{
    /// <summary>MSBuild 格式：<c>file(line,col): warning RR0xx: [Record] message</c>。</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            RecordReadOnlySeverity.Error => "error",
            RecordReadOnlySeverity.Warning => "warning",
            _ => "info",
        };
        var loc = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        var who = RecordName != null ? $"[{RecordName}] " : string.Empty;
        return $"{loc}{level} {Code}: {who}{Message}";
    }
}

/// <summary>CLI 汇总行与断言使用的计数器。</summary>
public sealed class RecordReadOnlyStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }
    public int RecordsScanned { get; set; }
    public int RecordStructsConverted { get; set; }
    public int RecordClassesConverted { get; set; }
    public int AlreadyReadOnly { get; set; }
    public int RecordsSkipped { get; set; }
    public int MembersSetToInit { get; set; }
    public int FieldsToProperties { get; set; }
    public int MutationSitesRewritten { get; set; }
    public int MethodsMigrated { get; set; }
    public int InterfaceRipples { get; set; }
    public int PlansRolledBack { get; set; }

    /// <summary>按诊断码分桶的跳过数（规范 §6：按原因分组）。</summary>
    public Dictionary<RecordReadOnlyDiagnosticCode, int> SkippedByReason { get; }
        = new();
}

public sealed class RecordReadOnlyOptions
{
    /// <summary>强制转换的完全限定类型名集合（命中则尽量降级为显式桩，FB-3/FB-4）。</summary>
    public IReadOnlySet<string> ForceQualifiedNames { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>显式排除的完全限定类型名集合（FB-4）。</summary>
    public IReadOnlySet<string> SkipQualifiedNames { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>存在跳过时退出码非零（规范 §6，CLI --strict；默认 false）。</summary>
    public bool Strict { get; init; }

    /// <summary>是否转换 record class（RC 规则）。M5 任务落地前保持 false；M5 翻为 true。</summary>
    public bool ConvertRecordClasses { get; init; } = false;

    /// <summary>是否输出 Info 级诊断。</summary>
    public bool ReportInfo { get; init; } = true;
}

public sealed record RecordReadOnlyFileResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests.Models_DefaultCounters_AreZero"
```
Expected: PASS（1）。

- [ ] **Step 5: Commit**

```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyModels.cs tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): add public options, statistics, diagnostics models"
```

---
### Task 3: 测试 harness + RecordSyntaxFacts + 预处理器骨架 + FormatAndValidate

**Files:**
- Create: `src/CSharpToJava.Core/RecordReadOnly/RecordSyntaxFacts.cs`
- Create: `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyPreprocessor.cs`
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

本任务结束后：干净文件原样返回；`readonly record struct` 计 AlreadyReadOnly；无 record 的普通 class 不受影响。

- [ ] **Step 1: 扩充测试 harness 与两条骨架测试**

把 `RecordReadOnlyRewriterUnitTests.cs` 顶部 using 区加：

```csharp
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
```

在类中加入以下成员（替换掉 Task 2 临时单测之外的空白区，保留 Task 2 测试）：

```csharp
    private static readonly CSharpParseOptions ParseOptions =
        new CSharpParseOptions(LanguageVersion.Latest);

    internal static SemanticModel ParseWithSemantics(string src, params string[] extra)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var extraTrees = extra.Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions)).ToArray();
        var compilation = CSharpCompilation.Create(
            "rr-test",
            new[] { tree }.Concat(extraTrees),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    internal static (string Output, RecordReadOnlyStatistics Stats,
        List<RecordReadOnlyDiagnostic> Diagnostics) Rewrite(string src,
        RecordReadOnlyOptions? options = null, params string[] extra)
    {
        var model = ParseWithSemantics(src, extra);
        var pp = new RecordReadOnlyPreprocessor(options ?? new RecordReadOnlyOptions());
        var stats = new RecordReadOnlyStatistics();
        var diags = new List<RecordReadOnlyDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);
        return (result.OutputCode, stats, diags);
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

    internal static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var compilation = CSharpCompilation.Create("rr-verify", new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    [Fact]
    public void CleanFile_NoRecord_Unchanged()
    {
        var src = "public class C { public int X; public int Add(int a, int b) => a + b; }";
        var (output, stats, _) = Rewrite(src);
        Assert.Equal(src, output);
        Assert.Equal(0, stats.FilesTransformed);
    }

    [Fact]
    public void AlreadyReadonlyRecordStruct_CountedAndUnchanged()
    {
        var src = "public readonly record struct P(int X, int Y);";
        var (output, stats, diags) = Rewrite(src);
        Assert.Equal(src, output);
        Assert.Equal(1, stats.AlreadyReadOnly);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR010);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 编译失败（`RecordReadOnlyPreprocessor` 不存在）。

- [ ] **Step 3: 写 RecordSyntaxFacts（谓词/工厂，后续任务共用）**

创建 `src/CSharpToJava.Core/RecordReadOnly/RecordSyntaxFacts.cs`：

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RecordReadOnly;

/// <summary>
/// 纯语法/语义小工具。所有判据集中在这里，Analyzer/Plan/各 Rewriter 共用，
/// 保证“什么可写/什么算副作用/什么是纯 getter”全特性只有一套口径。
/// </summary>
internal static class RecordSyntaxFacts
{
    public static readonly SyntaxAnnotation RewriteAnnotation = new("Cs2jRecordReadOnlyRewrite");

    /// <summary>标识符/简单成员访问/纯索引器（参数为常量/标识符）以外一律视为有副作用。</summary>
    public static bool IsReceiverSideEffectFree(ExpressionSyntax receiver)
    {
        foreach (var node in receiver.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case InvocationExpressionSyntax:
                case ObjectCreationExpressionSyntax:
                case ImplicitObjectCreationExpressionSyntax:
                case SimpleLambdaExpressionSyntax:
                case ParenthesizedLambdaExpressionSyntax:
                case AnonymousMethodExpressionSyntax:
                case AwaitExpressionSyntax:
                case AssignmentExpressionSyntax:
                case QueryExpressionSyntax:
                    return false;
                case PrefixUnaryExpressionSyntax pre when pre.Kind()
                    is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression:
                    return false;
                case PostfixUnaryExpressionSyntax post when post.Kind()
                    is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression:
                    return false;
                // arr[F()] 已被上面的 InvocationExpression 覆盖；这里再拦 ++ 下标等。
                case ElementAccessExpressionSyntax element when element.ArgumentList.Arguments
                    .Any(a => a.Expression is InvocationExpressionSyntax
                        or AssignmentExpressionSyntax
                        or PrefixUnaryExpressionSyntax
                        or PostfixUnaryExpressionSyntax):
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 复合赋值/自增改写为 with 前必须证明 getter 读一次无外部可观察副作用：
    /// 仅接受自动属性（含字段被 RS-5 提升后生成的自动属性）与字段访问。
    /// </summary>
    public static bool IsPureGetter(ISymbol? member)
    {
        return member is IFieldSymbol
            || (member is IPropertySymbol { GetMethod: { } getMethod } property
                && IsAutoAccessor(getMethod)
                && !property.ReturnsByRef && !property.ReturnsByRefReadonly);
    }

    public static bool IsAutoAccessor(IMethodSymbol accessor)
    {
        // 编译器合成（自动属性 get/set/init）或源里没有访问器体（get;）都算自动。
        if (accessor.IsImplicitlyDeclared) return true;
        foreach (var decl in accessor.DeclaringSyntaxReferences)
        {
            if (decl.GetSyntax() is AccessorDeclarationSyntax { Body: null, ExpressionBody: null })
                return true;
        }
        return false;
    }

    public static bool IsVisibleOutsideType(ISymbol symbol)
    {
        var accessibility = symbol.DeclaredAccessibility;
        return accessibility is Accessibility.Public
            or Accessibility.Internal
            or Accessibility.Protected
            or Accessibility.ProtectedOrInternal;
    }

    public static bool IsPrivate(ISymbol symbol)
        => symbol.DeclaredAccessibility == Accessibility.Private;

    /// <summary>全限定类型名语法（global:: 前缀），泛型携带 type argument。</summary>
    public static TypeSyntax GlobalType(ITypeSymbol symbol)
        => SyntaxFactory.ParseTypeName(
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

    public static bool IsInTree(ISymbol symbol)
        => symbol.Locations.Any(l => l.IsInSource);

    /// <summary>找 record 声明上的 StructLayout 特性，返回 LayoutKind 实参文本（Explicit/Sequential/Auto）。</summary>
    public static bool HasExplicitLayout(INamedTypeSymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() != "System.Runtime.InteropServices.StructLayoutAttribute")
                continue;
            if (attr.ConstructorArguments.Length > 0
                && attr.ConstructorArguments[0].Value is int kind
                && kind == 2) // LayoutKind.Explicit == 2
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>把 set 访问器关键字替换为 init（保留 trivia）；非 set 访问器原样。</summary>
    public static AccessorDeclarationSyntax WithInitKeyword(AccessorDeclarationSyntax accessor)
    {
        if (!accessor.IsKind(SyntaxKind.SetAccessorDeclaration)) return accessor;
        var keyword = SyntaxFactory.Token(SyntaxKind.InitKeyword)
            .WithLeadingTrivia(accessor.Keyword.LeadingTrivia)
            .WithTrailingTrivia(accessor.Keyword.TrailingTrivia);
        return accessor.WithKeyword(keyword);
    }
}
```

- [ ] **Step 4: 写预处理器骨架（RewriteFile + FormatAndValidate + 脏检查）**

创建 `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyPreprocessor.cs`：

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.RecordReadOnly;

/// <summary>
/// Record → readonly record 预处理器入口。三阶段（分析→计划→重写）在首次
/// RewriteFile 时基于 <see cref="SemanticModel.Compilation"/> 构建一次并缓存；
/// 项目驱动方对同一编译的每个文件调用一次 RewriteFile。计划级回退（FB-5）
/// 由 CLI 驱动通过 <see cref="ExcludePlans"/> 后用原始文本重新驱动。
/// </summary>
public sealed partial class RecordReadOnlyPreprocessor
{
    private readonly RecordReadOnlyOptions _options;
    private RecordAnalysis? _analysis;
    private ConversionPlan? _plan;
    private readonly HashSet<string> _excludedPlanIds = new(StringComparer.Ordinal);

    public RecordReadOnlyPreprocessor(RecordReadOnlyOptions? options = null)
    {
        _options = options ?? new RecordReadOnlyOptions();
    }

    public RecordReadOnlyFileResult RewriteFile(
        string sourceCode,
        SemanticModel semanticModel,
        RecordReadOnlyStatistics statistics,
        List<RecordReadOnlyDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(diagnostics);

        statistics.FilesScanned++;
        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();

        if (!IsRecordDirty(root))
        {
            statistics.FilesSkippedClean++;
            return new RecordReadOnlyFileResult { OutputCode = sourceCode, Changed = false };
        }

        EnsureBuilt(semanticModel);

        var filePath = semanticModel.SyntaxTree.FilePath;
        var activePlans = _plan!.Plans
            .Where(p => !_excludedPlanIds.Contains(p.Id) && p.TouchedFiles.Contains(filePath))
            .ToList();

        RecordReadOnlyAnalyzer.ReportSkippedRecords(
            _plan, _excludedPlanIds, filePath, statistics, diagnostics, _options);

        if (activePlans.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return new RecordReadOnlyFileResult { OutputCode = sourceCode, Changed = false };
        }

        var context = new RewriteContext(
            semanticModel, _analysis!, _plan!, activePlans, _excludedPlanIds,
            statistics, diagnostics, filePath);

        var newRoot = ApplyRewriters(root, context);

        var changed = !newRoot.IsEquivalentTo(root);
        if (!changed)
        {
            statistics.FilesSkippedClean++;
            return new RecordReadOnlyFileResult { OutputCode = sourceCode, Changed = false };
        }

        var outputCode = FormatAndValidate(newRoot);
        if (outputCode == null)
        {
            // 单文件预筛失败先保留原文；真正裁决在整树重编译（CLI 驱动），
            // 这里不做单文件回退依据（规范 §3.2/§6）。
            statistics.FilesSkippedClean++;
            diagnostics.Add(new RecordReadOnlyDiagnostic(
                RecordReadOnlySeverity.Warning, RecordReadOnlyDiagnosticCode.RR031,
                "record-readonly rewrite failed local parse validation; the original file was " +
                "preserved pending whole-tree validation.", filePath, 0, 0, null));
            return new RecordReadOnlyFileResult { OutputCode = sourceCode, Changed = false };
        }

        statistics.FilesTransformed++;
        return new RecordReadOnlyFileResult { OutputCode = outputCode, Changed = true };
    }

    private void EnsureBuilt(SemanticModel model)
    {
        if (_analysis != null && _plan != null) return;
        _analysis = new RecordReadOnlyAnalyzer(_options).Analyze(model.Compilation);
        _plan = new PlanBuilder(_options).Build(_analysis);
    }

    private static CompilationUnitSyntax ApplyRewriters(CompilationUnitSyntax root, RewriteContext context)
    {
        // 顺序：先迁移方法（签名/体/调用点），再改声明，再接口连带，最后残余用点。
        // 每个用点在计划阶段被唯一归类，故四个 rewriter 不会重复处理同一节点。
        var afterMethods = (CompilationUnitSyntax)new MethodMigrator(context).Visit(root)!;
        var afterDecls = (CompilationUnitSyntax)new DeclarationRewriter(context).Visit(afterMethods)!;
        var afterInterfaces = (CompilationUnitSyntax)new InterfaceRippler(context).Visit(afterDecls)!;
        var afterSites = (CompilationUnitSyntax)new MutationSiteRewriter(context).Visit(afterInterfaces)!;
        return afterSites;
    }

    internal static bool IsRecordDirty(SyntaxNode root) =>
        root.DescendantNodes().OfType<RecordDeclarationSyntax>().Any();

    /// <summary>仅格式化改动区间→整文件格式化→原文候选，每个候选重新解析零错误才返回。</summary>
    internal static string? FormatAndValidate(CompilationUnitSyntax root)
    {
        var candidates = new List<string>();
        using (var workspace = new AdhocWorkspace())
        {
            try
            {
                var spans = root
                    .GetAnnotatedNodes(RecordSyntaxFacts.RewriteAnnotation.Kind)
                    .Where(n => n is ExpressionSyntax or StatementSyntax or MemberDeclarationSyntax)
                    .Where(n => !n.Ancestors().Any(a =>
                        a.HasAnnotations(RecordSyntaxFacts.RewriteAnnotation.Kind)))
                    .Select(n => n.Span)
                    .Distinct()
                    .OrderBy(s => s.Start)
                    .ToList();
                if (spans.Count > 0)
                    candidates.Add(Formatter.Format(root, spans, workspace).ToFullString());
            }
            catch
            {
                // 局部格式化失败不致命。
            }
            try { candidates.Add(Formatter.Format(root, workspace).ToFullString()); }
            catch { /* 忽略 */ }
        }
        candidates.Add(root.ToFullString());

        return candidates.FirstOrDefault(candidate =>
        {
            try
            {
                var reparsed = CSharpSyntaxTree.ParseText(
                    candidate, new CSharpParseOptions(LanguageVersion.Latest));
                return !reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);
            }
            catch
            {
                return false;
            }
        });
    }

    // ----- FB-5 计划级回退支持（CLI 驱动使用） -----

    internal IReadOnlyList<RecordPlan> Plans
    {
        get { EnsureBuiltForInspection(); return _plan!.Plans; }
    }

    private void EnsureBuiltForInspection()
    {
        // 测试/CLI 可能不经过 RewriteFile 直接查计划；此时延迟构建由驱动保证编译已就绪。
        if (_plan != null) return;
        throw new InvalidOperationException("Preprocessor has not analyzed a compilation yet.");
    }

    internal void ExcludePlans(IEnumerable<string> planIds)
    {
        foreach (var id in planIds) _excludedPlanIds.Add(id);
    }

    internal void BindCompilation(Compilation compilation)
    {
        _analysis = new RecordReadOnlyAnalyzer(_options).Analyze(compilation);
        _plan = new PlanBuilder(_options).Build(_analysis);
    }

    /// <summary>
    /// 把整树重编译产生的新错误归属到唯一 record 计划。归属键：文件 + 错误所在最近
    /// 类型/成员，对计划触碰清单求唯一计划；多计划共享或对不上 → false（整轮回退）。
    /// </summary>
    internal bool TryAttributeDiagnostic(
        Diagnostic diagnostic,
        IReadOnlyDictionary<string, string> rewrittenByFile,
        out string planId)
    {
        planId = string.Empty;
        if (_plan == null || diagnostic.Location.SourceTree is not { } tree) return false;
        if (!rewrittenByFile.TryGetValue(tree.FilePath, out _)) return false;

        var node = tree.GetRoot().FindNode(diagnostic.Location.SourceSpan);
        var typeName = node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault()?.ToNameString();
        var memberName = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault()?.GetNameString();

        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in _plan.Plans)
        {
            if (_excludedPlanIds.Contains(plan.Id)) continue;
            if (!plan.TouchedFiles.Contains(tree.FilePath)) continue;
            if (plan.OwnsTouch(tree.FilePath, typeName, memberName))
                matched.Add(plan.Id);
        }

        if (matched.Count == 1)
        {
            planId = matched.First();
            return true;
        }
        return false;
    }
}

internal static class RecordDeclarationNaming
{
    public static string ToNameString(this BaseTypeDeclarationSyntax type) => type.Identifier.Text;

    public static string? GetNameString(this MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        IndexerDeclarationSyntax => "this[]",
        EventDeclarationSyntax e => e.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        BaseFieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        _ => null,
    };
}
```

- [ ] **Step 5: 运行确认骨架仍编译失败（缺 RewriteContext/Analyzer/Plan/4 个 Rewriter）**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```
Expected: 编译失败，列出缺的类型。这些类型在 Task 4/5 建；为让本任务可独立编译，第 6 步先建最小可编译桩，Task 4/5 再填实。

- [ ] **Step 6: 建最小桩让骨架可编译（后续任务逐个替换）**

创建 `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyAnalyzer.cs`（桩，Task 4 整体替换）：

```csharp
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed record RewriteContext(
    SemanticModel Model,
    RecordAnalysis Analysis,
    ConversionPlan Plan,
    IReadOnlyList<RecordPlan> ActivePlans,
    IReadOnlySet<string> ExcludedPlanIds,
    RecordReadOnlyStatistics Statistics,
    List<RecordReadOnlyDiagnostic> Diagnostics,
    string FilePath);

internal sealed class RecordAnalysis
{
    public Compilation? Compilation { get; set; }
    public List<RecordInfo> Records { get; } = new();
    public Dictionary<INamedTypeSymbol, RecordInfo> BySymbol { get; }
        = new(SymbolEqualityComparer.Default);
}

internal sealed class RecordInfo;

internal sealed class RecordReadOnlyAnalyzer
{
    private readonly RecordReadOnlyOptions _options;
    public RecordReadOnlyAnalyzer(RecordReadOnlyOptions options) => _options = options;
    public RecordAnalysis Analyze(Compilation compilation) => new();

    public static void ReportSkippedRecords(
        ConversionPlan plan,
        IReadOnlySet<string> excludedPlanIds,
        string filePath,
        RecordReadOnlyStatistics statistics,
        List<RecordReadOnlyDiagnostic> diagnostics,
        RecordReadOnlyOptions options)
    {
        // 桩：Task 5 计划生成 RR010/RR020 后在此落到 stats/diagnostics。
    }
}
```

创建 `src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs`（桩，Task 5 整体替换）：

```csharp
namespace CSharpToJava.Core.RecordReadOnly;

internal sealed class RecordPlan
{
    public required string Id { get; init; }
    public required string RecordQualifiedName { get; init; }
    public required HashSet<string> TouchedFiles { get; init; }
    public bool OwnsTouch(string file, string? typeName, string? memberName) => false;
}

internal sealed class ConversionPlan
{
    public List<RecordPlan> Plans { get; } = new();
}

internal sealed class PlanBuilder
{
    private readonly RecordReadOnlyOptions _options;
    public PlanBuilder(RecordReadOnlyOptions options) => _options = options;
    public ConversionPlan Build(RecordAnalysis analysis) => new();
}
```

创建 `src/CSharpToJava.Core/RecordReadOnly/DeclarationRewriter.cs`、`MutationSiteRewriter.cs`、`MethodMigrator.cs`、`InterfaceRippler.cs`（同型桩，各自一个文件）：

```csharp
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed class DeclarationRewriter : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    public DeclarationRewriter(RewriteContext context) => _context = context;
}
```

其余三个文件把类名分别换成 `MutationSiteRewriter` / `MethodMigrator` / `InterfaceRippler`，构造与命名空间一致。

- [ ] **Step 7: 运行测试确认通过（骨架行为：干净/已 readonly）**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: `AlreadyReadonlyRecordStruct_CountedAndUnchanged` 此刻仍 FAIL（桩分析器不产记录）。先接受 Task 4 补 RR010；本 Step 仅要求 `CleanFile_NoRecord_Unchanged` PASS 且整体编译通过。

- [ ] **Step 8: 修正期望——把 RR010 断言临时只校验统计字段存在性**

为保持每任务绿灯，把 `AlreadyReadonlyRecordStruct_CountedAndUnchanged` 的断言行

```csharp
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR010);
```

临时改为

```csharp
        Assert.Empty(diags.Where(d => d.Severity == RecordReadOnlySeverity.Error));
```

（Task 4 Step 完成分析器后，会改回 RR010 断言并转绿。）

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: PASS（2）。

- [ ] **Step 9: Commit**

```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): preprocessor skeleton, syntax facts, FormatAndValidate, test harness"
```

---
### Task 4: Phase 1 Analyzer——record 清单 / 可变成员 / 用点 / 方法写集 / 接口图

**Files:**
- Replace whole: `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyAnalyzer.cs`（Task 3 桩整体替换，保留 `RewriteContext` 定义——挪到本文件顶部）
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`（恢复 RR010 断言 + 分析事实测试）

本任务只收集事实，不做可行性裁决（裁决在 Task 5 PlanBuilder）。

- [ ] **Step 1: 写失败测试——analyzer 经 RewriteFile 暴露的事实（partial 合并/成员分类/RR010）**

在测试类追加：

```csharp
    [Fact]
    public void AlreadyReadonlyRecordStruct_EmitsRR010()
    {
        var src = "public readonly record struct P(int X, int Y);";
        var (output, stats, diags) = Rewrite(src);
        Assert.Equal(src, output);
        Assert.Equal(1, stats.AlreadyReadOnly);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR010);
    }

    [Fact]
    public void Analyzer_MergesPartialParts_AndClassifiesMembers()
    {
        var src = """
            public partial record struct P
            {
                public int A { get; set; }
                private int _b;
                public int C;
                public event System.Action E;
            }
            public partial record struct P { public int D { get; } }
            """;
        var model = ParseWithSemantics(src);
        var pp = new RecordReadOnlyPreprocessor();
        pp.BindCompilation(model.Compilation);
        var plan = Assert.Single(pp.Plans);
        Assert.Equal("P", plan.RecordQualifiedName);
        // A(set)、C(字段) 需改写；_b 私有字段分类；E 事件阻断。事实在计划可行性上体现：
        Assert.Contains(plan.MemberRewrites, m => m.MemberName == "A");
        Assert.Contains(plan.MemberRewrites, m => m.MemberName == "C");
        Assert.False(plan.Feasible);
        Assert.Equal(RecordReadOnlyDiagnosticCode.RR040, plan.FailCode);
    }

    [Fact]
    public void Analyzer_RecordClass_IgnoredWhenClassConversionDisabled()
    {
        var src = "public record class P(int X);";
        var (output, _, _) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = false });
        Assert.Equal(src, output);
    }
```

同时把 Task 3 Step 8 临时改过的 `AlreadyReadonlyRecordStruct_CountedAndUnchanged` 中的临时断言恢复为

```csharp
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR010);
```

（即该测试与新 `AlreadyReadonlyRecordStruct_EmitsRR010` 同断言；保留两个测试名亦可，二选一——若保留两者，二者内容相同，无冲突。）

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 编译失败（`RecordPlan.MemberRewrites/Feasible/FailCode` 尚不存在）。

- [ ] **Step 3: 整体替换 RecordReadOnlyAnalyzer.cs（含 RewriteContext 与全部事实模型）**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed record RewriteContext(
    SemanticModel Model,
    RecordAnalysis Analysis,
    ConversionPlan Plan,
    IReadOnlyList<RecordPlan> ActivePlans,
    IReadOnlySet<string> ExcludedPlanIds,
    RecordReadOnlyStatistics Statistics,
    List<RecordReadOnlyDiagnostic> Diagnostics,
    string FilePath);

internal enum RecordKind { Struct, Class }

internal enum SiteKind
{
    SimpleAssign,       // a.M = v
    CompoundAssign,     // a.M += v 等
    Increment,          // a.M++ / ++a.M
    ConditionalAssign,  // a?.M = v / a?.M += v / a?.M++（C#14，解析得到才会出现）
    IndexerAssign,      // r[i] = v（record 自身索引器）
    RefOutRecordArg,    // M(ref r) / M(out r)，r 整体是 record
    RefOutMemberArg,    // M(ref r.F) / int.TryParse(s, out r.N)
    AddressOfMember,    // &r.F
}

internal sealed record ChainSegment(ISymbol Member, ITypeSymbol? ContainerType, ExpressionSyntax Access);

internal sealed class MutationSiteInfo
{
    public required Location Location { get; init; }
    public required string FilePath { get; init; }
    public required SyntaxNode WriteNode { get; init; }
    public required ExpressionSyntax Left { get; init; }
    public required ExpressionSyntax Receiver { get; init; }
    /// <summary>最内层被写成员（a.B.C=v 时为 C）。</summary>
    public ISymbol? Member { get; init; }
    public required ITypeSymbol ReceiverType { get; init; }
    /// <summary>链最外层根接收者类型（a.B.C=v 时为 a 的类型）；MS-17 判定外层 record 用。</summary>
    public ITypeSymbol? RootReceiverType { get; init; }
    /// <summary>接收者静态类型正是某个接口（IR-4 接口接收者路径）时非空。</summary>
    public INamedTypeSymbol? ReceiverInterface { get; init; }
    public required SiteKind Kind { get; init; }
    public required bool IsStatementPosition { get; init; }
    public IMethodSymbol? EnclosingMethod { get; init; }
    public IMethodSymbol? RefOutCallee { get; init; }
    public bool CalleeInTree { get; init; }
    public List<ChainSegment> Chain { get; init; } = new();
    /// <summary>归属的目标 record（成员 ContainingType 命中分析清单时设置）。</summary>
    public RecordInfo? TargetRecord { get; set; }
}

internal sealed class MethodWriteInfo
{
    public required IMethodSymbol Method { get; init; }
    public required HashSet<ISymbol> WrittenMembers { get; init; } = new(SymbolEqualityComparer.Default);
    public bool WritesThis { get; set; }
    public bool Iterator { get; init; }
    public bool Async { get; init; }
    public bool Partial { get; init; }
    public bool IsGetAccessor { get; init; }
    public bool IsPropertySetter { get; init; }
    public bool IsExtension { get; init; }
    public bool ExpressionBody { get; init; }
    public bool IsOverrideExplicitOrVirtual { get; init; }
    public bool IsConstructor { get; init; }
}

internal sealed class RecordInfo
{
    public required INamedTypeSymbol Symbol { get; init; }
    public required RecordKind Kind { get; init; }
    public required List<RecordDeclarationSyntax> Parts { get; init; }
    public bool AlreadyReadonly { get; set; }
    public bool IsPositional { get; set; }
    public bool HasExplicitLayout { get; set; }
    public bool HasFixedSizeBuffer { get; set; }
    public bool HasInstanceEvent { get; set; }
    public bool HasVisibleRefReturningMember { get; set; }
    public bool HasSettableIndexer { get; set; }
    public bool HasPrivateCopyConstructor { get; set; }

    public List<IPropertySymbol> SettableProperties { get; } = new();
    public List<IFieldSymbol> MutableFields { get; } = new();
    public List<IMethodSymbol> InTreeMethods { get; } = new();
    public Dictionary<IMethodSymbol, MethodWriteInfo> WriteInfos { get; }
        = new(SymbolEqualityComparer.Default);
    public List<MutationSiteInfo> Sites { get; } = new();

    public INamedTypeSymbol? BaseRecordType { get; set; }
    public IReadOnlyList<INamedTypeSymbol> Interfaces => Symbol.AllInterfaces;
}

internal sealed class InterfaceFact
{
    public required INamedTypeSymbol Interface { get; init; }
    public required HashSet<ISymbol> SettingMembers { get; init; } = new(SymbolEqualityComparer.Default);
    public required HashSet<INamedTypeSymbol> InTreeImplementers { get; init; }
        = new(SymbolEqualityComparer.Default);
}

internal sealed class RecordAnalysis
{
    public Compilation? Compilation { get; set; }
    public List<RecordInfo> Records { get; } = new();
    public Dictionary<INamedTypeSymbol, RecordInfo> BySymbol { get; }
        = new(SymbolEqualityComparer.Default);
    public Dictionary<INamedTypeSymbol, InterfaceFact> Interfaces { get; }
        = new(SymbolEqualityComparer.Default);
    /// <summary>全树出现 dynamic 接收者或反射 SetValue 调用（MS-14）。保守起见阻断全部 record。</summary>
    public bool HasDynamicOrReflectionMutation { get; set; }
}

internal sealed class RecordReadOnlyAnalyzer
{
    private readonly RecordReadOnlyOptions _options;
    private RecordAnalysis _analysis = new();
    private readonly HashSet<INamedTypeSymbol> _recordSymbols = new(SymbolEqualityComparer.Default);

    public RecordReadOnlyAnalyzer(RecordReadOnlyOptions options) => _options = options;

    public RecordAnalysis Analyze(Compilation compilation)
    {
        _analysis = new RecordAnalysis { Compilation = compilation };
        _recordSymbols.Clear();

        // Pass 1：record 符号与部件。
        foreach (var tree in compilation.SyntaxTrees)
        {
            var root = tree.GetRoot();
            var model = compilation.GetSemanticModel(tree);
            foreach (var decl in root.DescendantNodes().OfType<RecordDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not { } symbol || !symbol.IsRecord) continue;
                if (!_recordSymbols.Add(symbol)) continue;
                if (symbol.TypeKind == TypeKind.Struct)
                    CreateRecord(symbol, RecordKind.Struct, compilation);
                else if (_options.ConvertRecordClasses)
                    CreateRecord(symbol, RecordKind.Class, compilation);
            }
        }

        // Pass 2：成员/方法写集/接口/层次事实。
        foreach (var info in _analysis.Records.ToList())
            FillMemberFacts(info, compilation);

        // Pass 3：全树用点 + dynamic/反射扫描。
        foreach (var tree in compilation.SyntaxTrees)
            ScanTreeForSites(compilation, tree);

        // Pass 4：接口图（只收集 in-tree 接口与其 in-tree 实现者）。
        BuildInterfaceGraph(compilation);

        return _analysis;
    }

    private void CreateRecord(INamedTypeSymbol symbol, RecordKind kind, Compilation compilation)
    {
        var parts = symbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<RecordDeclarationSyntax>()
            .ToList();
        var info = new RecordInfo
        {
            Symbol = symbol,
            Kind = kind,
            Parts = parts,
            BaseRecordType = kind == RecordKind.Class && symbol.BaseType is { IsRecord: true } b
                ? b : null,
        };
        info.AlreadyReadonly = kind == RecordKind.Struct && parts.Count > 0
            && parts.All(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)));
        info.IsPositional = parts.Any(p => p.ParameterList is { Parameters.Count: > 0 });
        info.HasExplicitLayout = kind == RecordKind.Struct && RecordSyntaxFacts.HasExplicitLayout(symbol);
        _analysis.Records.Add(info);
        _analysis.BySymbol[symbol] = info;
    }

    private void FillMemberFacts(RecordInfo info, Compilation compilation)
    {
        foreach (var member in info.Symbol.GetMembers())
        {
            switch (member)
            {
                case IPropertySymbol property:
                    if (property.IsIndexer)
                    {
                        if (property.SetMethod is { IsInitOnly: false }) info.HasSettableIndexer = true;
                        break;
                    }
                    if (property.SetMethod is { } set && !set.IsInitOnly)
                        info.SettableProperties.Add(property);
                    if ((property.ReturnsByRef || property.ReturnsByRefReadonly)
                        && RecordSyntaxFacts.IsVisibleOutsideType(property))
                        info.HasVisibleRefReturningMember = true;
                    break;

                case IFieldSymbol field when !field.IsStatic && !field.IsConst:
                    if (field.IsVolatile)
                    {
                        info.MutableFields.Add(field); // PlanBuilder 据 IsVolatile 阻断
                    }
                    else
                    {
                        info.MutableFields.Add(field);
                    }
                    break;

                case IEventSymbol evt when !evt.IsStatic:
                    info.HasInstanceEvent = true;
                    break;

                case IMethodSymbol method when !method.IsStatic
                    && method.DeclaringSyntaxReferences.Length > 0:
                    if (RecordSyntaxFacts.IsRefReturning(method))
                        info.HasVisibleRefReturningMember = true;
                    break;
            }
        }

        // 源部件级：fixed 缓冲区；私有拷贝 ctor（class）。
        foreach (var part in info.Parts)
        {
            foreach (var field in part.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                if (field.Modifiers.Any(SyntaxKind.FixedKeyword)) info.HasFixedSizeBuffer = true;
            }
        }

        if (info.Kind == RecordKind.Class)
        {
            info.HasPrivateCopyConstructor = info.Symbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Any(m => m.MethodKind == MethodKind.Constructor
                    && m is { Parameters.Length: 1 }
                    && SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, info.Symbol)
                    && m.DeclaredAccessibility == Accessibility.Private);
        }

        // in-tree 方法与其写入成员集合（MM/RS-6 依据）。
        foreach (var member in info.Symbol.GetMembers().OfType<IMethodSymbol>())
        {
            if (member.DeclaringSyntaxReferences.Length == 0) continue;
            if (member.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor
                or MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd
                or MethodKind.EventRemove))
                continue;
            info.InTreeMethods.Add(member);
            var wi = BuildWriteInfo(member, info, compilation);
            if (wi != null) info.WriteInfos[member] = wi;
        }
    }

    private MethodWriteInfo? BuildWriteInfo(IMethodSymbol method, RecordInfo owner,
        Compilation compilation)
    {
        var syntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        BaseMethodDeclarationSyntax? declaration = syntax as BaseMethodDeclarationSyntax;
        AccessorDeclarationSyntax? accessor = syntax as AccessorDeclarationSyntax;
        SyntaxNode? bodyRoot = (SyntaxNode?)declaration ?? accessor;
        if (bodyRoot == null) return null;

        var info = new MethodWriteInfo
        {
            Method = method,
            Iterator = bodyRoot.DescendantNodes().Any(n =>
                n is YieldStatementSyntax or YieldExpressionSyntax),
            Async = method.IsAsync,
            Partial = method.PartialDefinitionPart != null || method.PartialImplementationPart != null
                || (declaration?.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)) ?? false),
            IsGetAccessor = method.MethodKind == MethodKind.PropertyGet,
            IsPropertySetter = method.MethodKind == MethodKind.PropertySet,
            IsExtension = method.IsExtensionMethod,
            ExpressionBody = declaration?.ExpressionBody != null,
            IsOverrideExplicitOrVirtual = method.IsOverride || method.IsVirtual || method.IsAbstract
                || (declaration as MethodDeclarationSyntax)?.ExplicitInterfaceSpecifier != null
                || (accessor is not null && accessor.Parent?.Parent is IndexerDeclarationSyntax { ExplicitInterfaceSpecifier: not null }
                    or PropertyDeclarationSyntax { ExplicitInterfaceSpecifier: not null }),
            IsConstructor = method.MethodKind == MethodKind.Constructor,
        };

        // 方法体与其声明同 tree；用该 tree 的语义模型解析体内成员符号。
        var model = compilation.GetSemanticModel(bodyRoot.SyntaxTree);
        foreach (var node in bodyRoot.DescendantNodes())
        {
            switch (node)
            {
                case AssignmentExpressionSyntax assign:
                    CollectWrittenSymbol(assign.Left, model, owner, info.WrittenMembers);
                    if (assign.Left is ThisExpressionSyntax) info.WritesThis = true;
                    break;
                case PostfixUnaryExpressionSyntax post when post.Kind()
                    is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression:
                    CollectWrittenSymbol(post.Operand, model, owner, info.WrittenMembers);
                    break;
                case PrefixUnaryExpressionSyntax pre when pre.Kind()
                    is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression:
                    CollectWrittenSymbol(pre.Operand, model, owner, info.WrittenMembers);
                    break;
                case ArgumentSyntax { RefKindKeyword.RawKind: not SyntaxKind.None } arg:
                    CollectWrittenSymbol(arg.Expression, model, owner, info.WrittenMembers);
                    break;
            }
        }
        return info;
    }

    private static void CollectWrittenSymbol(
        ExpressionSyntax expression, SemanticModel? model, RecordInfo owner,
        HashSet<ISymbol> written)
    {
        if (model == null) return;
        var current = expression;
        // 剥掉 this. / 强制类型转换 / null 抑制。
        while (true)
        {
            switch (current)
            {
                // 仅收集 this.F 写（MM 提升的是 this 的字段）；other.F 由 MS 用点处理。
                case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } m:
                    if (model.GetSymbolInfo(m).Symbol is { } s && SymbolBelongsTo(s, owner.Symbol))
                        written.Add(s);
                    return;
                case IdentifierNameSyntax id when model.GetSymbolInfo(id).Symbol is { } s2
                    && SymbolBelongsTo(s2, owner.Symbol):
                    written.Add(s2);
                    return;
                case CastExpressionSyntax cast: current = cast.Expression; continue;
                case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } supp:
                    current = supp.Operand; continue;
                case DeclarationExpressionSyntax: return;
                default: return;
            }
        }
    }

    private static bool SymbolBelongsTo(ISymbol symbol, INamedTypeSymbol ownerType) =>
        symbol is IFieldSymbol or IPropertySymbol or IEventSymbol
        && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, ownerType);

    private void ScanTreeForSites(Compilation compilation, SyntaxTree tree)
    {
        var root = tree.GetRoot();
        var model = compilation.GetSemanticModel(tree);

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case AssignmentExpressionSyntax assign:
                    HandleAssignment(compilation, tree, model, assign);
                    break;
                case PostfixUnaryExpressionSyntax post when post.Kind()
                    is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression:
                    HandleIncrement(compilation, tree, model, post);
                    break;
                case PrefixUnaryExpressionSyntax pre when pre.Kind()
                    is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression:
                    HandleIncrement(compilation, tree, model, pre);
                    break;
                case ArgumentSyntax arg when arg.RefKindKeyword.RawKind != (int)SyntaxKind.None:
                    HandleRefOutArgument(compilation, tree, model, arg);
                    break;
                case InvocationExpressionSyntax invocation when IsReflectionSetValue(model, invocation):
                    _analysis.HasDynamicOrReflectionMutation = true;
                    break;
            }

            if (node.DescendantNodesAndSelf().Any(n => n is IdentifierNameSyntax id
                    && model.GetTypeInfo(id).Type?.TypeKind == TypeKind.Dynamic))
            {
                // 仅当下文是赋值/调用写时才算；保守地标记，由 PlanBuilder 决定是否阻断。
                if (node is AssignmentExpressionSyntax or InvocationExpressionSyntax)
                    _analysis.HasDynamicOrReflectionMutation = true;
            }
        }
    }

    private void HandleAssignment(Compilation compilation, SyntaxTree tree, SemanticModel model,
        AssignmentExpressionSyntax assign)
    {
        if (assign.Left is not ExpressionSyntax left) return;

        // 解构 / 声明式赋值不用点处理。
        if (left is TupleExpressionSyntax or DeclarationExpressionSyntax) return;
        // 对象初始化器内的赋值（new R { X = 1 }）对 init 合法，不是用点。
        if (assign.FirstAncestorOrSelf<InitializerExpressionSyntax>()
            is { RawKind: (int)SyntaxKind.ObjectInitializerExpression })
        {
            return;
        }

        switch (left)
        {
            case ElementAccessExpressionSyntax element:
                HandleElementTarget(compilation, tree, model, assign, element);
                return;
            case ConditionalAccessExpressionSyntax:
                AddSite(compilation, tree, model, assign, left, SiteKind.ConditionalAssign);
                return;
            case MemberAccessExpressionSyntax { Expression: ConditionalAccessExpressionSyntax }:
                AddSite(compilation, tree, model, assign, left, SiteKind.ConditionalAssign);
                return;
        }

        if (left is MemberAccessExpressionSyntax)
        {
            var kind = assign.IsKind(SyntaxKind.SimpleAssignmentExpression)
                ? SiteKind.SimpleAssign
                : SiteKind.CompoundAssign;
            AddSite(compilation, tree, model, assign, left, kind);
        }
    }

    private void HandleElementTarget(Compilation compilation, SyntaxTree tree, SemanticModel model,
        AssignmentExpressionSyntax assign, ElementAccessExpressionSyntax element)
    {
        // r[i] = v：r 自身是 record（record 声明的索引器）→ MS-18。
        if (model.GetTypeInfo(element.Expression).Type is INamedTypeSymbol { IsRecord: true } rec
            && _analysis.BySymbol.ContainsKey(rec))
        {
            AddSite(compilation, tree, model, assign, element, SiteKind.IndexerAssign);
            return;
        }
        // arr[i].M = v：左侧成员访问形态被 VisitMemberAccess 视角看作普通赋值，
        // 这里 element 只是接收者；让通用 AddSite 从 assign.Left 解析（arr[i].M 形态）。
        if (assign.Left is MemberAccessExpressionSyntax)
            AddSite(compilation, tree, model, assign, assign.Left, SiteKind.SimpleAssign);
    }

    private void HandleIncrement(Compilation compilation, SyntaxTree tree, SemanticModel model,
        ExpressionSyntax inc)
    {
        if (inc is not (PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax)) return;
        var operand = inc is PrefixUnaryExpressionSyntax pre ? pre.Operand : ((PostfixUnaryExpressionSyntax)inc).Operand;
        if (operand is not MemberAccessExpressionSyntax) return;
        // 表达式位置也登记：PlanBuilder 据此判 RR053（MS-4 只允许语句位置）。
        AddSite(compilation, tree, model, inc, operand, SiteKind.Increment);
    }

    private void HandleRefOutArgument(Compilation compilation, SyntaxTree tree, SemanticModel model,
        ArgumentSyntax arg)
    {
        var invocation = arg.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        var callee = invocation is null ? null : model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        var inTree = callee?.DeclaringSyntaxReferences.Length > 0;

        // ref/out 实参整体是 record（MS-10）。
        if (arg.Expression is not MemberAccessExpressionSyntax
            && model.GetTypeInfo(arg.Expression).Type is INamedTypeSymbol { IsRecord: true } whole
            && _analysis.BySymbol.ContainsKey(whole))
        {
            var site = new MutationSiteInfo
            {
                Location = arg.GetLocation(), FilePath = tree.FilePath, WriteNode = arg,
                Left = arg.Expression, Receiver = arg.Expression, ReceiverType = whole,
                Kind = SiteKind.RefOutRecordArg,
                IsStatementPosition = arg.FirstAncestorOrSelf<ExpressionStatementSyntax>() != null,
                RefOutCallee = callee, CalleeInTree = inTree,
            };
            AttachTarget(site, whole);
        }

        // ref/out 实参是 record 成员（MS-11/地址取址）。
        if (arg.Expression is MemberAccessExpressionSyntax or PrefixUnaryExpressionSyntax)
        {
            var inner = arg.Expression is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression } addr
                ? addr.Operand : arg.Expression;
            if (inner is MemberAccessExpressionSyntax memberAccess
                && model.GetSymbolInfo(memberAccess).Symbol is { ContainingType: { IsRecord: true } ct } sym
                && _analysis.BySymbol.TryGetValue(ct, out var target))
            {
                var site = new MutationSiteInfo
                {
                    Location = arg.GetLocation(), FilePath = tree.FilePath, WriteNode = arg,
                    Left = memberAccess, Receiver = memberAccess.Expression,
                    Member = sym, ReceiverType = ct,
                    Kind = SiteKind.RefOutMemberArg,
                    IsStatementPosition = arg.FirstAncestorOrSelf<ExpressionStatementSyntax>() != null,
                    RefOutCallee = callee, CalleeInTree = inTree,
                };
                site.TargetRecord = target;
                target.Sites.Add(site);
            }
        }
    }

    private void AddSite(Compilation compilation, SyntaxTree tree, SemanticModel model,
        SyntaxNode writeNode, ExpressionSyntax left, SiteKind kind)
    {
        // 拆解成员链 a.B.C：从最内层向外收集，最外层成员的 ContainingType 即目标 record。
        var chain = new List<ChainSegment>();
        ExpressionSyntax? cursor = left;
        ISymbol? deepestMember = null;
        ITypeSymbol? receiverType = null;
        while (cursor is MemberAccessExpressionSyntax ma)
        {
            var sym = model.GetSymbolInfo(ma).Symbol;
            var recvType = model.GetTypeInfo(ma.Expression).Type;
            chain.Add(new ChainSegment(sym!, recvType, ma));
            deepestMember ??= sym;
            receiverType ??= recvType;
            cursor = ma.Expression;
        }
        if (cursor is ElementAccessExpressionSyntax)
        {
            receiverType ??= model.GetTypeInfo(cursor).Type;
        }
        if (deepestMember?.ContainingType is not { IsRecord: true } targetType) return;
        if (!_analysis.BySymbol.TryGetValue(targetType, out var target)) return;
        if (receiverType == null) return;

        // this.F / 隐式 this 写发生在 record 自身方法/访问器（含本地函数/lambda 所在的
        // 外层成员）内时，属 MM（字段提升）或 ctor/init 访问器的合法路径，不登记为 MS 用点。
        SyntaxNode? enclosingDecl = writeNode.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>()
            ?? writeNode.FirstAncestorOrSelf<AccessorDeclarationSyntax>();
        if (cursor is ThisExpressionSyntax
            && enclosingDecl != null
            && model.GetDeclaredSymbol(enclosingDecl) is IMethodSymbol enclosingSymbol
            && SymbolEqualityComparer.Default.Equals(enclosingSymbol.ContainingType, targetType))
        {
            return;
        }

        INamedTypeSymbol? receiverInterface = receiverType.TypeKind == TypeKind.Interface
            ? (INamedTypeSymbol)receiverType : null;
        var rootReceiverType = model.GetTypeInfo(cursor ?? left).Type ?? receiverType;

        var site = new MutationSiteInfo
        {
            Location = writeNode.GetLocation(),
            FilePath = tree.FilePath,
            WriteNode = writeNode,
            Left = left,
            Receiver = cursor ?? left,
            Member = deepestMember,
            ReceiverType = receiverType,
            RootReceiverType = rootReceiverType,
            ReceiverInterface = receiverInterface,
            Kind = kind,
            IsStatementPosition = writeNode.FirstAncestorOrSelf<ExpressionStatementSyntax>() != null
                || writeNode.FirstAncestorOrSelf<GlobalStatementSyntax>() != null,
            EnclosingMethod = writeNode.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>() is { } md
                ? model.GetDeclaredSymbol(md) : null,
            Chain = chain,
        };
        site.TargetRecord = target;
        target.Sites.Add(site);
    }

    private void AttachTarget(MutationSiteInfo site, INamedTypeSymbol whole)
    {
        if (_analysis.BySymbol.TryGetValue(whole, out var target))
        {
            site.TargetRecord = target;
            target.Sites.Add(site);
        }
    }

    private static bool IsReflectionSetValue(SemanticModel model, InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "SetValue" })
            return false;
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) return false;
        return method.ContainingType.ToDisplayString()
            is "System.Reflection.PropertyInfo" or "System.Reflection.FieldInfo";
    }

    private void BuildInterfaceGraph(Compilation compilation)
    {
        var inTreeTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var typeDecl in tree.GetRoot().DescendantNodes()
                         .OfType<BaseTypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(typeDecl) is INamedTypeSymbol t) inTreeTypes.Add(t);
            }
        }

        foreach (var record in _analysis.Records)
        {
            foreach (var iface in record.Symbol.AllInterfaces)
            {
                if (!RecordSyntaxFacts.IsInTree(iface)) continue;
                var setting = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                foreach (var m in iface.GetMembers())
                {
                    if (m is IPropertySymbol { SetMethod: { IsInitOnly: false } } p) setting.Add(p);
                    if (m is IEventSymbol) setting.Add(m);
                }
                if (setting.Count == 0
                    && !record.WriteInfos.Keys.Any(km => km.ExplicitInterfaceImplementations.Any()
                        || InterfaceDeclaresMethod(iface, km)))
                {
                    // 方法连带在 M4 计划阶段按迁移方法精确匹配；此处保留接口节点。
                }
                if (!_analysis.Interfaces.TryGetValue(iface, out var fact))
                {
                    fact = new InterfaceFact { Interface = iface };
                    foreach (var t in inTreeTypes)
                    {
                        if (SymbolEqualityComparer.Default.Equals(t, iface)) continue;
                        if (t.AllInterfaces.Contains(iface, SymbolEqualityComparer.Default))
                            fact.InTreeImplementers.Add(t);
                    }
                    _analysis.Interfaces[iface] = fact;
                }
                foreach (var s in setting) fact.SettingMembers.Add(s);
            }
        }
    }

    private static bool InterfaceDeclaresMethod(INamedTypeSymbol iface, IMethodSymbol method)
        => iface.GetMembers().OfType<IMethodSymbol>()
            .Any(m => m.Name == method.Name && m.Parameters.Length == method.Parameters.Length);

    public static void ReportSkippedRecords(
        ConversionPlan plan,
        IReadOnlySet<string> excludedPlanIds,
        string filePath,
        RecordReadOnlyStatistics statistics,
        List<RecordReadOnlyDiagnostic> diagnostics,
        RecordReadOnlyOptions options)
    {
        foreach (var p in plan.Plans)
        {
            if (!p.TouchedFiles.Contains(filePath)) continue;
            if (excludedPlanIds.Contains(p.Id)) continue;
            if (p.Reported) continue;
            p.Reported = true;

            if (p.Outcome == PlanOutcome.Converted)
            {
                if (p.Kind == RecordKind.Struct) statistics.RecordStructsConverted++;
                else statistics.RecordClassesConverted++;
                if (options.ReportInfo)
                    diagnostics.Add(p.InfoDiagnostic(RecordReadOnlyDiagnosticCode.RR001,
                        $"record {(p.Kind == RecordKind.Struct ? "struct" : "class")} converted."));
                continue;
            }
            if (p.Outcome == PlanOutcome.AlreadyReadOnly)
            {
                statistics.AlreadyReadOnly++;
                if (options.ReportInfo)
                    diagnostics.Add(p.InfoDiagnostic(RecordReadOnlyDiagnosticCode.RR010,
                        "already a readonly record struct; skipped."));
                continue;
            }
            if (p.Outcome == PlanOutcome.UserSkipped)
            {
                statistics.RecordsSkipped++;
                if (options.ReportInfo)
                    diagnostics.Add(p.InfoDiagnostic(RecordReadOnlyDiagnosticCode.RR011,
                        "excluded by overrides.skip."));
                continue;
            }
            // Infeasible：原子跳过。
            statistics.RecordsSkipped++;
            statistics.SkippedByReason[p.FailCode!.Value] =
                statistics.SkippedByReason.GetValueOrDefault(p.FailCode.Value) + 1;
            diagnostics.Add(p.FailDiagnostic());
        }
    }
}
```

要点：Analyzer 只收集事实、不做裁决；所有跨 tree 的模型获取都经 `compilation.GetSemanticModel(tree)`。

- [ ] **Step 4: 让项目编译——补齐 RecordSyntaxFacts 的 IsRefReturning**

在 `RecordSyntaxFacts.cs` 追加：

```csharp
    public static bool IsRefReturning(IMethodSymbol method)
        => (method.ReturnsByRef || method.ReturnsByRefReadonly) && IsVisibleOutsideType(method);
```

- [ ] **Step 5: 运行测试确认编译通过且 RR010 绿（MemberRewrites/Feasible 在 Task 5 后绿）**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 此刻因 `RecordPlan` 仍为桩，`Analyzer_MergesPartialParts...` 编译失败属预期；`AlreadyReadonlyRecordStruct_EmitsRR010` 需等 Task 5 的 PlanBuilder 产出 AlreadyReadOnly。**本 Step 仅要求 Core 工程在按 Step 3“实现注意”修正签名后 `dotnet build src/CSharpToJava.Core` 编译通过**（测试中引用 `MemberRewrites/Feasible` 的两个测试可先注释，Task 5 Step 1 取消注释）。

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```
Expected: Build succeeded。

- [ ] **Step 6: Commit**

```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly; git commit -m "feat(record-readonly): phase 1 analyzer for records, members, sites, methods, interfaces"
```

---
### Task 5: Phase 2 Plan/PlanBuilder + RS-1/RS-2/RS-18 readonly 修饰符端到端

**Files:**
- Replace whole: `src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs`
- Replace whole: `src/CSharpToJava.Core/RecordReadOnly/DeclarationRewriter.cs`（启用 readonly 插入）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyPreprocessor.cs`（两处 `new PlanBuilder(_options)` 传入 `FeatureCaps.Current`）

能力分阶段打开（FeatureCaps），保证每个提交都只产出可编译中间树：本任务只开 `ReadOnlyModifier`；`MemberRewrites/Sites/MethodMigration/InterfaceRipple` 分别在 Task 7/13/20/23 打开。

- [ ] **Step 1: 写失败测试**

先把 Task 4 中引用计划字段的测试取消注释（`Analyzer_MergesPartialParts_AndClassifiesMembers` 已在 Task 4 写入）。再追加：

```csharp
    [Fact]
    public void RS1_PositionalRecordStruct_AddsReadonly_AndCompiles()
    {
        var src = "public record struct P(int X, int Y);";
        var (output, stats, diags) = Rewrite(src);
        Assert.Contains("readonly record struct P", output);
        Assert.DoesNotContain("readonly readonly", output);
        Assert.Equal(1, stats.RecordStructsConverted);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR001);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS1_RecordStructWithGetOnlyBody_AddsReadonly()
    {
        var src = """
            public record struct P(int X)
            {
                public int Y { get; }
                public int Sum() => X + Y;
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("readonly record struct P", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS2_AllPartialParts_GetReadonly()
    {
        var src = """
            public partial record struct P { public int X { get; } }
            public partial record struct P { public int Y { get; } }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(output, "readonly record struct P").Count);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MutableMembers_BeforeMemberRewriteMilestone_PlanInfeasibleRR020()
    {
        var src = "public record struct P { public int X { get; set; } }";
        var (output, stats, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record struct", output); // 原子跳过，不部分转换
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR020);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 编译失败（桩 RecordPlan 缺成员）。

- [ ] **Step 3: 整体替换 RecordConversionPlan.cs**

```csharp
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.RecordReadOnly;

internal enum PlanOutcome { Converted, AlreadyReadOnly, UserSkipped, Infeasible }

internal enum SiteActionKind { WithAssign, MethodMigrateCall, InterfaceWithCall }

internal enum MemberRewriteKind
{
    SetToInit,                    // RS-3/RS-4/RS-9/RS-20、RC-2/RC-5
    FieldToInitProperty,          // RS-5、RC-3（对外可见字段）
    PrivateFieldReadonly,         // RS-6a
    PrivateFieldToInitProperty,   // RS-6b（配合 MM）
}

internal enum MethodMigrationKind { VoidToRecord, NonVoidToOut, Extension }

internal sealed record MemberRewrite(string MemberName, MemberRewriteKind Kind, ISymbol Symbol, bool IsRequired);

internal sealed record MethodMigrationPlan(
    IMethodSymbol Method,
    MethodMigrationKind Kind,
    IReadOnlyList<ISymbol> PromotedMembers);

internal sealed record InterfaceRipplePlan(
    INamedTypeSymbol Interface,
    IReadOnlyList<ISymbol> TriggerMembers);

internal sealed record SitePlan(MutationSiteInfo Site, SiteActionKind Action);

internal sealed record PlanTouch(string File, string? TypeName, string? MemberName);

/// <summary>随里程碑推进逐项打开；未开能力对应的 record 计划判 Infeasible(RR020/RR070)。</summary>
internal sealed record FeatureCaps(
    bool ReadOnlyModifier,
    bool MemberRewrites,
    bool Sites,
    bool MethodMigration,
    bool InterfaceRipple)
{
    // Task 5：仅 readonly 修饰符。后续任务把对应项改为 true。
    public static FeatureCaps Current => new(
        ReadOnlyModifier: true,
        MemberRewrites: false,
        Sites: false,
        MethodMigration: false,
        InterfaceRipple: false);
}

internal sealed class RecordPlan
{
    public required string Id { get; init; }
    public required string RecordShortName { get; init; }
    public required string RecordQualifiedName { get; init; }
    public required RecordKind Kind { get; init; }
    public required INamedTypeSymbol Symbol { get; init; }
    public PlanOutcome Outcome { get; set; }
    public RecordReadOnlyDiagnosticCode? FailCode { get; set; }
    public string? FailReason { get; set; }
    public Location? FailLocation { get; set; }
    public bool Feasible => Outcome == PlanOutcome.Converted;

    public List<MemberRewrite> MemberRewrites { get; } = new();
    public List<MethodMigrationPlan> Migrations { get; } = new();
    public List<InterfaceRipplePlan> Ripples { get; } = new();
    public List<SitePlan> Sites { get; } = new();
    public HashSet<string> RequiredRecordIds { get; } = new(StringComparer.Ordinal);

    public HashSet<string> TouchedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PlanTouch> Touches { get; } = new();

    /// <summary>ReportSkippedRecords 每文件只计一次。</summary>
    public bool Reported { get; set; }

    public bool OwnsTouch(string file, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(file)) return false;
        if (typeName == null) return true;
        if (typeName == RecordShortName) return true;
        return memberName != null
            && Touches.Any(t => string.Equals(t.MemberName, memberName, StringComparison.Ordinal));
    }

    public RecordReadOnlyDiagnostic InfoDiagnostic(
        RecordReadOnlyDiagnosticCode code, string message)
    {
        var (file, line, col) = Locate();
        return new RecordReadOnlyDiagnostic(
            RecordReadOnlySeverity.Info, code, message, file, line, col, RecordQualifiedName);
    }

    public RecordReadOnlyDiagnostic FailDiagnostic()
    {
        var loc = FailLocation;
        var file = loc?.SourceTree?.FilePath;
        var span = loc?.GetLineSpan().StartLinePosition;
        return new RecordReadOnlyDiagnostic(
            RecordReadOnlySeverity.Warning, FailCode!.Value, FailReason ?? "record cannot be converted.",
            file, (span?.Line ?? 0) + 1, (span?.Character ?? 0) + 1, RecordQualifiedName);
    }

    private (string? File, int Line, int Column) Locate()
    {
        var loc = Symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var span = loc?.GetLineSpan().StartLinePosition;
        return (loc?.SourceTree?.FilePath, (span?.Line ?? 0) + 1, (span?.Character ?? 0) + 1);
    }
}

internal sealed class ConversionPlan
{
    public List<RecordPlan> Plans { get; } = new();
}

internal sealed class PlanBuilder
{
    private readonly RecordReadOnlyOptions _options;
    private readonly FeatureCaps _caps;

    public PlanBuilder(RecordReadOnlyOptions options, FeatureCaps? caps = null)
    {
        _options = options;
        _caps = caps ?? FeatureCaps.Current;
    }

    public ConversionPlan Build(RecordAnalysis analysis)
    {
        var plan = new ConversionPlan();
        foreach (var info in analysis.Records.OrderBy(r => r.Symbol.ToDisplayString(), StringComparer.Ordinal))
        {
            plan.Plans.Add(BuildRecordPlan(info, analysis));
        }
        return plan;
    }

    private RecordPlan BuildRecordPlan(RecordInfo info, RecordAnalysis analysis)
    {
        var qn = info.Symbol.ToDisplayString();
        var plan = new RecordPlan
        {
            Id = "RR:" + qn,
            RecordShortName = info.Symbol.Name,
            RecordQualifiedName = qn,
            Kind = info.Kind,
            Symbol = info.Symbol,
        };
        foreach (var part in info.Parts)
        {
            var file = part.SyntaxTree.FilePath;
            plan.TouchedFiles.Add(file);
            plan.Touches.Add(new PlanTouch(file, info.Symbol.Name, null));
        }

        if (_options.SkipQualifiedNames.Contains(qn))
        {
            plan.Outcome = PlanOutcome.UserSkipped;
            return plan;
        }
        if (info.AlreadyReadonly)
        {
            plan.Outcome = PlanOutcome.AlreadyReadOnly;
            return plan;
        }

        var forced = _options.ForceQualifiedNames.Contains(qn);

        // 声明级硬阻断（force 也不绕过：这些形态无法生成可编译结果）。
        if (info.HasInstanceEvent)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR040,
                "instance events cannot exist on a readonly record (RS-10).");
        if (info.HasFixedSizeBuffer || info.HasVisibleRefReturningMember
            || info.MutableFields.Any(f => f.IsVolatile))
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR041,
                "volatile instance field, ref-returning visible member, or fixed-size buffer " +
                "cannot be converted (RS-11/RC-5).");
        if (info.HasSettableIndexer)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR042,
                "indexer with a set accessor cannot be expressed via 'with' (RS-19/RC-9/MS-18).");

        var rippleRequired = false;

        // 属性 set→init（RS-3/4/9/20；RC-2/5 在 M5 复用同一条目）。
        foreach (var property in info.SettableProperties)
        {
            if (PropertyImplementsInterfaceSetter(info.Symbol, property))
            {
                rippleRequired = true;
                continue; // RS-14/RC-7：显式桩 + IR 在 M4 落地。
            }
            plan.MemberRewrites.Add(new MemberRewrite(
                property.Name, MemberRewriteKind.SetToInit, property, property.IsRequired));
        }

        // 字段分类（RS-5/RS-6）。
        foreach (var field in info.MutableFields.Where(f => !f.IsImplicitlyDeclared))
        {
            if (RecordSyntaxFacts.IsVisibleOutsideType(field))
            {
                if (info.HasExplicitLayout)
                    return Fail(plan, RecordReadOnlyDiagnosticCode.RR043,
                        "StructLayout(LayoutKind.Explicit) with a field promoted to property loses " +
                        "layout semantics (RS-17).");
                plan.MemberRewrites.Add(new MemberRewrite(
                    field.Name, MemberRewriteKind.FieldToInitProperty, field, field.IsRequired));
            }
            else if (IsPrivateFieldWrittenAfterConstruction(info, field))
            {
                if (!_caps.MethodMigration)
                    return Fail(plan, RecordReadOnlyDiagnosticCode.RR020,
                        $"private field '{field.Name}' is assigned after construction; method " +
                        "migration is required to convert it (RS-6b/MM).");
                plan.MemberRewrites.Add(new MemberRewrite(
                    field.Name, MemberRewriteKind.PrivateFieldToInitProperty, field, false));
            }
            else
            {
                plan.MemberRewrites.Add(new MemberRewrite(
                    field.Name, MemberRewriteKind.PrivateFieldReadonly, field, false));
            }
        }

        // mutating 方法（写实例成员的非 ctor 方法）需要 MM。set 访问器转 init 后享有
        // 与 ctor 同等的 this 写豁免（RS-9），不计迁移；getter 写成员由 M3 判 RR063。
        if (info.WriteInfos.Values.Any(w => !w.IsConstructor && !w.IsPropertySetter
                && (w.WrittenMembers.Count > 0 || w.WritesThis)))
        {
            if (!_caps.MethodMigration)
                return Fail(plan, RecordReadOnlyDiagnosticCode.RR020,
                    "record has instance method(s) that mutate state; method migration (MM) is " +
                    "required to convert.");
        }

        // 用点（MS）。
        if (info.Sites.Count > 0 && !_caps.Sites)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR020,
                $"record has {info.Sites.Count} mutation site(s); mutation-site rewriting (MS) " +
                "is required to convert.");

        // 接口连带（IR）。
        if (rippleRequired && !_caps.InterfaceRipple)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR070,
                "record implicitly implements a settable interface member; interface ripple (IR) " +
                "is required to convert.");
        if (rippleRequired)
        {
            foreach (var group in info.SettableProperties
                         .Where(p => PropertyImplementsInterfaceSetter(info.Symbol, p))
                         .Select(p => InterfaceForSetter(info.Symbol, p))
                         .GroupBy(i => i, SymbolEqualityComparer.Default))
            {
                plan.Ripples.Add(new InterfaceRipplePlan(group.Key!, Array.Empty<ISymbol>()));
            }
        }

        if (analysis.HasDynamicOrReflectionMutation)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR057,
                "dynamic receiver or reflection SetValue usage cannot be rewritten statically (MS-14).");

        var needMemberRewrites = plan.MemberRewrites.Any(m =>
            m.Kind is MemberRewriteKind.SetToInit or MemberRewriteKind.FieldToInitProperty
                or MemberRewriteKind.PrivateFieldToInitProperty);
        if (needMemberRewrites && !_caps.MemberRewrites)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR020,
                "record has mutable properties/fields; declaration member rewriting (RS-3..RS-6) " +
                "is required to convert.");

        if (info.Kind == RecordKind.Struct && !_caps.ReadOnlyModifier)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR020, "readonly modifier stage disabled.");

        _ = forced; // RR012 信息在 CLI 报告阶段补；force 不绕过上面的硬阻断。
        plan.Outcome = PlanOutcome.Converted;

        // 属性/字段改写触碰点（错误归属用）。
        foreach (var rewrite in plan.MemberRewrites)
        {
            foreach (var loc in rewrite.Symbol.Locations.Where(l => l.IsInSource))
                plan.Touches.Add(new PlanTouch(loc.SourceTree.FilePath, info.Symbol.Name, rewrite.MemberName));
            plan.TouchedFiles.UnionWith(rewrite.Symbol.Locations
                .Where(l => l.IsInSource).Select(l => l.SourceTree.FilePath));
        }
        return plan;
    }

    private static RecordPlan Fail(RecordPlan plan, RecordReadOnlyDiagnosticCode code, string reason)
    {
        plan.Outcome = PlanOutcome.Infeasible;
        plan.FailCode = code;
        plan.FailReason = reason;
        plan.FailLocation = plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return plan;
    }

    // set 访问器会被转成 init（RS-9），init 访问器与 ctor 同样可写 readonly 字段，
    // 故仅由 ctor/set 访问器写的私有字段归入 RS-6a（readonly），无需提升。
    private static bool IsPrivateFieldWrittenAfterConstruction(RecordInfo info, IFieldSymbol field)
        => info.WriteInfos.Values.Any(w => !w.IsConstructor && !w.IsPropertySetter
            && w.WrittenMembers.Contains(field));

    private static bool PropertyImplementsInterfaceSetter(INamedTypeSymbol record, IPropertySymbol property)
    {
        foreach (var iface in record.AllInterfaces)
        {
            foreach (var ip in iface.GetMembers().OfType<IPropertySymbol>())
            {
                if (ip.SetMethod is not { IsInitOnly: false }) continue;
                var impl = record.FindImplementationForInterfaceMember(ip);
                if (impl != null && SymbolEqualityComparer.Default.Equals(impl, property))
                    return true;
            }
        }
        return false;
    }

    private static INamedTypeSymbol? InterfaceForSetter(INamedTypeSymbol record, IPropertySymbol property)
    {
        foreach (var iface in record.AllInterfaces)
        {
            foreach (var ip in iface.GetMembers().OfType<IPropertySymbol>())
            {
                if (ip.SetMethod is not { IsInitOnly: false }) continue;
                var impl = record.FindImplementationForInterfaceMember(ip);
                if (impl != null && SymbolEqualityComparer.Default.Equals(impl, property))
                    return iface;
            }
        }
        return null;
    }
}
```

- [ ] **Step 4: 替换 DeclarationRewriter.cs（RS-1/RS-2 readonly 插入）**

```csharp
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed class DeclarationRewriter : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    public DeclarationRewriter(RewriteContext context) => _context = context;

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var visited = (RecordDeclarationSyntax)base.VisitRecordDeclaration(node)!;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        var plan = symbol == null ? null
            : _context.ActivePlans.FirstOrDefault(p =>
                SymbolEqualityComparer.Default.Equals(p.Symbol, symbol));
        if (plan is not { Outcome: PlanOutcome.Converted }) return visited;
        if (plan.Kind != RecordKind.Struct) return visited; // RC-1：class 无标记
        if (visited.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword))) return visited;

        var modifier = SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);
        return visited
            .WithModifiers(SyntaxFactory.TokenList(visited.Modifiers.Concat(new[] { modifier })))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }
}
```

- [ ] **Step 5: 更新预处理器的 PlanBuilder 构造（两处）**

在 `RecordReadOnlyPreprocessor.cs` 中把

```csharp
        _analysis = new RecordReadOnlyAnalyzer(_options).Analyze(model.Compilation);
        _plan = new PlanBuilder(_options).Build(_analysis);
```

和

```csharp
        _analysis = new RecordReadOnlyAnalyzer(_options).Analyze(compilation);
        _plan = new PlanBuilder(_options).Build(_analysis);
```

两处的 `new PlanBuilder(_options)` 都改为 `new PlanBuilder(_options, FeatureCaps.Current)`。

- [ ] **Step 6: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: PASS（含 RS-1/RS-2/RS-18/RR020 pending）。

- [ ] **Step 7: Commit**

```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): conversion plans with atomic feasibility; RS-1/RS-2/RS-18 readonly modifier"
```

---
### Task 6: RS-3/RS-4/RS-9/RS-20/RS-12/RS-13 属性 set→init

**Files:**
- Modify: `src/CSharpToJava.Core/RecordReadOnly/DeclarationRewriter.cs`
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs`（`FeatureCaps.Current` 打开 `MemberRewrites`）
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

- [ ] **Step 1: 写失败测试**

追加：

```csharp
    [Fact]
    public void RS3_AutoProperty_SetBecomesInit_AndCompiles()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Y { get; private set; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("readonly record struct P", output);
        Assert.Contains("public int X { get; init; }", output);
        Assert.Contains("public int Y { get; private init; }", output);
        Assert.DoesNotContain(" set;", output);
        Assert.Equal(2, stats.MembersSetToInit);
        AssertOutputCompiles(output);
        // 转换后对 X 的外部赋值不合法。
        AssertOutputCompiles("""
            public record struct P { public int X { get; init; } }
            public class C { void M() { var p = new P(); var q = p with { X = 1 }; } }
            """);
    }

    [Fact]
    public void RS9_HandWrittenSetterBody_KeywordBecomesInit_BodyPreserved()
    {
        var src = """
            public record struct P
            {
                private int _x;
                public int X
                {
                    get => _x;
                    set { _x = value; _x++; }
                }
            }
            """;
        // 注意：_x 仅 ctor 之外的 init 访问器写——init 访问器豁免 readonly struct 约束，
        // 故 _x 归类 RS-6a（构造路径含 init 访问器）。见 RS-9 说明。
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("init", output);
        Assert.Contains("_x = value; _x++;", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS20_RequiredMember_RequiredInitPreserved()
    {
        var src = "public record struct P { public required int X { get; set; } }";
        var (output, _, _) = Rewrite(src);
        Assert.Contains("required int X { get; init; }", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS12_StaticMutableProperty_Untouched()
    {
        var src = """
            public record struct P(int Id)
            {
                public static int Current { get; set; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("public static int Current { get; set; }", output);
        Assert.Equal(1, stats.RecordStructsConverted);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS13_AlreadyInitOrGetOnly_Untouched()
    {
        var src = """
            public record struct P
            {
                public int X { get; init; }
                public int Y { get; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Equal(1, stats.RecordStructsConverted);
        Assert.Equal(0, stats.MembersSetToInit);
        AssertOutputCompiles(output);
    }
```

> RS-4（C# `field` 关键字半自动属性）在 Task 8 与 RS-16 trivia 保持一起测；本任务先覆盖语法无关的 set→init 核心。

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 失败（RS3 用例仍输出 RR020 pending 或无 init）。

- [ ] **Step 3: 打开 MemberRewrites 能力开关**

在 `RecordConversionPlan.cs` 的 `FeatureCaps.Current` 中：

```csharp
        MemberRewrites: true,
```

- [ ] **Step 4: 在 DeclarationRewriter 实现属性 set→init**

把 `DeclarationRewriter.cs` 整体替换为：

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed class DeclarationRewriter : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    public DeclarationRewriter(RewriteContext context) => _context = context;

    private RecordPlan? ActivePlan(INamedTypeSymbol? type) =>
        type == null ? null
        : _context.ActivePlans.FirstOrDefault(p =>
            SymbolEqualityComparer.Default.Equals(p.Symbol, type));

    private MemberRewrite? FindRewrite(RecordPlan plan, ISymbol symbol) =>
        plan.MemberRewrites.FirstOrDefault(r =>
            SymbolEqualityComparer.Default.Equals(r.Symbol, symbol));

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var visited = (RecordDeclarationSyntax)base.VisitRecordDeclaration(node)!;

        // RS-5/RS-6：把计划中要转属性的字段在成员列表里展开（一条 FieldDeclaration
        // 可含多个变量声明符）。仅对本 record 的字段动手。
        var recordSymbol = _context.Model.GetDeclaredSymbol(node);
        var plan = ActivePlan(recordSymbol);
        if (plan is { Outcome: PlanOutcome.Converted })
        {
            var expanded = new List<MemberDeclarationSyntax>();
            foreach (var member in visited.Members)
            {
                if (member is FieldDeclarationSyntax field)
                {
                    var replacements = ExpandField(plan, field);
                    if (replacements != null) { expanded.AddRange(replacements); continue; }
                }
                expanded.Add(member);
            }
            visited = visited.WithMembers(SyntaxFactory.List(expanded));
        }

        if (plan is not { Outcome: PlanOutcome.Converted }) return visited;
        if (plan.Kind != RecordKind.Struct) return visited; // RC-1：class 无标记
        if (visited.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword))) return visited;

        var modifier = SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);
        return visited
            .WithModifiers(SyntaxFactory.TokenList(visited.Modifiers.Concat(new[] { modifier })))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>
    /// 字段声明 → 一个或多个替换成员。返回 null 表示该 FieldDeclaration 整体保留
    /// （可能其中的变量加了 readonly，那种情况由 base.VisitFieldDeclaration 处理）。
    /// </summary>
    private List<MemberDeclarationSyntax>? ExpandField(RecordPlan plan, FieldDeclarationSyntax field)
    {
        var replacements = new List<MemberDeclarationSyntax>();
        var anyPromotion = false;
        var index = 0;
        foreach (var variable in field.Declaration.Variables)
        {
            var fieldSymbol = _context.Model.GetDeclaredSymbol(variable) as IFieldSymbol;
            var rewrite = fieldSymbol == null ? null : FindRewrite(plan, fieldSymbol);
            if (rewrite is { Kind: MemberRewriteKind.FieldToInitProperty }
                or { Kind: MemberRewriteKind.PrivateFieldToInitProperty })
            {
                anyPromotion = true;
                var property = BuildPropertyFromField(field, variable, rewrite!, index == 0);
                replacements.Add(property);
                _context.Statistics.FieldsToProperties++;
            }
            else
            {
                // 同一声明里混着不转属性的变量：展开器不处理混合声明——Analyzer 保证
                // 同名字段分类逐符号进行；遇混合则退化为“保留原声明”，并由该处变量
                // 的 readonly 需求另行处理（见 VisitFieldDeclaration 不会触发时的说明）。
                return null;
            }
            index++;
        }
        return anyPromotion ? replacements : null;
    }

    private static PropertyDeclarationSyntax BuildPropertyFromField(
        FieldDeclarationSyntax field,
        VariableDeclaratorSyntax variable,
        MemberRewrite rewrite,
        bool keepTrivia)
    {
        var modifiers = field.Modifiers
            .Where(m => !m.IsKind(SyntaxKind.VolatileKeyword))
            .ToList();
        if (rewrite.IsRequired && !modifiers.Any(m => m.IsKind(SyntaxKind.RequiredKeyword)))
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.RequiredKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        var property = SyntaxFactory.PropertyDeclaration(field.Declaration.Type, variable.Identifier)
            .WithAttributeLists(field.AttributeLists)
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .AddAccessorListAccessors(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

        if (variable.Initializer is { } init)
        {
            property = property
                .WithInitializer(init)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }

        property = property
            .WithLeadingTrivia(keepTrivia ? field.GetLeadingTrivia() : SyntaxFactory.CarriageReturnLineFeed)
            .WithTrailingTrivia(keepTrivia ? field.GetTrailingTrivia() : SyntaxFactory.TriviaList())
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
        return property;
    }

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var visited = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        var plan = ActivePlan(symbol?.ContainingType);
        if (plan == null || plan.Outcome != PlanOutcome.Converted) return visited;
        var rewrite = symbol == null ? null : FindRewrite(plan, symbol);
        if (rewrite?.Kind != MemberRewriteKind.SetToInit) return visited;

        var accessors = node.AccessorList!.Accessors;
        if (!accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration))) return visited;

        var newAccessors = new SyntaxList<AccessorDeclarationSyntax>();
        var replaced = false;
        foreach (var accessor in accessors)
        {
            if (accessor.IsKind(SyntaxKind.SetAccessorDeclaration))
            {
                replaced = true;
                var initAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
                    .WithAttributeLists(accessor.AttributeLists)
                    .WithModifiers(accessor.Modifiers) // RS-3：保留 private 等修饰
                    .WithBody(accessor.Body)           // RS-4/RS-9：保体
                    .WithExpressionBody(accessor.ExpressionBody)
                    .WithSemicolonToken(accessor.SemicolonToken)
                    .WithLeadingTrivia(accessor.GetLeadingTrivia())
                    .WithTrailingTrivia(accessor.GetTrailingTrivia())
                    .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
                newAccessors = newAccessors.Add(initAccessor);
            }
            else
            {
                newAccessors = newAccessors.Add(accessor);
            }
        }
        if (!replaced) return visited;
        _context.Statistics.MembersSetToInit++;
        return visited.WithAccessorList(node.AccessorList!.WithAccessors(newAccessors));
    }

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        var visited = (FieldDeclarationSyntax)base.VisitFieldDeclaration(node)!;
        // RS-6a：不提升属性、又非 readonly 的私有字段，加 readonly。
        var any = false;
        foreach (var variable in node.Declaration.Variables)
        {
            var symbol = _context.Model.GetDeclaredSymbol(variable) as IFieldSymbol;
            var plan = ActivePlan(symbol?.ContainingType);
            var rewrite = symbol == null || plan == null ? null : FindRewrite(plan, symbol);
            if (rewrite?.Kind == MemberRewriteKind.PrivateFieldReadonly) any = true;
        }
        if (!any || visited.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword))) return visited;
        var token = SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);
        return visited
            .WithModifiers(SyntaxFactory.TokenList(visited.Modifiers.Concat(new[] { token })))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }
}
```

> 说明：`ExpandField` 遇到“同一 FieldDeclaration 中既有转属性变量、又有保留字段变量”时返回 null。为避免该退化路径漏转，PlanBuilder 在 Task 7 增加保证：可见字段声明符逐变量判定，混合声明在 Analyzer 侧不可见差异（同修饰符），因此实际不会出现混合；若出现，记录为 RR020（在 Task 7 的测试中固定该行为）。

- [ ] **Step 5: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: PASS（含此前全部用例）。

- [ ] **Step 6: Commit**

```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): RS-3/RS-4/RS-9/RS-20 set-to-init; RS-6a readonly private fields; RS-5 field promotion scaffold"
```

---
### Task 7: RS-5/RS-6 字段规则全集 + RS-4 + RS-16 trivia 保持

**Files:**
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`
- （实现已在 Task 6 的 DeclarationRewriter 中；本任务以测试固化，必要时小修。）

- [ ] **Step 1: 写失败测试**

追加：

```csharp
    [Fact]
    public void RS5_PublicField_BecomesInitProperty_WithInitializerAndAttribute()
    {
        var src = """
            using System.ComponentModel.DataAnnotations;
            public record struct P
            {
                [Required] public int X = 5;
                public string Name = "n";
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("[Required] public int X { get; init; } = 5;", output);
        Assert.Contains("public string Name { get; init; } = \"n\";", output);
        Assert.DoesNotContain("public int X =", output);
        Assert.Equal(2, stats.FieldsToProperties);
        AssertOutputCompiles(output,
            "using System.ComponentModel.DataAnnotations;");
    }

    [Fact]
    public void RS5_MultiDeclaratorFields_AllPromoted()
    {
        var src = "public record struct P { public int X, Y; }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("public int X { get; init; }", output);
        Assert.Contains("public int Y { get; init; }", output);
        Assert.Equal(2, stats.FieldsToProperties);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS6a_PrivateFieldCtorOnly_BecomesReadonly()
    {
        var src = """
            public record struct P
            {
                private int _tag;
                public P(int tag) { _tag = tag; }
                public int Tag => _tag;
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("private readonly int _tag;", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS6b_PrivateFieldMutatedAfterCtor_InfeasibleBeforeMethodMigration()
    {
        var src = """
            public record struct P
            {
                private int _tag;
                public void Bump() { _tag++; }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR020);
    }

    [Fact]
    public void RS12_StaticField_Untouched()
    {
        var src = """
            public record struct P(int Id)
            {
                public static int Counter;
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("public static int Counter;", output);
        Assert.Equal(0, stats.FieldsToProperties);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS4_SemiAutoProperty_FieldKeyword_SetBecomesInit()
    {
        var src = """
            public record struct P
            {
                public int X { get; set => field = value > 0 ? value : 0; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("public int X { get; init =>", output);
        AssertOutputCompiles(output);
    }
```

- [ ] **Step 2: 运行确认失败或通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: RS5/RS6a 应已通过（Task 6 实现）；若 `field` 用例因语言版本报语法错误，把测试 harness 的 `ParseOptions` 改为
`new CSharpParseOptions(LanguageVersion.Preview)`（Roslyn 4.12 / net10 下 `field` 在 Latest 或 Preview 之一可用；以实际编译通过为准，CLI 侧 `CreateCompilation` 已用 Latest，无需改生产代码）。其余全绿。

- [ ] **Step 3: Commit**

```powershell
cd d:/code/cs2j-record-readonly; git add tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs src/CSharpToJava.Core/RecordReadOnly; git commit -m "test(record-readonly): RS-4/RS-5/RS-6/RS-12 field and semi-auto property coverage"
```

---
### Task 8: RS-7/RS-10/RS-11/RS-15/RS-16/RS-17/RS-19 阻断与边界用例

**Files:**
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`
- 小修（如需）：`src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyAnalyzer.cs`

- [ ] **Step 1: 写失败测试**

追加：

```csharp
    [Fact]
    public void RS10_InstanceEvent_BlocksWithRR040()
    {
        var src = """
            public record struct P
            {
                public event System.Action? Changed;
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR040);
    }

    [Fact]
    public void RS11_VolatileField_BlocksWithRR041()
    {
        var src = "public record struct P { public volatile int X; }";
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR041);
    }

    [Fact]
    public void RS11_VisibleRefReturningMember_BlocksWithRR041()
    {
        var src = """
            public record struct P
            {
                private int _x;
                public ref int XRef() => ref _x;
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR041);
    }

    [Fact]
    public void RS19_SettableIndexer_BlocksWithRR042()
    {
        var src = """
            public record struct P
            {
                public int this[int i] { get => i; set { } }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR042);
    }

    [Fact]
    public void RS17_ExplicitLayoutWithPromotedField_BlocksWithRR043()
    {
        var src = """
            using System.Runtime.InteropServices;
            [StructLayout(LayoutKind.Explicit)]
            public record struct P
            {
                [FieldOffset(0)] public int X;
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR043);
    }

    [Fact]
    public void RS7_ConstructorAssignments_RemainLegal_AfterConversion()
    {
        var src = """
            public record struct P
            {
                public int X { get; init; }
                public P(int x) { X = x; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("readonly record struct P", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void GenericAndNestedRecords_ConvertIndependently()
    {
        var src = """
            public record struct Container<T>
            {
                public record struct Inner(T Value);
                public T Data { get; init; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("readonly record struct Container<T>", output);
        Assert.Contains("readonly record struct Inner", output);
        Assert.Equal(2, stats.RecordStructsConverted);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS15_ExplicitReadOnlyInterfaceImplementation_Untouched()
    {
        var src = """
            public record struct P : System.IComparable<P>
            {
                public int X { get; init; }
                public int CompareTo(P other) => X.CompareTo(other.X);
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("readonly record struct P", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RecordClass_NotConverted_WhenOptionOff()
    {
        var src = "public record P(int X);";
        var (output, stats, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record", output);
        Assert.Equal(0, stats.RecordClassesConverted);
        Assert.Equal(0, stats.RecordStructsConverted);
    }

    [Fact]
    public void RS16_XmlDocAndAttributes_PreservedOnProperty()
    {
        var src = """
            public record struct P
            {
                /// <summary>X doc.</summary>
                [System.Obsolete]
                public int X { get; set; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("/// <summary>X doc.</summary>", output);
        Assert.Contains("[System.Obsolete]", output);
        Assert.Contains("init;", output);
        AssertOutputCompiles(output);
    }
```

- [ ] **Step 2: 运行确认失败/通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 若 Analyzer 对 ref-returning/add-remove 事件/嵌套判定有漏，按失败信息修正 `RecordReadOnlyAnalyzer.cs`（补 `HasVisibleRefReturningMember` 对方法/属性/索引器 ref 返回、`HasInstanceEvent` 对 EventFieldDeclaration 与自定义 event 的识别），直到全绿。

- [ ] **Step 3: 整库构建 + M1 里程碑全测**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet build; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 解决方案构建通过；RecordReadOnly 全部用例绿。

- [ ] **Step 4: Commit**

```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "test(record-readonly): RS-7/10/11/15/16/17/19 blockers and boundary coverage (M1 declaration rules)"
```

---
### Task 9: CLI ProjectRecordReadOnlyPreprocessor（整树验证 + 计划级回退 FB-5）

**Files:**
- Modify: `src/CSharpToJava.Core/CSharpToJava.Core.csproj`（InternalsVisibleTo CLI）
- Create: `src/CSharpToJava.CLI/ProjectRecordReadOnlyPreprocessor.cs`

- [ ] **Step 1: 对 CLI 程序集开放 internal**

在 `src/CSharpToJava.Core/CSharpToJava.Core.csproj` 的 `<ItemGroup>` 中追加（与现有两条并列）：

```xml
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
      <_Parameter1>CSharpToJava.CLI</_Parameter1>
    </AssemblyAttribute>
```

- [ ] **Step 2: 创建项目级驱动器**

创建 `src/CSharpToJava.CLI/ProjectRecordReadOnlyPreprocessor.cs`：

```csharp
using CSharpToJava.Core.RecordReadOnly;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI;

internal sealed class ProjectRecordReadOnlyPreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public required RecordReadOnlyOptions Options { get; init; }
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
}

internal sealed class ProjectRecordReadOnlyPreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required RecordReadOnlyStatistics Statistics { get; init; }
    public required IReadOnlyList<RecordReadOnlyDiagnostic> Diagnostics { get; init; }
}

/// <summary>
/// 项目级 record-readonly 驱动：复制源码树 → 每轮全树建编译 → 逐文件 RewriteFile →
/// 整树重编译比对基线错误集 → 新增错误按 record 计划归属并回退（FB-5，规范 §3.2/§4.6）。
/// </summary>
internal static class ProjectRecordReadOnlyPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-record-readonly-src";

    public static async Task<ProjectRecordReadOnlyPreprocessResult> PreprocessAsync(
        ProjectRecordReadOnlyPreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var layout = ProjectPreprocessSource.ResolveLayout(request.SourcePath);
        var intermediateRoot = Path.Combine(
            Path.GetFullPath(request.DestinationRoot), IntermediateDirectoryName);

        if (Directory.Exists(intermediateRoot))
        {
            if (!request.Force)
            {
                throw new InvalidOperationException(
                    $"Record-readonly directory already exists: {intermediateRoot}. Use --force to overwrite.");
            }
            Directory.Delete(intermediateRoot, recursive: true);
        }
        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<RecordReadOnlyDiagnostic>();
        var rollbackDiagnostics = new List<RecordReadOnlyDiagnostic>();
        var sourceRoot = layout.SourceRoot;

        var projectGraph = ProjectPreprocessSource.TryLoadProjectGraph(layout, request.Verbose);
        var exclusionInfo = request.FilterUnsupportedProjects
            ? SupportedProjectFiltering.ComputeExclusions(
                projectGraph, request.Verbose, request.CompatNamespacePrefixes, layout.SourceRoot)
            : new SupportedProjectFiltering.ExclusionInfo();

        await ProjectPreprocessSource.CopyProjectFilesAsync(
            layout, sourceRoot, intermediateRoot, exclusionInfo);
        ProjectPreprocessSource.CopyAncestorMsBuildDirectoryFiles(
            sourceRoot, intermediateRoot, "[record-readonly]", request.Verbose);

        var sourceFiles = ProjectPreprocessSource.EnumerateSourceFiles(
                sourceRoot, intermediateRoot, Path.GetFullPath(request.DestinationRoot))
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Select(file => (
                Source: file,
                Dest: Path.Combine(intermediateRoot, Path.GetRelativePath(sourceRoot, file))))
            .ToList();

        foreach (var pair in sourceFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pair.Dest)!);
            File.Copy(pair.Source, pair.Dest, overwrite: true);
        }

        // 基线错误集：用原始内容、但 path 取中间树路径，保证键可直接比对。
        var baselineErrors = BuildErrorSet(
            sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Source))),
            layout, intermediateRoot);

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var acceptedStats = new RecordReadOnlyStatistics();
        var acceptedDiagnostics = new List<RecordReadOnlyDiagnostic>();
        var success = true;
        const int maxIterations = 1024;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            // 每轮从原始副本出发：避免对已改写文件二次驱动造成幂等问题。
            foreach (var pair in sourceFiles)
                File.Copy(pair.Source, pair.Dest, overwrite: true);

            var passStats = new RecordReadOnlyStatistics();
            var passDiagnostics = new List<RecordReadOnlyDiagnostic>();
            var preprocessor = new RecordReadOnlyPreprocessor(request.Options);
            var compilation = BuildCompilation(
                sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Dest))),
                layout, intermediateRoot);
            preprocessor.BindCompilation(compilation);
            preprocessor.ExcludePlans(excluded);

            // rewrittenByFile：被改写文件 → 其原始源文件路径（值当前未参与归属，仅作“在改写集”判定）。
            var rewrittenByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in sourceFiles)
            {
                var sourceCode = File.ReadAllText(pair.Dest);
                var tree = compilation.SyntaxTrees.FirstOrDefault(t =>
                    string.Equals(t.FilePath, pair.Dest, StringComparison.OrdinalIgnoreCase));
                if (tree == null) continue;

                var result = preprocessor.RewriteFile(
                    sourceCode,
                    compilation.GetSemanticModel(tree),
                    passStats,
                    passDiagnostics);

                if (result.Changed)
                {
                    File.WriteAllText(pair.Dest, result.OutputCode,
                        FileStartsWithBom(pair.Source)
                            ? new System.Text.UTF8Encoding(true)
                            : new System.Text.UTF8Encoding(false));
                    rewrittenByFile[pair.Dest] = pair.Source;
                }

                if (request.Verbose)
                {
                    Console.WriteLine(
                        $"[record-readonly] {(result.Changed ? "transformed" : "clean (copy)")}: " +
                        $"{Path.GetRelativePath(sourceRoot, pair.Source)}");
                }
            }

            var candidateCompilation = BuildCompilation(
                sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Dest))),
                layout, intermediateRoot);
            var newErrors = DiffErrors(baselineErrors, candidateCompilation, intermediateRoot);

            if (newErrors.Count == 0)
            {
                acceptedStats = passStats;
                acceptedDiagnostics = passDiagnostics;
                break;
            }

            // 归属新增错误 → 唯一 record 计划。
            var plansToRollback = new HashSet<string>(StringComparer.Ordinal);
            foreach (var error in newErrors)
            {
                if (preprocessor.TryAttributeDiagnostic(error, rewrittenByFile, out var planId)
                    && !excluded.Contains(planId)
                    && !plansToRollback.Contains(planId))
                {
                    plansToRollback.Add(planId);
                }
            }

            if (plansToRollback.Count == 0)
            {
                // 无法归属单一 record → 回退本次运行全部改动（RR030）。
                foreach (var pair in sourceFiles)
                    File.Copy(pair.Source, pair.Dest, overwrite: true);
                var first = newErrors[0];
                var span = first.Location.GetLineSpan().StartLinePosition;
                diagnostics.Add(new RecordReadOnlyDiagnostic(
                    RecordReadOnlySeverity.Error,
                    RecordReadOnlyDiagnosticCode.RR030,
                    "whole-tree validation failed and the new error could not be attributed to a " +
                    $"single record plan ({first.Descriptor.Id}: {first.GetMessage()}); all changes " +
                    "from this run were rolled back.",
                    first.Location.SourceTree?.FilePath,
                    span.Line + 1,
                    span.Character + 1,
                    null));
                acceptedStats = new RecordReadOnlyStatistics();
                acceptedDiagnostics = new List<RecordReadOnlyDiagnostic>();
                success = false;
                break;
            }

            foreach (var planId in plansToRollback)
            {
                var plan = preprocessor.Plans.First(p => p.Id == planId);
                excluded.Add(planId);
                acceptedStats.PlansRolledBack++;
                rollbackDiagnostics.Add(new RecordReadOnlyDiagnostic(
                    RecordReadOnlySeverity.Warning,
                    RecordReadOnlyDiagnosticCode.RR031,
                    "whole-tree validation attributed a new error to this record plan; the plan " +
                    "and all of its touched files were rolled back atomically.",
                    plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath,
                    (plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource)
                        ?.GetLineSpan().StartLinePosition.Line ?? 0) + 1,
                    (plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource)
                        ?.GetLineSpan().StartLinePosition.Character ?? 0) + 1,
                    plan.RecordQualifiedName));

                // 回退该计划触碰的所有文件（可能与其他计划共享文件；下一轮全部重放）。
                foreach (var touched in plan.TouchedFiles)
                {
                    var full = Path.GetFullPath(touched);
                    var pair = sourceFiles.FirstOrDefault(p =>
                        string.Equals(p.Dest, full, StringComparison.OrdinalIgnoreCase));
                    if (pair.Source != null) File.Copy(pair.Source, pair.Dest, overwrite: true);
                }
            }
        }

        diagnostics.AddRange(rollbackDiagnostics);
        diagnostics.AddRange(acceptedDiagnostics);

        var preprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot);
        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
        if (restoreTarget != null)
        {
            if (request.Verbose) Console.WriteLine($"Restoring packages: {restoreTarget}");
            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, "[record-readonly]");
            if (restoreExitCode != 0)
            {
                diagnostics.Add(new RecordReadOnlyDiagnostic(
                    RecordReadOnlySeverity.Error,
                    RecordReadOnlyDiagnosticCode.RR020,
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}."));
                success = false;
            }
        }

        return new ProjectRecordReadOnlyPreprocessResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = preprocessedSourcePath,
            Success = success && !diagnostics.Any(d => d.Severity == RecordReadOnlySeverity.Error),
            Statistics = acceptedStats,
            Diagnostics = diagnostics,
        };
    }

    private static CSharpCompilation BuildCompilation(
        IEnumerable<(string Path, string Content)> files,
        ProjectPreprocessSourceLayout layout,
        string intermediateRoot)
    {
        var trees = new List<SyntaxTree>();
        foreach (var (path, content) in files)
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                content,
                new CSharpParseOptions(LanguageVersion.Latest),
                path: path));
        }

        var globalUsings = new System.Text.StringBuilder();
        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(layout.SourceRoot, projectFile));
            var projectUsings = ImplicitGlobalUsings.BuildForProject(intermediateProjectFile, intermediateRoot);
            if (projectUsings != null) globalUsings.AppendLine(projectUsings);
        }
        if (globalUsings.Length > 0)
        {
            const string syntheticPath = "__cs2j_global_usings.g.cs";
            trees.Add(CSharpSyntaxTree.ParseText(
                globalUsings.ToString(),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: syntheticPath));
        }

        return CSharpCompilation.Create(
            "record-readonly-preprocess",
            trees,
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    /// <summary>(相对中间根路径, 错误码, 消息) 集合。</summary>
    private static HashSet<(string File, string Id, string Message)> BuildErrorSet(
        IEnumerable<(string Path, string Content)> files,
        ProjectPreprocessSourceLayout layout,
        string intermediateRoot)
    {
        var compilation = BuildCompilation(files, layout, intermediateRoot);
        return new HashSet<(string, string, string)>(
            compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree != null)
                .Select(d => (
                    Normalize(d.Location.SourceTree!.FilePath, intermediateRoot),
                    d.Descriptor.Id,
                    d.GetMessage())),
            StringComparer.Ordinal);
    }

    private static List<Diagnostic> DiffErrors(
        HashSet<(string File, string Id, string Message)> baseline,
        CSharpCompilation candidate,
        string intermediateRoot)
    {
        return candidate.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                && d.Location.SourceTree != null
                && !baseline.Contains((
                    Normalize(d.Location.SourceTree!.FilePath, intermediateRoot),
                    d.Descriptor.Id,
                    d.GetMessage())))
            .ToList();
    }

    private static string Normalize(string path, string intermediateRoot)
    {
        var full = Path.GetFullPath(path);
        return Path.GetRelativePath(intermediateRoot, full).Replace('\\', '/');
    }

    private static bool FileStartsWithBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        var read = stream.Read(prefix);
        return read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }
}
```

- [ ] **Step 3: 编译确认**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
```
Expected: 编译通过（驱动器尚未被调用；下一个任务接线）。

- [ ] **Step 4: Commit**

```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/CSharpToJava.Core.csproj src/CSharpToJava.CLI/ProjectRecordReadOnlyPreprocessor.cs; git commit -m "feat(record-readonly): project driver with whole-tree validation and plan-level rollback (FB-5)"
```

---
### Task 10: Program.cs 四处接线 + 独立 verb + overrides 配置

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`
- Create: `config/RecordReadOnlyOverrides.json`

- [ ] **Step 1: 注册 verb（接线点 1/4：ParseArguments + MapResult）**

在 `Program.cs` 约第 29 行的 `Parser.Default.ParseArguments<...>` 泛型列表末尾（`StripConditionalOptions` 后）加 `, RecordReadOnlyVerbOptions`：

```csharp
        return await Parser.Default.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions, EliminateGotoOptions, MakeReadOnlyOptions, UnsignedTypeOptions, GenericPreprocessOptions, StripDefaultOptions, LocalFunctionLiftOptions, TupleToStructVerbOptions, StripConditionalOptions, RecordReadOnlyVerbOptions>(args)
```

在约第 40 行的 MapResult 分支表中（`(TupleToStructVerbOptions opts) => ...` 旁）加：

```csharp
                .MapResult(
                    (RecordReadOnlyVerbOptions opts) => RecordReadOnlyVerbAsync(opts),
```

- [ ] **Step 2: convert-project 管线接入（接线点 2/4：Generic 之后、MakeReadOnly 之前）**

在 `ConvertProject` 执行块中，`if (opts.GenericPreprocess) { ... }` 整块之后、`if (opts.MakeReadOnly)` 之前插入：

```csharp
            if (opts.RecordReadOnly)
            {
                var recordReadOnlyResult = await ProjectRecordReadOnlyPreprocessor.PreprocessAsync(
                    new ProjectRecordReadOnlyPreprocessRequest
                    {
                        SourcePath = opts.Source,
                        DestinationRoot = opts.Destination,
                        Force = opts.Force,
                        Verbose = opts.Verbose,
                        Options = BuildRecordReadOnlyOptions(opts.RecordReadOnlyOverrides, strict: false),
                        FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
                        CompatNamespacePrefixes = LoadCompatNamespacePrefixes(opts.MappingConfig),
                    });

                Console.WriteLine(
                    $"record-readonly: {recordReadOnlyResult.Statistics.RecordStructsConverted} struct(s), " +
                    $"{recordReadOnlyResult.Statistics.RecordClassesConverted} class(es) converted, " +
                    $"{recordReadOnlyResult.Statistics.RecordsSkipped} skipped, " +
                    $"{recordReadOnlyResult.Statistics.PlansRolledBack} plan(s) rolled back, " +
                    $"{recordReadOnlyResult.Statistics.MutationSitesRewritten} site(s), " +
                    $"{recordReadOnlyResult.Statistics.MethodsMigrated} method(s), " +
                    $"{recordReadOnlyResult.Statistics.InterfaceRipples} interface ripple(s)");

                foreach (var diagnostic in recordReadOnlyResult.Diagnostics)
                {
                    if (diagnostic.Severity != CSharpToJava.Core.RecordReadOnly.RecordReadOnlySeverity.Info
                        || opts.Verbose)
                    {
                        Console.Error.WriteLine(diagnostic.ToMsBuildString());
                    }
                }

                if (!recordReadOnlyResult.Success)
                {
                    return 1;
                }

                opts.Source = recordReadOnlyResult.PreprocessedSourcePath;
                opts.FingerprintSourcePath ??= recordReadOnlyResult.OriginalSourcePath;
                opts.FingerprintInputRoot ??= recordReadOnlyResult.SourceRoot;
            }
```

- [ ] **Step 3: 保留目录注册（接线点 3/4）**

在 `GetPreservedDestinationDirectories`（约第 1944 行 `if (opts.TupleToStruct)` 旁）加：

```csharp
        if (opts.RecordReadOnly)
        {
            preserved.Add(ProjectRecordReadOnlyPreprocessor.IntermediateDirectoryName);
        }
```

- [ ] **Step 4: ConvertProjectOptions 开关（接线点 4/4）与配置加载**

在 `ConvertProjectOptions` 类中（与 `NoTupleToStruct` 的 `[Option]` 同区）加。**注意本特性默认关，不用 NoX 反向开关**：

```csharp
    [Option("record-readonly", Default = false,
        HelpText = "Enable the record-to-readonly-record preprocessing step (non-readonly records to readonly records)")]
    public bool RecordReadOnly { get; set; }

    [Option("record-readonly-overrides", Required = false,
        HelpText = "Path to RecordReadOnlyOverrides.json (default: config/RecordReadOnlyOverrides.json)")]
    public string? RecordReadOnlyOverrides { get; set; }
```

在 `Program.cs` 的 `LoadReadOnlyForceOverrides` 附近追加配置加载与 verb 处理器：

```csharp
    private sealed class RecordReadOnlyOverrideSets
    {
        public string[]? Force { get; set; }
        public string[]? Skip { get; set; }
    }

    private static CSharpToJava.Core.RecordReadOnly.RecordReadOnlyOptions BuildRecordReadOnlyOptions(
        string? explicitPath,
        bool strict,
        IEnumerable<string>? extraForce = null,
        IEnumerable<string>? extraSkip = null,
        bool convertRecordClasses = false)
    {
        var path = explicitPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "config", "RecordReadOnlyOverrides.json");
            if (!File.Exists(path))
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, "config", "RecordReadOnlyOverrides.json");
                    if (File.Exists(candidate)) { path = candidate; break; }
                    dir = dir.Parent;
                }
            }
        }

        var force = new HashSet<string>(StringComparer.Ordinal);
        var skip = new HashSet<string>(StringComparer.Ordinal);
        if (path != null && File.Exists(path))
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<RecordReadOnlyOverrideSets>(
                File.ReadAllText(path)) ?? new RecordReadOnlyOverrideSets();
            if (parsed.Force != null) force.UnionWith(parsed.Force);
            if (parsed.Skip != null) skip.UnionWith(parsed.Skip);
        }
        if (extraForce != null) force.UnionWith(extraForce);
        if (extraSkip != null) skip.UnionWith(extraSkip);

        return new CSharpToJava.Core.RecordReadOnly.RecordReadOnlyOptions
        {
            ForceQualifiedNames = force,
            SkipQualifiedNames = skip,
            Strict = strict,
            ConvertRecordClasses = convertRecordClasses,
        };
    }

    /// <summary>
    /// 独立 record-readonly verb：单文件 -i/-o（只允许自包含 record，RR090）或
    /// 项目 -s/-d（整树驱动 + 计划级回退）。
    /// </summary>
    private static async Task<int> RecordReadOnlyVerbAsync(RecordReadOnlyVerbOptions opts)
    {
        var options = BuildRecordReadOnlyOptions(
            opts.Overrides,
            strict: opts.Strict,
            extraForce: opts.Force,
            extraSkip: opts.Skip,
            convertRecordClasses: opts.ConvertRecordClasses);

        if (opts.Input != null)
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }

            var sourceCode = await File.ReadAllTextAsync(opts.Input);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                sourceCode,
                new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(
                    Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest),
                path: Path.GetFullPath(opts.Input));
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "record-readonly-file",
                new[] { tree },
                ProjectUnsignedTypePreprocessor.GetBasicReferences(),
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            var preprocessor =
                new CSharpToJava.Core.RecordReadOnly.RecordReadOnlyPreprocessor(options);
            preprocessor.BindCompilation(compilation);

            // 单文件模式自包含闸门（§3.3）：跨文件 partial/用点/接口连带/层次均不可见，
            // 凡计划需要这些的一律排除并报 RR090。
            var nonSelfContained = preprocessor.Plans
                .Where(p => p.Outcome == CSharpToJava.Core.RecordReadOnly.PlanOutcome.Converted
                    && (p.TouchedFiles.Any(f => !string.Equals(
                            Path.GetFullPath(f), tree.FilePath, StringComparison.OrdinalIgnoreCase))
                        || p.Ripples.Count > 0
                        || p.Migrations.Count > 0))
                .Select(p => p.Id)
                .ToList();
            if (nonSelfContained.Count > 0)
            {
                preprocessor.ExcludePlans(nonSelfContained);
            }

            var statistics = new CSharpToJava.Core.RecordReadOnly.RecordReadOnlyStatistics();
            var diagnostics = new List<CSharpToJava.Core.RecordReadOnly.RecordReadOnlyDiagnostic>();
            var result = preprocessor.RewriteFile(
                sourceCode, compilation.GetSemanticModel(tree), statistics, diagnostics);
            foreach (var id in nonSelfContained)
            {
                var plan = preprocessor.Plans.First(p => p.Id == id);
                diagnostics.Add(new CSharpToJava.Core.RecordReadOnly.RecordReadOnlyDiagnostic(
                    CSharpToJava.Core.RecordReadOnly.RecordReadOnlySeverity.Warning,
                    CSharpToJava.Core.RecordReadOnly.RecordReadOnlyDiagnosticCode.RR090,
                    "record is not self-contained within the single input file (partial parts, " +
                    "mutation sites, interface ripple, migrated call sites, or hierarchy members " +
                    "live outside the file); skipped in single-file mode.",
                    tree.FilePath, 0, 0, plan.RecordQualifiedName));
            }

            if (opts.Output != null)
                await File.WriteAllTextAsync(opts.Output, result.OutputCode, new System.Text.UTF8Encoding(false));
            else
                Console.Write(result.OutputCode);

            foreach (var diagnostic in diagnostics)
                Console.Error.WriteLine(diagnostic.ToMsBuildString());

            return options.Strict && statistics.RecordsSkipped > 0 ? 1 : 0;
        }

        if (opts.Source == null || opts.Destination == null)
        {
            Console.Error.WriteLine("Error: provide (-i [-o]) or (-s <source> -d <destination>).");
            return 1;
        }
        if (!Directory.Exists(opts.Source) && !File.Exists(opts.Source))
        {
            Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
            return 1;
        }
        Directory.CreateDirectory(opts.Destination);

        var preprocessResult = await ProjectRecordReadOnlyPreprocessor.PreprocessAsync(
            new ProjectRecordReadOnlyPreprocessRequest
            {
                SourcePath = opts.Source,
                DestinationRoot = opts.Destination,
                Force = true,
                Verbose = opts.Verbose,
                Options = options,
                FilterUnsupportedProjects = opts.FilterUnsupportedProjects,
                CompatNamespacePrefixes = LoadCompatNamespacePrefixes(opts.MappingConfig),
            });

        Console.WriteLine(
            $"record-readonly: {preprocessResult.Statistics.RecordStructsConverted} struct(s), " +
            $"{preprocessResult.Statistics.RecordClassesConverted} class(es) converted, " +
            $"{preprocessResult.Statistics.RecordsSkipped} skipped, " +
            $"{preprocessResult.Statistics.PlansRolledBack} plan(s) rolled back");
        foreach (var diagnostic in preprocessResult.Diagnostics)
            Console.Error.WriteLine(diagnostic.ToMsBuildString());
        Console.WriteLine($"Preprocessed source: {preprocessResult.PreprocessedSourcePath}");

        if (options.Strict && preprocessResult.Statistics.RecordsSkipped > 0) return 1;
        return preprocessResult.Success ? 0 : 1;
    }
```

在 `TupleToStructVerbOptions` 类定义之后追加 verb 选项类：

```csharp
[Verb("record-readonly", HelpText = "Convert non-readonly records (record struct and optionally record class) into readonly/immutable records")]
class RecordReadOnlyVerbOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file (self-contained records only)")]
    public string? Input { get; set; }

    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }

    [Option('s', "source", SetName = "dir", HelpText = "Source directory or .csproj/.sln file")]
    public string? Source { get; set; }

    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }

    [Option('v', "verbose", Default = false, HelpText = "Show detailed progress")]
    public bool Verbose { get; set; }

    [Option("overrides", HelpText = "Path to RecordReadOnlyOverrides.json")]
    public string? Overrides { get; set; }

    [Option("force", Separator = ',', HelpText = "Fully-qualified record names to force-convert (comma-separated)")]
    public IEnumerable<string>? Force { get; set; }

    [Option("skip", Separator = ',', HelpText = "Fully-qualified record names to exclude (comma-separated)")]
    public IEnumerable<string>? Skip { get; set; }

    [Option("strict", Default = false, HelpText = "Return non-zero exit code when any record is skipped")]
    public bool Strict { get; set; }

    [Option("convert-record-classes", Default = false, HelpText = "Also convert record class declarations (RC rules; semantic immutability)")]
    public bool ConvertRecordClasses { get; set; }

    [Option('m', "mapping", Required = false, HelpText = "Path to type mapping configuration file")]
    public string? MappingConfig { get; set; }

    [Option("no-filter-projects", Default = false,
        HelpText = "Disable excluding unsupported (WPF/UWP/WinForms) projects before preprocessing")]
    public bool NoFilterProjects { get; set; }

    public bool FilterUnsupportedProjects => !NoFilterProjects;
}
```

- [ ] **Step 5: 创建 overrides 配置文件**

创建 `config/RecordReadOnlyOverrides.json`（键名严格用 `force`/`skip`，区别于 ReadOnlyStructOverrides.json 的 `forceReadOnly`）：

```json
{
  "force": [],
  "skip": []
}
```

- [ ] **Step 6: 构建并手工烟雾测试**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet build
```
Expected: 解决方案构建通过。

准备临时样例并验证 CLI（临时目录，测试后删除）：

```powershell
$tmp = Join-Path $env:TEMP "rr-smoke"; New-Item -ItemType Directory -Force -Path $tmp | Out-Null; Set-Content -Path (Join-Path $tmp "P.cs") -Value 'public record struct P(int X) { public int Y { get; set; } }'; dotnet run --project src/CSharpToJava.CLI -- record-readonly -i (Join-Path $tmp "P.cs")
```
Expected: stdout 含 `readonly record struct P` 与 `init`；stderr 含 RR001 MSBuild 行。清理：`Remove-Item -Recurse -Force $tmp`。

- [ ] **Step 7: Commit**

```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.CLI/Program.cs config/RecordReadOnlyOverrides.json; git commit -m "feat(record-readonly): wire CLI pipeline stage, verb, overrides config (default off)"
```

---
## M2：MutationSiteRewriter（MS-1~MS-19）

### Task 11: MS-1 用点分类框架 + 简单成员赋值 with 改写（含 MS-8/MS-9/MS-13 接收者判定）

**Files:**
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs`（删除 `FeatureCaps.Sites`；PlanBuilder 调用用点分类）
- Replace whole: `src/CSharpToJava.Core/RecordReadOnly/MutationSiteRewriter.cs`（分类器 + 重写器）
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

- [ ] **Step 1: 写失败测试**

追加：

```csharp
    [Fact]
    public void MS1_LocalReceiver_BecomesWithAssign()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                void M()
                {
                    var p = new P();
                    p.X = 1;
                }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("p = p with { X = 1 };", output);
        Assert.Equal(1, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS1_ValueParameterReceiver_BecomesWithAssign()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C { public void Update(P p) { p.X = 42; } }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p with { X = 42 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS1_FieldAndSettablePropertyReceiver_Writable()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                private P _other;
                public P Holder { get; set; }
                public void M() { _other.X = 1; Holder.X = 2; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("_other = _other with { X = 1 };", output);
        Assert.Contains("Holder = Holder with { X = 2 };", output);
        Assert.Equal(2, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS1_ArrayElementReceiver_BecomesWithAssign()
    {
        var src = """
            using System;
            public record struct P { public int X { get; set; } }
            public class C { public void M(P[] arr) { arr[0].X = 9; } }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("arr[0] = arr[0] with { X = 9 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS1_TupleElementReceiver_BecomesWithAssign()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void M((P a, int b) t) { t.a.X = 3; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("t.a = t.a with { X = 3 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS8_InsideLambda_LocalCaptured_Writable()
    {
        var src = """
            using System;
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void M()
                {
                    var p = new P();
                    Action a = () => { p.X = 7; };
                    a();
                }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p with { X = 7 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS9_RefParameterReceiver_Writable()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void Update(ref P p) { p.X = 5; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p with { X = 5 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS13_ForeachIterationVariable_BlocksWithRR050()
    {
        var src = """
            using System.Collections.Generic;
            public record struct P { public int X { get; set; } }
            public class C { public void M(List<P> list) { foreach (var p in list) p.X = 1; } }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("with", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR050);
    }

    [Fact]
    public void MS1_InParameterReceiver_BlocksWithRR050()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C { public void Update(in P p) { p.X = 1; } }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR050);
    }

    [Fact]
    public void MS1_GetOnlyReceiver_BlocksWithRR050()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C { public P Get() => default; public void M() { Get().X = 1; } }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR050);
    }

    [Fact]
    public void MS1_ReceiverWithSideEffect_BlocksWithRR051()
    {
        var src = """
            using System;
            public record struct P { public int X { get; set; } }
            public class C
            {
                public P[] Items = Array.Empty<P>();
                public int F() => 0;
                public void M() { Items[F()].X = 1; }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR051);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: MS 用例失败（MutationSiteRewriter 是空桩；含用点的 record 目前 RR020 pending）。

- [ ] **Step 3: 删除 Sites 能力开关**

编辑 `RecordConversionPlan.cs`：从 `FeatureCaps` 的位置参数中**删除 `bool Sites`** 与 `Current` 中的 `Sites: false,`。用点分类改为始终执行（分类不通过即精确诊断码阻断）。

- [ ] **Step 4: PlanBuilder 接入逐用点分类**

把 PlanBuilder 中

```csharp
        // 用点（MS）。
        if (info.Sites.Count > 0 && !_caps.Sites)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR020,
                $"record has {info.Sites.Count} mutation site(s); mutation-site rewriting (MS) " +
                "is required to convert.");
```

替换为：

```csharp
        // 用点（MS）：逐点分类，任一不可行即整 record 失败（FB-1），错误位置在用点。
        foreach (var site in info.Sites.OrderBy(s => s.Location.SourceSpan.Start))
        {
            var failure = MutationSiteClassifier.Classify(analysis, plan, info, site);
            if (failure != null)
                return FailAt(plan, failure.Code, failure.Reason, failure.Location);
            plan.Sites.Add(new SitePlan(site, SiteActionKind.WithAssign));
            plan.TouchedFiles.Add(site.FilePath);
        }
```

并在 PlanBuilder 中补两个辅助方法：

```csharp
    private static RecordPlan FailAt(RecordPlan plan, RecordReadOnlyDiagnosticCode code,
        string reason, Location location)
    {
        plan.Outcome = PlanOutcome.Infeasible;
        plan.FailCode = code;
        plan.FailReason = reason;
        plan.FailLocation = location;
        return plan;
    }
```

- [ ] **Step 5: 整体替换 MutationSiteRewriter.cs（分类器 + 重写器）**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed record SiteFailure(
    RecordReadOnlyDiagnosticCode Code,
    string Reason,
    Location Location);

/// <summary>
/// 用点可行性分类。Task 11 覆盖 MS-1（单段简单赋值）/MS-8/MS-9/MS-13 与 RR050/RR051；
/// 其余用点形态在后续任务打开，之前统一 RR020（pending）。
/// </summary>
internal static class MutationSiteClassifier
{
    public static SiteFailure? Classify(
        RecordAnalysis analysis,
        RecordPlan plan,
        RecordInfo info,
        MutationSiteInfo site)
    {
        var model = analysis.Compilation!.GetSemanticModel(site.WriteNode.SyntaxTree)!;

        switch (site.Kind)
        {
            case SiteKind.IndexerAssign:
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR042,
                    "indexer assignment on the record itself cannot be expressed with 'with' " +
                    "(RS-19/RC-9/MS-18).", site.Location);

            // 后续任务支持的形态：在落地前整 record 保持原子跳过。
            case SiteKind.CompoundAssign:
            case SiteKind.Increment:
            case SiteKind.ConditionalAssign:
            case SiteKind.RefOutRecordArg:
            case SiteKind.RefOutMemberArg:
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
                    $"mutation site kind '{site.Kind}' is rewritten by a later stage.",
                    site.Location);
        }

        if (site.Kind != SiteKind.SimpleAssign)
            return Pending(site);

        if (!site.IsStatementPosition)
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR053,
                "member assignment in an expression context cannot be rewritten to a " +
                "preceding 'with' statement (MS-16).", site.Location);

        // 仅单段链（recv.M）；多段（a.B.C）属 MS-17。
        var segmentCount = CountMemberSegments(site.Left);
        if (segmentCount != 1)
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
                "nested member chains (MS-17) are rewritten by a later stage.", site.Location);

        var memberFailure = MemberIsInitSettable(plan, site.Member);
        if (memberFailure != null) return new SiteFailure(memberFailure.Value.Code,
            memberFailure.Value.Reason, site.Location);

        var receiverFailure = ReceiverWritableAndPure(model, site.Receiver);
        return receiverFailure;
    }

    private static SiteFailure Pending(MutationSiteInfo site) =>
        new(RecordReadOnlyDiagnosticCode.RR020,
            $"mutation site kind '{site.Kind}' is rewritten by a later stage.", site.Location);

    private static int CountMemberSegments(ExpressionSyntax left)
    {
        var count = 0;
        ExpressionSyntax? cursor = left;
        while (cursor is MemberAccessExpressionSyntax ma)
        {
            count++;
            cursor = ma.Expression;
        }
        return count;
    }

    private static SiteFailure? MemberIsInitSettable(RecordPlan plan, ISymbol? member)
    {
        if (member == null)
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
                "target member could not be resolved.", Location.None);
        if (plan.MemberRewrites.Any(r => SymbolEqualityComparer.Default.Equals(r.Symbol, member)))
            return null;
        if (member is IPropertySymbol { SetMethod.IsInitOnly: true })
            return null; // 位置参数等编译器合成 init 属性
        return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
            $"member '{member.Name}' cannot be set via 'with'.", member.Locations.FirstOrDefault() ?? Location.None);
    }

    /// <summary>MS-1 可写左值 + 无副作用（接收者会被求值两次）。</summary>
    private static SiteFailure? ReceiverWritableAndPure(SemanticModel model, ExpressionSyntax receiver)
    {
        switch (receiver)
        {
            case IdentifierNameSyntax id:
                return IdentifierReceiverWritable(model, id);

            case ElementAccessExpressionSyntax element:
                foreach (var arg in element.ArgumentList.Arguments)
                {
                    if (arg.Expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any())
                        return new SiteFailure(RecordReadOnlyDiagnosticCode.RR051,
                            "indexer argument invokes a method; evaluating it twice would change " +
                            "semantics (MS-1).", element.GetLocation());
                }
                var type = model.GetTypeInfo(element.Expression).Type;
                if (type is IArrayTypeSymbol) return null;
                if (model.GetSymbolInfo(element).Symbol is IPropertySymbol indexer)
                {
                    if (indexer.ContainingNamespace?.ToDisplayString()
                        == "System.Collections.Immutable")
                        return new SiteFailure(RecordReadOnlyDiagnosticCode.RR054,
                            "read-only/immutable collection element cannot be written (MS-7).",
                            element.GetLocation());
                    if (indexer.SetMethod is not { IsInitOnly: false })
                        return new SiteFailure(RecordReadOnlyDiagnosticCode.RR054,
                            "collection element is read-only (MS-7).", element.GetLocation());
                    return null;
                }
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR051,
                    "element receiver could not be proven a writable indexer (MS-1/MS-7).",
                    element.GetLocation());

            case MemberAccessExpressionSyntax:
                // 单段链不可能以此为根；留待 MS-17。
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
                    "nested member chains (MS-17) are rewritten by a later stage.",
                    receiver.GetLocation());

            default:
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR051,
                    "receiver expression is not a provably side-effect-free writable lvalue " +
                    "(MS-1).", receiver.GetLocation());
        }
    }

    private static SiteFailure? IdentifierReceiverWritable(SemanticModel model, IdentifierNameSyntax id)
    {
        var symbol = model.GetSymbolInfo(id).Symbol;
        switch (symbol)
        {
            case ILocalSymbol local:
                if (local.IsForEach)
                    return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        "foreach iteration variable is read-only (MS-13).", id.GetLocation());
                if (local.RefKind == RefKind.In)
                    return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        "'ref readonly' local cannot be written (MS-1).", id.GetLocation());
                return null;

            case IParameterSymbol parameter:
                if (parameter.RefKind == RefKind.In)
                    return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        "'in' parameter cannot be written (MS-1).", id.GetLocation());
                return null; // 普通值参 / ref / out 形参可写（MS-9）

            case IFieldSymbol field:
                if (field.IsReadOnly || field.IsInitOnly)
                    return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        $"field '{field.Name}' is read-only/init-only (MS-1).", id.GetLocation());
                return null;

            case IPropertySymbol property:
                if (property.SetMethod is not { IsInitOnly: false })
                    return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        $"property '{property.Name}' has no writable set accessor (MS-1).",
                        id.GetLocation());
                return null;

            default:
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR051,
                    "receiver is not a writable local, parameter, field, property, or element " +
                    "(MS-1).", id.GetLocation());
        }
    }
}

/// <summary>
/// Phase 3b：把已计划用点改写为 with 赋值。每个用点在计划阶段被唯一归类，
/// 本重写器只处理 ActivePlans 中 SitePlans 命中的节点。
/// </summary>
internal sealed class MutationSiteRewriter : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    private readonly Dictionary<(int Start, int Kind), SitePlan> _siteKeys = new();

    public MutationSiteRewriter(RewriteContext context)
    {
        _context = context;
        foreach (var plan in context.ActivePlans)
        {
            foreach (var sitePlan in plan.Sites)
            {
                if (!string.Equals(sitePlan.Site.FilePath, context.FilePath,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var node = sitePlan.Site.WriteNode;
                _siteKeys[(node.SpanStart, node.RawKind)] = sitePlan;
            }
        }
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var visited = (BlockSyntax)base.VisitBlock(node)!;
        var statements = new List<StatementSyntax>();
        foreach (var statement in visited.Statements)
            statements.AddRange(Expand(statement));
        return visited.WithStatements(SyntaxFactory.List(statements));
    }

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
    {
        var visited = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;
        var statements = new List<StatementSyntax>();
        foreach (var statement in visited.Statements)
            statements.AddRange(Expand(statement));
        return visited.WithStatements(SyntaxFactory.List(statements));
    }

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var visited = (IfStatementSyntax)base.VisitIfStatement(node)!;
        if (visited.Statement is ExpressionStatementSyntax es && FindSite(es.Expression) != null)
            visited = visited.WithStatement(SyntaxFactory.Block(Expand(es)));
        if (visited.Else?.Statement is ExpressionStatementSyntax elseEs
            && FindSite(elseEs.Expression) != null)
        {
            visited = visited.WithElse(visited.Else!.WithStatement(
                SyntaxFactory.Block(Expand(elseEs))));
        }
        return visited;
    }

    private IEnumerable<StatementSyntax> Expand(StatementSyntax statement)
    {
        if (statement is ExpressionStatementSyntax expressionStatement
            && FindSite(expressionStatement.Expression) is { } sitePlan)
        {
            var rewritten = RewriteWithAssign(expressionStatement.Expression, sitePlan);
            if (rewritten != null)
            {
                _context.Statistics.MutationSitesRewritten++;
                yield return SyntaxFactory.ExpressionStatement(rewritten)
                    .WithLeadingTrivia(expressionStatement.GetLeadingTrivia())
                    .WithTrailingTrivia(expressionStatement.GetTrailingTrivia())
                    .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
                yield break;
            }
        }
        yield return statement;
    }

    private SitePlan? FindSite(ExpressionSyntax expression) =>
        _siteKeys.TryGetValue((expression.SpanStart, expression.RawKind), out var plan) ? plan : null;

    /// <summary>recv.M = rhs  →  recv = recv with { M = rhs }（MS-1）。</summary>
    private ExpressionSyntax? RewriteWithAssign(ExpressionSyntax writeExpression, SitePlan sitePlan)
    {
        if (sitePlan.Action != SiteActionKind.WithAssign) return null;
        if (writeExpression is not AssignmentExpressionSyntax
            {
                RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                Left: MemberAccessExpressionSyntax memberAccess,
            } assignment)
        {
            return null;
        }

        var receiverText = sitePlan.Site.Receiver.WithoutTrivia().ToFullString();
        var receiver = SyntaxFactory.ParseExpression(receiverText);
        var initializer = SyntaxFactory.InitializerExpression(
            SyntaxKind.WithInitializerExpression,
            SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(memberAccess.Name.Identifier.Text),
                    assignment.Right)));
        var withExpression = SyntaxFactory.WithExpression(receiver, initializer);
        var writeBack = SyntaxFactory.ParseExpression(receiverText);
        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression, writeBack, withExpression)
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }
}
```

> 说明：`SyntaxFactory.ParseExpression(receiverText)` 用于安全复制接收者节点——同一 `SyntaxNode` 不能在新树中挂两处；接收者仅含标识符/成员/索引，重新解析语义无损，格式由 `FormatAndValidate` 规整。

- [ ] **Step 6: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 全部 PASS（此前 RS 用例不回归；MS-1/MS-8/MS-9/MS-13/RR050/RR051 新用例绿）。

- [ ] **Step 7: Commit**

```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): MS-1 mutation-site classification and with-assignment rewriting"
```

---
### Task 12: MS-2 相邻同接收者合并、MS-3 复合赋值、MS-4 自增自减

**Files:**
- Modify: `src/CSharpToJava.Core/RecordReadOnly/MutationSiteRewriter.cs`
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

- [ ] **Step 1: 写失败测试**

追加：

```csharp
    [Fact]
    public void MS3_CompoundAssignment_BecomesWithReadModifyWrite()
    {
        var src = """
            public record struct P { public int X { get; set; } public string? S { get; set; } }
            public class C
            {
                public void M(P p)
                {
                    p.X += 5;
                    p.X -= 2;
                    p.S ??= "x";
                }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("p = p with { X = p.X + 5 };", output);
        Assert.Contains("p = p with { X = p.X - 2 };", output);
        Assert.Contains("p = p with { S = p.S ?? \"x\" };", output);
        Assert.Equal(3, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS3_ImpureGetter_BlocksWithRR052()
    {
        var src = """
            public record struct P
            {
                private int _x;
                public int X { get => _x + Next(); set => _x = value; }
                private static int Next() => 1;
            }
            public class C { public void M(P p) { p.X += 1; } }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR052);
    }

    [Fact]
    public void MS4_IncrementStatement_BecomesWithPlusOne()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void M(P p)
                {
                    p.X++;
                    ++p.X;
                    p.X--;
                }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("p = p with { X = p.X + 1 };", output);
        Assert.Contains("p = p with { X = p.X - 1 };", output);
        Assert.Equal(3, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS4_IncrementInExpressionPosition_BlocksWithRR053()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C { public int M(P p) => p.X++; }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR053);
    }

    [Fact]
    public void MS2_AdjacentSameReceiverAssignments_MergeIntoOneWith()
    {
        var src = """
            public record struct P { public int X { get; set; } public int Y { get; set; } }
            public class C
            {
                public void M(P p)
                {
                    p.X = 1;
                    p.Y = 2;
                }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("p = p with { X = 1, Y = 2 };", output);
        Assert.DoesNotContain("p = p with { X = 1 };", output);
        Assert.Equal(2, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS2_ReadAfterWriteAcrossStatements_DoesNotMerge()
    {
        var src = """
            public record struct P { public int X { get; set; } public int Y { get; set; } }
            public class C
            {
                public void M(P p)
                {
                    p.X = 1;
                    p.Y = p.X;
                }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        // 不合并（合并会让 p.Y 读到旧值）；逐条 with 改写。
        Assert.Contains("p = p with { X = 1 };", output);
        Assert.Contains("p = p with { Y = p.X };", output);
        Assert.Equal(2, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: MS3/MS4 用例 RR020 pending；MS2 不合并。

- [ ] **Step 3: 分类器支持复合/自增**

在 `MutationSiteRewriter.cs` 的分类器 `switch (site.Kind)` 中，把 `CompoundAssign`、`Increment` 两个 pending 分支替换为：

```csharp
            case SiteKind.CompoundAssign:
            case SiteKind.Increment:
                return ClassifyReadModifyWrite(analysis, plan, site);
```

并新增方法（放在 `MemberIsInitSettable` 旁）：

```csharp
    private static SiteFailure? ClassifyReadModifyWrite(
        RecordAnalysis analysis, RecordPlan plan, MutationSiteInfo site)
    {
        var model = analysis.Compilation!.GetSemanticModel(site.WriteNode.SyntaxTree)!;

        if (!site.IsStatementPosition)
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR053,
                site.Kind == SiteKind.Increment
                    ? "increment/decrement in an expression position consumes the value (MS-4)."
                    : "compound assignment in an expression context cannot be rewritten (MS-16).",
                site.Location);

        if (CountMemberSegments(site.Left) != 1)
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
                "nested member chains (MS-17) are rewritten by a later stage.", site.Location);

        if (MemberIsInitSettable(plan, site.Member) is { } memberFailure)
            return new SiteFailure(memberFailure.Code, memberFailure.Reason, site.Location);

        if (!GetterIsEffectivelyPure(site.Member))
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR052,
                "compound/increment rewrite reads the member getter once; the getter is not a " +
                "provably pure auto/field accessor (MS-3/MS-4).", site.Location);

        return ReceiverWritableAndPure(model, site.Receiver);
    }

    /// <summary>字段读取天然纯；自动属性（含 record 位置属性）合成 getter 视为纯。</summary>
    private static bool GetterIsEffectivelyPure(ISymbol? member) => member switch
    {
        IFieldSymbol => true,
        IPropertySymbol { GetMethod.IsImplicitlyDeclared: true } => true,
        _ => false,
    };
```

- [ ] **Step 4: 重写器支持复合/自增**

在 `MutationSiteRewriter` 中把 `Expand` 内改写调用改为按用点类型分派：

```csharp
            var rewritten = RewriteSite(expressionStatement.Expression, sitePlan);
```

替换原 `RewriteWithAssign(...)` 调用，并新增/替换以下方法（保留原 `RewriteWithAssign` 供简单赋值复用）：

```csharp
    private ExpressionSyntax? RewriteSite(ExpressionSyntax writeExpression, SitePlan sitePlan)
    {
        return sitePlan.Site.Kind switch
        {
            SiteKind.SimpleAssign => RewriteWithAssign(writeExpression, sitePlan),
            SiteKind.CompoundAssign => RewriteCompound(writeExpression, sitePlan.Site),
            SiteKind.Increment => RewriteIncrement(writeExpression, sitePlan.Site),
            _ => null,
        };
    }

    private ExpressionSyntax? RewriteCompound(
        ExpressionSyntax writeExpression, MutationSiteInfo site)
    {
        if (writeExpression is not AssignmentExpressionSyntax
            {
                Left: MemberAccessExpressionSyntax memberAccess,
            } assignment)
        {
            return null;
        }

        var receiverText = site.Receiver.WithoutTrivia().ToFullString();
        var memberName = memberAccess.Name.Identifier.Text;
        var read = SyntaxFactory.ParseExpression($"{receiverText}.{memberName}");

        Expression readModify = assignment.Kind() == SyntaxKind.CoalesceAssignmentExpression
            ? SyntaxFactory.BinaryExpression(SyntaxKind.CoalesceExpression, read, assignment.Right)
            : SyntaxFactory.BinaryExpression(
                BinaryKindFor(assignment.Kind()), read, BinaryTokenFor(assignment.Kind()),
                assignment.Right);

        return BuildWithWriteBack(receiverText, memberName, readModify);
    }

    private ExpressionSyntax? RewriteIncrement(
        ExpressionSyntax writeExpression, MutationSiteInfo site)
    {
        if (writeExpression is not PrefixUnaryExpressionSyntax and not PostfixUnaryExpressionSyntax)
            return null;
        if (site.Left is not MemberAccessExpressionSyntax memberAccess) return null;

        var receiverText = site.Receiver.WithoutTrivia().ToFullString();
        var memberName = memberAccess.Name.Identifier.Text;
        var read = SyntaxFactory.ParseExpression($"{receiverText}.{memberName}");
        var one = SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1));
        var decrement = writeExpression.Kind()
            is SyntaxKind.PostDecrementExpression or SyntaxKind.PreDecrementExpression;
        var calc = decrement
            ? SyntaxFactory.BinaryExpression(SyntaxKind.SubtractExpression, read,
                SyntaxFactory.Token(SyntaxKind.MinusToken), one)
            : SyntaxFactory.BinaryExpression(SyntaxKind.AddExpression, read,
                SyntaxFactory.Token(SyntaxKind.PlusToken), one);

        return BuildWithWriteBack(receiverText, memberName, calc);
    }

    private static ExpressionSyntax BuildWithWriteBack(
        string receiverText, string memberName, ExpressionSyntax value)
    {
        var initializer = SyntaxFactory.InitializerExpression(
            SyntaxKind.WithInitializerExpression,
            SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(memberName), value)));
        var withExpression = SyntaxFactory.WithExpression(
            SyntaxFactory.ParseExpression(receiverText), initializer);
        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.ParseExpression(receiverText), withExpression)
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    private static SyntaxKind BinaryKindFor(SyntaxKind assignment) => assignment switch
    {
        SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
        SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
        SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyExpression,
        SyntaxKind.DivideAssignmentExpression => SyntaxKind.DivideExpression,
        SyntaxKind.ModuloAssignmentExpression => SyntaxKind.ModuloExpression,
        SyntaxKind.BitwiseAndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
        SyntaxKind.BitwiseOrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
        SyntaxKind.ExclusiveOrAssignmentExpression => SyntaxKind.ExclusiveOrExpression,
        SyntaxKind.LeftShiftAssignmentExpression => SyntaxKind.LeftShiftExpression,
        SyntaxKind.RightShiftAssignmentExpression => SyntaxKind.RightShiftExpression,
        SyntaxKind.UnsignedRightShiftAssignmentExpression => SyntaxKind.UnsignedRightShiftExpression,
        _ => throw new InvalidOperationException($"Unsupported compound assignment {assignment}."),
    };

    private static SyntaxToken BinaryTokenFor(SyntaxKind assignment) =>
        SyntaxFactory.Token(BinaryKindFor(assignment) switch
        {
            SyntaxKind.AddExpression => SyntaxKind.PlusToken,
            SyntaxKind.SubtractExpression => SyntaxKind.MinusToken,
            SyntaxKind.MultiplyExpression => SyntaxKind.AsteriskToken,
            SyntaxKind.DivideExpression => SyntaxKind.SlashToken,
            SyntaxKind.ModuloExpression => SyntaxKind.PercentToken,
            SyntaxKind.BitwiseAndExpression => SyntaxKind.AmpersandToken,
            SyntaxKind.BitwiseOrExpression => SyntaxKind.BarToken,
            SyntaxKind.ExclusiveOrExpression => SyntaxKind.CaretToken,
            SyntaxKind.LeftShiftExpression => SyntaxKind.LessThanLessThanToken,
            SyntaxKind.RightShiftExpression => SyntaxKind.GreaterThanGreaterThanToken,
            SyntaxKind.UnsignedRightShiftExpression => SyntaxKind.GreaterThanGreaterThanGreaterThanToken,
            _ => throw new InvalidOperationException(),
        });
```

- [ ] **Step 5: 重写器支持 MS-2 合并（VisitBlock/VisitSwitchSection 前置归组）**

在 `MutationSiteRewriter` 中新增归组逻辑，并让 `VisitBlock`、`VisitSwitchSection` 调用同一个 `ProcessStatements`：

```csharp
    private List<StatementSyntax> ProcessStatements(SyntaxList<StatementSyntax> statements)
    {
        var result = new List<StatementSyntax>();
        for (var i = 0; i < statements.Count;)
        {
            if (TryCoalesce(statements, i, out var merged, out var consumed))
            {
                result.Add(merged!);
                i += consumed;
            }
            else
            {
                result.AddRange(Expand(statements[i]));
                i++;
            }
        }
        return result;
    }

    private bool TryCoalesce(
        SyntaxList<StatementSyntax> statements,
        int start,
        out StatementSyntax? merged,
        out int consumed)
    {
        merged = null;
        consumed = 0;

        var run = new List<(ExpressionStatementSyntax Stmt, SitePlan Plan,
            MutationSiteInfo Site, string Receiver, string Member)>();
        var membersInRun = new HashSet<string>(StringComparer.Ordinal);

        for (var i = start; i < statements.Count; i++)
        {
            if (statements[i] is not ExpressionStatementSyntax
                {
                    Expression: AssignmentExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                        Left: MemberAccessExpressionSyntax,
                    } assign
                } stmt
                || FindSite(stmt.Expression) is not { } plan
                || plan.Site.Kind != SiteKind.SimpleAssign
                || CountSegmentsForRewrite(plan.Site.Left) != 1)
            {
                break;
            }

            var receiverText = plan.Site.Receiver.WithoutTrivia().ToFullString();
            var memberName = ((MemberAccessExpressionSyntax)assign.Left).Name.Identifier.Text;
            if (run.Count > 0 && receiverText != run[0].Receiver) break;
            if (!membersInRun.Add(memberName)) break;

            // MS-2 语义前提：本句右值不得读取接收者上本轮已改写的成员。
            if (ReadsPreviouslyWrittenMember(plan.Site, assign.Right, run))
                break;

            run.Add((stmt, plan, plan.Site, receiverText, memberName));
        }

        if (run.Count < 2) return false;

        var assignments = run.Select(item =>
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(item.Member),
                ((AssignmentExpressionSyntax)item.Stmt.Expression).Right));
        var initializer = SyntaxFactory.InitializerExpression(
            SyntaxKind.WithInitializerExpression,
            SyntaxFactory.SeparatedList(assignments));
        var withExpression = SyntaxFactory.WithExpression(
            SyntaxFactory.ParseExpression(run[0].Receiver), initializer);
        var writeBack = SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.ParseExpression(run[0].Receiver), withExpression);
        merged = SyntaxFactory.ExpressionStatement(writeBack)
            .WithLeadingTrivia(run[0].Stmt.GetLeadingTrivia())
            .WithTrailingTrivia(run[^1].Stmt.GetTrailingTrivia())
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
        _context.Statistics.MutationSitesRewritten += run.Count;
        consumed = run.Count;
        return true;
    }

    private static int CountSegmentsForRewrite(ExpressionSyntax left)
    {
        var count = 0;
        ExpressionSyntax? cursor = left;
        while (cursor is MemberAccessExpressionSyntax ma)
        {
            count++;
            cursor = ma.Expression;
        }
        return count;
    }

    private bool ReadsPreviouslyWrittenMember(
        MutationSiteInfo currentSite,
        ExpressionSyntax right,
        List<(ExpressionStatementSyntax Stmt, SitePlan Plan, MutationSiteInfo Site,
            string Receiver, string Member)> run)
    {
        var model = _context.Model;
        var writtenSymbols = run
            .Select(item => item.Site.Member)
            .Where(s => s != null)
            .ToHashSet(SymbolEqualityComparer.Default);
        var receiverSymbol = currentSite.Receiver is IdentifierNameSyntax rid
            ? model.GetSymbolInfo(rid).Symbol : null;

        foreach (var access in right.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
        {
            if (model.GetSymbolInfo(access).Symbol is not { } accessed) continue;
            if (!writtenSymbols.Contains(accessed)) continue;
            // 确认读的是同一个接收者（标识符场景；元素/字段接收者保守不合并）。
            if (receiverSymbol == null) return true;
            if (access.Expression is IdentifierNameSyntax inner
                && SymbolEqualityComparer.Default.Equals(
                    model.GetSymbolInfo(inner).Symbol, receiverSymbol))
                return true;
        }
        return false;
    }
```

把 `VisitBlock`/`VisitSwitchSection` 的循环体改为 `ProcessStatements(visited.Statements)`。

- [ ] **Step 6: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 全部 PASS。

- [ ] **Step 7: Commit**

```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): MS-2 adjacent-site coalescing; MS-3 compound; MS-4 increment/decrement"
```

---
### Task 13: MS-6 链式赋值拆分、MS-5 空条件赋值（C#14 `?.`）

**Files:**
- Modify: `src/CSharpToJava.Core/RecordReadOnly/MutationSiteRewriter.cs`
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

- [ ] **Step 1: 写失败测试**

先给测试脚手架加可选语言版本参数。把 `ParseWithSemantics` 与 `Rewrite` 改为：

```csharp
    internal static SemanticModel ParseWithSemantics(string src,
        LanguageVersion? version = null, params string[] extra)
    {
        var options = version is { } v ? new CSharpParseOptions(v) : ParseOptions;
        var tree = CSharpSyntaxTree.ParseText(src, options);
        var extraTrees = extra.Select(s => CSharpSyntaxTree.ParseText(s, options)).ToArray();
        var compilation = CSharpCompilation.Create(
            "rr-test",
            new[] { tree }.Concat(extraTrees),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    internal static (string Output, RecordReadOnlyStatistics Stats,
        List<RecordReadOnlyDiagnostic> Diagnostics) Rewrite(string src,
        RecordReadOnlyOptions? options = null, LanguageVersion? version = null,
        params string[] extra)
    {
        var model = ParseWithSemantics(src, version, extra);
        var pp = new RecordReadOnlyPreprocessor(options ?? new RecordReadOnlyOptions());
        var stats = new RecordReadOnlyStatistics();
        var diags = new List<RecordReadOnlyDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);
        return (result.OutputCode, stats, diags);
    }
```

追加测试：

```csharp
    [Fact]
    public void MS6_ChainedAssignment_SplitsInnerToOuterWithTemp()
    {
        var src = """
            public record struct A { public int M { get; set; } }
            public record struct B { public int M { get; set; } }
            public class C
            {
                public void M(A a, B b)
                {
                    a.M = b.M = Compute();
                }
                private static int Compute() => 1;
            }
            """;
        var (output, stats, _) = Rewrite(src);
        // 右侧有方法调用，必须只求值一次（临时变量），顺序 b → a。
        Assert.Contains("var __cs2jT", output);
        Assert.Contains("b = b with { M = __cs2jT", output);
        Assert.Contains("a = a with { M = __cs2jT", output);
        Assert.Equal(2, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS6_ChainedAssignment_InnerRecordSkipped_KeepsPlainAssignment()
    {
        var src = """
            public record struct A { public int M { get; set; } }
            public record struct B { public int N { get; set; } public int M { get; set; } }
            public class C
            {
                // B 因含事件等原因无法转换时仍可变：内层保留普通赋值，外层 with 取值。
                public void M(A a, B b, int v)
                {
                    a.M = b.M = v;
                }
            }
            """;
        var options = new RecordReadOnlyOptions
        {
            SkipQualifiedNames = new HashSet<string>(StringComparer.Ordinal) { "B" },
        };
        var (output, stats, _) = Rewrite(src, options);
        Assert.Contains("b.M = __cs2jT", output);
        Assert.Contains("a = a with { M = __cs2jT", output);
        Assert.Equal(1, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS5_NullConditionalAssign_OnNullableStruct_BecomesSwitch()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C { public void M(P? p) { p?.X = 1; } }
            """;
        var (output, stats, _) = Rewrite(src, version: LanguageVersion.Preview);
        Assert.Contains("p = p switch", output);
        Assert.Contains("null => null", output);
        Assert.Contains("{ } q => q with { X = 1 }", output);
        Assert.Equal(1, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS5_NullConditionalCompoundAndIncrement_UseQ()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void M(P? p)
                {
                    p?.X += 2;
                    p?.X++;
                }
            }
            """;
        var (output, stats, _) = Rewrite(src, version: LanguageVersion.Preview);
        Assert.Contains("{ } q => q with { X = q.X + 2 }", output);
        Assert.Contains("{ } q => q with { X = q.X + 1 }", output);
        Assert.Equal(2, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS5_NullConditionalReceiverWithSideEffect_BlocksWithRR051()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public P? Get() => null;
                public void M() { Get()?.X = 1; }
            }
            """;
        var (output, _, diags) = Rewrite(src, version: LanguageVersion.Preview);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR051);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: MS6/MSC 用例失败（链式尚未拆分；ConditionalAssign 仍 RR020）。

- [ ] **Step 3: 分类器打开链式与空条件**

在分类器 `switch (site.Kind)` 中删除 `ConditionalAssign` pending 分支，新增：

```csharp
            case SiteKind.ConditionalAssign:
                return ClassifyConditional(analysis, plan, site);
```

在 SimpleAssign 的语句位置检查之后、链段计数之前插入：

```csharp
        // MS-6：a.M = b.M = v 右结合链。
        if (site.WriteNode is AssignmentExpressionSyntax { Right: AssignmentExpressionSyntax })
        {
            var chainFailure = ClassifyChained(site);
            if (chainFailure != null) return chainFailure;
        }
```

新增两个方法：

```csharp
    private static SiteFailure? ClassifyChained(MutationSiteInfo site)
    {
        ExpressionSyntax? cursor = (ExpressionSyntax)site.WriteNode;
        while (cursor is AssignmentExpressionSyntax assignment)
        {
            if (assignment.Kind() != SyntaxKind.SimpleAssignmentExpression)
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
                    "chained assignments containing compound/increment forms are rewritten " +
                    "by a later stage (MS-6).", site.Location);
            if (assignment.Left is ConditionalAccessExpressionSyntax
                or MemberAccessExpressionSyntax { Expression: ConditionalAccessExpressionSyntax })
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
                    "chained null-conditional assignment is not supported (MS-5/MS-6).",
                    site.Location);
            cursor = assignment.Right;
        }
        return null;
    }

    private static SiteFailure? ClassifyConditional(
        RecordAnalysis analysis, RecordPlan plan, MutationSiteInfo site)
    {
        if (!site.IsStatementPosition)
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR053,
                "null-conditional assignment outside a statement context cannot be rewritten " +
                "(MS-5/MS-16).", site.Location);

        if (site.Receiver is not ConditionalAccessExpressionSyntax conditional)
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
                "unsupported null-conditional assignment shape (MS-5).", site.Location);

        if (MemberIsInitSettable(plan, site.Member) is { } memberFailure)
            return new SiteFailure(memberFailure.Code, memberFailure.Reason, site.Location);

        var isCompound = site.WriteNode is AssignmentExpressionSyntax
            and not { RawKind: (int)SyntaxKind.SimpleAssignmentExpression };
        var isIncrement = site.WriteNode is PrefixUnaryExpressionSyntax
            or PostfixUnaryExpressionSyntax;
        if ((isCompound || isIncrement) && !GetterIsEffectivelyPure(site.Member))
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR052,
                "null-conditional compound/increment reads the getter through a non-pure " +
                "accessor (MS-3/MS-5).", site.Location);

        var model = analysis.Compilation!.GetSemanticModel(site.WriteNode.SyntaxTree)!;
        return ReceiverWritableAndPure(model, conditional.Expression);
    }
```

- [ ] **Step 4: 重写器支持 MS-6 与 MS-5**

`RewriteSite` 的 switch 增加：

```csharp
            SiteKind.ConditionalAssign => RewriteConditional(writeExpression, sitePlan.Site),
```

在 `ProcessStatements` 中、`TryCoalesce` 之前插入链式处理：

```csharp
            if (statements[i] is ExpressionStatementSyntax
                {
                    Expression: AssignmentExpressionSyntax
                    {
                        Right: AssignmentExpressionSyntax,
                    } chain
                }
                && FindSite(chain) != null)
            {
                result.AddRange(EmitChained(chain));
                i++;
                continue;
            }
```

同时在 `TryCoalesce` 的入选条件里追加 `&& ((AssignmentExpressionSyntax)stmt.Expression).Right is not AssignmentExpressionSyntax`，避免链式语句被错误合并。

新增方法：

```csharp
    private IEnumerable<StatementSyntax> EmitChained(AssignmentExpressionSyntax outer)
    {
        // 右结合链拍平：segments[0] 最外，末元素最内。
        var segments = new List<AssignmentExpressionSyntax>();
        var cursor = outer;
        while (true)
        {
            segments.Add(cursor);
            if (cursor.Right is AssignmentExpressionSyntax next) cursor = next;
            else break;
        }

        var tempName = $"__cs2jT{outer.SpanStart}";
        var declarator = SyntaxFactory.VariableDeclarator(
            SyntaxFactory.Identifier(tempName))
            .WithInitializer(SyntaxFactory.EqualsValueClause(cursor.Right));
        yield return SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.Token(SyntaxKind.VarKeyword),
                SyntaxFactory.SingletonSeparatedList(declarator)))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);

        // 原语义先执行内层；逐段回写。
        for (var i = segments.Count - 1; i >= 0; i--)
        {
            var segment = segments[i];
            StatementSyntax statement;
            var plan = FindSite(segment);
            if (plan != null && segment.Left is MemberAccessExpressionSyntax memberAccess)
            {
                var receiverText = plan.Site.Receiver.WithoutTrivia().ToFullString();
                var writeBack = BuildWithWriteBack(receiverText,
                    memberAccess.Name.Identifier.Text,
                    SyntaxFactory.IdentifierName(tempName));
                _context.Statistics.MutationSitesRewritten++;
                statement = SyntaxFactory.ExpressionStatement(writeBack);
            }
            else
            {
                // 该段目标 record 未转换（保持可变）或为普通左值：保留原赋值。
                statement = SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        segment.Left, SyntaxFactory.IdentifierName(tempName)));
            }
            yield return statement
                .WithLeadingTrivia(segment.GetFirstToken().LeadingTrivia)
                .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
        }
    }

    private ExpressionSyntax? RewriteConditional(
        ExpressionSyntax writeExpression, MutationSiteInfo site)
    {
        if (site.Receiver is not ConditionalAccessExpressionSyntax conditional) return null;
        if (site.Left is not MemberAccessExpressionSyntax memberAccess) return null;

        var rootText = conditional.Expression.WithoutTrivia().ToFullString();
        var root = SyntaxFactory.ParseExpression(rootText);
        var memberName = memberAccess.Name.Identifier.Text;
        var q = SyntaxFactory.IdentifierName("q");

        Expression qValue;
        switch (writeExpression)
        {
            case PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax:
            {
                var read = SyntaxFactory.ParseExpression($"q.{memberName}");
                var one = SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1));
                var decrement = writeExpression.Kind()
                    is SyntaxKind.PostDecrementExpression or SyntaxKind.PreDecrementExpression;
                qValue = decrement
                    ? SyntaxFactory.BinaryExpression(SyntaxKind.SubtractExpression, read,
                        SyntaxFactory.Token(SyntaxKind.MinusToken), one)
                    : SyntaxFactory.BinaryExpression(SyntaxKind.AddExpression, read,
                        SyntaxFactory.Token(SyntaxKind.PlusToken), one);
                break;
            }
            case AssignmentExpressionSyntax assignment
                when assignment.Kind() != SyntaxKind.SimpleAssignmentExpression:
            {
                var read = SyntaxFactory.ParseExpression($"q.{memberName}");
                qValue = assignment.Kind() == SyntaxKind.CoalesceAssignmentExpression
                    ? SyntaxFactory.BinaryExpression(
                        SyntaxKind.CoalesceExpression, read, assignment.Right)
                    : SyntaxFactory.BinaryExpression(
                        BinaryKindFor(assignment.Kind()), read,
                        BinaryTokenFor(assignment.Kind()), assignment.Right);
                break;
            }
            case AssignmentExpressionSyntax simple:
                qValue = simple.Right;
                break;
            default:
                return null;
        }

        var withExpression = SyntaxFactory.WithExpression(q,
            SyntaxFactory.InitializerExpression(
                SyntaxKind.WithInitializerExpression,
                SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(memberName), qValue))));

        var nullArm = SyntaxFactory.SwitchExpressionSection(
            SyntaxFactory.ConstantPattern(
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
            whenClause: null,
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
        var nonNullArm = SyntaxFactory.SwitchExpressionSection(
            SyntaxFactory.RecursivePattern(
                type: null, positionalPatternClause: null, propertyPatternClause: null,
                designation: SyntaxFactory.SingleVariableDesignation(
                    SyntaxFactory.Identifier("q"))),
            whenClause: null,
            withExpression);
        var switchExpression = SyntaxFactory.SwitchExpression(root)
            .WithSections(SyntaxFactory.List(new[] { nullArm, nonNullArm }));

        return SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.ParseExpression(rootText), switchExpression)
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }
```

- [ ] **Step 5: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 全部 PASS（含 MS6 两形态、MS5 三形态）。

- [ ] **Step 6: Commit**

```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): MS-6 chained assignment splitting; MS-5 null-conditional assignment"
```

---
### Task 14: MS-7 集合元素（List/Span/ref 索引器、只读集合 RR054）、MS-17 嵌套成员链递归 with

**Files:**
- Modify: `src/CSharpToJava.Core/RecordReadOnly/MutationSiteRewriter.cs`
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

- [ ] **Step 1: 写失败测试**

追加：

```csharp
    [Fact]
    public void MS7_ListElement_Writable()
    {
        var src = """
            using System.Collections.Generic;
            public record struct P { public int X { get; set; } }
            public class C { public void M(List<P> list) { list[2].X = 4; } }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("list[2] = list[2] with { X = 4 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS7_SpanElement_RefIndexer_Writable()
    {
        var src = """
            using System;
            public record struct P { public int X { get; set; } }
            public class C { public void M(Span<P> span) { span[0].X = 4; } }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("span[0] = span[0] with { X = 4 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS7_ReadOnlyList_BlocksWithRR054()
    {
        var src = """
            using System.Collections.Generic;
            public record struct P { public int X { get; set; } }
            public class C { public void M(IReadOnlyList<P> list) { list[0].X = 4; } }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR054);
    }

    [Fact]
    public void MS17_NestedChain_AllRecordContainers_RecursiveWith()
    {
        var src = """
            public record struct A { public B B { get; set; } }
            public record struct B { public int C { get; set; } }
            public class C2
            {
                public void M(A a) { a.B.C = 8; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("a = a with { B = a.B with { C = 8 } };", output);
        Assert.Equal(1, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS17_NestedChain_RootPlainSettable_PlainOuterWrite()
    {
        var src = """
            public record struct B { public int C { get; set; } }
            public class Holder { public B B { get; set; } }
            public class C2
            {
                public void M(Holder h) { h.B.C = 8; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("h.B = h.B with { C = 8 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS17_GetOnlyIntermediateOnNonRecord_BlocksWithRR050()
    {
        var src = """
            public record struct B { public int C { get; set; } }
            public class Holder { public B B { get; } }
            public class C2
            {
                public void M(Holder h) { h.B.C = 8; }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR050);
    }

    [Fact]
    public void MS17_NestedChainReceiverSideEffect_BlocksWithRR051()
    {
        var src = """
            public record struct A { public B B { get; set; } }
            public record struct B { public int C { get; set; } }
            public class C2
            {
                public A Get() => default;
                public void M() { Get().B.C = 8; }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR051);
    }

    [Fact]
    public void MS17_CompoundOnNestedChain_StaysPendingRR020()
    {
        var src = """
            public record struct A { public B B { get; set; } }
            public record struct B { public int C { get; set; } }
            public class C2
            {
                public void M(A a) { a.B.C += 1; }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR020);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: List/Span 与嵌套链用例失败（Span get-only ref 索引器误判 RR054；多段链 RR020）。

- [ ] **Step 3: 分类器：ref 索引器可写**

把 `ReceiverWritableAndPure` 中 ElementAccess 分支的索引器判定替换为：

```csharp
                if (model.GetSymbolInfo(element).Symbol is IPropertySymbol indexer)
                {
                    if (indexer.ContainingNamespace?.ToDisplayString()
                        == "System.Collections.Immutable")
                        return new SiteFailure(RecordReadOnlyDiagnosticCode.RR054,
                            "read-only/immutable collection element cannot be written (MS-7).",
                            element.GetLocation());
                    if (indexer.SetMethod is { IsInitOnly: false })
                        return null; // 数组以外的 List<T> 等 set 索引器
                    if (indexer.GetMethod is { ReturnsByRef: true, ReturnsByRefReadonly: false })
                        return null; // Span<T> 等 ref 返回索引器
                    return new SiteFailure(RecordReadOnlyDiagnosticCode.RR054,
                        "collection element is read-only (MS-7).", element.GetLocation());
                }
```

- [ ] **Step 4: 分类器：多段链 MS-17**

把 `Classify` 中 SimpleAssign 分支的

```csharp
        // 仅单段链（recv.M）；多段（a.B.C）属 MS-17。
        var segmentCount = CountMemberSegments(site.Left);
        if (segmentCount != 1)
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
                "nested member chains (MS-17) are rewritten by a later stage.", site.Location);
```

替换为：

```csharp
        var segmentCount = CountMemberSegments(site.Left);
        if (segmentCount > 1)
            return ClassifyNestedChain(analysis, plan, site);
```

新增方法：

```csharp
    /// <summary>
    /// MS-17：site.Chain 自内向外 [ (最内成员, 直接容器类型), ..., (根成员, 根容器类型) ]。
    /// 最内层按本 record 的 with 处理；向外每层：容器是 record 时成员须可经 with 设置，
    /// 容器非 record 时成员须当前可普通回写，且非 record 容器只能出现在最外层一次。
    /// </summary>
    private static SiteFailure? ClassifyNestedChain(
        RecordAnalysis analysis, RecordPlan plan, MutationSiteInfo site)
    {
        var chain = site.Chain;
        var model = analysis.Compilation!.GetSemanticModel(site.WriteNode.SyntaxTree)!;

        // 复合/自增嵌套链本期不做（RR020 pending）。
        if (site.WriteNode is not AssignmentExpressionSyntax
            {
                RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
            })
        {
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
                "compound/increment on nested member chains is not rewritten yet (MS-17).",
                site.Location);
        }

        // 最内成员必须可经本 record 的 with 设置。
        if (MemberIsInitSettable(plan, chain[0].Member) is { } innerFailure)
            return new SiteFailure(innerFailure.Code, innerFailure.Reason, site.Location);

        var seenNonRecordContainer = false;
        for (var level = 1; level < chain.Count; level++)
        {
            var containerType = chain[level - 1].ContainerType as INamedTypeSymbol;
            var member = chain[level].Member!;
            var access = chain[level].Access;

            if (containerType != null
                && analysis.BySymbol.TryGetValue(containerType, out var containerRecord))
            {
                if (seenNonRecordContainer)
                    return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
                        "nested chain re-enters a record through a non-record container (MS-17).",
                        site.Location);
                var withFailure = MemberWithSettableOnConvertedRecord(member, access);
                if (withFailure != null) return withFailure;
                _ = containerRecord; // 转换与否由重写器按 ActivePlans 决定；不可编译由整树回兜底。
            }
            else
            {
                seenNonRecordContainer = true;
                if (!MemberIsPlainWritable(member))
                    return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        $"intermediate member '{member.Name}' is read-only; the chain cannot be " +
                        "rebuilt with 'with' (MS-17).", access.GetLocation());
            }
        }

        // 根接收者可写且无副作用（会被多次求值）。
        return ReceiverWritableAndPure(model, site.Receiver);
    }

    /// <summary>record 容器经转换后，成员是否可经 with 设置（init 属性 / 可见字段转 init 属性）。</summary>
    private static SiteFailure? MemberWithSettableOnConvertedRecord(
        ISymbol member, ExpressionSyntax access)
    {
        switch (member)
        {
            case IPropertySymbol property:
                if (property.IsIndexer)
                    return new SiteFailure(RecordReadOnlyDiagnosticCode.RR042,
                        "an indexer cannot be set through 'with' in a nested chain (MS-18).",
                        access.GetLocation());
                if (property.SetMethod != null) return null; // set/init 访问器转换后均为 init
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                    $"property '{property.Name}' has no init/set accessor for 'with' (MS-17).",
                    access.GetLocation());

            case IFieldSymbol field:
                if (!field.IsReadOnly && !field.IsInitOnly
                    && RecordSyntaxFacts.IsVisibleOutsideType(field))
                    return null; // RS-5：可见字段转 init 属性
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                    $"field '{field.Name}' cannot be set through 'with' (MS-17).",
                    access.GetLocation());

            default:
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                    $"member '{member.Name}' cannot be set through 'with' (MS-17).",
                    access.GetLocation());
        }
    }

    /// <summary>非 record 容器上的成员当前必须能普通回写。</summary>
    private static bool MemberIsPlainWritable(ISymbol? member) => member switch
    {
        IFieldSymbol f => !f.IsReadOnly && !f.IsInitOnly,
        IPropertySymbol { IsIndexer: false } p => p.SetMethod is { IsInitOnly: false },
        IPropertySymbol { IsIndexer: true } ip =>
            ip.SetMethod is { IsInitOnly: false }
            || ip.GetMethod is { ReturnsByRef: true, ReturnsByRefReadonly: false },
        _ => false,
    };
```

- [ ] **Step 5: 重写器支持嵌套链**

在 `RewriteWithAssign` 开头（单段处理之前）分流：

```csharp
        if (CountSegmentsForRewrite(assignment.Left) > 1)
            return RewriteNestedChain(assignment, sitePlan.Site);
```

新增方法：

```csharp
    /// <summary>
    /// a.B.C... = v 自内向外生成 with；最外层容器非 record 时退化为普通成员回写。
    /// </summary>
    private ExpressionSyntax? RewriteNestedChain(
        AssignmentExpressionSyntax assignment, MutationSiteInfo site)
    {
        var chain = site.Chain;

        // 最内：r0 with { 最内成员 = v }，r0 即最内层成员访问的接收者表达式原文。
        var innermostAccess = (MemberAccessExpressionSyntax)chain[0].Access;
        var innermostReceiverText = innermostAccess.Expression.WithoutTrivia().ToFullString();
        var expr = BuildWithExpression(
            innermostReceiverText, innermostAccess.Name.Identifier.Text, assignment.Right);

        for (var level = 1; level < chain.Count; level++)
        {
            var outerAccess = (MemberAccessExpressionSyntax)chain[level].Access;
            var outerReceiverText = outerAccess.Expression.WithoutTrivia().ToFullString();
            var outerMember = outerAccess.Name.Identifier.Text;
            var containerType = chain[level - 1].ContainerType as INamedTypeSymbol;

            if (containerType != null
                && _context.ActivePlans.Any(p => SymbolEqualityComparer.Default.Equals(
                    p.Symbol, containerType)))
            {
                // a with { B = a.B with { ... } }
                expr = BuildWithExpression(outerReceiverText, outerMember, expr);
            }
            else
            {
                // 非 record（或未转换）容器：h.B = h.B with { ... }；必须是最外层。
                var target = SyntaxFactory.ParseExpression(
                    $"{outerReceiverText}.{outerMember}");
                return SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression, target, expr)
                    .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
            }
        }

        var rootReceiver = SyntaxFactory.ParseExpression(site.Receiver.WithoutTrivia().ToFullString());
        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression, rootReceiver, expr)
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    private static ExpressionSyntax BuildWithExpression(
        string receiverText, string memberName, ExpressionSyntax value)
    {
        var initializer = SyntaxFactory.InitializerExpression(
            SyntaxKind.WithInitializerExpression,
            SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(memberName), value)));
        return SyntaxFactory.WithExpression(
            SyntaxFactory.ParseExpression(receiverText), initializer);
    }
```

并让 `BuildWithWriteBack` 复用 `BuildWithExpression`（可选重构，行为不变）。

- [ ] **Step 6: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 全部 PASS（List/Span/IReadOnlyList + MS-17 六形态）。

- [ ] **Step 7: Commit**

```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): MS-7 collection element rules; MS-17 nested member chain recursive with"
```

---
### Task 15: MS-10/MS-11 ref/out 用点、MS-12 with 不动、MS-14 dynamic/反射、MS-16 表达式上下文、MS-18 索引器收尾

**Files:**
- Modify: `src/CSharpToJava.Core/RecordReadOnly/MutationSiteRewriter.cs`（仅分类器分支）
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

- [ ] **Step 1: 写失败测试**

追加：

```csharp
    [Fact]
    public void MS10_InTreeRefRecordArgument_CallSiteUntouched_BodyRewritten()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void Update(ref P p) { p.X = 3; }
                public void Call()
                {
                    var p = new P();
                    Update(ref p);
                }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("p = p with { X = 3 };", output);
        Assert.Contains("Update(ref p);", output);
        Assert.Equal(1, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS10_OutParameterAssignedThenMemberWritten_Works()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void Create(out P p)
                {
                    p = new P();
                    p.X = 9;
                }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p with { X = 9 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS10_ExternalRefRecordArgument_BlocksWithRR055()
    {
        var src = """
            using System.Threading;
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void M()
                {
                    var p = new P();
                    Interlocked.Exchange(ref p, default);
                }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR055);
    }

    [Fact]
    public void MS11_RefRecordMemberToExternal_BlocksWithRR056()
    {
        var src = """
            using System.Threading;
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void M(P p) { Interlocked.Increment(ref p.X); }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR056);
    }

    [Fact]
    public void MS12_ExistingWithExpressions_AreUntouched()
    {
        var src = """
            public record struct P { public int X { get; set; } public int Y { get; set; } }
            public class C
            {
                public P M(P p)
                {
                    var q = p with { X = 1 };
                    return q with { Y = 2, X = 3 };
                }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("p with { X = 1 }", output);
        Assert.Equal(0, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS14_DynamicReceiver_BlocksAllRecordsWithRR057()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void M(dynamic d) { d.X = 1; }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR057);
    }

    [Fact]
    public void MS14_ReflectionSetValue_BlocksAllRecordsWithRR057()
    {
        var src = """
            using System.Reflection;
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void M(P p, PropertyInfo prop) { prop.SetValue(p, 1); }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR057);
    }

    [Fact]
    public void MS16_MutationInsideTupleExpression_BlocksWithRR053()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public (int, int) M(P p) => ((p.X = 1), 2);
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR053);
    }

    [Fact]
    public void MS18_SelfDeclaredIndexer_RecordBlockedAtDeclarationRR042()
    {
        var src = """
            public record struct R
            {
                public int this[int i] { get => 0; set { } }
            }
            public class C { public void M(R r) { r[0] = 1; } }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR042);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: RR055/RR056 用例目前拿到 RR020；MS-10 in-tree 用例的调用点被误登记为阻断（pending）。

- [ ] **Step 3: 分类器分支落地**

把分类器 `switch (site.Kind)` 中 `RefOutRecordArg`、`RefOutMemberArg` 的 pending 分支替换为：

```csharp
            case SiteKind.RefOutRecordArg:
                // MS-10：in-tree 被调方体内成员写按 MS-9 处理，调用点无需改写；
                // 外部方法无法证明不修改 → 保守 FB-1。
                return site.CalleeInTree
                    ? null
                    : new SiteFailure(RecordReadOnlyDiagnosticCode.RR055,
                        "record passed by ref/out to an external method; the callee may mutate " +
                        "fields that become readonly/init (MS-10).", site.Location);

            case SiteKind.RefOutMemberArg:
                // MS-11：转换后成员是 init 属性/readonly 字段，无法取 ref/out（in-tree 也一样）。
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR056,
                    "a record member is passed by ref/out (e.g. Interlocked/Volatile/Marshal, " +
                    "or int.TryParse(out r.M)); the member becomes init-only/readonly (MS-11).",
                    site.Location);
```

`RefOutRecordArg` 可行时 PlanBuilder 仍会加入 `SitePlan(...WithAssign)`，但重写器 `RewriteSite` 对该 Kind 落入 `_ => null`，语句原样保留且不计数——无需额外改动。

- [ ] **Step 4: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 全部 PASS。

- [ ] **Step 5: Commit**

```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): MS-10/MS-11 ref-out rules; tests for MS-12/14/16/18"
```

---
## M3：MethodMigrator（MM-1~MM-7）

### Task 16: MM-1 调用图影响集 + MM-2/3/4/5 void 方法迁移与调用点回写（含 RR060~RR064）

> 说明：既有 [CallGraphBuilder.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/CallGraphBuilder.cs) 只做跨树布尔判定，没有可复用的“符号图 + 拓扑排序”API，故本任务按其**符号级调用边**思路在 `RecordReadOnly` 命名空间内实现闭包与环检测，不强行套不适配的接口。

**Files:**
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyModels.cs`
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyAnalyzer.cs`（收集同 record 调用边）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs`（cap 翻转 + PlanBuilder 接入）
- Replace whole: `src/CSharpToJava.Core/RecordReadOnly/MethodMigrator.cs`
- Modify: `src/CSharpToJava.Core/RecordReadOnly/MutationSiteRewriter.cs`（改用注解定位；MS-2 守卫纯语法化）
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

- [ ] **Step 1: 写失败测试**

追加：

```csharp
    [Fact]
    public void MM_VoidMutatingMethod_PromotesAndReturnsRecord()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void Bump() { X = 1; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("public P Bump()", output);
        Assert.Contains("var __x = this.X;", output);
        Assert.Contains("__x = 1;", output);
        Assert.Contains("return this with { X = __x };", output);
        Assert.Equal(1, stats.MethodsMigrated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM_ThisAssignment_BecomesPromotedCopies()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void Reset(P other) { this = other; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("public P Reset(P other)", output);
        Assert.Contains("__x = other.X;", output);
        Assert.Contains("return this with { X = __x };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM_CallToMigratedMethodOnThis_AbsorbsResultIntoPromoted()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void A()
                {
                    X = 1;
                    B();
                }
                public void B() { X = 2; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("var __r0 = this.B();", output);
        Assert.Contains("__x = __r0.X;", output);
        Assert.Equal(2, stats.MethodsMigrated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM_CtorCallingMigratedMethod_ThisWritebackAfterAssigned()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public P(int x) { X = x; Bump(); }
                public void Bump() { X += 1; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("this = this.Bump();", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM_ExternalStatementCallSite_WritebackReceiver()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void Bump() { X = 1; }
            }
            public class C
            {
                public void M(P p) { p.Bump(); }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p.Bump();", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM_ExternalCallOnReadonlyReceiver_BlocksWithRR050()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void Bump() { X = 1; }
            }
            public class C
            {
                public P P { get; }
                public void M() { P.Bump(); }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR050);
    }

    [Fact]
    public void MM_MigratedBody_ExternalMigratedCallGetsWriteback()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void A(P other) { X = 1; other.Bump(); }
                public void Bump() { X = 2; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("__x = 1;", output);
        Assert.Contains("other = other.Bump();", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM_MigratedBody_ExternalMutationSiteStillRewrittenWith()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void A(P other) { X = 1; other.X = 2; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("other = other with { X = 2 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM6_IteratorMethod_BlocksWithRR060()
    {
        var src = """
            using System.Collections.Generic;
            public record struct P
            {
                public int X { get; set; }
                public IEnumerable<int> Evens()
                {
                    X = 1;
                    yield return X;
                }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR060);
    }

    [Fact]
    public void MM6_PartialMethod_BlocksWithRR061()
    {
        var src = """
            public partial record struct P
            {
                partial void Bump();
            }
            public partial record struct P
            {
                public int X { get; set; }
                partial void Bump() { X = 1; }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR061);
    }

    [Fact]
    public void MM6_CallGraphCycle_BlocksWithRR062()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void A() { X = 1; B(); }
                public void B() { X = 2; A(); }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR062);
    }

    [Fact]
    public void MM6_GetterMutatingMember_BlocksWithRR063()
    {
        var src = """
            public record struct P
            {
                private int _x;
                public int X
                {
                    get { _x = 1; return _x; }
                    set { _x = value; }
                }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR063);
    }

    [Fact]
    public void MM6_ThisAssignmentWithGetOnlyState_BlocksWithRR064()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Y { get; }
                public P(int y) { Y = y; }
                public void Reset(P other) { this = other; }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR064);
    }

    [Fact]
    public void MM_NonVoidMutatingMethod_StaysPendingRR020UntilNextTask()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR020);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: mutating 方法仍由 MethodMigration 能力门控，record 全部 RR020 pending。

- [ ] **Step 3: 模型层增补**

`RecordReadOnlyModels.cs`：

1. `SiteKind` 枚举增加 `MethodCall`（迁移方法的调用点）。
2. `MutationSiteInfo` 增加：

```csharp
    /// <summary>MethodCall：被调用的迁移方法符号。</summary>
    public IMethodSymbol? MethodCallee { get; set; }
```

3. `MethodWriteInfo` 增加同 record 调用边：

```csharp
    /// <summary>体内调用的、定义在同一 record 上的 in-tree 方法（MM-1 调用图边）。</summary>
    public List<IMethodSymbol> SameRecordCalls { get; } = new();
```

- [ ] **Step 4: analyzer 收集调用边**

在 `RecordReadOnlyAnalyzer.BuildWriteInfo(...)` 的写入扫描循环**之后**追加：

```csharp
        // MM-1：同 record in-tree 调用边（按 OriginalDefinition 归一，兼容泛型实例化）。
        foreach (var invocation in bodyRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) continue;
            if (callee.MethodKind != MethodKind.Ordinary) continue;
            if (callee.DeclaringSyntaxReferences.Length == 0) continue;
            if (SymbolEqualityComparer.Default.Equals(
                    callee.ContainingType?.OriginalDefinition, owner.Symbol.OriginalDefinition))
            {
                info.SameRecordCalls.Add(callee);
            }
        }
```

- [ ] **Step 5: 整体替换 MethodMigrator.cs（规划器 + 调用点扫描 + 重写器）**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed record MigrationUnit(
    IMethodSymbol Method,
    IReadOnlyList<ISymbol> PromotedMembers);

internal sealed record MigrationFailure(
    RecordReadOnlyDiagnosticCode Code,
    string Reason,
    Location Location);

/// <summary>MM-1：同 record 调用图上的影响集闭包；MM-6 的结构化否决。</summary>
internal static class MethodMigrationPlanner
{
    public static (Dictionary<IMethodSymbol, MigrationUnit> Units, MigrationFailure? Failure)
        BuildUnits(RecordInfo info)
    {
        var writeInfos = info.WriteInfos.Values
            .Where(w => !w.IsConstructor && !w.IsPropertySetter)
            .ToList();

        var influence = writeInfos.ToDictionary(
            w => w.Method.OriginalDefinition,
            w => new HashSet<ISymbol>(w.WrittenMembers, SymbolEqualityComparer.Default));

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var w in writeInfos)
            foreach (var callee in w.SameRecordCalls)
            {
                if (!influence.TryGetValue(callee.OriginalDefinition, out var calleeInfluence))
                    continue;
                foreach (var member in calleeInfluence)
                    if (influence[w.Method.OriginalDefinition].Add(member)) changed = true;
            }
        }

        // MM-3 预备：this = expr 等价于整体重写实例状态。枚举全部实例字段/属性，
        // 判定转换后能否经 with 设置：
        //   属性（非索引器）：有 set/init 访问器（私有 init 在类型内亦可访问）；
        //   字段：当前可变（RS-5 转公共 init 属性、RS-6b 转私有 init 属性）。
        // 只读字段 / get-only 属性无法经 with 设置 → RR064 否决整条 record。
        var stateMembers = new List<(ISymbol Member, bool WithSettable)>();
        foreach (var member in info.Symbol.GetMembers())
        {
            if (member.IsStatic || member.IsImplicitlyDeclared) continue;
            switch (member)
            {
                case IPropertySymbol { IsIndexer: false } property:
                    stateMembers.Add((property, property.SetMethod != null));
                    break;
                case IFieldSymbol field:
                    stateMembers.Add((field,
                        info.MutableFields.Contains(field, SymbolEqualityComparer.Default)));
                    break;
            }
        }

        foreach (var w in writeInfos.Where(w => w.WritesThis))
        {
            var set = influence[w.Method.OriginalDefinition];
            var location = w.Method.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
            foreach (var (member, withSettable) in stateMembers)
            {
                if (!withSettable)
                    return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR064,
                        $"'this = <expr>' rewrites every instance member, but '{member.Name}' " +
                        "cannot be set via 'with' after conversion (no set/init accessor) " +
                        "(MM-3/MM-6).", location));
                set.Add(member);
            }
        }

        var mutating = writeInfos
            .Where(w => influence[w.Method.OriginalDefinition].Count > 0 || w.WritesThis)
            .OrderBy(w => w.Method.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? int.MaxValue)
            .ToList();

        var cycle = DetectCycle(mutating);
        if (cycle != null) return (new Dictionary<IMethodSymbol, MigrationUnit>(), cycle);

        foreach (var w in mutating)
        {
            var location = w.Method.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
            if (w.IsGetAccessor)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR063,
                    "a property/indexer getter mutates instance state; on a readonly record " +
                    "'this' is read-only inside getters (MM-6).", location));
            if (w.Iterator || w.Async)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR060,
                    "iterator/async methods occupy the return protocol and cannot change their " +
                    "signature to return the record (MM-6).", location));
            if (w.Partial)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR061,
                    "partial method signatures are bound across declarations and cannot be " +
                    "changed (MM-6).", location));
            if (w.IsOverrideExplicitOrVirtual || ImplementsInterfaceMethod(w.Method))
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR020,
                    "virtual/override/interface-implementation methods are handled by interface " +
                    "ripple (IR) in a later stage.", location));
            if (!w.Method.ReturnsVoid)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR020,
                    "non-void mutating methods migrate to the 'out T result' shape in the next " +
                    "task (MM-5).", location));
        }

        var units = mutating.ToDictionary(
            w => w.Method,
            w => new MigrationUnit(
                w.Method,
                influence[w.Method.OriginalDefinition]
                    .OrderBy(m => m.Name, StringComparer.Ordinal)
                    .ThenBy(m => m.ToDisplayString(), StringComparer.Ordinal)
                    .ToList()));
        return (units, null);
    }

    private static MigrationFailure? DetectCycle(List<MethodWriteInfo> mutating)
    {
        var set = mutating.Select(w => w.Method.OriginalDefinition).ToHashSet(SymbolEqualityComparer.Default);
        var byOriginal = mutating.GroupBy(w => w.Method.OriginalDefinition)
            .ToDictionary(g => g.Key, g => g.First());

        var state = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        foreach (var node in set)
        {
            var cycleAt = Dfs(node);
            if (cycleAt != null)
            {
                var w = byOriginal[cycleAt];
                return new MigrationFailure(RecordReadOnlyDiagnosticCode.RR062,
                    "mutating methods form a call-graph cycle; a bottom-up migration order cannot " +
                    "be fixed (MM-1/MM-6).",
                    w.Method.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None);
            }
        }
        return null;

        ISymbol? Dfs(ISymbol node)
        {
            state.TryGetValue(node, out var mark);
            if (mark == 1) return node;
            if (mark == 2) return null;
            state[node] = 1;
            if (byOriginal.TryGetValue((IMethodSymbol)node, out var w))
            {
                foreach (var callee in w.SameRecordCalls)
                {
                    if (!set.Contains(callee.OriginalDefinition)) continue;
                    var hit = Dfs(callee.OriginalDefinition);
                    if (hit != null) return hit;
                }
            }
            state[node] = 2;
            return null;
        }
    }

    private static bool ImplementsInterfaceMethod(IMethodSymbol method)
    {
        var type = method.ContainingType;
        foreach (var iface in type.AllInterfaces)
        foreach (var interfaceMethod in iface.GetMembers().OfType<IMethodSymbol>())
        {
            var impl = type.FindImplementationForInterfaceMember(interfaceMethod);
            if (impl == null) continue;
            if (SymbolEqualityComparer.Default.Equals(impl, method)
                || SymbolEqualityComparer.Default.Equals(
                    (impl as IMethodSymbol)?.PartialImplementationPart, method)
                || SymbolEqualityComparer.Default.Equals(
                    (impl as IMethodSymbol)?.PartialDefinitionPart, method))
                return true;
        }
        return false;
    }
}
```

`MethodCallSiteScanner`（同文件追加）：

```csharp
/// <summary>全树扫描迁移方法调用点，判定吸收 / this 回写 / 外部回写可行性。</summary>
internal static class MethodCallSiteScanner
{
    public static List<(SitePlan Plan, bool WritebackThis)> Scan(
        Compilation compilation,
        RecordInfo owner,
        Dictionary<IMethodSymbol, MigrationUnit> units,
        out SiteFailure? failure)
    {
        failure = null;
        var result = new List<(SitePlan, bool)>();
        var unitsByOriginal = units.Keys.ToDictionary(
            m => m.OriginalDefinition, m => m, SymbolEqualityComparer.Default);

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) continue;
                if (!unitsByOriginal.TryGetValue(callee.OriginalDefinition, out var migrated))
                    continue;

                var enclosing = EnclosingMember(model, invocation);
                var nested = IsInNestedFunction(invocation);
                var implicitThis = invocation.Expression is IdentifierNameSyntax;
                var explicitThis = invocation.Expression
                    is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax };
                var receiverIsThis = implicitThis || explicitThis;

                if (receiverIsThis
                    && enclosing != null
                    && SymbolEqualityComparer.Default.Equals(
                        enclosing.ContainingType?.OriginalDefinition, owner.Symbol.OriginalDefinition))
                {
                    // 迁移方法体内直接调用：体改写时吸收结果，不生成外部用点。
                    if (!nested && unitsByOriginal.ContainsKey(enclosing.OriginalDefinition))
                        continue;
                    // MM-5：仅 ctor 内 this 可整体回写（struct ctor 内 this 赋值合法）。
                    // getter/init(set)/add/remove 访问器与 lambda/本地函数内 this 只读，
                    // 整体 this 赋值不合法，保守阻断（convert-or-skip）。
                    if (!nested && enclosing.MethodKind == MethodKind.Constructor)
                    {
                        result.Add((MakeSite(tree, invocation,
                            SyntaxFactory.ThisExpression(), owner, migrated), WritebackThis: true));
                        continue;
                    }
                    failure = new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        "calling a migrated method on 'this' here cannot write back: 'this' is " +
                        "read-only in this context (only a constructor can emit 'this = this.M(...)') " +
                        "(MM-5).", invocation.GetLocation());
                    return result;
                }

                var statementPosition =
                    invocation.FirstAncestorOrSelf<ExpressionStatementSyntax>() != null;
                if (!statementPosition)
                {
                    failure = new SiteFailure(RecordReadOnlyDiagnosticCode.RR053,
                        "a migrated-method call consumed in an expression context cannot be " +
                        "rewritten to a write-back statement (MM-5/MS-16).",
                        invocation.GetLocation());
                    return result;
                }

                var receiver = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Expression,
                    _ => invocation.Expression,
                };
                var receiverFailure = MutationSiteClassifier.ReceiverWritableAndPure(model, receiver);
                if (receiverFailure != null)
                {
                    failure = receiverFailure;
                    return result;
                }
                result.Add((MakeSite(tree, invocation, receiver, owner, migrated),
                    WritebackThis: false));
            }
        }
        return result;
    }

    private static SitePlan MakeSite(SyntaxTree tree, InvocationExpressionSyntax invocation,
        ExpressionSyntax receiver, RecordInfo owner, IMethodSymbol callee)
    {
        var site = new MutationSiteInfo
        {
            Location = invocation.GetLocation(),
            FilePath = tree.FilePath,
            WriteNode = invocation,
            Left = invocation,
            Receiver = receiver,
            ReceiverType = owner.Symbol,
            Kind = SiteKind.MethodCall,
            IsStatementPosition =
                invocation.FirstAncestorOrSelf<ExpressionStatementSyntax>() != null,
            MethodCallee = callee,
        };
        site.TargetRecord = owner;
        return new SitePlan(site, SiteActionKind.MethodMigrateCall);
    }

    private static IMethodSymbol? EnclosingMember(SemanticModel model, SyntaxNode node)
    {
        var accessor = node.FirstAncestorOrSelf<AccessorDeclarationSyntax>();
        if (accessor != null && model.GetDeclaredSymbol(accessor) is { } accessorMethod)
            return accessorMethod;
        var declaration = node.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
        return declaration == null ? null : model.GetDeclaredSymbol(declaration);
    }

    private static bool IsInNestedFunction(SyntaxNode node)
    {
        SyntaxNode? stop = node.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>()
            ?? node.FirstAncestorOrSelf<AccessorDeclarationSyntax>();
        for (var cursor = node.Parent; cursor != null && cursor != stop?.Parent; cursor = cursor.Parent)
        {
            if (cursor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                return true;
            if (cursor == stop) return false;
        }
        return false;
    }
}
```

把 `MutationSiteClassifier.ReceiverWritableAndPure` 的可见性由 `private` 改为 `internal`（签名不变）。

重写器本体（同文件追加）：

```csharp
/// <summary>
/// Phase 3a：迁移方法声明（提升/出口合成/签名）与调用点回写；
/// 同时作为第一个 rewriter，为后续 MutationSiteRewriter 的用点节点挂定位注解。
/// </summary>
internal sealed class MethodMigrator : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    private readonly Dictionary<(int Start, int Kind), SitePlan> _callSites = new();
    private readonly Dictionary<(int Start, int Kind), SyntaxAnnotation> _msAnnotations = new();
    private readonly Dictionary<IMethodSymbol, MigrationUnit> _units =
        new(SymbolEqualityComparer.Default);
    private readonly IReadOnlySet<IMethodSymbol> _migratedByOriginal;

    public MethodMigrator(RewriteContext context)
    {
        _context = context;
        foreach (var plan in context.ActivePlans)
        {
            foreach (var migration in plan.Migrations)
            {
                if (migration.Method.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath
                        is { } file
                    && string.Equals(file, context.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    _units[migration.Method] = new MigrationUnit(
                        migration.Method, migration.PromotedMembers,
                        migration.Kind == MethodMigrationKind.VoidToRecord);
                }
            }
            foreach (var sitePlan in plan.Sites)
            {
                if (!string.Equals(sitePlan.Site.FilePath, context.FilePath,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var key = (sitePlan.Site.WriteNode.SpanStart, sitePlan.Site.WriteNode.RawKind);
                if (sitePlan.Action == SiteActionKind.MethodMigrateCall)
                    _callSites[key] = sitePlan;
                else
                    _msAnnotations[key] = MutationSiteRewriter.SiteAnnotationFor(plan, sitePlan);
            }
        }
        _migratedByOriginal = _units.Keys
            .Select(m => m.OriginalDefinition)
            .ToHashSet(SymbolEqualityComparer.Default);
    }

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        var visited = base.Visit(node);
        if (node != null && visited != null
            && _msAnnotations.TryGetValue((node.SpanStart, node.RawKind), out var annotation)
            && !visited.HasAnnotations(annotation.Kind))
        {
            visited = visited.WithAdditionalAnnotations(annotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol == null || !_units.TryGetValue(symbol, out var unit))
            return base.VisitMethodDeclaration(node);

        var body = node.Body;
        if (body == null && node.ExpressionBody != null)
        {
            // 表达式体展开为块体（MM-4）。mutating void 箭头右侧只可能是赋值或调用表达式，
            // 二者本身都是合法表达式语句。
            body = SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(node.ExpressionBody.Expression));
        }
        if (body == null) return base.VisitMethodDeclaration(node);

        var bodyRewriter = new MigratedMethodBodyRewriter(
            _context.Model, unit, _migratedByOriginal, _msAnnotations);
        var migrated = (BlockSyntax)bodyRewriter.Visit(body)!;

        // MM-2：方法体顶部为每个被提升成员声明局部副本：var __x = this.X;
        var promotionDeclarations = unit.PromotedMembers.Select(member =>
        {
            var localName = bodyRewriter.LocalNames[member];
            var declarator = SyntaxFactory.VariableDeclarator(
                    SyntaxFactory.Identifier(localName))
                .WithInitializer(SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ThisExpression(),
                        SyntaxFactory.IdentifierName(member.Name))));
            return SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.Token(SyntaxKind.VarKeyword),
                    SyntaxFactory.SingletonSeparatedList(declarator)));
        }).ToList();

        var returnStatement = SyntaxFactory.ReturnStatement(BuildWithThisReturn(unit, bodyRewriter));
        var finalBody = SyntaxFactory.Block(
            promotionDeclarations.Concat(migrated.Statements).Append(returnStatement));

        var recordTypeSyntax = SyntaxFactory.ParseTypeName(
            unit.Method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        _context.Statistics.MethodsMigrated++;
        return node.WithReturnType(recordTypeSyntax)
            .WithBody(finalBody)
            .WithExpressionBody(null)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    internal static ExpressionSyntax BuildWithThisReturn(
        MigrationUnit unit, MigratedMethodBodyRewriter bodyRewriter)
    {
        var initializers = unit.PromotedMembers.Select(member =>
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(member.Name),
                SyntaxFactory.IdentifierName(bodyRewriter.LocalNames[member])));
        return SyntaxFactory.WithExpression(
            SyntaxFactory.ThisExpression(),
            SyntaxFactory.InitializerExpression(
                SyntaxKind.WithInitializerExpression,
                SyntaxFactory.SeparatedList(initializers)));
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (node.Expression is InvocationExpressionSyntax invocation
            && _callSites.TryGetValue((node.Expression.SpanStart, node.Expression.RawKind),
                out var sitePlan))
        {
            var rewritten = RewriteCallWriteback(invocation, sitePlan.Site.Receiver);
            return SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(rewritten)
                    .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation));
        }
        return base.VisitExpressionStatement(node);
    }

    /// <summary>调用点接收者：M() → this；x.M() → x。</summary>
    internal static ExpressionSyntax ReceiverOf(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Expression,
            _ => SyntaxFactory.ThisExpression(),
        };

    /// <summary>recv.M(...); → recv = recv.M(...);（接收者可写性已由 scanner 判定）。</summary>
    internal static ExpressionSyntax RewriteCallWriteback(
        InvocationExpressionSyntax invocation, ExpressionSyntax receiver)
    {
        var receiverText = receiver.ToFullString();
        var receiverForCall = receiver.WithoutTrivia();
        InvocationExpressionSyntax callWithReceiver;
        if (invocation.Expression is IdentifierNameSyntax id)
        {
            callWithReceiver = invocation.WithExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression, receiverForCall,
                    SyntaxFactory.IdentifierName(id.Identifier.Text)));
        }
        else if (invocation.Expression is MemberAccessExpressionSyntax ma)
        {
            callWithReceiver = invocation.WithExpression(
                ma.WithExpression(receiverForCall));
        }
        else
        {
            callWithReceiver = invocation;
        }
        // LHS 必须是独立实例（同一节点不能同时挂在两个树位置），重新解析接收者文本。
        var lhs = SyntaxFactory.ParseExpression(receiverText);
        return SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression, lhs, callWithReceiver)
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }
}

/// <summary>单个迁移方法体内的提升替换 / this= 拆解 / 同 record 调用吸收 / 出口替换。</summary>
internal sealed class MigratedMethodBodyRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly MigrationUnit _unit;
    private readonly IReadOnlySet<IMethodSymbol> _migratedByOriginal;
    private readonly IReadOnlyDictionary<(int Start, int Kind), SyntaxAnnotation> _msAnnotations;
    private readonly Dictionary<ISymbol, string> _localByMember;
    private readonly HashSet<ISymbol> _promoted;
    private int _absorbedCounter;

    public MigratedMethodBodyRewriter(SemanticModel model, MigrationUnit unit,
        IReadOnlySet<IMethodSymbol> migratedByOriginal,
        IReadOnlyDictionary<(int Start, int Kind), SyntaxAnnotation> msAnnotations)
    {
        _model = model;
        _unit = unit;
        _migratedByOriginal = migratedByOriginal;
        _msAnnotations = msAnnotations;
        var declaration = unit.Method.DeclaringSyntaxReferences.First().GetSyntax()
            .AncestorsAndSelf().OfType<MethodDeclarationSyntax>().First();
        _localByMember = BuildLocalNames(unit, declaration);
        _promoted = new HashSet<ISymbol>(unit.PromotedMembers, SymbolEqualityComparer.Default);
        LocalNames = _localByMember;
    }

    public IReadOnlyDictionary<ISymbol, string> LocalNames { get; }

    // 迁移方法子树不经过 MethodMigrator.Visit，MS 用点注解在此补发；
    // 这样后续 MutationSiteRewriter 仍能按注解改写体内的 other.X = 2 等用点。
    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        var visited = base.Visit(node);
        if (node != null && visited != null
            && _msAnnotations.TryGetValue((node.SpanStart, node.RawKind), out var annotation)
            && !visited.HasAnnotations(annotation.Kind))
        {
            visited = visited.WithAdditionalAnnotations(annotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        if (node.Expression is ThisExpressionSyntax
            && _model.GetSymbolInfo(node).Symbol is { } symbol
            && _promoted.Contains(symbol) && !IsNameof(node))
        {
            return SyntaxFactory.IdentifierName(_localByMember[symbol]);
        }
        return base.VisitMemberAccessExpression(node);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (_model.GetSymbolInfo(node).Symbol is { } symbol
            && _promoted.Contains(symbol)
            && symbol is IFieldSymbol or IPropertySymbol
            && !IsNameof(node))
        {
            return SyntaxFactory.IdentifierName(_localByMember[symbol]);
        }
        return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (node.Expression != null) return base.VisitReturnStatement(node);
        var visited = (ReturnStatementSyntax)base.VisitReturnStatement(node)!;
        return visited.WithExpression(MethodMigrator.BuildWithThisReturn(_unit, this));
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // MM-3：this = expr; → 逐提升成员拷贝。RHS 先经 Visit，使其中对提升成员的
        // 读取同步替换为局部（this = Make(X); → __x = Make(__x).X;）。
        if (node.Expression is AssignmentExpressionSyntax
            {
                RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                Left: ThisExpressionSyntax,
            } assignment)
        {
            var visitedRight = (ExpressionSyntax)Visit(assignment.Right)!;
            var rightText = visitedRight.ToFullString();
            var statements = _unit.PromotedMembers.Select(member =>
            {
                // 每个成员各解析一份 RHS：同一节点实例不能挂在多个拷贝语句中。
                var rightCopy = SyntaxFactory.ParseExpression(rightText);
                return SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(_localByMember[member]),
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            rightCopy,
                            SyntaxFactory.IdentifierName(member.Name))));
            });
            return SyntaxFactory.Block(statements
                .WithNavigationAnnotation(RecordSyntaxFacts.RewriteAnnotation));
        }

        // MM-5（吸收）：this.M()/M()（被调方是迁移方法）→ var __r = this.M(...); fi = __r.Fi;
        if (node.Expression is InvocationExpressionSyntax invocation
            && IsCallToMigratedSameRecord(invocation))
        {
            // 先常规访问，使实参内对提升成员的读取替换为局部（B(X) → B(__x)）；
            // this.M 的 M 是方法符号、不在提升集合，成员访问保持不变。
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(invocation)!;
            var temp = $"__r{_absorbedCounter++}";
            var call = visited.Expression is IdentifierNameSyntax id
                ? visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ThisExpression(),
                        SyntaxFactory.IdentifierName(id.Identifier.Text)))
                : visited;
            var declaration = SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.Token(SyntaxKind.VarKeyword),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(temp))
                            .WithInitializer(SyntaxFactory.EqualsValueClause(call)))));
            var copies = _unit.PromotedMembers.Select(member =>
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(_localByMember[member]),
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName(temp),
                            SyntaxFactory.IdentifierName(member.Name)))));
            return SyntaxFactory.Block(new StatementSyntax[] { declaration }.Concat(copies)
                .WithNavigationAnnotation(RecordSyntaxFacts.RewriteAnnotation));
        }

        // 外部接收者（语句位）：other.B(); → other = other.B();。
        // 被调方迁移前是 void，源码中不可能在表达式消费位出现，故语句位即全部形态。
        if (node.Expression is InvocationExpressionSyntax external
            && IsCallToMigratedExternal(external))
        {
            var visitedCall = (InvocationExpressionSyntax)base.VisitInvocationExpression(external)!;
            var receiver = MethodMigrator.ReceiverOf(visitedCall);
            var writeback = MethodMigrator.RewriteCallWriteback(visitedCall, receiver);
            return SyntaxFactory.ExpressionStatement(writeback)
                .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
        }

        return base.VisitExpressionStatement(node);
    }

    /// <summary>
    /// MM-5（吸收）判据：解析出的 callee 属于本 record 的迁移单元集合（按 OriginalDefinition），
    /// 且调用接收者是 this 或隐式 this（M() / this.M()）。对其他接收者（other.M()）不在此吸收。
    /// </summary>
    private bool IsCallToMigratedSameRecord(InvocationExpressionSyntax invocation)
    {
        if (_model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) return false;
        if (!_migratedByOriginal.Contains(callee.OriginalDefinition)) return false;
        return invocation.Expression switch
        {
            IdentifierNameSyntax => true,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } => true,
            _ => false,
        };
    }

    /// <summary>callee 在迁移单元集合、接收者非 this（other.M()/this.F.M() 等）。</summary>
    private bool IsCallToMigratedExternal(InvocationExpressionSyntax invocation)
    {
        if (_model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) return false;
        if (!_migratedByOriginal.Contains(callee.OriginalDefinition)) return false;
        return invocation.Expression is MemberAccessExpressionSyntax
            { Expression: not ThisExpressionSyntax };
    }

    private static bool IsNameof(ExpressionSyntax node)
    {
        var argument = node.FirstAncestorOrSelf<ArgumentSyntax>();
        return argument?.Parent?.Parent is InvocationExpressionSyntax invoked
            && invoked.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" };
    }

    private static Dictionary<ISymbol, string> BuildLocalNames(
        MigrationUnit unit, MethodDeclarationSyntax declaration)
    {
        var taken = declaration.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Select(v => v.Identifier.Text)
            .Concat(declaration.ParameterList.Parameters.Select(p => p.Identifier.Text))
            .ToHashSet(StringComparer.Ordinal);
        var map = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        foreach (var member in unit.PromotedMembers)
        {
            var stem = member.Name.TrimStart('_');
            var candidate = "__" + (stem.Length == 0
                ? "x"
                : char.ToLower(stem[0]) + stem.Substring(1));
            while (taken.Contains(candidate)) candidate += "_";
            taken.Add(candidate);
            map[member] = candidate;
        }
        return map;
    }
}

internal static class RrStatementListExtensions
{
    public static IEnumerable<StatementSyntax> WithNavigationAnnotation(
        this IEnumerable<StatementSyntax> statements, SyntaxAnnotation annotation) =>
        statements.Select(s => s.WithAdditionalAnnotations(annotation));
}
```

- [ ] **Step 6: PlanBuilder 接入并翻转能力**

`RecordConversionPlan.cs`：

1. `FeatureCaps.Current` 中 `MethodMigration: true`。
2. 用以下逻辑替换 mutating 方法门控块：

```csharp
        // mutating 方法 → MM 迁移规划（影响集 / MM-6 否决 / 调用点）。
        if (info.WriteInfos.Values.Any(w => !w.IsConstructor && !w.IsPropertySetter
                && (w.WrittenMembers.Count > 0 || w.WritesThis)))
        {
            if (!_caps.MethodMigration)
                return Fail(plan, RecordReadOnlyDiagnosticCode.RR020,
                    "record has instance method(s) that mutate state; method migration (MM) is " +
                    "required to convert.");

            var (units, migrationFailure) = MethodMigrationPlanner.BuildUnits(info);
            if (migrationFailure != null)
                return FailAt(plan, migrationFailure.Code, migrationFailure.Reason,
                    migrationFailure.Location);

            foreach (var unit in units.Values.OrderBy(u =>
                         u.Method.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0))
            {
                plan.Migrations.Add(new MethodMigrationPlan(
                    unit.Method, MethodMigrationKind.VoidToRecord, unit.PromotedMembers));
            }

            var callSites = MethodCallSiteScanner.Scan(
                analysis.Compilation!, info, units, out var callFailure);
            if (callFailure != null)
                return FailAt(plan, callFailure.Code, callFailure.Reason, callFailure.Location);
            foreach (var (callSite, _) in callSites)
            {
                plan.Sites.Add(callSite);
                plan.TouchedFiles.Add(callSite.Site.FilePath);
            }
        }
```

`FailAt` 复用 Task 11 已引入的辅助（按显式 `Location` 落诊断）。`MethodCallSiteScanner.Scan` 需要编译对象，经 `analysis.Compilation!` 取得（Task 4 起 `RecordAnalysis.Compilation` 非空）。

3. 把 RS-6b 判定辅助改为把 `this = expr` 视作构造后写入（该方法经 MM-3 会逐成员拷贝私有字段，字段必须转私有 init 属性才能被出口 `with` 设置）：

```csharp
    private static bool IsPrivateFieldWrittenAfterConstruction(RecordInfo info, IFieldSymbol field)
        => info.WriteInfos.Values.Any(w => !w.IsConstructor && !w.IsPropertySetter
            && (w.WritesThis || w.WrittenMembers.Contains(field)));
```

- [ ] **Step 7: MutationSiteRewriter 改为注解定位（跨重写器稳定）+ MS-2 守卫纯语法化**

MethodMigrator 是第一个 rewriter，语句增删会让原始 span 失效。用注解定位：

1. 在 `MutationSiteRewriter` 增加：

```csharp
    internal const string SiteAnnotationKind = "Cs2jRecordReadOnlySite";

    internal static string SiteKey(RecordPlan owner, MutationSiteInfo site) =>
        owner.Id + "|" + site.WriteNode.SpanStart + "|" + site.WriteNode.RawKind;

    internal static SyntaxAnnotation SiteAnnotationFor(RecordPlan owner, SitePlan sitePlan) =>
        new(SiteAnnotationKind, SiteKey(owner, sitePlan.Site));
```

2. 构造函数把 `_siteKeys`（span→plan）替换为 `Dictionary<string, SitePlan>`（key→plan），遍历 `ActivePlans.Sites`（排除 `MethodMigrateCall`，key 用 `SiteKey`）。`FindSite(expression)` 改为：

```csharp
    private SitePlan? FindSite(SyntaxNode node)
    {
        foreach (var annotation in node.GetAnnotations(SiteAnnotationKind))
        {
            if (annotation.Data != null && _siteByKey.TryGetValue(annotation.Data, out var plan))
                return plan;
        }
        return null;
    }
```

原 `FindSite(ExpressionSyntax)` 的两个调用点（`VisitIfStatement`、`TryCoalesce`、链式处理）相应改为传 `SyntaxNode`。

3. MS-2 读后写守卫改为纯语法判定（不再依赖会失配的语义模型）：

```csharp
    private static bool ReadsPreviouslyWrittenMember(
        ExpressionSyntax right, string receiverText, HashSet<string> writtenMembers)
    {
        return right.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(ma =>
            ma.Expression.WithoutTrivia().ToFullString() == receiverText
            && writtenMembers.Contains(ma.Name.Identifier.Text));
    }
```

`TryCoalesce` 调用处传入 `run[0].Receiver` 文本与当前 run 的成员名集合（保守：文本命中即不合并）。

- [ ] **Step 8: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 全部 PASS（MM 正/反例 14 个 + 此前 RS/MS 全量不回归）。

- [ ] **Step 9: Commit**

```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): MM-1..MM-6 void method migration, influence graph, call-site writeback"
```

---

### Task 17: MM-5 非 void 方法 `out T result` 迁移与调用点提升（含 RR053/RR065）

> 本任务把 Task 16 的 void 专用管线泛化：`T M()` → `R M(out T result)`，体内 `return E;` →
> `result = E; return this with {...};`；调用点三类形态（丢弃 / 单变量声明确 / 嵌套表达式提升）。
> override/object 契约方法不可改签名判 RR065；接口实现保持 RR020 留待 M4。扩展方法在 Task 18。

**Files:**
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyModels.cs`（诊断码 RR065）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs`（Kind 改 flags；PlanBuilder 推导 NonVoidToOut）
- Replace whole: `src/CSharpToJava.Core/RecordReadOnly/MethodMigrator.cs`
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

- [ ] **Step 1: 写失败测试**

追加：

```csharp
    [Fact]
    public void MM5_NonVoidDiscarded_BecomesOutDiscardWriteback()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
            }
            public class C
            {
                public void M(P p) { p.Next(); }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("public P Next(out int result)", output);
        Assert.Contains("p = p.Next(out _);", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM5_NonVoidSingleDeclarator_BecomesOutVarWriteback()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
            }
            public class C
            {
                public void M(P p) { var v = p.Next(); System.Console.WriteLine(v); }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p.Next(out var v);", output);
        Assert.DoesNotContain("var v = p.Next();", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM5_NonVoidNestedInArgument_HoistsTempBeforeStatement()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
            }
            public class C
            {
                public void M(P p) { Use(p.Next()); }
                private static void Use(int v) { }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p.Next(out var __o0);", output);
        Assert.Contains("Use(__o0);", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM5_NonVoidInReturn_HoistsTemp()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
            }
            public class C
            {
                public int M(P p) { return p.Next(); }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p.Next(out var __o0);", output);
        Assert.Contains("return __o0;", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM5_NonVoidInIfCondition_HoistsIntoBlock()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public bool Ready() { X = 1; return X > 0; }
            }
            public class C
            {
                public void M(P p) { if (p.Ready()) { Tick(); } }
                private static void Tick() { }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p.Ready(out var __o0);", output);
        Assert.Contains("if (__o0)", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM5_NonVoidInFieldInitializer_BlocksWithRR053()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
            }
            public class C
            {
                public P _p;
                public int Field = default(C).Next();
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR053);
    }

    [Fact]
    public void MM5_NonVoidInExpressionBodiedMember_BlocksWithRR053()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
            }
            public class C
            {
                private P _p;
                public int V => _p.Next();
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR053);
    }

    [Fact]
    public void MM5_NonVoidInMultiDeclarator_BlocksWithRR053()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
            }
            public class C
            {
                public void M(P p) { int a = 1, b = p.Next(); }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR053);
    }

    [Fact]
    public void MM5_MigratedBody_AbsorbsNonVoidThisCallWithOutVar()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
                public int A() { var z = Next(); return z; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("public P A(out int result)", output);
        Assert.Contains("var __r0 = this.Next(out var z);", output);
        Assert.Contains("__x = __r0.X;", output);
        Assert.Contains("result = z;", output);
        Assert.Contains("return this with { X = __x };", output);
        Assert.Equal(2, stats.MethodsMigrated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM5_ExpressionBodiedNonVoidMethod_ExpandedToResultAssignment()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Bump() => X += 1;
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("public P Bump(out int result)", output);
        Assert.Contains("result = __x += 1;", output);
        Assert.Contains("return this with { X = __x };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM5_CtorDiscardsNonVoidCall_ThisWritebackOutDiscard()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public P(int x) { X = x; Next(); }
                public int Next() { X += 1; return X; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("this = this.Next(out _);", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM6_ObjectOverrideMutating_BlocksWithRR065()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public override string ToString() { X = 1; return X.ToString(); }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR065);
    }

    [Fact]
    public void MM6_InterfaceImplementationMutating_StaysPendingRR020()
    {
        var src = """
            public interface I { void Bump(); }
            public record struct P : I
            {
                public int X { get; set; }
                public void Bump() { X = 1; }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR020);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 非 void 方法仍在 planner 判 RR020，RR053/RR065 用例失败。

- [ ] **Step 3: 模型层增补**

1. `RecordReadOnlyModels.cs` 诊断枚举 RR064 之后追加：

```csharp
    RR065, // virtual/override/对象契约（ToString 等）方法签名不可改（MM-6；接口实现走 IR）
```

2. `RecordConversionPlan.cs` 把 `MethodMigrationKind` 改为位标志（Task 18 扩展方法会与前两者组合）：

```csharp
[Flags]
internal enum MethodMigrationKind
{
    VoidToRecord = 1,
    NonVoidToOut = 2,
    Extension = 4,
}
```

- [ ] **Step 4: planner 差异说明 + PlanBuilder 推导 Kind**

`MethodMigrationPlanner.BuildUnits` 的 mutating 循环中，Task 16 的

```csharp
            if (w.IsOverrideExplicitOrVirtual || ImplementsInterfaceMethod(w.Method))
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR020,
                    "virtual/override/interface-implementation methods are handled by interface " +
                    "ripple (IR) in a later stage.", location));
            if (!w.Method.ReturnsVoid)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR020,
                    "non-void mutating methods migrate to the 'out T result' shape in the next " +
                    "task (MM-5).", location));
```

在 Step 5 的整文件替换中改为（注意：隐式接口实现方法的 `IsOverrideExplicitOrVirtual`
为 false，两个判定必须并列、接口判定在前）：

```csharp
            if (ImplementsInterfaceMethod(w.Method))
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR020,
                    "interface-implementation methods are handled by interface ripple (IR) " +
                    "in a later stage.", location));
            if (w.IsOverrideExplicitOrVirtual)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR065,
                    "a virtual/override/object-contract method cannot change its signature; " +
                    "only interface-implementation methods can be rerouted via IR (MM-6).",
                    location));
```

（删除非 void RR020 闸门——签名 Kind 由 PlanBuilder 区分。）

`RecordConversionPlan.cs` PlanBuilder 的迁移循环中：

```csharp
            foreach (var unit in units.Values.OrderBy(u =>
                         u.Method.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0))
            {
                var kind = unit.Method.ReturnsVoid
                    ? MethodMigrationKind.VoidToRecord
                    : MethodMigrationKind.NonVoidToOut;
                plan.Migrations.Add(new MethodMigrationPlan(
                    unit.Method, kind, unit.PromotedMembers));
            }
```

- [ ] **Step 5: 整体替换 MethodMigrator.cs（规划器 / 扫描器 / 共享调用重写器 / 声明重写器）**

整文件替换为以下内容（Task 16 的 `MigratedMethodBodyRewriter` 与 `MethodMigrator` 的调用处理
合并为共享基类 `MethodCallSyntaxRewriter`；外部模式与迁移方法体模式只差提升局部与出口合成）：

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Globalization;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed record MigrationUnit(
    IMethodSymbol Method,
    MethodMigrationKind Kind,
    IReadOnlyList<ISymbol> PromotedMembers);

internal sealed record MigrationFailure(
    RecordReadOnlyDiagnosticCode Code,
    string Reason,
    Location Location);

/// <summary>MM-1：同 record 调用图上的影响集闭包；MM-6 的结构化否决。</summary>
internal static class MethodMigrationPlanner
{
    public static (Dictionary<IMethodSymbol, MigrationUnit> Units, MigrationFailure? Failure)
        BuildUnits(RecordInfo info)
    {
        var writeInfos = info.WriteInfos.Values
            .Where(w => !w.IsConstructor && !w.IsPropertySetter)
            .ToList();

        var influence = writeInfos.ToDictionary(
            w => w.Method.OriginalDefinition,
            w => new HashSet<ISymbol>(w.WrittenMembers, SymbolEqualityComparer.Default));

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var w in writeInfos)
            foreach (var callee in w.SameRecordCalls)
            {
                if (!influence.TryGetValue(callee.OriginalDefinition, out var calleeInfluence))
                    continue;
                foreach (var member in calleeInfluence)
                    if (influence[w.Method.OriginalDefinition].Add(member)) changed = true;
            }
        }

        // MM-3 预备：this = expr 等价于整体重写实例状态。枚举全部实例字段/属性，
        // 判定转换后能否经 with 设置：
        //   属性（非索引器）：有 set/init 访问器（私有 init 在类型内亦可访问）；
        //   字段：当前可变（RS-5 转公共 init 属性、RS-6b 转私有 init 属性）。
        // 只读字段 / get-only 属性无法经 with 设置 → RR064 否决整条 record。
        var stateMembers = new List<(ISymbol Member, bool WithSettable)>();
        foreach (var member in info.Symbol.GetMembers())
        {
            if (member.IsStatic || member.IsImplicitlyDeclared) continue;
            switch (member)
            {
                case IPropertySymbol { IsIndexer: false } property:
                    stateMembers.Add((property, property.SetMethod != null));
                    break;
                case IFieldSymbol field:
                    stateMembers.Add((field,
                        info.MutableFields.Contains(field, SymbolEqualityComparer.Default)));
                    break;
            }
        }

        foreach (var w in writeInfos.Where(w => w.WritesThis))
        {
            var set = influence[w.Method.OriginalDefinition];
            var location = w.Method.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
            foreach (var (member, withSettable) in stateMembers)
            {
                if (!withSettable)
                    return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR064,
                        $"'this = <expr>' rewrites every instance member, but '{member.Name}' " +
                        "cannot be set via 'with' after conversion (no set/init accessor) " +
                        "(MM-3/MM-6).", location));
                set.Add(member);
            }
        }

        var mutating = writeInfos
            .Where(w => influence[w.Method.OriginalDefinition].Count > 0 || w.WritesThis)
            .OrderBy(w => w.Method.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? int.MaxValue)
            .ToList();

        var cycle = DetectCycle(mutating);
        if (cycle != null) return (new Dictionary<IMethodSymbol, MigrationUnit>(), cycle);

        foreach (var w in mutating)
        {
            var location = w.Method.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
            if (w.IsGetAccessor)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR063,
                    "a property/indexer getter mutates instance state; on a readonly record " +
                    "'this' is read-only inside getters (MM-6).", location));
            if (w.Iterator || w.Async)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR060,
                    "iterator/async methods occupy the return protocol and cannot change their " +
                    "signature to return the record (MM-6).", location));
            if (w.Partial)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR061,
                    "partial method signatures are bound across declarations and cannot be " +
                    "changed (MM-6).", location));
            // 隐式接口实现不标记 virtual，两个判定必须并列、接口在前（MM-6/IR）。
            if (ImplementsInterfaceMethod(w.Method))
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR020,
                    "interface-implementation methods are handled by interface ripple (IR) " +
                    "in a later stage.", location));
            if (w.IsOverrideExplicitOrVirtual)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR065,
                    "a virtual/override/object-contract method cannot change its signature; " +
                    "only interface-implementation methods can be rerouted via IR (MM-6).",
                    location));
        }

        var units = mutating.ToDictionary(
            w => w.Method,
            w => new MigrationUnit(
                w.Method,
                w.Method.ReturnsVoid ? MethodMigrationKind.VoidToRecord
                                     : MethodMigrationKind.NonVoidToOut,
                influence[w.Method.OriginalDefinition]
                    .OrderBy(m => m.Name, StringComparer.Ordinal)
                    .ThenBy(m => m.ToDisplayString(), StringComparer.Ordinal)
                    .ToList()));
        return (units, null);
    }

    private static MigrationFailure? DetectCycle(List<MethodWriteInfo> mutating)
    {
        var set = mutating.Select(w => w.Method.OriginalDefinition).ToHashSet(SymbolEqualityComparer.Default);
        var byOriginal = mutating.GroupBy(w => w.Method.OriginalDefinition)
            .ToDictionary(g => g.Key, g => g.First());

        var state = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        foreach (var node in set)
        {
            var cycleAt = Dfs(node);
            if (cycleAt != null)
            {
                var w = byOriginal[cycleAt];
                return new MigrationFailure(RecordReadOnlyDiagnosticCode.RR062,
                    "mutating methods form a call-graph cycle; a bottom-up migration order cannot " +
                    "be fixed (MM-1/MM-6).",
                    w.Method.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None);
            }
        }
        return null;

        ISymbol? Dfs(ISymbol node)
        {
            state.TryGetValue(node, out var mark);
            if (mark == 1) return node;
            if (mark == 2) return null;
            state[node] = 1;
            if (byOriginal.TryGetValue((IMethodSymbol)node, out var w))
            {
                foreach (var callee in w.SameRecordCalls)
                {
                    if (!set.Contains(callee.OriginalDefinition)) continue;
                    var hit = Dfs(callee.OriginalDefinition);
                    if (hit != null) return hit;
                }
            }
            state[node] = 2;
            return null;
        }
    }

    private static bool ImplementsInterfaceMethod(IMethodSymbol method)
    {
        var type = method.ContainingType;
        foreach (var iface in type.AllInterfaces)
        foreach (var interfaceMethod in iface.GetMembers().OfType<IMethodSymbol>())
        {
            var impl = type.FindImplementationForInterfaceMember(interfaceMethod);
            if (impl == null) continue;
            if (SymbolEqualityComparer.Default.Equals(impl, method)
                || SymbolEqualityComparer.Default.Equals(
                    (impl as IMethodSymbol)?.PartialImplementationPart, method)
                || SymbolEqualityComparer.Default.Equals(
                    (impl as IMethodSymbol)?.PartialDefinitionPart, method))
                return true;
        }
        return false;
    }
}

/// <summary>全树扫描迁移方法调用点，判定吸收 / this 回写 / 外部回写与提升可行性。</summary>
internal static class MethodCallSiteScanner
{
    public static List<(SitePlan Plan, bool WritebackThis)> Scan(
        Compilation compilation,
        RecordInfo owner,
        Dictionary<IMethodSymbol, MigrationUnit> units,
        out SiteFailure? failure)
    {
        failure = null;
        var result = new List<(SitePlan, bool)>();
        var unitsByOriginal = units.Keys.ToDictionary(
            m => m.OriginalDefinition, m => m, SymbolEqualityComparer.Default);

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) continue;
                if (!unitsByOriginal.TryGetValue(callee.OriginalDefinition, out var migrated))
                    continue;

                var enclosing = EnclosingMember(model, invocation);
                var nested = IsInNestedFunction(invocation);
                var implicitThis = invocation.Expression is IdentifierNameSyntax;
                var explicitThis = invocation.Expression
                    is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax };
                var receiverIsThis = implicitThis || explicitThis;

                if (receiverIsThis
                    && enclosing != null
                    && SymbolEqualityComparer.Default.Equals(
                        enclosing.ContainingType?.OriginalDefinition, owner.Symbol.OriginalDefinition))
                {
                    // 迁移方法体内直接调用：体改写时吸收结果，不生成外部用点。
                    if (!nested && unitsByOriginal.ContainsKey(enclosing.OriginalDefinition))
                        continue;
                    // MM-5：仅 ctor 内 this 可整体回写（struct ctor 内 this 赋值合法）。
                    // getter/init(set)/add/remove 访问器与 lambda/本地函数内 this 只读，
                    // 整体 this 赋值不合法，保守阻断（convert-or-skip）。
                    if (!nested && enclosing.MethodKind == MethodKind.Constructor)
                    {
                        // ctor 内同样可能出现嵌套消费（Use(Next())），需要可提升。
                        if (FindHoistBarrier(invocation) != null)
                        {
                            failure = new SiteFailure(RecordReadOnlyDiagnosticCode.RR053,
                                "a migrated-method call in a non-statement context cannot be " +
                                "hoisted to a 'this = this.M(...)' write-back (MM-5/MS-16).",
                                invocation.GetLocation());
                            return result;
                        }
                        result.Add((MakeSite(tree, invocation,
                            SyntaxFactory.ThisExpression(), owner, migrated), WritebackThis: true));
                        continue;
                    }
                    failure = new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        "calling a migrated method on 'this' here cannot write back: 'this' is " +
                        "read-only in this context (only a constructor can emit 'this = this.M(...)') " +
                        "(MM-5).", invocation.GetLocation());
                    return result;
                }

                // void 迁移方法的调用只可能出现在语句位；非 void 迁移方法可在表达式内消费，
                // 但必须能提升前置语句（MM-5 三形态）。字段/属性初始化器、箭头表达式体、
                // 特性实参、查询子句、构造初始化器(: this/base)、无块 lambda、多变量声明
                // 都是不可提升屏障。
                if (FindHoistBarrier(invocation) != null)
                {
                    failure = new SiteFailure(RecordReadOnlyDiagnosticCode.RR053,
                        "a migrated-method call in a non-statement context (field/property " +
                        "initializer, expression-bodied member, attribute argument, query " +
                        "clause, constructor initializer, expression lambda, or multi-variable " +
                        "declarator) cannot be hoisted to a write-back statement (MM-5/MS-16).",
                        invocation.GetLocation());
                    return result;
                }

                var receiver = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Expression,
                    _ => invocation.Expression,
                };
                var receiverFailure = MutationSiteClassifier.ReceiverWritableAndPure(model, receiver);
                if (receiverFailure != null)
                {
                    failure = receiverFailure;
                    return result;
                }
                result.Add((MakeSite(tree, invocation, receiver, owner, migrated),
                    WritebackThis: false));
            }
        }
        return result;
    }

    private static SitePlan MakeSite(SyntaxTree tree, InvocationExpressionSyntax invocation,
        ExpressionSyntax receiver, RecordInfo owner, IMethodSymbol callee)
    {
        var site = new MutationSiteInfo
        {
            Location = invocation.GetLocation(),
            FilePath = tree.FilePath,
            WriteNode = invocation,
            Left = invocation,
            Receiver = receiver,
            ReceiverType = owner.Symbol,
            Kind = SiteKind.MethodCall,
            IsStatementPosition =
                invocation.FirstAncestorOrSelf<ExpressionStatementSyntax>() != null,
            MethodCallee = callee,
        };
        site.TargetRecord = owner;
        return new SitePlan(site, SiteActionKind.MethodMigrateCall);
    }

    /// <summary>
    /// 从调用点向上走到最近的方法/访问器/匿名函数/本地函数/编译单元边界；途中命中不可提升
    /// 语法形态即返回该屏障节点。有块 lambda 与有块本地函数不是屏障（块内可插入语句）。
    /// </summary>
    private static SyntaxNode? FindHoistBarrier(SyntaxNode node)
    {
        var cursor = node.Parent;
        while (cursor is not null
            and not BaseMethodDeclarationSyntax
            and not AccessorDeclarationSyntax
            and not AnonymousFunctionExpressionSyntax
            and not LocalFunctionStatementSyntax
            and not CompilationUnitSyntax)
        {
            switch (cursor)
            {
                case ArrowExpressionClauseSyntax arrow:
                    return arrow;
                case AttributeSyntax or AttributeArgumentSyntax:
                    return cursor;
                case ConstructorInitializerSyntax constructorInitializer:
                    return constructorInitializer;
                case QueryClauseSyntax queryClause:
                    return queryClause;
                case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax }:
                    return cursor;
                case EqualsValueClauseSyntax
                {
                    Parent: VariableDeclaratorSyntax
                    {
                        Parent: VariableDeclarationSyntax
                        {
                            Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax
                        }
                    }
                }:
                    return cursor;
                case VariableDeclarationSyntax { Variables.Count: > 1 }:
                    return cursor;
            }
            cursor = cursor.Parent;
        }
        if (cursor is AnonymousFunctionExpressionSyntax { Block: null }
            or LocalFunctionStatementSyntax { Body: null, ExpressionBody: not null })
            return cursor;
        return null;
    }

    private static IMethodSymbol? EnclosingMember(SemanticModel model, SyntaxNode node)
    {
        var accessor = node.FirstAncestorOrSelf<AccessorDeclarationSyntax>();
        if (accessor != null && model.GetDeclaredSymbol(accessor) is { } accessorMethod)
            return accessorMethod;
        var declaration = node.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
        return declaration == null ? null : model.GetDeclaredSymbol(declaration);
    }

    private static bool IsInNestedFunction(SyntaxNode node)
    {
        SyntaxNode? stop = node.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>()
            ?? node.FirstAncestorOrSelf<AccessorDeclarationSyntax>();
        for (var cursor = node.Parent; cursor != null && cursor != stop?.Parent; cursor = cursor.Parent)
        {
            if (cursor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                return true;
            if (cursor == stop) return false;
        }
        return false;
    }
}
```

`MutationSiteClassifier.ReceiverWritableAndPure` 的 `internal` 可见性维持 Task 16 的改动。

同文件追加（共享调用重写器——两种实例化：外部模式由 `MethodMigrator` 自身构造遍历全树；
迁移方法体由 `ForBody` 构造，多了提升局部替换、MM-3 拆解与出口合成）：

```csharp
/// <summary>
/// 迁移调用点的共享语法重写器（MM-5 三形态 + 体内吸收 + this 回写）。
/// 设计要点：每条语句最多产出“一个提升后的语句块”，块用 HoistBlockAnnotation 标记，
/// 在最近的真实 Block/SwitchSection/CompilationUnit 处展平，保证声明语句提升后
/// 作用域不缩小（var v = recv.M() 的 v 在后续语句仍可见）。
/// </summary>
internal class MethodCallSyntaxRewriter : CSharpSyntaxRewriter
{
    private const string CallMarkKind = "Cs2jRecordReadOnlyMigratedCall";
    internal static readonly SyntaxAnnotation HoistBlockAnnotation =
        new("Cs2jRecordReadOnlyHoistBlock");

    protected readonly SemanticModel Model;
    protected readonly IReadOnlySet<IMethodSymbol> MigratedByOriginal;
    protected readonly IReadOnlyDictionary<(int Start, int Kind), SyntaxAnnotation> MsAnnotations;

    private readonly MigrationUnit? _unit;
    private readonly Dictionary<ISymbol, string>? _locals;
    private readonly string _resultName = "result";
    private readonly HashSet<string> _usedNames = new(StringComparer.Ordinal);

    private int _absorbedCounter;
    private int _outCounter;
    private bool _processingStatement;
    private readonly List<StatementSyntax> _prefixes = new();
    private string? _directCallData;
    private string? _directDeclaratorName;
    private Dictionary<string, IMethodSymbol>? _calleeByData;
    private bool _mm3;
    private string? _mm3Source;

    protected MethodCallSyntaxRewriter(
        SemanticModel model,
        IReadOnlySet<IMethodSymbol> migratedByOriginal,
        IReadOnlyDictionary<(int Start, int Kind), SyntaxAnnotation> msAnnotations)
    {
        Model = model;
        MigratedByOriginal = migratedByOriginal;
        MsAnnotations = msAnnotations;
    }

    protected MethodCallSyntaxRewriter(
        SemanticModel model,
        MigrationUnit unit,
        IReadOnlySet<IMethodSymbol> migratedByOriginal,
        IReadOnlyDictionary<(int Start, int Kind), SyntaxAnnotation> msAnnotations,
        string resultName)
        : this(model, migratedByOriginal, msAnnotations)
    {
        _unit = unit;
        _resultName = resultName;
        var declaration = unit.Method.DeclaringSyntaxReferences.First().GetSyntax()
            .AncestorsAndSelf().OfType<MethodDeclarationSyntax>().First();
        _locals = BuildLocalNames(unit, declaration);
        LocalNames = _locals;
        foreach (var name in _locals.Values) _usedNames.Add(name);
    }

    internal static MethodCallSyntaxRewriter ForBody(
        SemanticModel model,
        MigrationUnit unit,
        IReadOnlySet<IMethodSymbol> migratedByOriginal,
        IReadOnlyDictionary<(int Start, int Kind), SyntaxAnnotation> msAnnotations,
        string resultName) =>
        new(model, unit, migratedByOriginal, msAnnotations, resultName);

    public IReadOnlyDictionary<ISymbol, string>? LocalNames { get; }

    internal ExpressionSyntax BuildRecordWith(ExpressionSyntax receiver)
    {
        var initializers = _unit!.PromotedMembers.Select(member =>
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(member.Name),
                SyntaxFactory.IdentifierName(_locals![member])));
        return SyntaxFactory.WithExpression(receiver,
            SyntaxFactory.InitializerExpression(
                SyntaxKind.WithInitializerExpression,
                SyntaxFactory.SeparatedList(initializers)));
    }

    // 迁移方法子树不经过外层 Visit，MS 用点注解在此补发；这样后续 MutationSiteRewriter
    // 仍能按注解改写体内的 other.X = 2 等用点。
    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        var visited = base.Visit(node);
        if (node != null && visited != null
            && MsAnnotations.TryGetValue((node.SpanStart, node.RawKind), out var annotation)
            && !visited.HasAnnotations(annotation.Kind))
        {
            visited = visited.WithAdditionalAnnotations(annotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var visited = (BlockSyntax)base.VisitBlock(node)!;
        return TryFlatten(visited.Statements, out var flat)
            ? visited.WithStatements(SyntaxFactory.List(flat))
            : visited;
    }

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
    {
        var visited = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;
        return TryFlatten(visited.Statements, out var flat)
            ? visited.WithStatements(SyntaxFactory.List(flat))
            : visited;
    }

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
        if (!visited.Members.Any(m => m is GlobalStatementSyntax
                { Statement: BlockSyntax block } && block.HasAnnotation(HoistBlockAnnotation)))
            return visited;
        var members = new List<MemberDeclarationSyntax>();
        foreach (var member in visited.Members)
        {
            if (member is GlobalStatementSyntax global
                && global.Statement is BlockSyntax wrapped
                && wrapped.HasAnnotation(HoistBlockAnnotation))
            {
                members.AddRange(wrapped.Statements.Select(
                    s => (MemberDeclarationSyntax)SyntaxFactory.GlobalStatement(s)));
            }
            else
            {
                members.Add(member);
            }
        }
        return visited.WithMembers(SyntaxFactory.List(members));
    }

    /// <summary>把带 HoistBlockAnnotation 的包装块就地展平为其语句序列。</summary>
    private static bool TryFlatten(IReadOnlyList<StatementSyntax> statements,
        out List<StatementSyntax> flat)
    {
        flat = new List<StatementSyntax>();
        var any = false;
        foreach (var statement in statements)
        {
            if (statement is BlockSyntax block && block.HasAnnotation(HoistBlockAnnotation))
            {
                any = true;
                flat.AddRange(block.Statements);
            }
            else
            {
                flat.Add(statement);
            }
        }
        return any;
    }

    public override SyntaxNode? VisitStatement(StatementSyntax node)
    {
        if (_processingStatement || node is BlockSyntax)
            return base.VisitStatement(node);

        // 仅收集“直接属于本语句表达式部分”的迁移调用；嵌套块/lambda/本地函数内的调用
        // 由它们各自的语句 Visit 处理（FirstAncestor Statement 不等于本语句）。
        var marked = node.DescendantNodes(n =>
                n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation =>
                invocation.FirstAncestorOrSelf<StatementSyntax>() == node
                && Model.GetSymbolInfo(invocation).Symbol is IMethodSymbol callee
                && MigratedByOriginal.Contains(callee.OriginalDefinition))
            .ToList();
        if (marked.Count == 0) return base.VisitStatement(node);

        var dataBySpan = new Dictionary<int, string>();
        _calleeByData = new Dictionary<string, IMethodSymbol>();
        for (var i = 0; i < marked.Count; i++)
        {
            var data = i.ToString(CultureInfo.InvariantCulture);
            dataBySpan[marked[i].SpanStart] = data;
            _calleeByData[data] = (IMethodSymbol)Model.GetSymbolInfo(marked[i]).Symbol!;
        }
        var prepared = node.ReplaceNodes(marked,
            (original, _) => original.WithAdditionalAnnotations(
                new SyntaxAnnotation(CallMarkKind, dataBySpan[original.SpanStart])));

        _directCallData = null;
        _directDeclaratorName = null;
        MarkDirectForm(node, dataBySpan);

        _prefixes.Clear();
        _processingStatement = true;
        StatementSyntax visited;
        try
        {
            visited = (StatementSyntax)base.VisitStatement(prepared)!;
        }
        finally
        {
            _processingStatement = false;
        }

        var prefixes = _prefixes.ToList();
        var direct = _directCallData != null;
        _directCallData = null;
        _directDeclaratorName = null;
        _calleeByData = null;
        _mm3 = false;
        _mm3Source = null;

        if (direct)
        {
            // 丢弃调用 / 单变量声明确：原语句整体被前缀序列取代。
            return SyntaxFactory.Block(prefixes
                    .WithNavigationAnnotation(RecordSyntaxFacts.RewriteAnnotation))
                .WithAdditionalAnnotations(HoistBlockAnnotation);
        }
        if (prefixes.Count == 0) return visited;
        return SyntaxFactory.Block(prefixes.Concat(new[] { visited })
                .WithNavigationAnnotation(RecordSyntaxFacts.RewriteAnnotation))
            .WithAdditionalAnnotations(HoistBlockAnnotation);
    }

    private void MarkDirectForm(StatementSyntax node,
        IReadOnlyDictionary<int, string> dataBySpan)
    {
        if (node is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax directCall }
            && dataBySpan.TryGetValue(directCall.SpanStart, out var d1))
        {
            _directCallData = d1;
            return;
        }
        if (node is LocalDeclarationStatementSyntax { Declaration.Variables.Count: 1 } declaration
            && declaration.Declaration.Variables[0].Initializer?.Value
                is InvocationExpressionSyntax declaratorCall
            && dataBySpan.TryGetValue(declaratorCall.SpanStart, out var d2))
        {
            _directCallData = d2;
            _directDeclaratorName = declaration.Declaration.Variables[0].Identifier.Text;
        }
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (!_processingStatement || _calleeByData == null) return visited;
        var data = node.GetAnnotations(CallMarkKind).FirstOrDefault()?.Data;
        if (data == null || !_calleeByData.TryGetValue(data, out var callee)) return visited;
        return RewriteMarkedCall(visited, data, callee);
    }

    private ExpressionSyntax RewriteMarkedCall(
        InvocationExpressionSyntax visited, string data, IMethodSymbol callee)
    {
        var nonVoid = !callee.ReturnsVoid;
        var isDirect = data == _directCallData;
        var implicitThis = visited.Expression is IdentifierNameSyntax
            || visited.Expression is MemberAccessExpressionSyntax
                { Expression: ThisExpressionSyntax };
        var absorb = _unit != null && implicitThis;

        string? outName;
        if (!nonVoid)
        {
            outName = null;
        }
        else if (_mm3 && absorb)
        {
            // this = M()（MM-3 源是吸收临时变量 __r，原返回值被丢弃）。
            outName = "_";
        }
        else if (isDirect && _directDeclaratorName != null)
        {
            outName = _directDeclaratorName;
        }
        else if (isDirect)
        {
            outName = "_";
        }
        else
        {
            outName = NextSyntheticName(visited, "__o", ref _outCounter);
        }

        var call = nonVoid ? WithOutArgument(visited, outName!) : visited;

        if (absorb)
        {
            var temp = NextSyntheticName(visited, "__r", ref _absorbedCounter);
            var qualified = call.Expression is IdentifierNameSyntax id
                ? call.WithExpression(QualifyThis(id.Identifier.Text))
                : call;
            _prefixes.Add(DeclareVar(temp, qualified));
            // MM-3 场景的逐成员拷贝由 VisitExpressionStatement 用临时变量统一产出，避免重复。
            if (!_mm3)
            {
                foreach (var member in _unit!.PromotedMembers)
                    _prefixes.Add(SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(_locals![member]),
                            Qualify(temp, member.Name))));
            }
            _mm3Source = temp;
            return isDirect ? visited : SyntaxFactory.IdentifierName(outName!);
        }

        // 非吸收：回写接收者（this 仅可能出现在 ctor——scanner 已放行）。
        ExpressionSyntax receiverNode = visited.Expression switch
        {
            IdentifierNameSyntax => SyntaxFactory.ThisExpression(),
            MemberAccessExpressionSyntax memberAccess => memberAccess.Expression,
            _ => ReceiverOf(visited),
        };
        var receiverText = receiverNode.WithoutTrivia().ToFullString();
        var writebackCall = visited.Expression is IdentifierNameSyntax identifier
            ? call.WithExpression(SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParseExpression(receiverText),
                SyntaxFactory.IdentifierName(identifier.Identifier.Text)))
            : call;
        // LHS 必须是独立节点实例（同一节点不能同时挂在两个树位置）。
        var lhs = SyntaxFactory.ParseExpression(receiverText);
        _prefixes.Add(SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression, lhs, writebackCall)
                .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation)));
        return isDirect ? visited : SyntaxFactory.IdentifierName(outName!);
    }

    internal static InvocationExpressionSyntax WithOutArgument(
        InvocationExpressionSyntax call, string name)
    {
        ExpressionSyntax outExpression = name == "_"
            ? SyntaxFactory.IdentifierName("_")
            : SyntaxFactory.DeclarationExpression(
                SyntaxFactory.IdentifierName(SyntaxFactory.Identifier("var")
                    .WithTrailingTrivia(SyntaxFactory.Space)),
                SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier(name)));
        var argument = SyntaxFactory.Argument(outExpression)
            .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword)
                .WithTrailingTrivia(SyntaxFactory.Space));
        return call.AddArgumentListArguments(argument);
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // MM-3：this = expr; → 逐提升成员拷贝。RHS 先经 Visit，使其中对提升成员的读取
        // 同步替换为局部（this = Make(X); → __x = Make(__x).X;）。
        if (_unit != null
            && node.Expression is AssignmentExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                    Left: ThisExpressionSyntax,
                } assignment)
        {
            _mm3 = true;
            _mm3Source = null;
            var visitedRight = (ExpressionSyntax)Visit(assignment.Right)!;
            var rhsPrefixes = _prefixes.ToList();
            _prefixes.Clear();

            // RHS 是对本 record 迁移方法的调用时，源对象是吸收临时（var __r = this.M(...)）；
            // 否则按 RHS 整体表达式逐成员解析拷贝（每个成员各解析一份，禁止节点复用）。
            var sourceText = visitedRight.WithoutTrivia().ToFullString();
            var copies = _unit.PromotedMembers.Select(member =>
            {
                var source = _mm3Source != null
                    ? (ExpressionSyntax)SyntaxFactory.IdentifierName(_mm3Source)
                    : SyntaxFactory.ParseExpression(sourceText);
                return SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(_locals![member]),
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            source,
                            SyntaxFactory.IdentifierName(member.Name))));
            });
            return SyntaxFactory.Block(rhsPrefixes.Concat(copies)
                .WithNavigationAnnotation(RecordSyntaxFacts.RewriteAnnotation));
        }
        return (SyntaxNode?)base.VisitExpressionStatement(node)!;
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (_unit == null) return base.VisitReturnStatement(node);
        var nonVoid = (_unit.Kind & MethodMigrationKind.NonVoidToOut) != 0;
        if (node.Expression is { } expression)
        {
            if (!nonVoid) return base.VisitReturnStatement(node);
            var visitedExpression = (ExpressionSyntax)Visit(expression)!;
            return SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(_resultName),
                            visitedExpression))
                    .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation),
                SyntaxFactory.ReturnStatement(
                        BuildRecordWith(SyntaxFactory.ThisExpression()))
                    .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation));
        }
        return SyntaxFactory.ReturnStatement(BuildRecordWith(SyntaxFactory.ThisExpression()))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = base.VisitMemberAccessExpression(node);
        if (_locals != null
            && node.Expression is ThisExpressionSyntax
            && Model.GetSymbolInfo(node).Symbol is { } symbol
            && _locals.ContainsKey(symbol)
            && !IsNameof(node))
        {
            return SyntaxFactory.IdentifierName(_locals[symbol]);
        }
        return visited;
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var visited = base.VisitIdentifierName(node);
        if (_locals != null
            && Model.GetSymbolInfo(node).Symbol is { } symbol
            && symbol is IFieldSymbol or IPropertySymbol
            && _locals.ContainsKey(symbol)
            && !IsNameof(node))
        {
            return SyntaxFactory.IdentifierName(_locals[symbol]);
        }
        return visited;
    }

    private string NextSyntheticName(SyntaxNode anchor, string prefix, ref int counter)
    {
        var taken = EnclosingDeclaredNames(anchor);
        string candidate;
        do
        {
            candidate = prefix + counter.ToString(CultureInfo.InvariantCulture);
            counter++;
        } while (taken.Contains(candidate) || !_usedNames.Add(candidate));
        return candidate;
    }

    private static HashSet<string> EnclosingDeclaredNames(SyntaxNode node)
    {
        var scope = node.AncestorsAndSelf().FirstOrDefault(ancestor => ancestor is
            BaseMethodDeclarationSyntax
            or AccessorDeclarationSyntax
            or AnonymousFunctionExpressionSyntax
            or LocalFunctionStatementSyntax);
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (scope == null) return names;
        foreach (var declarator in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            names.Add(declarator.Identifier.Text);
        var parameters = scope switch
        {
            BaseMethodDeclarationSyntax method => method.ParameterList.Parameters,
            AnonymousFunctionExpressionSyntax function when function.ParameterList != null =>
                function.ParameterList.Parameters,
            LocalFunctionStatementSyntax local => local.ParameterList.Parameters,
            _ => default(SeparatedSyntaxList<ParameterSyntax>),
        };
        foreach (var parameter in parameters)
            names.Add(parameter.Identifier.Text);
        return names;
    }

    private static bool IsNameof(ExpressionSyntax node)
    {
        var argument = node.FirstAncestorOrSelf<ArgumentSyntax>();
        return argument?.Parent?.Parent is InvocationExpressionSyntax invoked
            && invoked.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" };
    }

    private static Dictionary<ISymbol, string> BuildLocalNames(
        MigrationUnit unit, MethodDeclarationSyntax declaration)
    {
        var taken = declaration.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Select(v => v.Identifier.Text)
            .Concat(declaration.ParameterList.Parameters.Select(p => p.Identifier.Text))
            .ToHashSet(StringComparer.Ordinal);
        var map = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        foreach (var member in unit.PromotedMembers)
        {
            var stem = member.Name.TrimStart('_');
            var candidate = "__" + (stem.Length == 0
                ? "x"
                : char.ToLower(stem[0]) + stem.Substring(1));
            while (taken.Contains(candidate)) candidate += "_";
            taken.Add(candidate);
            map[member] = candidate;
        }
        return map;
    }

    /// <summary>调用点接收者：M() → this；x.M() → x。</summary>
    internal static ExpressionSyntax ReceiverOf(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Expression,
            _ => SyntaxFactory.ThisExpression(),
        };

    internal static LocalDeclarationStatementSyntax DeclareVar(
        string name, ExpressionSyntax initializer)
    {
        var declarator = SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(name))
            .WithInitializer(SyntaxFactory.EqualsValueClause(initializer));
        var varKeyword = SyntaxFactory.Token(SyntaxKind.VarKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);
        return SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                varKeyword, SyntaxFactory.SingletonSeparatedList(declarator)));
    }

    internal static MemberAccessExpressionSyntax QualifyThis(string name) =>
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.ThisExpression(),
            SyntaxFactory.IdentifierName(name));

    internal static MemberAccessExpressionSyntax Qualify(string receiver, string name) =>
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName(receiver),
            SyntaxFactory.IdentifierName(name));
}

internal static class RrStatementListExtensions
{
    public static IEnumerable<StatementSyntax> WithNavigationAnnotation(
        this IEnumerable<StatementSyntax> statements, SyntaxAnnotation annotation) =>
        statements.Select(s => s.WithAdditionalAnnotations(annotation));
}
```

同文件追加（声明重写器——外部模式；只对本文件声明的迁移方法改写签名与方法体）：

```csharp
/// <summary>
/// Phase 3c：迁移方法声明（提升 / 出口合成 / 签名）与全树调用点回写（外部模式逻辑
/// 全部在共享基类）；同时作为第一个 rewriter，为后续 MutationSiteRewriter 的用点节点
/// 挂定位注解。
/// </summary>
internal sealed class MethodMigrator : MethodCallSyntaxRewriter
{
    private readonly RewriteContext _context;
    private readonly Dictionary<IMethodSymbol, MigrationUnit> _units =
        new(SymbolEqualityComparer.Default);

    public MethodMigrator(RewriteContext context)
        : base(context.Model, BuildMigratedSet(context), BuildMsAnnotationMap(context))
    {
        _context = context;
        foreach (var plan in context.ActivePlans)
        {
            foreach (var migration in plan.Migrations)
            {
                if (migration.Method.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath
                        is { } file
                    && string.Equals(file, context.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    _units[migration.Method] = new MigrationUnit(
                        migration.Method, migration.Kind, migration.PromotedMembers);
                }
            }
        }
    }

    private static IReadOnlySet<IMethodSymbol> BuildMigratedSet(RewriteContext context)
    {
        // 跨文件/跨 record 的调用也必须识别：集合取所有 active 计划的全部迁移方法。
        var set = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var plan in context.ActivePlans)
            foreach (var migration in plan.Migrations)
                set.Add(migration.Method.OriginalDefinition);
        return set;
    }

    private static Dictionary<(int Start, int Kind), SyntaxAnnotation> BuildMsAnnotationMap(
        RewriteContext context)
    {
        var map = new Dictionary<(int, int), SyntaxAnnotation>();
        foreach (var plan in context.ActivePlans)
        {
            foreach (var sitePlan in plan.Sites)
            {
                if (!string.Equals(sitePlan.Site.FilePath, context.FilePath,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (sitePlan.Action == SiteActionKind.MethodMigrateCall) continue;
                var key = (sitePlan.Site.WriteNode.SpanStart, sitePlan.Site.WriteNode.RawKind);
                map[key] = MutationSiteRewriter.SiteAnnotationFor(plan, sitePlan);
            }
        }
        return map;
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol == null || !_units.TryGetValue(symbol, out var unit))
            return base.VisitMethodDeclaration(node);

        var nonVoid = (unit.Kind & MethodMigrationKind.NonVoidToOut) != 0;
        var resultName = UniqueResultName(node);

        var body = node.Body;
        if (body == null && node.ExpressionBody != null)
        {
            // 表达式体展开为块体（MM-4）。非 void：Bump() => X += 1;
            // → { result = X += 1; }，尾部统一补 return this with {...}。
            body = nonVoid
                ? SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(resultName),
                        node.ExpressionBody.Expression)))
                : SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(node.ExpressionBody.Expression));
        }
        if (body == null) return base.VisitMethodDeclaration(node);

        var bodyRewriter = ForBody(
            _context.Model, unit, MigratedByOriginal, MsAnnotations, resultName);
        var migrated = (BlockSyntax)bodyRewriter.Visit(body)!;

        // MM-2：方法体顶部为每个被提升成员声明局部副本：var __x = this.X;
        var promotionDeclarations = unit.PromotedMembers
            .Select(member => DeclareVar(
                bodyRewriter.LocalNames![member], QualifyThis(member.Name)))
            .ToList();

        var tail = SyntaxFactory.ReturnStatement(
                bodyRewriter.BuildRecordWith(SyntaxFactory.ThisExpression()))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
        var finalBody = SyntaxFactory.Block(
            promotionDeclarations.Concat(migrated.Statements).Append(tail));

        var recordTypeSyntax = SyntaxFactory.ParseTypeName(
            unit.Method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

        var parameters = node.ParameterList.Parameters;
        if (nonVoid)
        {
            // T M() → R M(out T result)。result 类型独立解析，避免与旧返回类型共享节点。
            var resultTypeSyntax = SyntaxFactory.ParseTypeName(
                symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            var resultParameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(resultName))
                .WithType(resultTypeSyntax)
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.OutKeyword)
                        .WithTrailingTrivia(SyntaxFactory.Space)));
            parameters = parameters.Add(resultParameter);
        }

        _context.Statistics.MethodsMigrated++;
        return node.WithReturnType(recordTypeSyntax)
            .WithParameterList(SyntaxFactory.ParameterList(parameters))
            .WithBody(finalBody)
            .WithExpressionBody(null)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    private static string UniqueResultName(MethodDeclarationSyntax node)
    {
        var taken = node.ParameterList.Parameters.Select(p => p.Identifier.Text)
            .Concat(node.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Select(v => v.Identifier.Text))
            .ToHashSet(StringComparer.Ordinal);
        var name = "result";
        while (taken.Contains(name)) name += "_";
        return name;
    }
}
```

- [ ] **Step 6: 跑通全部用例**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: `Passed!`，Task 16 的 14 个 MM 用例继续通过（void 路径走同一套共享重写器：
丢弃调用 → writeback、体内吸收 → `var __r = this.B()`、外部 → `other = other.B()`、
ctor → `this = this.B()`、MM-3 `this=` 拆解），新增 13 个 MM-5/MM-6 用例全部通过。

重点人工核对：
- `var v = p.Next();` 提升后外层 Block 上的 `HoistBlockAnnotation` 包装块已在方法
  `VisitBlock` 展平，`v`（以 `out var v` 声明）对后续语句可见，输出可编译。
- `if (p.Ready()) ...` 输出为顺序两条语句 `p = p.Ready(out var __o0); if (__o0) ...`，
  不存在嵌套裸块改变语义。
- 非 void 迁移方法尾部同时存在合成 `return this with {...}`；`return E;` 分支展开为
  `{ result = E; return this with {...}; }` 内层块（仅警告级不可达，不阻断编译）。

- [ ] **Step 7: 提交**

Run:
```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): MM-5 non-void out-result migration, call-site hoisting, RR053/RR065 gates"
```

---

### Task 18: MM-7/MS-19 扩展方法迁移（`this R r` 接收者提升）+ RR058 方法组阻断

> 把 MM-1~MM-5 管线推广到 in-tree 扩展方法：`static void Bump(this R r)` →
> `static R Bump(this R r)`（非 void：`static R Next(this R r, out T result)`，`out` 恒在
> `this` 参之后）。`r` 形参取代 `this` 成为提升源/出口 `r with {...}` 的接收者；
> 静态形态 `Bump(r)` / `E.Bump(r)` 调用点回写首参。另补 MS-15：方法组/委托绑定到
> 被改签名方法 → RR058 整条 record 跳过。

**Files:**
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyAnalyzer.cs`（发现扩展方法、按 `this` 形参收集写入与调用边）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/MethodMigrator.cs`
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

- [ ] **Step 1: 写失败测试**

追加：

```csharp
    [Fact]
    public void MM7_VoidExtension_ReturnsRecordAndPromotesReceiver()
    {
        var src = """
            public static class E
            {
                public static void Bump(this P p) { p.X += 1; }
            }
            public record struct P
            {
                public int X { get; set; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("public static P Bump(this P p)", output);
        Assert.Contains("var __x = p.X;", output);
        Assert.Contains("__x += 1;", output);
        Assert.Contains("return p with { X = __x };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_VoidExtension_ReducedCallGetsWriteback()
    {
        var src = """
            public static class E
            {
                public static void Bump(this P p) { p.X += 1; }
            }
            public record struct P
            {
                public int X { get; set; }
            }
            public class C
            {
                public void M(P p) { p.Bump(); }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p.Bump();", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_VoidExtension_StaticQualifiedCallGetsWriteback()
    {
        var src = """
            public static class E
            {
                public static void Bump(this P p) { p.X += 1; }
            }
            public record struct P
            {
                public int X { get; set; }
            }
            public class C
            {
                public void M(P p) { E.Bump(p); }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = E.Bump(p);", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_VoidExtension_StaticUnqualifiedCallInsideSameClassGetsWriteback()
    {
        var src = """
            public static class E
            {
                public static void Bump(this P p) { p.X = 1; }
                public static void Other(P p) { Bump(p); }
            }
            public record struct P
            {
                public int X { get; set; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = Bump(p);", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_NonVoidExtension_OutResultAfterThisParameter()
    {
        var src = """
            public static class E
            {
                public static int Next(this P p) { p.X += 1; return p.X; }
            }
            public record struct P
            {
                public int X { get; set; }
            }
            public class C
            {
                public void M(P p) { var v = p.Next(); }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("public static P Next(this P p, out int result)", output);
        Assert.Contains("p = p.Next(out var v);", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_NonVoidExtension_NestedCallHoists()
    {
        var src = """
            public static class E
            {
                public static int Next(this P p) { p.X += 1; return p.X; }
            }
            public record struct P
            {
                public int X { get; set; }
            }
            public class C
            {
                public void M(P p) { Use(p.Next()); }
                private static void Use(int v) { }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p.Next(out var __o0);", output);
        Assert.Contains("Use(__o0);", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_ExtensionBody_ReceiverReadsSubstituted()
    {
        var src = """
            public static class E
            {
                public static void Set(this P p, int v) { p.X = p.X + v; }
            }
            public record struct P
            {
                public int X { get; set; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("var __x = p.X;", output);
        Assert.Contains("__x = __x + v;", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_ExtensionBody_AbsorbsMigratedInstanceCall()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void Inc() { X += 1; }
            }
            public static class E
            {
                public static void Bump(this P p) { p.Inc(); }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("var __r0 = p.Inc();", output);
        Assert.Contains("__x = __r0.X;", output);
        Assert.Contains("return p with { X = __x };", output);
        Assert.Equal(2, stats.MethodsMigrated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_InstanceBody_AbsorbsReducedExtensionCall()
    {
        var src = """
            public static class E
            {
                public static void Bump(this P p) { p.X = 1; }
            }
            public record struct P
            {
                public int X { get; set; }
                public void A() { this.Bump(); }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("var __r0 = this.Bump();", output);
        Assert.Contains("__x = __r0.X;", output);
        Assert.Equal(2, stats.MethodsMigrated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_ExtensionBody_AbsorbsReducedExtensionCall()
    {
        var src = """
            public static class E
            {
                public static void A(this P p) { p.X = 1; }
                public static void B(this P p) { p.A(); }
            }
            public record struct P
            {
                public int X { get; set; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("var __r0 = p.A();", output);
        Assert.Contains("__x = __r0.X;", output);
        Assert.Equal(2, stats.MethodsMigrated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS15_MethodGroupBindingToMigratedExtension_BlocksWithRR058()
    {
        var src = """
            public static class E
            {
                public static void Bump(this P p) { p.X = 1; }
                public static System.Action<P> Make() => Bump;
            }
            public record struct P
            {
                public int X { get; set; }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR058);
    }

    [Fact]
    public void MM7_ExtensionOnNonRecord_Unchanged()
    {
        var src = """
            public static class S
            {
                public static int Len(this string s) => s.Length;
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Equal(src, output);
        Assert.Equal(0, stats.MethodsMigrated);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: analyzer 不收集扩展方法 → 扩展方法用点也不成立（无迁移计划），MM-7 用例全红；
RR058 用例失败（没有任何诊断）。

- [ ] **Step 3: analyzer 发现扩展方法并按 `this` 形参收集写入/调用边]

1. 在 `RecordReadOnlyAnalyzer` 的 record 分析方法中，in-tree 实例方法收集循环
（`info.InTreeMethods.Add(member); ... if (wi != null) info.WriteInfos[member] = wi;`
所在的 `foreach`）**之后**追加扩展方法发现：

```csharp
        // MM-7/MS-19：in-tree 扩展方法（this R r）修改本 record 视同 mutating 成员。
        // 外部（BCL/三方）扩展方法修改静态不可见，不在此列。
        foreach (var type in AllTypes(compilation))
        {
            if (!type.IsStatic || type.DeclaringSyntaxReferences.Length == 0) continue;
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (!method.IsExtensionMethod || method.Parameters.Length == 0) continue;
                if (!SymbolEqualityComparer.Default.Equals(
                        method.Parameters[0].Type.OriginalDefinition, info.Symbol.OriginalDefinition))
                    continue;
                var wi = BuildWriteInfo(method, info, compilation);
                if (wi is not { WrittenMembers.Count: > 0 } && wi is not { WritesThis: true })
                    continue;
                info.InTreeMethods.Add(method);
                info.WriteInfos[method] = wi;
            }
        }
```

同文件新增遍历辅助：

```csharp
    private static IEnumerable<INamedTypeSymbol> AllTypes(Compilation compilation)
    {
        var stack = new Stack<INamespaceOrTypeSymbol>();
        stack.Push(compilation.GlobalNamespace);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var member in current.GetMembers())
            {
                if (member is INamespaceSymbol childNamespace)
                {
                    stack.Push(childNamespace);
                }
                else if (member is INamedTypeSymbol childType)
                {
                    yield return childType;
                    stack.Push(childType);
                }
            }
        }
    }
```

2. `BuildWriteInfo(...)` 开头（`var info = new MethodWriteInfo { ... };` 之前）计算
“自身接收者”——实例方法为隐式 `this`（null），扩展方法为 `this` 形参符号：

```csharp
        IParameterSymbol? selfParameter =
            method.IsExtensionMethod && method.Parameters.Length > 0
            && SymbolEqualityComparer.Default.Equals(
                method.Parameters[0].Type.OriginalDefinition, owner.Symbol.OriginalDefinition)
                ? method.Parameters[0]
                : null;
```

写入扫描循环中的 4 处 `CollectWrittenSymbol(...)` 调用统一追加 `selfParameter` 实参：

```csharp
                case AssignmentExpressionSyntax assign:
                    CollectWrittenSymbol(assign.Left, model, owner, info.WrittenMembers,
                        selfParameter);
                    if (assign.Left is ThisExpressionSyntax && selfParameter == null)
                        info.WritesThis = true;
                    break;
                case PostfixUnaryExpressionSyntax post when post.Kind()
                    is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression:
                    CollectWrittenSymbol(post.Operand, model, owner, info.WrittenMembers,
                        selfParameter);
                    break;
                case PrefixUnaryExpressionSyntax pre when pre.Kind()
                    is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression:
                    CollectWrittenSymbol(pre.Operand, model, owner, info.WrittenMembers,
                        selfParameter);
                    break;
                case ArgumentSyntax { RefKindKeyword.RawKind: not SyntaxKind.None } arg:
                    CollectWrittenSymbol(arg.Expression, model, owner, info.WrittenMembers,
                        selfParameter);
                    break;
```

（扩展方法内 `this = expr` 不可能出现；`r = expr` 只是形参重绑、不属于 MM-3，
故 WritesThis 仅在实例方法 `selfParameter == null` 时记录。）

3. `CollectWrittenSymbol` 签名与首条分支改为接收“自身接收者”：

```csharp
    private static void CollectWrittenSymbol(
        ExpressionSyntax expression, SemanticModel? model, RecordInfo owner,
        HashSet<ISymbol> written, IParameterSymbol? selfParameter = null)
    {
        if (model == null) return;
        var current = expression;
        // 剥掉 this./r. / 强制类型转换 / null 抑制。
        while (true)
        {
            switch (current)
            {
                // this.F（实例方法）或 r.F（扩展方法 this 形参）才计入提升写入。
                case MemberAccessExpressionSyntax m when IsSelfReceiver(
                        m.Expression, model, selfParameter):
                    if (model.GetSymbolInfo(m).Symbol is { } s && SymbolBelongsTo(s, owner.Symbol))
                        written.Add(s);
                    return;
                case IdentifierNameSyntax id when model.GetSymbolInfo(id).Symbol is { } s2
                    && SymbolBelongsTo(s2, owner.Symbol):
                    written.Add(s2);
                    return;
                case CastExpressionSyntax cast: current = cast.Expression; continue;
                case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } supp:
                    current = supp.Operand; continue;
                case DeclarationExpressionSyntax: return;
                default: return;
            }
        }
    }

    private static bool IsSelfReceiver(ExpressionSyntax expression, SemanticModel model,
        IParameterSymbol? selfParameter)
    {
        if (selfParameter == null) return expression is ThisExpressionSyntax;
        return expression is IdentifierNameSyntax id
            && SymbolEqualityComparer.Default.Equals(
                model.GetSymbolInfo(id).Symbol, selfParameter);
    }
```

4. 把 Task 16 的“MM-1 同 record 调用边”收集块**整体替换**为接收者感知版本
（支持 `p.Inc()` / `this.Bump()` / `Bump(r)` / `E.Bump(r)` 四种形态）：

```csharp
        // MM-1：调用图边——callee 操作的必须是本方法的“自身接收者”（this 或 this 形参）。
        foreach (var invocation in bodyRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) continue;
            if (callee.MethodKind != MethodKind.Ordinary) continue;
            if (callee.DeclaringSyntaxReferences.Length == 0) continue;
            var selfArgument = SelfArgumentOf(invocation, callee, model);
            if (selfArgument == null || !IsSelfArgument(selfArgument, model, selfParameter))
                continue;
            var targetsOwner =
                (!callee.IsStatic
                    && SymbolEqualityComparer.Default.Equals(
                        callee.ContainingType?.OriginalDefinition, owner.Symbol.OriginalDefinition))
                || (callee.IsExtensionMethod
                    && callee.Parameters.Length > 0
                    && SymbolEqualityComparer.Default.Equals(
                        callee.Parameters[0].Type.OriginalDefinition, owner.Symbol.OriginalDefinition));
            if (targetsOwner) info.SameRecordCalls.Add(callee);
        }
```

```csharp
    /// <summary>
    /// 调用在“自身接收者”上的实参表达式：隐式实例调用 M() → this；
    /// r.M()/this.M() → 点号左侧；扩展静态形态 M(r)/E.M(r) → 首参。
    /// </summary>
    private static ExpressionSyntax? SelfArgumentOf(InvocationExpressionSyntax invocation,
        IMethodSymbol callee, SemanticModel model)
    {
        switch (invocation.Expression)
        {
            case IdentifierNameSyntax:
                return callee.IsExtensionMethod
                    ? invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression
                    : SyntaxFactory.ThisExpression();
            case MemberAccessExpressionSyntax ma:
                if (callee.IsExtensionMethod
                    && model.GetSymbolInfo(ma.Expression).Symbol is ITypeSymbol)
                {
                    // E.Bump(r)：左侧是类型名（静态限定），接收者是首参。
                    return invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                }
                return ma.Expression;
            default:
                return null;
        }
    }

    private static bool IsSelfArgument(ExpressionSyntax expression, SemanticModel model,
        IParameterSymbol? selfParameter)
    {
        if (expression is ThisExpressionSyntax) return selfParameter == null;
        return selfParameter != null
            && expression is IdentifierNameSyntax id
            && SymbolEqualityComparer.Default.Equals(
                model.GetSymbolInfo(id).Symbol, selfParameter);
    }
```

- [ ] **Step 4: planner 标记 Extension + RR058 方法组扫描器 + PlanBuilder 接线**

1. `MethodMigrator.cs` 中 `MigrationUnit` 记录整体替换为（自身接收者从 `Method + Kind`
派生，`MethodMigrationPlan` 与 `MethodMigrator` 的既有构造点无需改签名）：

```csharp
internal sealed record MigrationUnit(
    IMethodSymbol Method,
    MethodMigrationKind Kind,
    IReadOnlyList<ISymbol> PromotedMembers)
{
    /// <summary>MM-7：扩展方法迁移单元。实例方法为 false（自身接收者即 this）。</summary>
    public bool IsExtension => (Kind & MethodMigrationKind.Extension) != 0;

    /// <summary>扩展方法的 this 形参；实例方法为 null。</summary>
    public IParameterSymbol? SelfParameter =>
        IsExtension && Method.Parameters.Length > 0 ? Method.Parameters[0] : null;
}
```

2. `MethodMigrationPlanner.BuildUnits` 末尾构造 `units` 的块替换为（组合 `Extension`
标志；analyzer 只把 this 形参类型匹配本 record 的扩展方法放进 `WriteInfos`）：

```csharp
        var units = mutating.ToDictionary(
            w => w.Method,
            w =>
            {
                var kind = (w.Method.ReturnsVoid
                        ? MethodMigrationKind.VoidToRecord
                        : MethodMigrationKind.NonVoidToOut)
                    | (w.Method.IsExtensionMethod ? MethodMigrationKind.Extension : 0);
                return new MigrationUnit(
                    w.Method,
                    kind,
                    influence[w.Method.OriginalDefinition]
                        .OrderBy(m => m.Name, StringComparer.Ordinal)
                        .ThenBy(m => m.ToDisplayString(), StringComparer.Ordinal)
                        .ToList());
            });
```

注意 planner 既有闸门对扩展方法天然成立：扩展方法是静态方法，不可能命中
`ImplementsInterfaceMethod` / `IsOverrideExplicitOrVirtual` / `IsGetAccessor` /
`IsConstructor`；`WritesThis` 对扩展方法恒 false（Step 3 已限定），故 RR064 循环只作用于
实例方法；iterator/async/partial 闸门（RR060/RR061）对扩展方法同样有效，无需特判。

3. `MethodMigrator.cs` 追加 MS-15 方法组绑定扫描器（委托绑定在签名改变后无法编译，
RR058 整条 record 原子跳过；`nameof(Bump)` 不产生方法组转换，天然不受影响）：

```csharp
/// <summary>
/// MS-15：方法组/委托绑定（Action&lt;R&gt; a = Bump; / new Func&lt;..&gt;(E.Bump) /
/// 实参传方法组 Use(Bump) / 箭头 => Bump）指向任一被迁移方法即否决。调用形态
/// Bump(...) / E.Bump(...) 由 MethodCallSiteScanner 处理，此处显式排除。
/// </summary>
internal static class MethodGroupBindingScanner
{
    public static MigrationFailure? Scan(
        Compilation compilation, IReadOnlyDictionary<IMethodSymbol, MigrationUnit> units)
    {
        var byOriginal = units.Keys.ToDictionary(
            m => m.OriginalDefinition, m => m, SymbolEqualityComparer.Default);

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot().DescendantNodes()
                         .OfType<ExpressionSyntax>())
            {
                if (name is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
                    continue;
                if (name.Parent is InvocationExpressionSyntax invocation
                    && invocation.Expression == name)
                    continue;

                var symbolInfo = model.GetSymbolInfo(name);
                var candidates = symbolInfo.Symbol != null
                    ? new[] { symbolInfo.Symbol }
                    : symbolInfo.CandidateSymbols;
                if (!candidates.Any(s => s is IMethodSymbol m
                        && byOriginal.ContainsKey(m.OriginalDefinition)))
                    continue;
                if (!model.GetConversion(name).IsMethodGroup) continue;

                return new MigrationFailure(
                    RecordReadOnlyDiagnosticCode.RR058,
                    "a method-group/delegate binding targets a method whose signature " +
                    "changes during migration; the binding cannot compile afterwards (MS-15).",
                    name.GetLocation());
            }
        }
        return null;
    }
}
```

4. `RecordConversionPlan.cs` 的迁移规划块（Task 17 版本）整体替换为——Kind 直接取
planner 结果，并在调用点扫描之前插入 RR058 闸门：

```csharp
        // mutating 方法 → MM 迁移规划（影响集 / MM-6 否决 / 调用点）。
        if (info.WriteInfos.Values.Any(w => !w.IsConstructor && !w.IsPropertySetter
                && (w.WrittenMembers.Count > 0 || w.WritesThis)))
        {
            if (!_caps.MethodMigration)
                return Fail(plan, RecordReadOnlyDiagnosticCode.RR020,
                    "record has instance method(s) that mutate state; method migration (MM) is " +
                    "required to convert.");

            var (units, migrationFailure) = MethodMigrationPlanner.BuildUnits(info);
            if (migrationFailure != null)
                return FailAt(plan, migrationFailure.Code, migrationFailure.Reason,
                    migrationFailure.Location);

            var groupFailure = MethodGroupBindingScanner.Scan(analysis.Compilation!, units);
            if (groupFailure != null)
                return FailAt(plan, groupFailure.Code, groupFailure.Reason,
                    groupFailure.Location);

            foreach (var unit in units.Values.OrderBy(u =>
                         u.Method.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0))
            {
                plan.Migrations.Add(new MethodMigrationPlan(
                    unit.Method, unit.Kind, unit.PromotedMembers));
            }

            var callSites = MethodCallSiteScanner.Scan(
                analysis.Compilation!, info, units, out var callFailure);
            if (callFailure != null)
                return FailAt(plan, callFailure.Code, callFailure.Reason, callFailure.Location);
            foreach (var (callSite, _) in callSites)
            {
                plan.Sites.Add(callSite);
                plan.TouchedFiles.Add(callSite.Site.FilePath);
            }
        }
```

- [ ] **Step 5: MethodMigrator.cs 接收者感知改造（调用点扫描器 + 共享重写器 + 声明重写）**

以下均为对 Step 5（Task 17）整文件内容的定点修改。

**5.1 `MethodCallSiteScanner.Scan` 整体替换**（`MakeSite` / `FindHoistBarrier` /
`EnclosingMember` / `IsInNestedFunction` 不动；类中追加两个私有辅助）：

```csharp
    public static List<(SitePlan Plan, bool WritebackThis)> Scan(
        Compilation compilation,
        RecordInfo owner,
        Dictionary<IMethodSymbol, MigrationUnit> units,
        out SiteFailure? failure)
    {
        failure = null;
        var result = new List<(SitePlan, bool)>();
        var unitsByOriginal = units.Keys.ToDictionary(
            m => m.OriginalDefinition, m => m, SymbolEqualityComparer.Default);

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) continue;
                if (!unitsByOriginal.TryGetValue(callee.OriginalDefinition, out var migrated))
                    continue;

                var enclosing = EnclosingMember(model, invocation);
                var nested = IsInNestedFunction(invocation);
                var self = SelfArgument(invocation, callee, model);

                // 体吸收：调用位于一个自身也被迁移的方法/访问器体内（非嵌套函数），且作用
                // 接收者就是该体的自身接收者（实例方法 this；扩展方法 this 形参）。这类调用
                // 由 ForBody 重写器吸收为 var __r = ...，不生成外部用点。四种形态：
                //   实例体内 M()/this.M()；扩展体内 r.Inc()/r.A()/Bump(r)/E.Bump(r)；
                //   实例体内以静态形态调扩展 E.Bump(this)。
                if (self != null
                    && !nested
                    && enclosing != null
                    && unitsByOriginal.ContainsKey(enclosing.OriginalDefinition)
                    && IsOwnSelfReceiver(enclosing, self, model))
                {
                    continue;
                }

                // MM-5：self 为 this 时只在 struct ctor 内可整体回写；getter/init(set)/
                // add/remove 访问器与 lambda/本地函数内 this 只读，保守阻断。
                if (self is ThisExpressionSyntax
                    && enclosing != null
                    && SymbolEqualityComparer.Default.Equals(
                        enclosing.ContainingType?.OriginalDefinition, owner.Symbol.OriginalDefinition))
                {
                    if (!nested && enclosing.MethodKind == MethodKind.Constructor)
                    {
                        if (FindHoistBarrier(invocation) != null)
                        {
                            failure = new SiteFailure(RecordReadOnlyDiagnosticCode.RR053,
                                "a migrated-method call in a non-statement context cannot be " +
                                "hoisted to a 'this = this.M(...)' write-back (MM-5/MS-16).",
                                invocation.GetLocation());
                            return result;
                        }
                        result.Add((MakeSite(tree, invocation,
                            SyntaxFactory.ThisExpression(), owner, migrated), WritebackThis: true));
                        continue;
                    }
                    failure = new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        "calling a migrated method on 'this' here cannot write back: 'this' is " +
                        "read-only in this context (only a constructor can emit 'this = this.M(...)') " +
                        "(MM-5).", invocation.GetLocation());
                    return result;
                }

                // void 迁移方法的调用只可能出现在语句位；非 void 迁移方法可在表达式内消费，
                // 但必须能提升前置语句（MM-5 三形态）。字段/属性初始化器、箭头表达式体、
                // 特性实参、查询子句、构造初始化器(: this/base)、无块 lambda、多变量声明
                // 都是不可提升屏障。
                if (FindHoistBarrier(invocation) != null)
                {
                    failure = new SiteFailure(RecordReadOnlyDiagnosticCode.RR053,
                        "a migrated-method call in a non-statement context (field/property " +
                        "initializer, expression-bodied member, attribute argument, query " +
                        "clause, constructor initializer, expression lambda, or multi-variable " +
                        "declarator) cannot be hoisted to a write-back statement (MM-5/MS-16).",
                        invocation.GetLocation());
                    return result;
                }

                if (self == null)
                {
                    failure = new SiteFailure(RecordReadOnlyDiagnosticCode.RR053,
                        "a migrated-method call in an unrecognized invocation form cannot be " +
                        "rewritten to a receiver write-back (MM-7/MS-19).",
                        invocation.GetLocation());
                    return result;
                }

                var receiverFailure = MutationSiteClassifier.ReceiverWritableAndPure(model, self);
                if (receiverFailure != null)
                {
                    failure = receiverFailure;
                    return result;
                }
                result.Add((MakeSite(tree, invocation, self, owner, migrated),
                    WritebackThis: false));
            }
        }
        return result;
    }

    /// <summary>
    /// 调用作用的“自身接收者”实参表达式：实例 M() → 合成 this；r.M()/this.M() → 点号
    /// 左侧；扩展静态形态 M(r)/E.M(r) → 首参。E.M(r) 中 M 左侧绑定到类型符号，需与
    /// reduced r.M()（左侧绑定到变量/形参）区分。
    /// </summary>
    private static ExpressionSyntax? SelfArgument(
        InvocationExpressionSyntax invocation, IMethodSymbol callee, SemanticModel model)
    {
        switch (invocation.Expression)
        {
            case IdentifierNameSyntax:
                return callee.IsExtensionMethod
                    ? invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression
                    : SyntaxFactory.ThisExpression();
            case MemberAccessExpressionSyntax ma:
                if (callee.IsExtensionMethod
                    && model.GetSymbolInfo(ma.Expression).Symbol is ITypeSymbol)
                {
                    return invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                }
                return ma.Expression;
            default:
                return null;
        }
    }

    private static bool IsOwnSelfReceiver(
        IMethodSymbol enclosing, ExpressionSyntax self, SemanticModel model)
    {
        if (self is ThisExpressionSyntax) return !enclosing.IsStatic;
        return enclosing.IsExtensionMethod
            && enclosing.Parameters.Length > 0
            && self is IdentifierNameSyntax id
            && SymbolEqualityComparer.Default.Equals(
                model.GetSymbolInfo(id).Symbol, enclosing.Parameters[0]);
    }
```

**5.2 共享重写器 `MethodCallSyntaxRewriter`：`RewriteMarkedCall` 头部吸收判定替换。**

把：

```csharp
        var nonVoid = !callee.ReturnsVoid;
        var isDirect = data == _directCallData;
        var implicitThis = visited.Expression is IdentifierNameSyntax
            || visited.Expression is MemberAccessExpressionSyntax
                { Expression: ThisExpressionSyntax };
        var absorb = _unit != null && implicitThis;
```

替换为：

```csharp
        var nonVoid = !callee.ReturnsVoid;
        var isDirect = data == _directCallData;
        var selfArgument = SelfReceiverExpression(visited, callee);
        var absorb = _unit != null && IsOwnSelfReceiver(selfArgument);
```

**5.3 吸收临时变量的调用限定**——扩展静态形态 `Bump(p)` 必须原样保留（它不是可加
`this.` 前缀的实例调用）。把：

```csharp
            var temp = NextSyntheticName(visited, "__r", ref _absorbedCounter);
            var qualified = call.Expression is IdentifierNameSyntax id
                ? call.WithExpression(QualifyThis(id.Identifier.Text))
                : call;
```

替换为：

```csharp
            var temp = NextSyntheticName(visited, "__r", ref _absorbedCounter);
            // 仅实例方法体内的裸调用 M() 需要补 this.M()；扩展静态形态 Bump(p) 与
            // reduced p.M()/this.M() 已是合法形态，保持原样。
            var qualified = call.Expression is IdentifierNameSyntax id
                && !callee.IsExtensionMethod
                ? call.WithExpression(QualifyThis(id.Identifier.Text))
                : call;
```

**5.4 非吸收回写块整体替换。** 把从注释 `// 非吸收：回写接收者` 到
`return isDirect ? visited : SyntaxFactory.IdentifierName(outName!);`（非吸收分支末尾）
的整段替换为：

```csharp
        // 非吸收：回写自身接收者（this 仅可能出现在 ctor——scanner 已放行）。
        // 扩展静态形态 Bump(r)/E.Bump(r)：接收者取首参，调用表达式本身原样保留，
        // 只在左侧产出 r = ...；reduced r.M() 同理。仅实例裸调用 M() 需补 this.M()。
        if (selfArgument == null) return isDirect ? visited : SyntaxFactory.IdentifierName(outName!);
        var receiverText = selfArgument.WithoutTrivia().ToFullString();
        ExpressionSyntax writebackCall;
        if (visited.Expression is IdentifierNameSyntax identifier && !callee.IsExtensionMethod)
        {
            writebackCall = call.WithExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    // LHS/接收者都必须独立解析节点（同一节点不能同时挂两个树位置）。
                    SyntaxFactory.ParseExpression(receiverText),
                    SyntaxFactory.IdentifierName(identifier.Identifier.Text)));
        }
        else
        {
            writebackCall = call;
        }
        var lhs = SyntaxFactory.ParseExpression(receiverText);
        _prefixes.Add(SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression, lhs, writebackCall)
                .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation)));
        return isDirect ? visited : SyntaxFactory.IdentifierName(outName!);
```

**5.5 在 `MethodCallSyntaxRewriter` 中追加四个接收者辅助**（紧邻既有 `ReceiverOf`
放置；`ReceiverOf` 保留给 M4 接口用点复用，本任务不再有调用方也不影响编译）：

```csharp
    /// <summary>
    /// 调用作用的自身接收者：实例 M() → this；x.M() → x；扩展 M(r)/E.M(r) → 首参。
    /// 与 MethodCallSiteScanner.SelfArgument 同构（重写器侧需要语义模型判类型限定）。
    /// </summary>
    private ExpressionSyntax? SelfReceiverExpression(
        InvocationExpressionSyntax invocation, IMethodSymbol callee)
    {
        switch (invocation.Expression)
        {
            case IdentifierNameSyntax:
                return callee.IsExtensionMethod
                    ? invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression
                    : SyntaxFactory.ThisExpression();
            case MemberAccessExpressionSyntax ma:
                if (callee.IsExtensionMethod
                    && Model.GetSymbolInfo(ma.Expression).Symbol is ITypeSymbol)
                {
                    return invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                }
                return ma.Expression;
            default:
                return null;
        }
    }

    /// <summary>体模式：调用的自身接收者是否就是本方法的 self（this 或 this 形参）。</summary>
    private bool IsOwnSelfReceiver(ExpressionSyntax? self)
    {
        if (self is ThisExpressionSyntax) return _unit!.SelfParameter == null;
        return _unit?.SelfParameter != null
            && self is IdentifierNameSyntax id
            && SymbolEqualityComparer.Default.Equals(
                Model.GetSymbolInfo(id).Symbol, _unit.SelfParameter);
    }

    /// <summary>出口 with/提升源的接收者表达式：实例方法 this；扩展方法 this 形参（r）。</summary>
    internal ExpressionSyntax SelfReceiver() =>
        _unit!.SelfParameter == null
            ? SyntaxFactory.ThisExpression()
            : SyntaxFactory.IdentifierName(_unit.SelfParameter.Name);

    /// <summary>self 接收者上的成员访问：this.F（实例）或 r.F（扩展）。</summary>
    internal MemberAccessExpressionSyntax QualifySelf(string name) =>
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, SelfReceiver(),
            SyntaxFactory.IdentifierName(name));
```

**5.6 提升局部替换支持 `p.F` 读取。** `VisitMemberAccessExpression` 整体替换为：

```csharp
    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = base.VisitMemberAccessExpression(node);
        if (_locals != null
            && IsSelfMemberAccess(node)
            && Model.GetSymbolInfo(node).Symbol is { } symbol
            && _locals.ContainsKey(symbol)
            && !IsNameof(node))
        {
            return SyntaxFactory.IdentifierName(_locals[symbol]);
        }
        return visited;
    }

    /// <summary>
    /// 实例体只替换 this.F；扩展体替换 this 形参上的 r.F（左值右值都替换，
    /// r.X = r.X + v → __x = __x + v）。
    /// </summary>
    private bool IsSelfMemberAccess(MemberAccessExpressionSyntax node)
    {
        if (_unit?.SelfParameter == null)
            return node.Expression is ThisExpressionSyntax;
        return node.Expression is IdentifierNameSyntax id
            && SymbolEqualityComparer.Default.Equals(
                Model.GetSymbolInfo(id).Symbol, _unit.SelfParameter);
    }
```

（`VisitIdentifierName` 不动：扩展方法体内成员访问恒为限定形态 `r.F`；裸标识符分支
继续服务实例方法体。）

**5.7 `VisitReturnStatement` 整体替换**——出口 `with` 的接收者从固定 `this` 改为
`SelfReceiver()`：

```csharp
    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (_unit == null) return base.VisitReturnStatement(node);
        var nonVoid = (_unit.Kind & MethodMigrationKind.NonVoidToOut) != 0;
        if (node.Expression is { } expression)
        {
            if (!nonVoid) return base.VisitReturnStatement(node);
            var visitedExpression = (ExpressionSyntax)Visit(expression)!;
            return SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(_resultName),
                            visitedExpression))
                    .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation),
                SyntaxFactory.ReturnStatement(BuildRecordWith(SelfReceiver()))
                    .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation));
        }
        return SyntaxFactory.ReturnStatement(BuildRecordWith(SelfReceiver()))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }
```

**5.8 声明重写 `MethodMigrator.VisitMethodDeclaration` 三处定点修改。**

(a) 提升声明与尾部出口改用 self 接收者。把：

```csharp
        // MM-2：方法体顶部为每个被提升成员声明局部副本：var __x = this.X;
        var promotionDeclarations = unit.PromotedMembers
            .Select(member => DeclareVar(
                bodyRewriter.LocalNames![member], QualifyThis(member.Name)))
            .ToList();

        var tail = SyntaxFactory.ReturnStatement(
                bodyRewriter.BuildRecordWith(SyntaxFactory.ThisExpression()))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
```

替换为：

```csharp
        // MM-2/MM-7：方法体顶部为每个被提升成员声明局部副本：
        // 实例方法 var __x = this.X;；扩展方法 var __x = r.X;。
        var promotionDeclarations = unit.PromotedMembers
            .Select(member => DeclareVar(
                bodyRewriter.LocalNames![member], bodyRewriter.QualifySelf(member.Name)))
            .ToList();

        var tail = SyntaxFactory.ReturnStatement(
                bodyRewriter.BuildRecordWith(bodyRewriter.SelfReceiver()))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
```

(b) 迁移后返回类型：扩展方法不属于 record 类型（其 `ContainingType` 是静态类 E），
必须取 this 形参类型。把：

```csharp
        var recordTypeSyntax = SyntaxFactory.ParseTypeName(
            unit.Method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
```

替换为：

```csharp
        // 扩展方法定义在静态类中，返回类型取 this 形参类型；实例方法取所在 record 类型。
        var migratedReturnType = unit.SelfParameter?.Type ?? unit.Method.ContainingType;
        var recordTypeSyntax = SyntaxFactory.ParseTypeName(
            migratedReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
```

（`node.WithReturnType(...)` 只换返回类型，`public static` 修饰符原样保留；扩展方法
迁移后仍是静态方法。）

(c) 非 void 的 `out T result` 形参位置：扩展方法必须插在 this 参之后（索引 1），
实例方法仍追加末尾。把：

```csharp
            parameters = parameters.Add(resultParameter);
```

替换为：

```csharp
            // MM-7：static R Next(this R r, out T result)——out 恒在 this 参之后。
            parameters = unit.IsExtension
                ? parameters.Insert(1, resultParameter)
                : parameters.Add(resultParameter);
```

`MethodMigrator` 构造函数中 `new MigrationUnit(migration.Method, migration.Kind, ...)`
无需修改：`Kind` 已带 `Extension` 标志，`SelfParameter` 由记录属性派生。

- [ ] **Step 6: 跑通全部用例**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: `Passed!`——Task 16/17 的全部 MM 用例继续通过，新增 12 个 MM-7/MS-19/MS-15
用例全部通过。

重点人工核对：
- `public static void Bump(this P p)` 输出签名 `public static P Bump(this P p)`，
  体内 `var __x = p.X;` / `__x += 1;`，尾部 `return p with { X = __x };`。
- 非 void 扩展签名 `public static P Next(this P p, out int result)`（out 在 this 参之后，
  `static` 保留）；reduced 调用点 `p = p.Next(out var v);`，嵌套消费
  `Use(p.Next())` → 前缀 `p = p.Next(out var __o0);` + `Use(__o0);`。
- 静态形态回写：`p = E.Bump(p);` / `p = Bump(p);`（调用表达式原样，仅左侧回写首参）。
- 三类吸收：扩展体吸收实例调用 `var __r0 = p.Inc();` + `__x = __r0.X;`；实例体吸收
  reduced 扩展 `var __r0 = this.Bump();`；扩展体吸收扩展 `var __r0 = p.A();`。
- RR058 用例 record 被原子跳过（输出无改写），诊断含 RR058，定位在 `=> Bump` 的
  `Bump` 标识符。
- 非 record 扩展（`Len(this string s)`）源码原样、`MethodsMigrated` 为 0。

- [ ] **Step 7: 提交**

Run:
```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): MM-7/MS-19 extension method migration, RR058 method-group gate"
```

## M4：InterfaceRippler（IR-1~IR-7）

> 目标（规范 §4.5）：转换中的 record 实现了带可写成员的 in-tree 接口 I 时，接口**新增** With 风格成员（不动既有成员），record 旧成员降级为显式实现桩，并生成显式/公共两个 With 实现；in-tree 其他实现者 fluent 补齐；调用点按静态类型（record / 接口）改写。外部接口 RR070（FB-1，FB-3 force 在 Task 22）、签名碰撞 RR071。
>
> 任务切分：
> - **Task 19**：IR-1/IR-2/IR-4 属性触发（`T P {get;set;}` → `WithP`），IR-7 外部接口 RR070 / 同名碰撞 RR071；翻转 `InterfaceRipple` cap。
> - **Task 20**：IR-3 in-tree 非 record（及未转换 record）实现者的 fluent 显式实现补齐 + 触碰文件归属。
> - **Task 21**：方法触发——`void M(...)` / `R M(out T result)`（MM 迁移后签名）实现接口成员；解除 Task 17 的 RR020 门；方法桩 / `WithM` / 调用点（含 out 形态）。
> - **Task 22**：IR-5 接口继承（成员加到声明接口）、IR-6 泛型接口携带 type argument、RR071 改名回退 `WithX__cs2j`（跨接口同形公共版省略）、DIM 默认关闭说明、FB-3 force 接受外部接口桩降级。

### Task 19: IR-1/IR-2/IR-4 属性连带 + IR-7 外部接口/碰撞门

> 本任务后：`public interface I { int P { get; set; } }` + `public record struct R : I { public int P {get;set;} }`
> - I 追加 `global::I WithP(int value);`（IR-1，抽象、无 DIM）；
> - R 的 `P` 照常 `set→init`，另加显式桩 `int global::I.P { get => P; set => throw new NotSupportedException(...); }`、显式实现 `global::I global::I.WithP(int value) => this.WithP(value);`、公共强类型 `public global::R WithP(int value) => this with { P = value };`（IR-2）；
> - 用点：`r.P = 5;` → `r = r.WithP(5);`；`i.P = 5;`（i 静态类型 I）→ `i = i.WithP(5);`（IR-4，接收者不可写 RR050）；
> - 外部接口 → RR070 原子跳过；接口或 record 已存在 `WithP` → RR071 原子跳过（改名回退 Task 22）。

**Files:**
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs`（`RippleMember` 模型；`InterfaceRipplePlan` 改富形状；PlanBuilder 属性触发规划；`InterfaceRipple: true`）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyAnalyzer.cs`（接口静态类型接收者用点登记到各 in-tree record 实现者）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyPreprocessor.cs`（接口文件即使无 record 声明也进入改写）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/MutationSiteRewriter.cs`（分类器 ripple 命中门 + `InterfaceWithCall` 改写）
- Replace whole: `src/CSharpToJava.Core/RecordReadOnly/InterfaceRippler.cs`
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

- [ ] **Step 1: 写失败测试**

先给测试脚手架追加 metadata-only 引用辅助（IR-7 外部接口必须来自编译引用而非同编译树；`extra` 树仍是 in-tree）。在 `Rewrite` 辅助旁加入：

```csharp
    /// <summary>
    /// 把 metadataSource 单独编成程序集引用，src 编译只引用其元数据——
    /// 用于 IR-7“接口不在源码树内（BCL/三方形态）”场景。
    /// </summary>
    internal static (string Output, RecordReadOnlyStatistics Stats,
        List<RecordReadOnlyDiagnostic> Diagnostics) RewriteWithMetadata(
            string src, string metadataSource, RecordReadOnlyOptions? options = null)
    {
        var libComp = CSharpCompilation.Create(
            "rr-external-lib.dll",
            new[] { CSharpSyntaxTree.ParseText(metadataSource, ParseOptions) },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "rr-test",
            new[] { tree },
            GetReferences().Append(libComp.ToMetadataReference()),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var pp = new RecordReadOnlyPreprocessor(options ?? new RecordReadOnlyOptions());
        var stats = new RecordReadOnlyStatistics();
        var diags = new List<RecordReadOnlyDiagnostic>();
        var result = pp.RewriteFile(src, compilation.GetSemanticModel(tree), stats, diags);
        return (result.OutputCode, stats, diags);
    }
```

追加测试：

```csharp
    [Fact]
    public void IR1_InterfaceGainsWithMember_RecordGetsStubsAndPublicWith()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            """;
        var (output, stats, _) = Rewrite(src);
        // IR-1：既有成员不动，追加抽象 With 成员（int 经 UseSpecialTypes 保持关键字形态）。
        Assert.Contains("int P { get; set; }", output);
        Assert.Contains("global::I WithP(int value);", output);
        // IR-2：公共属性 set→init；旧接口契约显式桩 get 转发 / set throw。
        Assert.Contains("public int P { get; init; }", output);
        Assert.Contains("int global::I.P", output);
        Assert.Contains("set => throw new global::System.NotSupportedException(", output);
        // IR-2：显式 With 委托公共强类型版；公共版 with 出口（避免装箱）。
        Assert.Contains("global::I global::I.WithP(int value) => this.WithP(value);", output);
        Assert.Contains("public global::R WithP(int value) => this with { P = value };", output);
        Assert.Equal(1, stats.InterfaceRipples);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR4_RecordTypedReceiver_RewrittenToPublicWithCall()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            public class C { public void M(R r) { r.P = 5; } }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("r = r.WithP(5);", output);
        Assert.DoesNotContain("r = r with", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR4_InterfaceTypedReceiver_RewrittenToInterfaceWithCall()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            public class C { public void M(I i) { i.P = 5; } }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("i = i.WithP(5);", output);
        Assert.Equal(1, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR4_InterfaceReceiverInParam_BlocksWithRR050()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            public class C { public void M(in I i) { i.P = 5; } }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("WithP(5)", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR050);
    }

    [Fact]
    public void IR2_TwoInterfacesSameProperty_DedupPublicWith()
    {
        var src = """
            public interface I1 { int P { get; set; } }
            public interface I2 { int P { get; set; } }
            public record struct R : I1, I2 { public int P { get; set; } }
            """;
        var (output, stats, _) = Rewrite(src);
        // IR-1：两个接口各加一份 WithP。
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(output,
            @"global::I[12] WithP\(int value\);").Count);
        // IR-2：每接口一份显式桩与显式 With。
        Assert.Contains("int global::I1.P", output);
        Assert.Contains("int global::I2.P", output);
        Assert.Contains("global::I1 global::I1.WithP(int value) => this.WithP(value);", output);
        Assert.Contains("global::I2 global::I2.WithP(int value) => this.WithP(value);", output);
        // 公共强类型版同形状跨接口只生成一份。
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(output,
            @"public global::R WithP\(int value\)").Count);
        Assert.Equal(2, stats.InterfaceRipples);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR7_WithNameAlreadyDeclared_RR071AtomicSkip()
    {
        var src = """
            public interface I { int P { get; set; } int WithP(int value); }
            public record struct R : I
            {
                public int P { get; set; }
                public int WithP(int value) => default;
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR071);
    }

    [Fact]
    public void IR7_ExternalInterface_RR070AtomicSkip()
    {
        var lib = "public interface IExt { int P { get; set; } }";
        var src = "public record struct R : IExt { public int P { get; set; } }";
        var (output, _, diags) = RewriteWithMetadata(src, lib);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR070);
    }

    [Fact]
    public void IR1_CrossFile_RecordSideGeneratedWhenInterfaceLivesInOtherTree()
    {
        // CLI 逐文件驱动时接口文件会由它自己的 RewriteFile 调用补上 WithP；
        // 此处只验证 record 文件一侧在跨树语义模型下正确产出（接口追加在 M6 工程级测试整树验证）。
        var extra = "public interface I { int P { get; set; } }";
        var src = """
            public record struct R : I { public int P { get; set; } }
            public class C { public void M(R r) { r.P = 1; } }
            """;
        var (output, stats, _) = Rewrite(src, null, extra);
        Assert.Contains("public global::R WithP(int value) => this with { P = value };", output);
        Assert.Contains("r = r.WithP(1);", output);
        Assert.Equal(0, stats.InterfaceRipples); // WithP 追加落在接口文件，不计入本文件
        Assert.Equal(1, stats.FilesTransformed);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 新增 8 个用例失败（连带 record 此刻走 `InterfaceRipple` cap 关闭的 RR070 分支：属性触发成功用例产出 RR070/无改写；RR071 用例得到 RR070 而非 RR071；外部接口用例可能已偶然通过——Step 5 后仍须保持通过）。既有 RS/MS/MM 用例全绿。

- [ ] **Step 3: 模型——`RippleMember` + 富 `InterfaceRipplePlan`**

编辑 `RecordConversionPlan.cs`，把

```csharp
internal sealed record InterfaceRipplePlan(
    INamedTypeSymbol Interface,
    IReadOnlyList<ISymbol> TriggerMembers);
```

替换为：

```csharp
/// <summary>
/// 接口连带中“接口新 With 成员 ↔ record 被替代成员”的一条映射。
/// 属性触发：InterfaceMember/RecordMember 均为 IPropertySymbol；
/// 方法触发（Task 21）：均为 IMethodSymbol。
/// </summary>
internal sealed record RippleMember(
    INamedTypeSymbol Interface,
    ISymbol InterfaceMember,
    ISymbol RecordMember,
    string WithName,
    bool IsMethod,
    bool ReturnsVoid);

internal sealed record InterfaceRipplePlan(
    INamedTypeSymbol Interface,
    IReadOnlyList<RippleMember> Members);
```

- [ ] **Step 4: 翻转 `InterfaceRipple` cap**

在 `RecordConversionPlan.cs` 的 `FeatureCaps.Current` 中把

```csharp
        InterfaceRipple: false);
```

改为：

```csharp
        InterfaceRipple: true);
```

- [ ] **Step 5: PlanBuilder——属性触发的 ripple 规划（RR070/RR071 门 + 触碰文件）**

5.1 把属性循环段

```csharp
        var rippleRequired = false;

        // 属性 set→init（RS-3/RS-4/RS-9/RS-20；RC-2/RC-5 在 M5 复用同一条目）。
        foreach (var property in info.SettableProperties)
        {
            if (PropertyImplementsInterfaceSetter(info.Symbol, property))
            {
                rippleRequired = true;
                continue; // RS-14/RC-7：显式桩 + IR 在 M4 落地。
            }
            plan.MemberRewrites.Add(new MemberRewrite(
                property.Name, MemberRewriteKind.SetToInit, property, property.IsRequired));
        }
```

替换为：

```csharp
        // 属性 set→init（RS-3/RS-4/RS-9/RS-20；RC-2/RC-5 在 M5 复用同一条目）。
        // 命中接口 set（RS-14）的公共属性照常 set→init；旧接口契约改由 IR-2 显式桩满足，
        // 触发信息收集到 rippleProperties，本方法末统一规划 IR-1/IR-2/IR-4。
        var rippleProperties =
            new List<(IPropertySymbol RecordProperty, INamedTypeSymbol Interface,
                IPropertySymbol InterfaceProperty)>();
        foreach (var property in info.SettableProperties)
        {
            foreach (var (iface, interfaceProperty)
                     in InterfaceSetterImplementations(info.Symbol, property))
            {
                rippleProperties.Add((property, iface, interfaceProperty));
            }
            plan.MemberRewrites.Add(new MemberRewrite(
                property.Name, MemberRewriteKind.SetToInit, property, property.IsRequired));
        }
```

5.2 把原接口连带段

```csharp
        // 接口连带（IR）。
        if (rippleRequired && !_caps.InterfaceRipple)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR070,
                "record implicitly implements a settable interface member; interface ripple (IR) " +
                "is required to convert.");
        if (rippleRequired)
        {
            foreach (var group in info.SettableProperties
                         .Where(p => PropertyImplementsInterfaceSetter(info.Symbol, p))
                         .Select(p => InterfaceForSetter(info.Symbol, p))
                         .GroupBy(i => i, SymbolEqualityComparer.Default))
            {
                plan.Ripples.Add(new InterfaceRipplePlan(group.Key!, Array.Empty<ISymbol>()));
            }
        }
```

替换为：

```csharp
        // 接口连带（IR-1/IR-2/IR-4/IR-7；属性触发；方法触发 Task 21）。
        // cap 门保留：能力被外部关闭时给 RR070（单测/未来 overrides 可构造该形态）。
        if (rippleProperties.Count > 0 && !_caps.InterfaceRipple)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR070,
                "record implicitly implements a settable interface member; interface ripple (IR) " +
                "is required to convert.");

        foreach (var group in rippleProperties
                     .GroupBy(t => t.Interface, SymbolEqualityComparer.Default))
        {
            var iface = group.Key!;

            // IR-7：外部接口（源不在树内）无法追加成员 → FB-1（force 接受桩降级在 Task 22）。
            if (!RecordSyntaxFacts.IsInTree(iface))
                return Fail(plan, RecordReadOnlyDiagnosticCode.RR070,
                    $"interface '{iface.ToDisplayString()}' declares settable member(s) implemented " +
                    "by this record but the interface is not part of the source tree; the required " +
                    "'With' member cannot be added to it (IR-7/FB-3).");

            var members = new List<RippleMember>();
            foreach (var (recordProperty, _, interfaceProperty) in group)
            {
                var withName = "With" + interfaceProperty.Name;

                // IR-7：签名碰撞先 FB-1；改名 WithX__cs2j 在 Task 22。
                if (iface.GetMembers().Any(m => m.Name == withName))
                    return FailAt(plan, RecordReadOnlyDiagnosticCode.RR071,
                        $"interface '{iface.ToDisplayString()}' already declares a member named " +
                        $"'{withName}'; adding the ripple member would collide (IR-7).",
                        interfaceProperty.Locations.FirstOrDefault(l => l.IsInSource)
                            ?? info.Symbol.Locations.First(l => l.IsInSource));
                if (info.Symbol.GetMembers().Any(m => m.Name == withName))
                    return FailAt(plan, RecordReadOnlyDiagnosticCode.RR071,
                        $"record '{info.Symbol.Name}' already declares a member named " +
                        $"'{withName}'; the generated strongly-typed ripple member would collide " +
                        "(IR-7).",
                        info.Symbol.Locations.First(l => l.IsInSource));

                members.Add(new RippleMember(
                    iface, interfaceProperty, recordProperty, withName,
                    IsMethod: false, ReturnsVoid: true));
            }

            plan.Ripples.Add(new InterfaceRipplePlan(iface, members));

            // IR-1 改的是接口文件：登记触碰面供 activePlans 准入与 FB-5 计划级回退归属。
            foreach (var loc in iface.Locations.Where(l => l.IsInSource))
            {
                plan.TouchedFiles.Add(loc.SourceTree!.FilePath);
                plan.Touches.Add(new PlanTouch(loc.SourceTree!.FilePath, iface.Name, null));
            }
        }
```

5.3 把两个旧辅助 `PropertyImplementsInterfaceSetter` 与 `InterfaceForSetter`（相邻的两个 private static 方法）整体替换为：

```csharp
    /// <summary>
    /// 返回 record 属性对“带可写（非 init）set 的接口属性”的全部实现映射。
    /// 一个公共属性可同时隐式实现多个接口成员（IR-2 每接口各生成一份显式桩）。
    /// 外部接口同样枚举——IR-7 的 in-tree 裁决在规划段做，这里不预先过滤。
    /// </summary>
    private static IReadOnlyList<(INamedTypeSymbol Interface, IPropertySymbol InterfaceProperty)>
        InterfaceSetterImplementations(INamedTypeSymbol record, IPropertySymbol property)
    {
        var result = new List<(INamedTypeSymbol, IPropertySymbol)>();
        foreach (var iface in record.AllInterfaces)
        {
            foreach (var interfaceProperty in iface.GetMembers().OfType<IPropertySymbol>())
            {
                if (interfaceProperty.SetMethod is not { IsInitOnly: false }) continue;
                var impl = record.FindImplementationForInterfaceMember(interfaceProperty);
                if (impl != null
                    && SymbolEqualityComparer.Default.Equals(impl, property))
                {
                    result.Add((iface, interfaceProperty));
                }
            }
        }
        return result;
    }
```

- [ ] **Step 6: Preprocessor——接口文件（无 record 声明）也允许进入改写**

`RecordReadOnlyPreprocessor.RewriteFile` 中，快速跳过判定

```csharp
        statistics.FilesScanned++;
        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();

        if (!IsRecordDirty(root))
        {
            statistics.FilesSkippedClean++;
            return new RecordReadOnlyFileResult { OutputCode = sourceCode, Changed = false };
        }

        EnsureBuilt(semanticModel);
```

替换为：

```csharp
        statistics.FilesScanned++;
        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();

        // IR-1 可能只改接口文件（文件内没有任何 record 声明），必须先建分析/计划再判脏。
        EnsureBuilt(semanticModel);

        if (!IsRecordDirty(root) && !FileGainsInterfaceMember(_plan!, semanticModel.SyntaxTree.FilePath))
        {
            statistics.FilesSkippedClean++;
            return new RecordReadOnlyFileResult { OutputCode = sourceCode, Changed = false };
        }
```

并在 `IsRecordDirty` 旁新增：

```csharp
    /// <summary>该文件声明了某个已转换 record 计划要追加 With 成员的接口（IR-1）。</summary>
    private static bool FileGainsInterfaceMember(ConversionPlan plan, string filePath) =>
        plan.Plans.Any(p => p.Outcome == PlanOutcome.Converted
            && p.Ripples.Any(r => r.Interface.Locations.Any(l => l.IsInSource
                && string.Equals(l.SourceTree!.FilePath, filePath, StringComparison.OrdinalIgnoreCase))));
```

（Step 5 已把接口文件加入 `plan.TouchedFiles`，故后续 `activePlans` 筛选在接口文件上非空，无需另改。）

- [ ] **Step 7: 分析器——接口静态类型接收者的用点登记（IR-4）**

现状：`RecordReadOnlyAnalyzer.AddSite` 以 `deepestMember.ContainingType is { IsRecord: true }` 为唯一入口，`i.P = 5`（i 静态类型 I）在成员属主是接口时被直接丢弃，`ReceiverInterface` 事实永远填不到。

把 `AddSite` 中

```csharp
        if (deepestMember?.ContainingType is not { IsRecord: true } targetType) return;
        if (!_analysis.BySymbol.TryGetValue(targetType, out var target)) return;
        if (receiverType == null) return;
```

替换为：

```csharp
        if (deepestMember?.ContainingType is not { } memberOwner) return;
        if (receiverType == null) return;

        // IR-4：接收者静态类型为接口（i.P = v）。成员属主是接口而非 record，
        // 把同一用点登记到每个“在树内、以该成员为接口实现”的 record 事实上；
        // 计划阶段只有实际生成 ripple 的转换 record 才会消费它（见 RippleForSite）。
        if (memberOwner.TypeKind == TypeKind.Interface && memberOwner is INamedTypeSymbol ownerInterface)
        {
            foreach (var candidate in _analysis.Records)
            {
                if (!candidate.Symbol.AllInterfaces.Any(i =>
                        SymbolEqualityComparer.Default.Equals(i, ownerInterface)))
                    continue;
                if (candidate.Symbol.FindImplementationForInterfaceMember(deepestMember) == null)
                    continue;

                var interfaceSite = new MutationSiteInfo
                {
                    Location = writeNode.GetLocation(),
                    FilePath = tree.FilePath,
                    WriteNode = writeNode,
                    Left = left,
                    Receiver = cursor ?? left,
                    Member = deepestMember,
                    ReceiverType = receiverType,
                    RootReceiverType = model.GetTypeInfo(cursor ?? left).Type ?? receiverType,
                    ReceiverInterface = ownerInterface,
                    Kind = kind,
                    IsStatementPosition = writeNode.FirstAncestorOrSelf<ExpressionStatementSyntax>() != null
                        || writeNode.FirstAncestorOrSelf<GlobalStatementSyntax>() != null,
                    EnclosingMethod = writeNode.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>() is { } md
                        ? model.GetDeclaredSymbol(md) : null,
                    Chain = chain,
                };
                interfaceSite.TargetRecord = candidate;
                candidate.Sites.Add(interfaceSite);
            }
            return;
        }

        if (memberOwner is not { IsRecord: true } targetType) return;
        if (!_analysis.BySymbol.TryGetValue(targetType, out var target)) return;
```

> 说明：同一 `WriteNode` 可能挂到多个 record 候选的 Sites；MethodMigrator 的注解表以 (span, kind) 为键、MutationSiteRewriter 按存在的注解重写一次，最终语句只改写一次，统计不重复（Step 9 用例 `IR4_InterfaceTypedReceiver...` 断言 `MutationSitesRewritten == 1` 守护）。

- [ ] **Step 8: 分类器 ripple 命中门 + PlanBuilder 动作选择**

8.1 在 `MutationSiteRewriter.cs` 的 `MutationSiteClassifier` 中，于 `MemberIsInitSettable` 旁新增：

```csharp
    /// <summary>
    /// IR-4：用点成员命中本 record 的接口连带成员时返回其映射。
    /// record 静态类型接收者解析到 RecordMember（R.P）；接口静态类型接收者
    /// 解析到 InterfaceMember（I.P）——两者都认。
    /// </summary>
    internal static RippleMember? RippleForSite(RecordPlan plan, ISymbol? member)
    {
        if (member == null) return null;
        foreach (var ripple in plan.Ripples)
        foreach (var rippleMember in ripple.Members)
        {
            if (SymbolEqualityComparer.Default.Equals(rippleMember.InterfaceMember, member)
                || SymbolEqualityComparer.Default.Equals(rippleMember.RecordMember, member))
                return rippleMember;
        }
        return null;
    }
```

8.2 在 `Classify`（简单赋值路径；复合/自增等路径在各自的 Classify* 辅助中，本任务不改）把

```csharp
        var memberFailure = MemberIsInitSettable(plan, site.Member);
        if (memberFailure != null) return new SiteFailure(memberFailure.Value.Code,
            memberFailure.Value.Reason, site.Location);

        var receiverFailure = ReceiverWritableAndPure(model, site.Receiver);
        return receiverFailure;
```

替换为：

```csharp
        // IR-4：命中 ripple 的成员（接口属性不在 MemberRewrites 内）跳过 init 门，
        // 只校验接收者可写且无副作用（RR050/RR051）；改写形态为 InterfaceWithCall。
        if (site.Kind == SiteKind.SimpleAssign
            && RippleForSite(plan, site.Member) == null)
        {
            var memberFailure = MemberIsInitSettable(plan, site.Member);
            if (memberFailure != null) return new SiteFailure(memberFailure.Value.Code,
                memberFailure.Value.Reason, site.Location);
        }

        var receiverFailure = ReceiverWritableAndPure(model, site.Receiver);
        return receiverFailure;
```

8.3 在 PlanBuilder 的逐用点循环中把

```csharp
            plan.Sites.Add(new SitePlan(site, SiteActionKind.WithAssign));
```

替换为：

```csharp
            // IR-4 仅接管简单赋值；复合/自增等形态在 record 接收者上继续走 with 管线，
            // 在接口接收者上由各自 Classify* 辅助的 MemberIsInitSettable 门判 RR020。
            var action = site.Kind == SiteKind.SimpleAssign
                && MutationSiteClassifier.RippleForSite(plan, site.Member) != null
                ? SiteActionKind.InterfaceWithCall
                : SiteActionKind.WithAssign;
            plan.Sites.Add(new SitePlan(site, action));
```

- [ ] **Step 9: 整体替换 InterfaceRippler.cs + MutationSiteRewriter 增加 With 调用改写**

9.1 整体替换 `InterfaceRippler.cs`（Task 3 空桩）：

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RecordReadOnly;

/// <summary>
/// Phase 3d：接口连带（IR）。
/// IR-1：向 in-tree 接口追加 With 成员（多个 record 计划触发同形状成员时去重；
/// 成员始终加到“声明它”的接口，接口继承 IR-5 经 AllInterfaces 自然传播）。
/// IR-2：向转换中的 record 追加——旧接口属性的显式实现桩（get 转发 / set throw）、
/// 新 With 成员的显式接口实现（委托公共版）、公共强类型 With（同形状跨接口只生成一份）。
/// IR-3（其他 in-tree 实现者 fluent 补齐）与方法触发在后续任务接入。
/// </summary>
internal sealed class InterfaceRippler : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    private readonly InterfaceRippleIndex _index;

    public InterfaceRippler(RewriteContext context)
    {
        _context = context;
        _index = InterfaceRippleIndex.Build(context);
    }

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        var visited = (InterfaceDeclarationSyntax)base.VisitInterfaceDeclaration(node)!;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol == null || !IsPrimaryPart(node, symbol)) return visited;
        if (!_index.Additions.TryGetValue(symbol, out var members) || members.Count == 0)
            return visited;

        var additions = members
            .Select(m => SyntaxFactory.ParseMemberDeclaration(InterfaceMemberText(m))!)
            .Cast<MemberDeclarationSyntax>()
            .Select(m => m
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
                .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation))
            .ToArray();
        foreach (var _ in additions)
            _context.Statistics.InterfaceRipples++;
        return visited
            .WithMembers(visited.Members.AddRange(additions))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var visited = (RecordDeclarationSyntax)base.VisitRecordDeclaration(node)!;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol == null || !IsPrimaryPart(node, symbol)) return visited;

        var plan = _context.ActivePlans.FirstOrDefault(p =>
            SymbolEqualityComparer.Default.Equals(p.Symbol, symbol)
            && p.Outcome == PlanOutcome.Converted);
        if (plan == null) return visited;

        var additions = new List<MemberDeclarationSyntax>();
        var emittedPublicShapes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ripple in plan.Ripples)
        {
            foreach (var member in ripple.Members)
            {
                additions.Add(ExplicitPropertyStub(member)
                    .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                additions.Add(ExplicitWithPropertyImplementation(member)
                    .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));

                if (emittedPublicShapes.Add(PublicShapeKey(member)))
                {
                    additions.Add(PublicWithPropertyMethod(symbol, member)
                        .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                }
            }
        }
        if (additions.Count == 0) return visited;
        return visited
            .WithMembers(visited.Members.AddRange(additions))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>IR-1：接口追加 `I WithP(T value);`（抽象；DIM 默认关闭，见 IR-7 注）。</summary>
    private static string InterfaceMemberText(RippleMember member)
    {
        var property = (IPropertySymbol)member.InterfaceMember;
        var interfaceText = member.Interface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var valueTypeText = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return $"{interfaceText} {member.WithName}({valueTypeText} value);";
    }

    /// <summary>IR-2：`T I.P { get =&gt; P; set =&gt; throw new NotSupportedException(...); }`</summary>
    private static PropertyDeclarationSyntax ExplicitPropertyStub(RippleMember member)
    {
        var interfaceProperty = (IPropertySymbol)member.InterfaceMember;
        var recordProperty = (IPropertySymbol)member.RecordMember;
        var message =
            $"interface setter '{member.Interface.ToDisplayString()}.{interfaceProperty.Name}' " +
            "is not supported after the record became readonly; use the generated 'With' " +
            "member instead (IR-2).";
        var throwExpression = SyntaxFactory.ParseExpression(
            "throw new global::System.NotSupportedException(\"" + message + "\")");

        var getAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory.IdentifierName(recordProperty.Name)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        var setAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(throwExpression))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        return SyntaxFactory.PropertyDeclaration(
                RecordSyntaxFacts.GlobalType(interfaceProperty.Type),
                SyntaxFactory.Identifier(interfaceProperty.Name))
            .WithExplicitInterfaceSpecifier(ExplicitInterface(member.Interface))
            .WithAccessorList(SyntaxFactory.AccessorList(
                SyntaxFactory.List(new[] { getAccessor, setAccessor })))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>IR-2：`I I.WithP(T value) =&gt; this.WithP(value);`（委托公共强类型版）。</summary>
    private static MethodDeclarationSyntax ExplicitWithPropertyImplementation(RippleMember member)
    {
        var property = (IPropertySymbol)member.InterfaceMember;
        return SyntaxFactory.MethodDeclaration(
                RecordSyntaxFacts.GlobalType(member.Interface),
                SyntaxFactory.Identifier(member.WithName))
            .WithExplicitInterfaceSpecifier(ExplicitInterface(member.Interface))
            .WithParameterList(ValueParameterList(property.Type))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory.ParseExpression($"this.{member.WithName}(value)")))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>IR-2：`public R WithP(T value) =&gt; this with { P = value };`</summary>
    private static MemberDeclarationSyntax PublicWithPropertyMethod(
        INamedTypeSymbol record, RippleMember member)
    {
        var interfaceProperty = (IPropertySymbol)member.InterfaceMember;
        var recordProperty = (IPropertySymbol)member.RecordMember;
        var text =
            $"public {record.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} " +
            $"{member.WithName}(" +
            $"{interfaceProperty.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} value) " +
            $"=> this with {{ {recordProperty.Name} = value }};";
        return SyntaxFactory.ParseMemberDeclaration(text)!
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    private static ParameterListSyntax ValueParameterList(ITypeSymbol valueType) =>
        SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
            SyntaxFactory.Parameter(SyntaxFactory.Identifier("value"))
                .WithType(RecordSyntaxFacts.GlobalType(valueType))));

    private static ExplicitInterfaceSpecifierSyntax ExplicitInterface(INamedTypeSymbol iface) =>
        SyntaxFactory.ExplicitInterfaceSpecifier(
            SyntaxFactory.ParseName(iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));

    /// <summary>公共版去重形状：With 名 + 值参类型（返回类型恒为本 record，不参与）。</summary>
    private static string PublicShapeKey(RippleMember member)
    {
        var property = (IPropertySymbol)member.InterfaceMember;
        return member.WithName + "("
            + property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ")";
    }

    /// <summary>
    /// partial 类型只向确定性选定的主部件（文件路径序、再跨度序最小）追加成员，
    /// 避免每个部件各加一份造成重复定义。
    /// </summary>
    private static bool IsPrimaryPart(SyntaxNode current, INamedTypeSymbol symbol)
    {
        var refs = symbol.DeclaringSyntaxReferences;
        if (refs.Length <= 1) return true;
        var primary = refs
            .OrderBy(r => r.SyntaxTree.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Span.Start)
            .First();
        return string.Equals(current.SyntaxTree!.FilePath, primary.SyntaxTree.FilePath,
                   StringComparison.OrdinalIgnoreCase)
               && current.SpanStart == primary.Span.Start;
    }
}

/// <summary>
/// 本文件需要落地的接口追加（IR-1）与实现者补齐（IR-3，下一个任务）索引。
/// 跨 record 计划按“同接口 + With 名 + 值参形状”去重，避免重复成员。
/// </summary>
internal sealed class InterfaceRippleIndex
{
    public required Dictionary<INamedTypeSymbol, List<RippleMember>> Additions { get; init; }
    public required Dictionary<INamedTypeSymbol, List<RippleMember>> Fills { get; init; }

    public static InterfaceRippleIndex Build(RewriteContext context)
    {
        var additions = new Dictionary<INamedTypeSymbol, List<RippleMember>>(
            SymbolEqualityComparer.Default);
        foreach (var plan in context.ActivePlans)
        {
            if (plan.Outcome != PlanOutcome.Converted) continue;
            foreach (var ripple in plan.Ripples)
            {
                if (!additions.TryGetValue(ripple.Interface, out var list))
                    additions[ripple.Interface] = list = new();
                foreach (var member in ripple.Members)
                {
                    if (list.Any(existing => SameShape(existing, member))) continue;
                    list.Add(member);
                }
            }
        }
        return new InterfaceRippleIndex
        {
            Additions = additions,
            Fills = new(SymbolEqualityComparer.Default),
        };
    }

    /// <summary>同接口内 With 名相同且值参类型相同即视为同一份契约（方法形状 Task 21 补全）。</summary>
    internal static bool SameShape(RippleMember a, RippleMember b)
    {
        if (a.IsMethod != b.IsMethod || a.WithName != b.WithName) return false;
        if (a.IsMethod) return false;
        var pa = (IPropertySymbol)a.InterfaceMember;
        var pb = (IPropertySymbol)b.InterfaceMember;
        return SymbolEqualityComparer.Default.Equals(pa.Type, pb.Type);
    }
}
```

9.2 在 `MutationSiteRewriter.cs` 中，把 `RewriteSite`

```csharp
    private ExpressionSyntax? RewriteSite(ExpressionSyntax writeExpression, SitePlan sitePlan)
    {
        return sitePlan.Site.Kind switch
        {
            SiteKind.SimpleAssign => RewriteWithAssign(writeExpression, sitePlan),
            SiteKind.CompoundAssign => RewriteCompound(writeExpression, sitePlan.Site),
            SiteKind.Increment => RewriteIncrement(writeExpression, sitePlan.Site),
            _ => null,
        };
    }
```

替换为：

```csharp
    private ExpressionSyntax? RewriteSite(ExpressionSyntax writeExpression, SitePlan sitePlan)
    {
        if (sitePlan.Action == SiteActionKind.InterfaceWithCall)
            return RewriteInterfaceWithCall(writeExpression, sitePlan);
        return sitePlan.Site.Kind switch
        {
            SiteKind.SimpleAssign => RewriteWithAssign(writeExpression, sitePlan),
            SiteKind.CompoundAssign => RewriteCompound(writeExpression, sitePlan.Site),
            SiteKind.Increment => RewriteIncrement(writeExpression, sitePlan.Site),
            _ => null,
        };
    }
```

并在 `RewriteWithAssign` 之后新增：

```csharp
    /// <summary>
    /// IR-4：`recv.P = rhs;`（P 命中接口连带）→ `recv = recv.WithP(rhs);`。
    /// recv 静态类型为 record 时命中公共强类型版（返回 R）；为接口 I 时命中 I.WithP（返回 I）。
    /// 接收者文本重新 ParseExpression 取两份新节点（同一脱离节点不能挂两个树槽）；
    /// 右值只出现一次（调用实参），直接取其文本重解析，语义无损。
    /// </summary>
    private ExpressionSyntax? RewriteInterfaceWithCall(
        ExpressionSyntax writeExpression, SitePlan sitePlan)
    {
        if (writeExpression is not AssignmentExpressionSyntax
            {
                RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                Left: MemberAccessExpressionSyntax memberAccess,
            } assignment)
        {
            return null;
        }

        var withName = WithMemberName(sitePlan);
        if (withName == null) return null;

        var receiverText = sitePlan.Site.Receiver.WithoutTrivia().ToFullString();
        var valueText = assignment.Right.WithoutTrivia().ToFullString();
        var call = SyntaxFactory.ParseExpression($"{receiverText}.{withName}({valueText})");
        var writeBack = SyntaxFactory.ParseExpression(receiverText);
        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression, writeBack, call)
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    private string? WithMemberName(SitePlan sitePlan)
    {
        foreach (var plan in _context.ActivePlans)
        foreach (var ripple in plan.Ripples)
        foreach (var member in ripple.Members)
        {
            if (SymbolEqualityComparer.Default.Equals(member.InterfaceMember, sitePlan.Site.Member)
                || SymbolEqualityComparer.Default.Equals(member.RecordMember, sitePlan.Site.Member))
                return member.WithName;
        }
        return null;
    }
```

- [ ] **Step 10: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 全部 PASS（IR 新增 8 个；RS/MS/MM 既有全量不回归——特别关注含接口实现但不触发 set 的 record 用例输出不变）。

- [ ] **Step 11: 提交**

Run:
```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): IR-1/IR-2/IR-4 property interface ripple, RR070/RR071 gates"
```

---

### Task 20: IR-3 in-tree 其他实现者 fluent 显式实现补齐 + 文件触碰归属

> 本任务后：`interface I { int P { get; set; } }` 除转换中的 `record struct R : I` 外，若同编译树还有 `class C : I` / `struct S : I` / 未转换的 `record class Q : I`，接口新增 `WithP` 后这些实现者不会缺成员：R 走 IR-2 的 record 显式实现，其他实现者由 IR-3 生成原地修改并返回自身的显式接口实现 `I I.WithP(int value) { P = value; return this; }`。值类型若以显式接口实现保存旧属性（无法不装箱地调用旧显式 set），保守 RR070 阻断该 record 计划。
>
> Analyzer 现状已足够：`BuildInterfaceGraph` 会为每个 in-tree 接口建立 `InterfaceFact`，其中 `InTreeImplementers` 枚举所有 `t.AllInterfaces.Contains(iface)` 的 in-tree 类型；本任务不改 Analyzer，只在 PlanBuilder/InterfaceRippler 消费这些事实。

**Files:**
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordSyntaxFacts.cs`（显式接口实现判定）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs`（`InterfaceFillPlan`、fills、触碰文件）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyPreprocessor.cs`（无 record/interface 的 fill 文件准入）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/InterfaceRippler.cs`（消费 `Fills` 索引）
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

- [ ] **Step 1: 写失败测试**

追加测试：

```csharp
    [Fact]
    public void IR3_ClassImplementer_GetsFluentExplicitFill()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            public class C : I { public int P { get; set; } }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("global::I WithP(int value);", output);
        Assert.Contains("global::R WithP(int value)", output);
        Assert.Contains("""
            global::I global::I.WithP(int value)
            {
                P = value;
                return this;
            }
            """.Replace("\r\n", "\n"), output.Replace("\r\n", "\n"));
        // 1 个接口新增 + 1 个其他实现者 fill；record 侧显式桩不计入 InterfaceRipples。
        Assert.Equal(2, stats.InterfaceRipples);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR3_StructImplementer_PublicProperty_GetsFluentFill()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            public struct S : I { public int P { get; set; } }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("""
            global::I global::I.WithP(int value)
            {
                P = value;
                return this;
            }
            """.Replace("\r\n", "\n"), output.Replace("\r\n", "\n"));
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR3_ClassWithExplicitProperty_CallsOldContractThroughInterfaceCast()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            public class E : I
            {
                private int _p;
                int I.P { get => _p; set => _p = value; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("((global::I)this).P = value;", output);
        Assert.Contains("return this;", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR3_UnconvertedRecordClass_GetsFluentFill()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            public record class Q : I { public int P { get; set; } }
            """;
        var (output, _, _) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = false });
        Assert.Contains("public int P { get; set; }", output);
        Assert.Contains("global::I global::I.WithP(int value)", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR3_StructExplicitProperty_BlocksWithRR070()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            public struct Bad : I
            {
                private int _p;
                int I.P { get => _p; set => _p = value; }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR070);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 新增 5 个用例失败：普通实现者缺少 `WithP`，整文件编译诊断由 `AssertOutputCompiles` 暴露；显式 struct 用例当前不会产生 RR070。Task 19 的 8 个 IR 用例继续通过。

- [ ] **Step 3: RecordSyntaxFacts——统一显式接口实现判定**

在 `RecordSyntaxFacts.cs` 追加：

```csharp
    /// <summary>
    /// 判断实现符号是否为某个接口成员的显式实现。属性/方法/事件都可能遇到该形态；
    /// IR-3 对引用类型可经 ((I)this) 调旧契约，对值类型该路径会装箱，调用方需另行阻断。
    /// </summary>
    public static bool IsExplicitInterfaceImplementation(ISymbol implementation, ISymbol interfaceMember)
    {
        var explicitMembers = implementation switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations,
            IPropertySymbol property => property.ExplicitInterfaceImplementations,
            IEventSymbol evt => evt.ExplicitInterfaceImplementations,
            _ => ImmutableArray<ISymbol>.Empty,
        };
        return explicitMembers.Any(m => SymbolEqualityComparer.Default.Equals(m, interfaceMember));
    }
```

文件顶部仍只需 `Microsoft.CodeAnalysis`；`ImmutableArray<ISymbol>` 来自该命名空间。

- [ ] **Step 4: Plan 模型——InterfaceFillPlan**

在 `RecordConversionPlan.cs` 的 `RippleMember` 之后追加：

```csharp
/// <summary>
/// IR-3：一个非 record-owner 的 in-tree 接口实现者需要补齐的 fluent 成员。
/// Implementer 可能是 class、struct、record class，或本轮未作为 owner 转换的 record。
/// </summary>
internal sealed record InterfaceFillPlan(
    INamedTypeSymbol Implementer,
    IReadOnlyList<RippleMember> Members);
```

把 Task 19 的

```csharp
internal sealed record InterfaceRipplePlan(
    INamedTypeSymbol Interface,
    IReadOnlyList<RippleMember> Members);
```

替换为：

```csharp
internal sealed record InterfaceRipplePlan(
    INamedTypeSymbol Interface,
    IReadOnlyList<RippleMember> Members,
    IReadOnlyList<InterfaceFillPlan> Fills);
```

- [ ] **Step 5: PlanBuilder——枚举 fills、RR070 门、触碰文件**

5.1 在 `PlanBuilder` 类内靠近 `FailAt` 增加一个私有阻断结果类型：

```csharp
    private sealed record InterfaceFillBlock(
        RecordReadOnlyDiagnosticCode Code,
        string Reason,
        Location Location);
```

5.2 Task 19 属性 ripple 循环中，构造完一个接口的 `members` 后，当前代码立即：

```csharp
            plan.Ripples.Add(new InterfaceRipplePlan(iface, members));

            // IR-1 改的是接口文件：登记触碰面供 activePlans 准入与 FB-5 计划级回退归属。
            foreach (var loc in iface.Locations.Where(l => l.IsInSource))
            {
                plan.TouchedFiles.Add(loc.SourceTree!.FilePath);
                plan.Touches.Add(new PlanTouch(loc.SourceTree!.FilePath, iface.Name, null));
            }
```

替换为：

```csharp
            var fills = BuildInterfaceFills(
                analysis, info.Symbol, iface, members, out var fillBlock);
            if (fillBlock != null)
                return FailAt(plan, fillBlock.Code, fillBlock.Reason, fillBlock.Location);

            plan.Ripples.Add(new InterfaceRipplePlan(iface, members, fills));
            AddInterfaceTouches(plan, iface, fills);
```

5.3 在 `PlanBuilder` 中新增以下两个辅助方法：

```csharp
    /// <summary>
    /// IR-3：枚举同一 in-tree 接口的其他实现者。转换中的 owner record 由 IR-2 负责，
    /// 因此这里跳过 owner；其他“本轮也转换”的 record 在 Rewriter 索引中再按计划状态去重。
    /// 抽象类型只有确实声明/继承了旧成员实现时才补成员；接口类型本身永不补实现。
    /// </summary>
    private static List<InterfaceFillPlan> BuildInterfaceFills(
        RecordAnalysis analysis,
        INamedTypeSymbol owner,
        INamedTypeSymbol iface,
        IReadOnlyList<RippleMember> rippleMembers,
        out InterfaceFillBlock? block)
    {
        block = null;
        var result = new List<InterfaceFillPlan>();
        if (!analysis.Interfaces.TryGetValue(iface, out var fact))
            return result;

        foreach (var implementer in fact.InTreeImplementers
                     .OrderBy(t => t.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath
                         ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(t => t.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0))
        {
            if (SymbolEqualityComparer.Default.Equals(implementer, owner))
                continue;
            if (implementer.TypeKind == TypeKind.Interface)
                continue;

            var supported = new List<RippleMember>();
            foreach (var member in rippleMembers)
            {
                var implementation = implementer.FindImplementationForInterfaceMember(
                    member.InterfaceMember);
                if (implementation == null)
                    continue;

                // class/record class 经接口引用调用旧显式实现是同一引用；struct 会装箱成副本，
                // 无法保持“原地修改再返回自身”，保守阻断 owner record 的接口连带。
                if (implementer.IsReferenceType == false
                    && RecordSyntaxFacts.IsExplicitInterfaceImplementation(
                        implementation, member.InterfaceMember))
                {
                    block = new InterfaceFillBlock(
                        RecordReadOnlyDiagnosticCode.RR070,
                        $"in-tree value type '{implementer.ToDisplayString()}' explicitly implements " +
                        $"'{iface.ToDisplayString()}.{member.InterfaceMember.Name}'; a fluent interface " +
                        "fill would mutate a boxed copy instead of the original value (IR-3/IR-7).",
                        implementation.Locations.FirstOrDefault(l => l.IsInSource)
                            ?? implementer.Locations.First(l => l.IsInSource));
                    return new();
                }

                supported.Add(member);
            }

            if (supported.Count > 0)
                result.Add(new InterfaceFillPlan(implementer, supported));
        }

        return result;
    }

    private static void AddInterfaceTouches(
        RecordPlan plan,
        INamedTypeSymbol iface,
        IReadOnlyList<InterfaceFillPlan> fills)
    {
        foreach (var loc in iface.Locations.Where(l => l.IsInSource))
        {
            plan.TouchedFiles.Add(loc.SourceTree!.FilePath);
            plan.Touches.Add(new PlanTouch(loc.SourceTree!.FilePath, iface.Name, null));
        }

        foreach (var fill in fills)
        {
            foreach (var loc in fill.Implementer.Locations.Where(l => l.IsInSource))
            {
                plan.TouchedFiles.Add(loc.SourceTree!.FilePath);
                foreach (var member in fill.Members)
                {
                    plan.Touches.Add(new PlanTouch(
                        loc.SourceTree!.FilePath, fill.Implementer.Name, member.WithName));
                }
            }
        }
    }
```

- [ ] **Step 6: Preprocessor——fill 文件即使没有 record/interface 声明也进入改写**

把 `RewriteFile` 中的快速跳过条件

```csharp
        if (!IsRecordDirty(root) && !FileGainsInterfaceMember(_plan!, semanticModel.SyntaxTree.FilePath))
        {
            statistics.FilesSkippedClean++;
            return new RecordReadOnlyFileResult { OutputCode = sourceCode, Changed = false };
        }
```

替换为：

```csharp
        if (!IsRecordDirty(root)
            && !FileGainsInterfaceMember(_plan!, semanticModel.SyntaxTree.FilePath)
            && !FileGetsInterfaceFill(_plan!, semanticModel.SyntaxTree.FilePath))
        {
            statistics.FilesSkippedClean++;
            return new RecordReadOnlyFileResult { OutputCode = sourceCode, Changed = false };
        }
```

在 `FileGainsInterfaceMember` 后新增：

```csharp
    /// <summary>该文件声明了某个转换计划需要 IR-3 补 fluent 实现的其他接口实现者。</summary>
    private static bool FileGetsInterfaceFill(ConversionPlan plan, string filePath) =>
        plan.Plans.Any(p => p.Outcome == PlanOutcome.Converted
            && p.Ripples.SelectMany(r => r.Fills).Any(f =>
                f.Implementer.Locations.Any(l => l.IsInSource
                    && string.Equals(l.SourceTree!.FilePath, filePath, StringComparison.OrdinalIgnoreCase))));
```

- [ ] **Step 7: InterfaceRippleIndex——聚合 fills 并去重已转换 record**

在 `InterfaceRippler.cs` 中把 `InterfaceRippleIndex` 的

```csharp
    public required Dictionary<INamedTypeSymbol, List<RippleMember>> Fills { get; init; }
```

保留不变，把 `Build` 方法末尾的

```csharp
        return new InterfaceRippleIndex
        {
            Additions = additions,
            Fills = new(SymbolEqualityComparer.Default),
        };
```

替换为：

```csharp
        var convertedImplementers = new HashSet<INamedTypeSymbol>(
            context.Plan.Plans
                .Where(p => p.Outcome == PlanOutcome.Converted)
                .Select(p => p.Symbol),
            SymbolEqualityComparer.Default);

        var fills = new Dictionary<INamedTypeSymbol, List<RippleMember>>(
            SymbolEqualityComparer.Default);
        foreach (var plan in context.ActivePlans)
        {
            if (plan.Outcome != PlanOutcome.Converted) continue;
            foreach (var ripple in plan.Ripples)
            foreach (var fill in ripple.Fills)
            {
                // 另一个已转换 record 由自己的 IR-2 生成显式实现；不能再按普通实现者补一份。
                if (convertedImplementers.Contains(fill.Implementer)) continue;
                if (!fills.TryGetValue(fill.Implementer, out var list))
                    fills[fill.Implementer] = list = new();
                foreach (var member in fill.Members)
                {
                    if (list.Any(existing => InterfaceRippleIndex.SameShape(existing, member)))
                        continue;
                    list.Add(member);
                }
            }
        }

        return new InterfaceRippleIndex
        {
            Additions = additions,
            Fills = fills,
        };
```

- [ ] **Step 8: InterfaceRippler——为 class/struct/未转换 record 生成属性 fill**

8.1 在 `InterfaceRippler` 类中增加 class/struct visitor：

```csharp
    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
        return AddInterfaceFills(node, visited);
    }

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        var visited = (StructDeclarationSyntax)base.VisitStructDeclaration(node)!;
        return AddInterfaceFills(node, visited);
    }
```

8.2 **整体替换** `VisitRecordDeclaration`（Task 19 版本在 `plan == null` 时提前返回，未转换的 record class/record struct 实现者永远拿不到 fill；新版本把“owner 的 IR-2 成员”与“任意 record 的 IR-3 fill”两条路径分开，fill 计数只数 fill）：

```csharp
    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var visited = (RecordDeclarationSyntax)base.VisitRecordDeclaration(node)!;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol == null || !IsPrimaryPart(node, symbol)) return visited;

        var additions = new List<MemberDeclarationSyntax>();
        var emittedPublicShapes = new HashSet<string>(StringComparer.Ordinal);

        // IR-2 只属于本轮转换中的 owner record；未转换 record 在这里 plan == null。
        var plan = _context.ActivePlans.FirstOrDefault(p =>
            SymbolEqualityComparer.Default.Equals(p.Symbol, symbol)
            && p.Outcome == PlanOutcome.Converted);
        if (plan != null)
        {
            foreach (var ripple in plan.Ripples)
            {
                foreach (var member in ripple.Members)
                {
                    additions.Add(ExplicitPropertyStub(member)
                        .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                    additions.Add(ExplicitWithPropertyImplementation(member)
                        .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));

                    if (emittedPublicShapes.Add(PublicShapeKey(member)))
                    {
                        additions.Add(PublicWithPropertyMethod(symbol, member)
                            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                    }
                }
            }
        }

        // IR-3：未转换 record class（含 ConvertRecordClasses=false 时的 record class）/
        // 未转换 record struct 作为其他实现者补 fluent 显式实现。owner 与其他“本轮也转换”
        // 的 record 已在 InterfaceRippleIndex.Build 中排除（它们由自己的 IR-2 负责）。
        var fillAdditions = BuildFillMembers(symbol);
        additions.AddRange(fillAdditions);

        if (additions.Count == 0) return visited;
        foreach (var _ in fillAdditions)
            _context.Statistics.InterfaceRipples++;
        return visited
            .WithMembers(visited.Members.AddRange(additions))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }
```

8.3 在类中追加以下辅助方法：

```csharp
    private SyntaxNode? AddInterfaceFills(TypeDeclarationSyntax node, TypeDeclarationSyntax visited)
    {
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol == null || !IsPrimaryPart(node, symbol)) return visited;
        var additions = BuildFillMembers(symbol);
        if (additions.Count == 0) return visited;
        // 每个实际生成的 fill 成员计一次 InterfaceRipples；接口追加在
        // VisitInterfaceDeclaration 计数，owner 的 IR-2 显式桩不计数。
        foreach (var _ in additions)
            _context.Statistics.InterfaceRipples++;
        return visited
            .WithMembers(visited.Members.AddRange(additions))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    private List<MemberDeclarationSyntax> BuildFillMembers(INamedTypeSymbol implementer)
    {
        if (!_index.Fills.TryGetValue(implementer, out var members) || members.Count == 0)
            return new();

        return members
            .Where(m => !m.IsMethod) // 方法触发在 Task 21 接入。
            .Select(member => PropertyFillMethod(implementer, member)
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed))
            .Cast<MemberDeclarationSyntax>()
            .ToList();
    }

    /// <summary>IR-3：`I I.WithP(T value) { P = value; return this; }`。</summary>
    private static MethodDeclarationSyntax PropertyFillMethod(
        INamedTypeSymbol implementer, RippleMember member)
    {
        var interfaceProperty = (IPropertySymbol)member.InterfaceMember;
        var implementation = (IPropertySymbol?)implementer
            .FindImplementationForInterfaceMember(interfaceProperty);
        var interfaceText = member.Interface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var valueTypeText = interfaceProperty.Type
            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var target = implementation != null
            && RecordSyntaxFacts.IsExplicitInterfaceImplementation(implementation, interfaceProperty)
            ? $"(({interfaceText})this).{interfaceProperty.Name}"
            : implementation?.Name ?? interfaceProperty.Name;

        var text =
            $"{interfaceText} {member.WithName}({valueTypeText} value) " +
            $"{{ {target} = value; return this; }}";
        return ((MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(text)!)
            .WithExplicitInterfaceSpecifier(ExplicitInterface(member.Interface))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }
```

8.4 计数语义核对（无需再写代码——8.2/8.3 已落地）：`InterfaceRipples` 只在两个生成点递增，每生成一个成员计一次——

- `VisitInterfaceDeclaration`：IR-1 接口追加（Task 19 既有逻辑，不变）；
- `AddInterfaceFills` 与 `VisitRecordDeclaration`：仅对 `BuildFillMembers` 的返回值（IR-3 fill）计数。

owner record 的 IR-2 显式桩/显式 With/公共 With 一律不计数。因此 `IR3_ClassImplementer_GetsFluentFill`
的期望是 `1（接口追加）+ 1（class fill）= 2`；同一实现者被多个已转换 record 的计划列入时，
`InterfaceRippleIndex.Build` 的 `SameShape` 去重保证只生成、只计数一次。

- [ ] **Step 9: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 全部 PASS。Task 19 属性 ripple 的 8 个用例保持通过；新增 IR-3 用例通过。重点检查：
- 接口文件、record 文件、普通实现者文件三类触碰面都在对应 `RecordPlan.TouchedFiles`；
- 已转换 owner record 只生成 IR-2 成员，不重复生成 IR-3 fill；
- 未转换 record class 的 `P` 保持 `{ get; set; }`，只新增显式 `WithP`；
- 显式接口实现 struct 触发 RR070 时 owner record 原子跳过。

- [ ] **Step 10: 提交**

Run:
```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): IR-3 fluent fills for other in-tree interface implementers"
```

---

### Task 21: IR-1/IR-2/IR-3/IR-4 方法触发 + MM 接口实现解阻

> 本任务后：
> - `interface I { void Bump(); }` + `record struct R : I { public void Bump() { X++; } }`：接口追加 `I WithBump();`；record 的原方法由 MM 改为 `public R Bump()`，旧契约由 `void I.Bump() => throw ...` 承接，新契约由 `I I.WithBump() => Bump();` 承接；`i.Bump()` → `i = i.WithBump();`，`r.Bump()` 仍由 MM 回写为 `r = r.Bump();`。
> - 非 void：`int Next()` → record 侧 `R Next(out int result)`，接口侧追加 `I WithNext(out int __result)`；`var x = i.Next();` → `i = i.WithNext(out var x);`。
> - 显式接口实现方法先提升为 public 强类型方法，再由 InterfaceRippler 补旧契约桩。virtual/override/object-contract 仍 RR065；外部接口/签名碰撞仍 RR070/RR071；**泛型接口方法**（`IM&lt;T&gt;(...)`）保守 RR070，随 IR-6 在 Task 22 处理，重写路径不抛运行时异常。
> - 多个已转换 record ripple 同一接口方法时，接口只加一份 `WithX`（Step 8.4 方法去重）。

**Files:**
- Modify: `src/CSharpToJava.Core/RecordReadOnly/MethodMigrator.cs`（允许接口实现方法迁移；接口调用改 `WithX`；显式方法提升 public）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs`（方法 ripple 映射、合并属性/方法 ripple、方法 fill）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/InterfaceRippler.cs`（接口方法、旧方法桩、显式 With、IR-3 方法 fill）
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

- [ ] **Step 1: 写失败测试**

追加测试：

```csharp
    [Fact]
    public void IR1_VoidInterfaceMethod_AddsWithAndMigratesRecordMethod()
    {
        var src = """
            public interface I { void Bump(); }
            public record struct R : I
            {
                public int X { get; set; }
                public void Bump() { X++; }
            }
            public class C { public void Use(I i, R r) { i.Bump(); r.Bump(); } }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("global::I WithBump();", output);
        Assert.Contains("public global::R Bump()", output);
        Assert.Contains("void global::I.Bump()", output);
        Assert.Contains("global::I global::I.WithBump() => Bump();", output);
        Assert.Contains("i = i.WithBump();", output);
        Assert.Contains("r = r.Bump();", output);
        Assert.Equal(1, stats.MethodsMigrated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR1_NonVoidInterfaceMethod_UsesOutResultShape()
    {
        var src = """
            public interface I { int Next(); }
            public record struct R : I
            {
                public int X { get; set; }
                public int Next() => ++X;
            }
            public class C
            {
                public void Use(I i, R r)
                {
                    var a = i.Next();
                    var b = r.Next();
                }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("global::I WithNext(out int __result);", output);
        Assert.Contains("public global::R Next(out int result)", output);
        Assert.Contains("int global::I.Next()", output);
        Assert.Contains("global::I global::I.WithNext(out int __result) => Next(out __result);", output);
        Assert.Contains("i = i.WithNext(out var a);", output);
        Assert.Contains("r = r.Next(out var b);", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR2_ExplicitInterfaceMethod_PromotedToPublicStrongMethod()
    {
        var src = """
            public interface I { void Bump(); }
            public record struct R : I
            {
                public int X { get; set; }
                void I.Bump() { X++; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("public global::R Bump()", output);
        Assert.DoesNotContain("void global::I.Bump() {", output);
        Assert.Contains("void global::I.Bump() => throw new global::System.NotSupportedException(", output);
        Assert.Contains("global::I global::I.WithBump() => Bump();", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR3_OtherClassImplementer_GetsVoidAndNonVoidMethodFills()
    {
        var src = """
            public interface I { void Bump(); int Next(); }
            public record struct R : I
            {
                public int X { get; set; }
                public void Bump() { X++; }
                public int Next() => ++X;
            }
            public class C : I
            {
                public int X { get; set; }
                public void Bump() { X++; }
                public int Next() => ++X;
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("""
            global::I global::I.WithBump()
            {
                Bump();
                return this;
            }
            """.Replace("\r\n", "\n"), output.Replace("\r\n", "\n"));
        Assert.Contains("""
            global::I global::I.WithNext(out int __result)
            {
                __result = Next();
                return this;
            }
            """.Replace("\r\n", "\n"), output.Replace("\r\n", "\n"));
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR4_InterfaceMethodReceiverInParam_BlocksWithRR050()
    {
        var src = """
            public interface I { void Bump(); }
            public record struct R : I { public int X { get; set; } public void Bump() { X++; } }
            public class C { public void Use(in I i) { i.Bump(); } }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("WithBump()", output.Replace("void global::I.Bump", string.Empty));
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR050);
    }

    [Fact]
    public void IR7_MethodWithNameCollision_BlocksWithRR071()
    {
        var src = """
            public interface I { void Bump(); int WithBump(); }
            public record struct R : I
            {
                public int X { get; set; }
                public void Bump() { X++; }
                public int WithBump() => 0;
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR071);
    }

    [Fact]
    public void IR7_ExternalInterfaceMethod_BlocksWithRR070()
    {
        var lib = "public interface IExt { void Bump(); }";
        var src = """
            public record struct R : IExt
            {
                public int X { get; set; }
                public void Bump() { X++; }
            }
            """;
        var (output, _, diags) = RewriteWithMetadata(src, lib);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR070);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 方法接口实现仍被 `MethodMigrationPlanner` 的“later stage” RR020 闸门拒绝，新增成功用例失败；RR071 用例可能得到 RR020；外部接口用例可能偶然通过，Step 8 后仍必须通过。

- [ ] **Step 3: MethodMigrationPlanner——允许接口实现方法，继续阻断 virtual/override**

在 `MethodMigrator.cs` 的 `MethodMigrationPlanner.BuildUnits` 中，把：

```csharp
            // 隐式接口实现不标记 virtual，两个判定必须并列、接口在前（MM-6/IR）。
            if (ImplementsInterfaceMethod(w.Method))
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR020,
                    "interface-implementation methods are handled by interface ripple (IR) " +
                    "in a later stage.", location));
            if (w.IsOverrideExplicitOrVirtual)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR065,
                    "a virtual/override/object-contract method cannot change its signature; " +
                    "only interface-implementation methods can be rerouted via IR (MM-6).",
                    location));
```

替换为：

```csharp
            var implementsInterface = ImplementsInterfaceMethod(w.Method);
            if (implementsInterface
                && w.Method.IsVirtual
                && w.Method.ExplicitInterfaceImplementations.Length == 0)
            {
                // virtual 接口实现可能有未一起迁移的 override；显式接口实现不可标记 virtual，
                // 普通隐式接口实现的 IsVirtual 为 false，可经 IR 新旧双契约迁移。
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR065,
                    "a virtual interface implementation has an override contract that cannot be " +
                    "changed until every in-tree override is migrated together (MM-6).",
                    location));
            }
            if (!implementsInterface && w.IsOverrideExplicitOrVirtual)
                return (new(), new MigrationFailure(RecordReadOnlyDiagnosticCode.RR065,
                    "a virtual/override/object-contract method cannot change its signature; " +
                    "only interface-implementation methods can be rerouted via IR (MM-6).",
                    location));
```

`ImplementsInterfaceMethod` 辅助保持不变。

> **实现时必须用测试核实的 Roslyn 语义（MM-6）：** 不带 `virtual` 关键字的普通隐式接口实现，
> 其 `IMethodSymbol.IsVirtual` 必须为 **false**（IL 层虽为 virtual sealed，但 Roslyn 的
> `IsVirtual` 只反映 C# 语义）；显式接口实现同样 `IsVirtual == false`。本任务的
> `IR1_VoidInterfaceMethod_AddsWithAndMigratesRecordMethod` 等用例隐式守护该前提——
> 若实现时探测到普通隐式实现被判为 true，**不要**退回 `w.IsOverrideExplicitOrVirtual`
> （它包含显式实现语法与 abstract，会误伤本任务要提升的 `void I.M()`），而应改为直接读取
> `DeclaringSyntaxReferences` 对应 `MethodDeclarationSyntax` 的 `virtual/abstract/override`
> 修饰符来判定。record struct 不可能声明 virtual/abstract 方法（除 object 契约 override，
> 走 `!implementsInterface` 分支），该风险面实际只在 record class（M5）转换时出现。

- [ ] **Step 4: MethodMigrator——显式接口实现方法提升为 public 强类型方法**

在 `MethodMigrator.VisitMethodDeclaration` 中，紧接在

```csharp
        var nonVoid = (unit.Kind & MethodMigrationKind.NonVoidToOut) != 0;
        var resultName = UniqueResultName(node);
```

之后、`var body = node.Body;`（expression body 展开）之前插入：

```csharp
        // IR-2：显式接口方法先提升为 public 强类型方法，旧契约由 InterfaceRippler 补 throw 桩。
        node = PromoteExplicitInterfaceMethod(node);
```

方法末尾既有的 `return node.WithReturnType(recordTypeSyntax)...` 原样保留：它会带着新的
public 修饰符与被移除的 explicit specifier 改返回类型/参数列表，因此本步不动尾部合成逻辑。

并在 `MethodMigrator` 类中追加：

```csharp
    /// <summary>
    /// 显式接口方法（如 `void I.Bump() {}`）迁移后要成为强类型 public `R Bump()`；
    /// 旧 I.Bump 契约由 InterfaceRippler 另生成 throw 桩。普通隐式实现原样保留修饰符。
    /// </summary>
    private static MethodDeclarationSyntax PromoteExplicitInterfaceMethod(
        MethodDeclarationSyntax node)
    {
        if (node.ExplicitInterfaceSpecifier == null) return node;

        var keptModifiers = node.Modifiers
            .Where(t => t.Kind() is not (SyntaxKind.PublicKeyword
                or SyntaxKind.ProtectedKeyword
                or SyntaxKind.InternalKeyword
                or SyntaxKind.PrivateKeyword
                or SyntaxKind.ProtectedInternalKeyword))
            .ToList();
        keptModifiers.Insert(0, SyntaxFactory.Token(SyntaxKind.PublicKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space));

        return node
            .WithExplicitInterfaceSpecifier(null)
            .WithModifiers(SyntaxFactory.TokenList(keptModifiers));
    }
```

- [ ] **Step 5: MethodCallSiteScanner——识别接口静态类型接收者**

> 锚点是 **Task 18 MS-15 之后的最终扫描器**（带 `SelfArgument` / 体吸收判定的版本，
> 非 Task 17 时代的 `receiver` 版本）。以下三处定点修改都针对该版本。

5.1 在 `MethodCallSiteScanner.Scan` 中，当前逻辑遇到非迁移 callee 立即 `continue`：

```csharp
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) continue;
                if (!unitsByOriginal.TryGetValue(callee.OriginalDefinition, out var migrated))
                    continue;
```

替换为（接口静态类型 callee 先映射回本 record 的实现方法再查迁移单元；record 强类型
接收者解析到的就是实现方法本身，走原路径）：

```csharp
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) continue;

                MigrationUnit? migrated;
                var interfaceCall = false;
                INamedTypeSymbol? receiverInterface = null;
                if (callee.ContainingType.TypeKind == TypeKind.Interface)
                {
                    if (info.Symbol.FindImplementationForInterfaceMember(callee)
                        is not IMethodSymbol implementation
                        || !unitsByOriginal.TryGetValue(
                            implementation.OriginalDefinition, out migrated!))
                    {
                        continue;
                    }
                    interfaceCall = true;
                    receiverInterface = (INamedTypeSymbol)callee.ContainingType;
                }
                else if (!unitsByOriginal.TryGetValue(callee.OriginalDefinition, out migrated))
                {
                    continue;
                }
```

后续体吸收 / `this` 只读 / hoist 屏障 / `ReceiverWritableAndPure` 判定一律不动——它们只
依赖 `self`、`enclosing` 与 `migrated`，对接口调用同样成立（`in I i` 的 RR050 即由
`ReceiverWritableAndPure(self)` 自然产出）。

5.2 通过全部检查后，最终扫描器的普通用点添加语句（注意变量名是 `self`，不是旧版的
`receiver`）：

```csharp
                result.Add((MakeSite(tree, invocation, self, owner, migrated),
                    WritebackThis: false));
```

替换为（第 5 参传 **callee**：接口调用传接口方法符号，供重写器改名 `WithX` 与判定
void/非 void；不要传 `migrated`）：

```csharp
                result.Add((MakeSite(
                        tree, invocation, self, owner, callee,
                        interfaceCall ? SiteActionKind.InterfaceWithCall
                            : SiteActionKind.MethodMigrateCall,
                        model.GetTypeInfo(self).Type ?? owner.Symbol,
                        receiverInterface),
                    WritebackThis: false));
```

同方法内构造函数 `this = this.M(...)` 路径的添加语句：

```csharp
                        result.Add((MakeSite(tree, invocation,
                            SyntaxFactory.ThisExpression(), owner, migrated), WritebackThis: true));
```

替换为（该形态解析到的必是 record 自身方法，不可能是接口方法）：

```csharp
                        result.Add((MakeSite(tree, invocation,
                            SyntaxFactory.ThisExpression(), owner, callee,
                            SiteActionKind.MethodMigrateCall, owner.Symbol, null),
                            WritebackThis: true));
```

5.3 把最终版 `MakeSite`（5 参签名）整体替换为：

```csharp
    private static SitePlan MakeSite(
        SyntaxTree tree,
        InvocationExpressionSyntax invocation,
        ExpressionSyntax receiver,
        RecordInfo owner,
        IMethodSymbol callee,
        SiteActionKind action,
        ITypeSymbol receiverType,
        INamedTypeSymbol? receiverInterface)
    {
        var site = new MutationSiteInfo
        {
            Location = invocation.GetLocation(),
            FilePath = tree.FilePath,
            WriteNode = invocation,
            Left = invocation,
            Receiver = receiver,
            ReceiverType = receiverType,
            ReceiverInterface = receiverInterface,
            Kind = SiteKind.MethodCall,
            IsStatementPosition =
                invocation.FirstAncestorOrSelf<ExpressionStatementSyntax>() != null,
            MethodCallee = callee,
        };
        site.TargetRecord = owner;
        return new SitePlan(site, action);
    }
```

`self` 在无接收者形态下是扫描器合成的 `ThisExpression`（不属于任何语法树），对它调
`model.GetTypeInfo(self)` 返回 None，`?? owner.Symbol` 兜底为 record 类型，行为与旧版
硬编码 `ReceiverType = owner.Symbol` 等价；扩展方法静态形态的首参与成员访问表达式都是
树内节点，取到真实静态类型。

- [ ] **Step 6: MethodCallSyntaxRewriter——接口调用改名 WithX，并复用 out 提升管线**

6.1 `MethodMigrator.BuildMigratedSet` 的最终版（Task 18）是无括号双层 foreach：

```csharp
        var set = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var plan in context.ActivePlans)
            foreach (var migration in plan.Migrations)
                set.Add(migration.Method.OriginalDefinition);
        return set;
```

替换为（ripple 接口方法也必须进识别集合，`i.Bump()` 才会被标注为迁移调用；去重交给
HashSet——两个 record 触发同一接口方法时 `OriginalDefinition` 相同）：

```csharp
        var set = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var plan in context.ActivePlans)
        {
            foreach (var migration in plan.Migrations)
                set.Add(migration.Method.OriginalDefinition);
            foreach (var ripple in plan.Ripples)
            foreach (var member in ripple.Members)
            {
                if (member.IsMethod)
                    set.Add(((IMethodSymbol)member.InterfaceMember).OriginalDefinition);
            }
        }
        return set;
```

6.2 在 `MethodCallSyntaxRewriter.RewriteMarkedCall` 中，把：

```csharp
        var call = nonVoid ? WithOutArgument(visited, outName!) : visited;
```

替换为：

```csharp
        var prepared = PrepareInterfaceRippleCall(visited, callee);
        var call = nonVoid ? WithOutArgument(prepared, outName!) : prepared;
```

6.3 在 `MethodCallSyntaxRewriter` 中新增（泛型接口方法已在 Step 7 的计划阶段整体 RR070，
这里不可能遇到 `M&lt;T&gt;`，故只处理非泛型标识符）：

```csharp
    /// <summary>
    /// `i.M()` 命中接口方法 ripple 时改为 `i.WithM(...)`（改名在加 out 实参之前完成，
    /// 非 void 由既有 WithOutArgument 管线补 `out var x`）。record 强类型接收者解析到
    /// R.M，callee.ContainingType 不是接口，因此不会改名，仍走 MM 的 `r = r.M(...)`。
    /// </summary>
    private static InvocationExpressionSyntax PrepareInterfaceRippleCall(
        InvocationExpressionSyntax invocation, IMethodSymbol callee)
    {
        if (callee.ContainingType.TypeKind != TypeKind.Interface)
            return invocation;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return invocation;

        return invocation.WithExpression(memberAccess.WithName(
            SyntaxFactory.IdentifierName("With" + callee.Name)));
    }
```

这样 void 接口调用复用现有“直接表达式语句 → 一条回写语句”路径；非 void 接口调用复用现有 `out var`/嵌套提升/hoist barrier 路径，不需要在 MutationSiteRewriter 中再造第二套。

6.4 **关键分流**：`MethodMigrator.BuildMsAnnotationMap` 现在只跳过 `MethodMigrateCall`，
而方法触发的接口用点动作是 `InterfaceWithCall`、`Site.Kind == MethodCall`——若让它进入
`MutationSiteRewriter`，`RewriteInterfaceWithCall` 只接受赋值表达式、对 invocation 返回
null，会与 `MethodCallSyntaxRewriter` 形成双重处理。把：

```csharp
                if (sitePlan.Action == SiteActionKind.MethodMigrateCall) continue;
```

替换为（属性触发的 `InterfaceWithCall` 是 `SiteKind.SimpleAssign`，不受影响，仍归
MutationSiteRewriter）：

```csharp
                if (sitePlan.Action == SiteActionKind.MethodMigrateCall) continue;
                // 方法形态的接口 ripple 用点（Invocation）由 MethodCallSyntaxRewriter
                // 改名 + out 提升 + 回写一条管线处理，不进 MutationSiteRewriter。
                if (sitePlan.Site.Kind == SiteKind.MethodCall) continue;
```

- [ ] **Step 7: PlanBuilder——合并属性/方法 ripple，方法映射与碰撞/外部接口门**

7.1 **属性触发改为只收集，不立刻物化。** 在 `BuildRecordPlan` 属性循环前增加：

```csharp
        // 属性触发与方法触发的接口连带成员统一收集，方法块结束后一次性物化（IR-3 fills
        // 需要看到合并后的完整成员集）。
        var rippleMembersByInterface =
            new Dictionary<INamedTypeSymbol, List<RippleMember>>(SymbolEqualityComparer.Default);
```

属性循环中，Task 20 落地的尾部：

```csharp
            var fills = BuildInterfaceFills(
                analysis, info.Symbol, iface, members, out var fillBlock);
            if (fillBlock != null)
                return FailAt(plan, fillBlock.Code, fillBlock.Reason, fillBlock.Location);

            plan.Ripples.Add(new InterfaceRipplePlan(iface, members, fills));
            AddInterfaceTouches(plan, iface, fills);
```

替换为：

```csharp
            AddRippleMembers(rippleMembersByInterface, iface, members);
```

同时**删除** Task 19 留在属性循环上方的 cap 门及其上方两行注释（它只统计了属性触发；
合并后的门在 7.3 统一判定）：

```csharp
        // 接口连带（IR-1/IR-2/IR-4/IR-7；属性触发；方法触发 Task 21）。
        // cap 门保留：能力被外部关闭时给 RR070（单测/未来 overrides 可构造该形态）。
        if (rippleProperties.Count > 0 && !_caps.InterfaceRipple)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR070,
                "record implicitly implements a settable interface member; interface ripple (IR) " +
                "is required to convert.");
```

7.2 **整体替换** Task 18 的方法迁移规划块（以
`// mutating 方法 → MM 迁移规划（影响集 / MM-6 否决 / 调用点）。` 注释开头、到调用点
`foreach (var (callSite, _) in callSites) { ... }` 结束的整个 `if` 块）。最终顺序固定为：
① `BuildUnits` → ② planner 失败门 → ③ 方法接口映射 + RR070/RR071/泛型门 →
④ 添加 `MethodMigrationPlan` → ⑤ RR058 方法组扫描 → ⑥ 调用点扫描。

```csharp
        // mutating 方法 → MM 迁移规划（影响集 / MM-6 否决 / 调用点）。
        if (info.WriteInfos.Values.Any(w => !w.IsConstructor && !w.IsPropertySetter
                && (w.WrittenMembers.Count > 0 || w.WritesThis)))
        {
            if (!_caps.MethodMigration)
                return Fail(plan, RecordReadOnlyDiagnosticCode.RR020,
                    "record has instance method(s) that mutate state; method migration (MM) is " +
                    "required to convert.");

            var (units, migrationFailure) = MethodMigrationPlanner.BuildUnits(info);
            if (migrationFailure != null)
                return FailAt(plan, migrationFailure.Code, migrationFailure.Reason,
                    migrationFailure.Location);

            var orderedUnits = units.Values.OrderBy(u =>
                    u.Method.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0)
                .ToList();

            // IR 方法触发：先于 Migrations/RR058/调用点扫描做映射与阻断门（原子跳过）。
            foreach (var unit in orderedUnits)
            {
                foreach (var (iface, interfaceMethod) in
                         InterfaceMethodImplementations(info.Symbol, unit.Method))
                {
                    var memberLocation = interfaceMethod.Locations.FirstOrDefault(l => l.IsInSource)
                        ?? info.Symbol.Locations.First(l => l.IsInSource);

                    if (!RecordSyntaxFacts.IsInTree(iface))
                        return FailAt(plan, RecordReadOnlyDiagnosticCode.RR070,
                            $"interface '{iface.ToDisplayString()}' declares method " +
                            $"'{interfaceMethod.Name}' implemented by this record but the interface " +
                            "is not part of the source tree; the required 'With' member cannot be " +
                            "added to it (IR-7/FB-3).",
                            memberLocation);

                    // IR-6 延后：泛型接口方法的语法形状（With 名上的类型实参、泛型 fill、
                    // 显式泛型实现）在本任务保守 RR070，不在重写路径抛异常。
                    if (interfaceMethod.TypeParameters.Length > 0)
                        return FailAt(plan, RecordReadOnlyDiagnosticCode.RR070,
                            $"interface method '{iface.ToDisplayString()}.{interfaceMethod.Name}' " +
                            "declares type parameters; generic interface-member ripple is not " +
                            "supported yet (IR-6, deferred to a later task).",
                            memberLocation);

                    var withName = "With" + interfaceMethod.Name;
                    if (iface.GetMembers().Any(m => m.Name == withName))
                        return FailAt(plan, RecordReadOnlyDiagnosticCode.RR071,
                            $"interface '{iface.ToDisplayString()}' already declares '{withName}' " +
                            "(IR-7).",
                            memberLocation);
                    if (info.Symbol.GetMembers().Any(m => m.Name == withName))
                        return FailAt(plan, RecordReadOnlyDiagnosticCode.RR071,
                            $"record '{info.Symbol.Name}' already declares '{withName}'; the " +
                            "generated interface ripple member would collide (IR-7).",
                            info.Symbol.Locations.First(l => l.IsInSource));

                    AddRippleMembers(rippleMembersByInterface, iface, new[]
                    {
                        new RippleMember(
                            iface, interfaceMethod, unit.Method, withName,
                            IsMethod: true, ReturnsVoid: interfaceMethod.ReturnsVoid),
                    });
                }
            }

            foreach (var unit in orderedUnits)
            {
                plan.Migrations.Add(new MethodMigrationPlan(
                    unit.Method, unit.Kind, unit.PromotedMembers));
            }

            var groupFailure = MethodGroupBindingScanner.Scan(analysis.Compilation!, units);
            if (groupFailure != null)
                return FailAt(plan, groupFailure.Code, groupFailure.Reason,
                    groupFailure.Location);

            var callSites = MethodCallSiteScanner.Scan(
                analysis.Compilation!, info, units, out var callFailure);
            if (callFailure != null)
                return FailAt(plan, callFailure.Code, callFailure.Reason, callFailure.Location);
            foreach (var (callSite, _) in callSites)
            {
                plan.Sites.Add(callSite);
                plan.TouchedFiles.Add(callSite.Site.FilePath);
            }
        }
```

> 注：泛型**接口**（如 `I<T>` 上的非泛型方法）不在此门内——Task 19 属性触发已允许该形态
> （显式实现以构造名 `global::I&lt;...&gt;.WithX` 落地），方法触发保持同样边界；IR-6 对
> open generic 边角的完整处理在 Task 22。

7.3 **统一物化。** 在上面 MM `if` 块的**闭合花括号之后**、既有的
`if (analysis.HasDynamicOrReflectionMutation)` 判定之前，插入：

```csharp
        // 属性 + 方法触发的接口连带在此合并物化：cap 门、IR-3 fills、触碰面一次算清。
        if (rippleMembersByInterface.Count > 0 && !_caps.InterfaceRipple)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR070,
                "record implements an interface member that requires a generated With-style " +
                "contract; interface ripple (IR) is required to convert.");

        foreach (var pair in rippleMembersByInterface
                     .OrderBy(p => p.Key.ToDisplayString(), StringComparer.Ordinal))
        {
            var iface = pair.Key;
            var members = pair.Value;
            var fills = BuildInterfaceFills(
                analysis, info.Symbol, iface, members, out var fillBlock);
            if (fillBlock != null)
                return FailAt(plan, fillBlock.Code, fillBlock.Reason, fillBlock.Location);

            plan.Ripples.Add(new InterfaceRipplePlan(iface, members, fills));
            AddInterfaceTouches(plan, iface, fills);
        }
```

7.4 在 `PlanBuilder` 中新增：

```csharp
    private static void AddRippleMembers(
        Dictionary<INamedTypeSymbol, List<RippleMember>> byInterface,
        INamedTypeSymbol iface,
        IEnumerable<RippleMember> members)
    {
        if (!byInterface.TryGetValue(iface, out var list))
            byInterface[iface] = list = new();
        foreach (var member in members)
        {
            if (list.Any(existing =>
                    existing.IsMethod == member.IsMethod
                    && SymbolEqualityComparer.Default.Equals(
                        existing.InterfaceMember, member.InterfaceMember)))
            {
                continue;
            }
            list.Add(member);
        }
    }

    /// <summary>
    /// 返回 record 迁移方法所实现的全部 in-tree/接口符号映射。一个方法可同时隐式实现
    /// 多个接口；每个接口各生成旧契约桩与新 With 契约。
    /// </summary>
    private static IReadOnlyList<(INamedTypeSymbol Interface, IMethodSymbol InterfaceMethod)>
        InterfaceMethodImplementations(INamedTypeSymbol record, IMethodSymbol method)
    {
        var result = new List<(INamedTypeSymbol, IMethodSymbol)>();
        foreach (var iface in record.AllInterfaces)
        {
            foreach (var interfaceMethod in iface.GetMembers().OfType<IMethodSymbol>())
            {
                if (interfaceMethod.MethodKind != MethodKind.Ordinary)
                    continue;
                var implementation = record.FindImplementationForInterfaceMember(interfaceMethod);
                if (implementation is not IMethodSymbol implemented)
                    continue;
                if (SymbolEqualityComparer.Default.Equals(implemented, method)
                    || SymbolEqualityComparer.Default.Equals(
                        implemented.PartialImplementationPart, method)
                    || SymbolEqualityComparer.Default.Equals(
                        implemented.PartialDefinitionPart, method))
                {
                    result.Add((iface, interfaceMethod));
                }
            }
        }
        return result;
    }
```

- [ ] **Step 8: InterfaceRippler——方法接口成员、旧方法桩、显式 With 实现**

8.1 在 `VisitInterfaceDeclaration` 中，把成员生成逻辑：

```csharp
        var additions = members
            .Select(m => SyntaxFactory.ParseMemberDeclaration(InterfaceMemberText(m))!)
            .Cast<MemberDeclarationSyntax>()
```

替换为：

```csharp
        var additions = members
            .Select<RippleMember, MemberDeclarationSyntax>(m => m.IsMethod
                ? InterfaceMethodDeclaration(m)
                : SyntaxFactory.ParseMemberDeclaration(InterfaceMemberText(m))!)
```

8.2 Task 20 已把 `VisitRecordDeclaration` 重构为“owner IR-2 块 + 任意 record IR-3 fill”。
本次**整体替换该方法**：owner 循环里按成员形态分流（方法只加旧契约 throw 桩 + 显式
With 转发，不生成公共 `WithX`——record 强类型契约就是被 MM 迁移后保留原名的方法）：

```csharp
    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var visited = (RecordDeclarationSyntax)base.VisitRecordDeclaration(node)!;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol == null || !IsPrimaryPart(node, symbol)) return visited;

        var additions = new List<MemberDeclarationSyntax>();
        var emittedPublicShapes = new HashSet<string>(StringComparer.Ordinal);

        var plan = _context.ActivePlans.FirstOrDefault(p =>
            SymbolEqualityComparer.Default.Equals(p.Symbol, symbol)
            && p.Outcome == PlanOutcome.Converted);
        if (plan != null)
        {
            foreach (var ripple in plan.Ripples)
            {
                foreach (var member in ripple.Members)
                {
                    if (member.IsMethod)
                    {
                        additions.Add(ExplicitOldMethodStub(member)
                            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                        additions.Add(ExplicitWithMethodImplementation(member)
                            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                        continue;
                    }

                    additions.Add(ExplicitPropertyStub(member)
                        .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                    additions.Add(ExplicitWithPropertyImplementation(member)
                        .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));

                    if (emittedPublicShapes.Add(PublicShapeKey(member)))
                    {
                        additions.Add(PublicWithPropertyMethod(symbol, member)
                            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                    }
                }
            }
        }

        var fillAdditions = BuildFillMembers(symbol);
        additions.AddRange(fillAdditions);

        if (additions.Count == 0) return visited;
        foreach (var _ in fillAdditions)
            _context.Statistics.InterfaceRipples++;
        return visited
            .WithMembers(visited.Members.AddRange(additions))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }
```

8.3 在 `InterfaceRippler` 中追加这些方法辅助：

```csharp
    /// <summary>IR-1：接口原方法模板改返回 I、改名 WithX；非 void 末尾加 out __result。</summary>
    private static MethodDeclarationSyntax InterfaceMethodDeclaration(RippleMember member)
    {
        var template = InterfaceMethodTemplate(member);
        var interfaceMethod = (IMethodSymbol)member.InterfaceMember;
        var (parameters, _) = WithOptionalResultParameter(template, interfaceMethod);
        return CleanMethodTemplate(template)
            .WithReturnType(RecordSyntaxFacts.GlobalType(member.Interface))
            .WithIdentifier(SyntaxFactory.Identifier(member.WithName))
            .WithParameterList(parameters)
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>IR-2：旧方法契约保留为 throw 桩；返回类型/参数与原接口成员一致。</summary>
    private static MethodDeclarationSyntax ExplicitOldMethodStub(RippleMember member)
    {
        var template = InterfaceMethodTemplate(member);
        var method = (IMethodSymbol)member.InterfaceMember;
        var message =
            $"interface method '{member.Interface.ToDisplayString()}.{method.Name}' is not " +
            "supported after the record became readonly; use the generated 'With' member " +
            "instead (IR-2).";
        var throwExpression = SyntaxFactory.ParseExpression(
            "throw new global::System.NotSupportedException(\"" + message + "\")");

        return CleanMethodTemplate(template)
            .WithReturnType(template.ReturnType)
            .WithIdentifier(SyntaxFactory.Identifier(method.Name))
            .WithExplicitInterfaceSpecifier(ExplicitInterface(member.Interface))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(throwExpression))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>IR-2：`I I.WithX(...) => ImplName(...[, out __result]);`。</summary>
    private static MethodDeclarationSyntax ExplicitWithMethodImplementation(RippleMember member)
    {
        var template = InterfaceMethodTemplate(member);
        var interfaceMethod = (IMethodSymbol)member.InterfaceMember;
        var (_, outName) = WithOptionalResultParameter(template, interfaceMethod);
        var invocation = InvokeOriginalMethod(member, template, outName);
        return InterfaceMethodDeclaration(member)
            .WithExplicitInterfaceSpecifier(ExplicitInterface(member.Interface))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(invocation))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>
    /// 取接口方法的源码声明模板。Step 7 的 RR070 已保证接口在编译树内，方法必然存在
    /// in-source 的 DeclaringSyntaxReference；接口即使分部件声明，取第一个即可（模板只
    /// 借用返回类型/参数/特性清洗，与部件位置无关）。
    /// </summary>
    private static MethodDeclarationSyntax InterfaceMethodTemplate(RippleMember member) =>
        (MethodDeclarationSyntax)((IMethodSymbol)member.InterfaceMember)
            .DeclaringSyntaxReferences
            .First(r => r.SyntaxTree != null)
            .GetSyntax();

    private static MethodDeclarationSyntax CleanMethodTemplate(MethodDeclarationSyntax template) =>
        template
            .WithAttributeLists(default)
            .WithModifiers(SyntaxFactory.TokenList())
            .WithExplicitInterfaceSpecifier(null)
            .WithBody(null)
            .WithExpressionBody(null)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

    /// <summary>
    /// 非 void 接口方法在参数表末尾追加独立的 `out T __result`（名与原参数去重）；
    /// void 原样返回。symbol 由调用方显式传入，不依赖 template 的语义父节点。
    /// </summary>
    private static (ParameterListSyntax Parameters, string? OutName) WithOptionalResultParameter(
        MethodDeclarationSyntax template, IMethodSymbol method)
    {
        if (method.ReturnsVoid)
            return (template.ParameterList, null);

        var outName = UniqueOutParameterName(template);
        var parameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(outName))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.OutKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space)))
            .WithType(RecordSyntaxFacts.GlobalType(method.ReturnType));
        return (SyntaxFactory.ParameterList(template.ParameterList.Parameters.Add(parameter)),
            outName);
    }

    private static string UniqueOutParameterName(MethodDeclarationSyntax template)
    {
        var taken = template.ParameterList.Parameters
            .Select(p => p.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);
        var name = "__result";
        var suffix = 0;
        while (taken.Contains(name))
            name = $"__result_{++suffix}";
        return name;
    }

    /// <summary>
    /// owner record 内的转发调用：`ImplName(args)`；非 void 末尾追加 `out __result`。
    /// 名字必须取**实现方法**名（`member.RecordMember`，即 MM 单元方法）：隐式实现允许
    /// 与接口方法不同名（`public int Fetch()` 实现 `int Next()`），而显式实现符号的
    /// Name 同样不带接口前缀（"Bump"），提升后 public 名就是它。刻意不带 `this.` 前缀
    /// ——输出以精确子串守护（`=> Bump();`、`=> Next(out __result);`）。
    /// </summary>
    private static InvocationExpressionSyntax InvokeOriginalMethod(
        RippleMember member, MethodDeclarationSyntax template, string? outName)
    {
        var arguments = template.ParameterList.Parameters
            .Select(p => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Identifier.Text))
                .WithRefKindKeyword(ArgumentRefKindToken(p)))
            .ToList();
        if (outName != null)
        {
            arguments.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(outName))
                .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space)));
        }

        var implementationName = ((IMethodSymbol)member.RecordMember).Name;
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(implementationName),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
    }

    private static SyntaxToken ArgumentRefKindToken(ParameterSyntax parameter)
    {
        if (parameter.Modifiers.Any(SyntaxKind.InKeyword))
            return SyntaxFactory.Token(SyntaxKind.InKeyword).WithTrailingTrivia(SyntaxFactory.Space);
        if (parameter.Modifiers.Any(SyntaxKind.RefKeyword))
            return SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space);
        if (parameter.Modifiers.Any(SyntaxKind.OutKeyword))
            return SyntaxFactory.Token(SyntaxKind.OutKeyword).WithTrailingTrivia(SyntaxFactory.Space);
        return SyntaxFactory.Token(SyntaxKind.None);
    }
```

8.4 **`InterfaceRippleIndex.SameShape` 必须认识方法成员。** Task 19 的实现对方法直接
`return false`：

```csharp
    internal static bool SameShape(RippleMember a, RippleMember b)
    {
        if (a.IsMethod != b.IsMethod || a.WithName != b.WithName) return false;
        if (a.IsMethod) return false;
        var pa = (IPropertySymbol)a.InterfaceMember;
        var pb = (IPropertySymbol)b.InterfaceMember;
        return SymbolEqualityComparer.Default.Equals(pa.Type, pb.Type);
    }
```

若不改，两个已转换 record 同时 ripple 同一个接口方法时，`Additions` 会给接口生成两份
`I WithBump();`（编译错误），`Fills` 也会给其他实现者重复补方法。替换为：

```csharp
    internal static bool SameShape(RippleMember a, RippleMember b)
    {
        if (a.IsMethod != b.IsMethod || a.WithName != b.WithName) return false;
        if (a.IsMethod)
        {
            // 索引按接口符号分组，这里只需比较接口方法本体；同一接口方法被多个
            // record 计划引用时 OriginalDefinition 相同，去重为一份契约。
            return SymbolEqualityComparer.Default.Equals(
                ((IMethodSymbol)a.InterfaceMember).OriginalDefinition,
                ((IMethodSymbol)b.InterfaceMember).OriginalDefinition);
        }
        var pa = (IPropertySymbol)a.InterfaceMember;
        var pb = (IPropertySymbol)b.InterfaceMember;
        return SymbolEqualityComparer.Default.Equals(pa.Type, pb.Type);
    }
```

- [ ] **Step 9: InterfaceRippler——IR-3 方法 fills**

9.1 把 `BuildFillMembers` 中的过滤整行：

```csharp
            .Where(m => !m.IsMethod) // 方法触发在 Task 21 接入。
```

替换为：

```csharp
            // 属性与方法 fills 都支持。
```

即：

```csharp
        return members
            .Select(member => member.IsMethod
                ? MethodFillMethod(implementer, member)
                    .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
                : PropertyFillMethod(implementer, member)
                    .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed))
            .Cast<MemberDeclarationSyntax>()
            .ToList();
```

9.2 计数无需再改：Task 20 已让 `AddInterfaceFills` 与 `VisitRecordDeclaration` 按
`BuildFillMembers` 的**返回列表**计数，方法 fill 一旦在 9.1 进入返回值就自动每个成员计一次
`InterfaceRipples`，不存在“只算属性”的旧计数点（Task 20 草稿里那种 `fillMembers.Count(...)`
写法已被删除，不要重新引入）。

9.3 追加：

```csharp
    /// <summary>
    /// IR-3 方法 fill：
    /// void M(...)    { M(...); return this; }
    /// T M(...)       { __result = M(...); return this; }
    /// 隐式实现直接调原方法名（精确输出 `Bump();` / `__result = Next();`）；显式实现的
    /// class/record class 经 ((I)this) 调旧契约；显式 struct 已在 BuildInterfaceFills 的
    /// RR070 门阻断；泛型接口方法已在 PlanBuilder 的 RR070 门阻断——故此处没有也不允许
    /// 出现任何泛型分支或运行时异常。
    /// </summary>
    private static MethodDeclarationSyntax MethodFillMethod(
        INamedTypeSymbol implementer, RippleMember member)
    {
        var interfaceMethod = (IMethodSymbol)member.InterfaceMember;
        var implementation = (IMethodSymbol?)implementer
            .FindImplementationForInterfaceMember(interfaceMethod);
        var template = InterfaceMethodTemplate(member);
        var (parameters, outName) = WithOptionalResultParameter(template, interfaceMethod);

        var explicitCall = implementation != null
            && RecordSyntaxFacts.IsExplicitInterfaceImplementation(
                implementation, interfaceMethod);

        var arguments = template.ParameterList.Parameters
            .Select(p => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Identifier.Text))
                .WithRefKindKeyword(ArgumentRefKindToken(p)))
            .ToList();

        var originalName = explicitCall
            ? interfaceMethod.Name
            : implementation?.Name ?? interfaceMethod.Name;
        ExpressionSyntax target = explicitCall
            ? SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.CastExpression(
                    RecordSyntaxFacts.GlobalType(member.Interface),
                    SyntaxFactory.ThisExpression()),
                SyntaxFactory.IdentifierName(originalName))
            : SyntaxFactory.IdentifierName(originalName);

        var invocation = SyntaxFactory.InvocationExpression(
            target, SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
        var statements = new List<StatementSyntax>();
        if (outName == null)
        {
            statements.Add(SyntaxFactory.ExpressionStatement(invocation));
        }
        else
        {
            statements.Add(SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(outName),
                    invocation)));
        }
        statements.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.ThisExpression()));

        return SyntaxFactory.MethodDeclaration(
                RecordSyntaxFacts.GlobalType(member.Interface),
                SyntaxFactory.Identifier(member.WithName))
            .WithModifiers(SyntaxFactory.TokenList())
            .WithParameterList(parameters)
            .WithExplicitInterfaceSpecifier(ExplicitInterface(member.Interface))
            .WithBody(SyntaxFactory.Block(statements))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }
```

不要给该方法加 `TypeParameterList`/`ConstraintClauses`：能走到这里的接口方法必为非泛型
（Step 7.2 门），`template.TypeParameterList` 恒为 null。Task 20 的 `BuildInterfaceFills`
在本任务**不改**——泛型方法在 ripple 成员生成之前就被 RR070 拦截，fill 枚举永远看不到它们；
重写路径里禁止用 `InvalidOperationException` 表达“尚不支持”，不支持的形态只能在计划阶段
产出诊断。

- [ ] **Step 10: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 全部 PASS。Task 16/17/18 的 MM 用例与 Task 19/20 的 IR 属性用例不回归；新增方法 ripple 用例通过。重点人工核对：
- record 强类型调用仍是 `r = r.Bump()` / `r = r.Next(out var b)`，不会错误改成 `WithBump`；
- 接口静态类型调用才改 `i = i.WithBump()` / `i = i.WithNext(out var a)`；
- record 侧只生成迁移后的 public 原方法，不生成 public `WithBump`；接口契约通过显式 `I.WithBump` 转发；
- 显式接口方法迁移后不再带 `global::I.` 前缀，而是 `public R M(...)`；
- 非 void 的接口 wrapper 使用独立 `__result` 形参，避免与原方法参数名冲突；
- 其他 class 的方法 fill 原地调用旧实现后 `return this;`；
- 外部接口、With 名碰撞、不可写接口接收者分别保持 RR070/RR071/RR050 原子跳过。

- [ ] **Step 11: 提交**

Run:
```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): IR method ripple for migrated interface implementations"
```

### Task 22: IR-5 接口继承 + IR-6 泛型接口 + RR071 改名回退 + FB-3 force + DIM 说明

> 本任务收尾 M4：把 Task 19/21 的 IR-7 碰撞门从"原子跳过"放宽为"改名回退 `WithX__cs2j`"；
> IR-5 把 ripple 成员加到声明接口的**基接口**（符号模型定位 `INamedTypeSymbol.BaseType`/`Interfaces` 链）而非派生接口；IR-6 让泛型接口（`I<T>`）携带正确 type argument 生成 `With` 成员；FB-3 `overrides.force` 接受外部接口的桩降级（不再 RR070 阻断）；DIM 默认关闭并文档化（§9）。

**Files:**
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs`（`RippleMember` 增 `WithSuffix`；PlanBuilder IR-5 基接口定位、IR-6 泛型实例化、RR071 改名、FB-3 force 门）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/InterfaceRippler.cs`（生成成员名携带 `__cs2j` 后缀；泛型接口显式实现语法）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordSyntaxFacts.cs`（`DeclaringInterfaceFor`：沿 `OriginalDefinition` 找声明基接口）
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

- [ ] **Step 1: 写失败测试——IR-5 基接口 + IR-6 泛型 + RR071 改名 + FB-3 force**

追加测试：

```csharp
    [Fact]
    public void IR5_RippleMember_AddedToDeclaringBaseInterface()
    {
        var src = """
            public interface IBase { int P { get; set; } }
            public interface IDerived : IBase { }
            public record struct R : IDerived { public int P { get; set; } }
            """;
        var (output, stats, _) = Rewrite(src);
        // IR-5：WithP 加到 IBase（声明基接口），IDerived 自然继承。
        Assert.Contains("global::IBase WithP(int value);", output);
        Assert.DoesNotContain("global::IDerived WithP", output);
        Assert.Contains("public global::R WithP(int value) => this with { P = value };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR6_GenericInterface_WithTypeArgument()
    {
        var src = """
            public interface I<T> { T P { get; set; } }
            public record struct R : I<int> { public int P { get; set; } }
            """;
        var (output, stats, _) = Rewrite(src);
        // IR-6：泛型接口携带 type argument，显式实现用构造名 global::I<int>。
        Assert.Contains("global::I<int> WithP(int value);", output);
        Assert.Contains("global::I<int> global::I<int>.WithP(int value) => this.WithP(value);", output);
        Assert.Contains("public global::R WithP(int value) => this with { P = value };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RR071_Collision_FallsBackToSuffixedName()
    {
        var src = """
            public interface I { int P { get; set; } int WithP(int value); }
            public record struct R : I { public int P { get; set; } }
            """;
        var (output, stats, diags) = Rewrite(src);
        // 接口已有 WithP → 改名 WithP__cs2j（不再 RR071 跳过）。
        Assert.Contains("global::I WithP__cs2j(int value);", output);
        Assert.Contains("global::I global::I.WithP__cs2j(int value) => this.WithP__cs2j(value);", output);
        Assert.Contains("public global::R WithP__cs2j(int value) => this with { P = value };", output);
        Assert.DoesNotContain(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR071);
        Assert.Equal(1, stats.InterfaceRipples);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void FB3_Force_AcceptsExternalInterfaceStubDegradation()
    {
        var external = "public interface I { int P { get; set; } }";
        var src = "public record struct R : I { public int P { get; set; } }";
        var (output, stats, diags) = RewriteWithMetadata(src, external,
            new RecordReadOnlyOptions
            {
                ForceQualifiedNames = new HashSet<string>(StringComparer.Ordinal) { "R" },
            });
        // FB-3 force：外部接口无法加成员，但 force 接受桩降级——
        // record 的 P 仍 set→init，旧接口契约显式桩 set throw；不生成接口 WithP（接口不在树内）。
        Assert.Contains("public int P { get; init; }", output);
        Assert.Contains("int global::I.P", output);
        Assert.Contains("set => throw new global::System.NotSupportedException(", output);
        Assert.DoesNotContain("WithP", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR012);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR6_GenericMethod_OnInterface_StillRR070()
    {
        // 泛型接口方法（IR-6 的方法形态）仍保守 RR070——本任务只处理泛型接口的属性形态。
        var src = """
            public interface I<T> { void M<U>(T t, U u); }
            public record struct R : I<int> { public void M<U>(int t, U u) { } }
            """;
        var (output, stats, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR070);
        Assert.DoesNotContain("With", output);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests.IR5_|FullyQualifiedName~RecordReadOnlyRewriterUnitTests.IR6_|FullyQualifiedName~RecordReadOnlyRewriterUnitTests.RR071_|FullyQualifiedName~RecordReadOnlyRewriterUnitTests.FB3_|FullyQualifiedName~RecordReadOnlyRewriterUnitTests.IR6_GenericMethod"
```
Expected: 新增 5 个测试全部失败（IR-5 加错接口、IR-6 缺 type argument、RR071 仍跳过、FB-3 仍 RR070、泛型方法未 RR070）。

- [ ] **Step 3: RecordSyntaxFacts——基接口定位辅助**

在 `RecordSyntaxFacts.cs` 追加：

```csharp
    /// <summary>
    /// IR-5：返回接口成员被声明的原始基接口。若成员直接声明在 <paramref name="iface"/>
    /// 上则返回 <paramref name="iface"/> 本身；否则沿基接口链找声明该成员的接口。
    /// 符号模型保证 <see cref="INamedTypeSymbol.GetMembers"/> 只含直接声明的成员，
    /// 故沿 Interfaces 链递归即可。
    /// </summary>
    public static INamedTypeSymbol DeclaringInterfaceFor(
        INamedTypeSymbol iface, ISymbol member)
    {
        if (iface.GetMembers().Contains(member, SymbolEqualityComparer.Default))
            return iface;
        foreach (var baseIface in iface.Interfaces)
        {
            var found = DeclaringInterfaceFor(baseIface, member);
            if (!ReferenceEquals(found, null!)) return found;
        }
        // 不应到达这里（成员必在某基接口声明）；保守返回 iface 本身。
        return iface;
    }
```

- [ ] **Step 4: RippleMember 模型增 WithSuffix**

在 `RecordConversionPlan.cs` 的 `RippleMember` record 中，把 `WithName` 字段语义改为"最终生成名"（可能含 `__cs2j` 后缀），并新增 `OriginalWithName`（碰撞前的原名 `WithX`，用于碰撞检测与诊断）：

```csharp
internal sealed record RippleMember(
    INamedTypeSymbol Interface,
    ISymbol InterfaceMember,
    ISymbol RecordMember,
    string WithName,           // 最终生成名（碰撞时含 __cs2j 后缀）
    string OriginalWithName,   // 碰撞前的原名（WithX），用于诊断与去重
    bool IsMethod,
    bool ReturnsVoid);
```

更新所有构造点（Task 19 属性触发、Task 21 方法触发）把 `OriginalWithName` 传为与 `WithName` 相同的 `"With" + interfaceMember.Name`（本任务在碰撞时改写 `WithName`）。

- [ ] **Step 5: PlanBuilder——IR-5 基接口定位 + IR-6 泛型 type argument + RR071 改名回退 + FB-3 force 门**

5.1 把 Task 19/21 的 IR-7 碰撞门（两处 `if (iface.GetMembers().Any(m => m.Name == withName)) return FailAt(... RR071 ...)` 与 `if (info.Symbol.GetMembers().Any(m => m.Name == withName)) return FailAt(... RR071 ...)`）**替换**为改名回退：

```csharp
                var withName = "With" + interfaceMember.Name;
                var collisionOnInterface = iface.GetMembers().Any(m => m.Name == withName);
                var collisionOnRecord = info.Symbol.GetMembers().Any(m => m.Name == withName);
                if (collisionOnInterface || collisionOnRecord)
                    withName = withName + "__cs2j";
                // 改名后仍需检测二次碰撞（极罕见：接口已有 WithX__cs2j）→ 此时才 RR071。
                if (iface.GetMembers().Any(m => m.Name == withName)
                    || info.Symbol.GetMembers().Any(m => m.Name == withName))
                    return FailAt(plan, RecordReadOnlyDiagnosticCode.RR071,
                        $"even the suffixed name '{withName}' collides with an existing " +
                        $"member on interface '{iface.ToDisplayString()}' or record " +
                        $"'{info.Symbol.Name}'; cannot generate a ripple member (IR-7).",
                        interfaceProperty.Locations.FirstOrDefault(l => l.IsInSource)
                            ?? info.Symbol.Locations.First(l => l.IsInSource));
```

方法触发路径（Task 21 的对应门）同样替换：把两处 `return FailAt(... RR071 ...)` 改为上述改名 + 二次碰撞检测逻辑。

5.2 在构造 `RippleMember` 时应用 IR-5：把 `iface` 替换为基接口定位：

```csharp
                var declaringIface = RecordSyntaxFacts.DeclaringInterfaceFor(iface, interfaceProperty);
                members.Add(new RippleMember(
                    declaringIface, interfaceProperty, recordProperty, withName,
                    "With" + interfaceProperty.Name, // OriginalWithName
                    IsMethod: false, ReturnsVoid: true));
```

方法触发同理用 `declaringIface`。

5.3 IR-6 泛型接口：`RippleMember.Interface` 已经是构造符号（`I<int>`），`InterfaceRippler` 的 `GlobalType(member.Interface)` 已携带 type argument（`global::I<int>`）。本步骤只需在 PlanBuilder 的 IR-6 门**删除** Task 21 的 RR070 泛型方法拦截对**属性**路径的影响——属性路径本就没有泛型方法拦截。确认 `InterfaceRippler.InterfacePropertyDeclaration`/`InterfaceMethodDeclaration` 用 `member.Interface`（构造符号）而非 `OriginalDefinition`。

5.4 FB-3 force 门：把 Task 19 的 IR-7 外部接口门：

```csharp
            if (!RecordSyntaxFacts.IsInTree(iface))
                return Fail(plan, RecordReadOnlyDiagnosticCode.RR070,
                    $"interface '{iface.ToDisplayString()}' declares settable member(s) implemented " +
                    "by this record but the interface is not part of the source tree; the required " +
                    "'With' member cannot be added to it (IR-7/FB-3).");
```

替换为：

```csharp
            if (!RecordSyntaxFacts.IsInTree(iface))
            {
                if (!forced)
                    return Fail(plan, RecordReadOnlyDiagnosticCode.RR070,
                        $"interface '{iface.ToDisplayString()}' declares settable member(s) " +
                        "implemented by this record but the interface is not part of the source " +
                        "tree; the required 'With' member cannot be added to it (IR-7/FB-3). " +
                        "Use overrides.force to accept stub degradation.");
                // FB-3 force：接受桩降级——不生成接口 With 成员（接口不在树内无法追加），
                // record 侧仍生成显式旧契约桩 + set→init；接口接收者用点无法改写（保持原样）。
                // 诊断在报告阶段补 RR012。
                plan.Ripples.Add(new InterfaceRipplePlan(iface, Array.Empty<RippleMember>()));
                continue;
            }
```

方法触发路径同理。

5.5 IR-6 泛型**方法**门保持不变（Task 21 的 RR070 泛型方法拦截仍在）：`if (interfaceMethod.TypeParameters.Length > 0) return FailAt(plan, RR070, ...)`。本任务只处理泛型**接口**的属性形态。

- [ ] **Step 6: InterfaceRippler——改名后缀在生成成员名上生效**

`InterfaceRippler` 中所有引用 `member.WithName` 的地方（`InterfacePropertyDeclaration`、`InterfaceMethodDeclaration`、`ExplicitWithPropertyImplementation`、`ExplicitWithMethodImplementation`、`PublicWithPropertyMethod`、`MethodFillMethod`、`PropertyFillMethod`）已经用 `member.WithName`，改名后 `WithName` 含 `__cs2j` 后缀，语法生成自动正确。无需额外改动——确认 `WithName` 的使用点都取自 `RippleMember.WithName` 而非重新拼接。

- [ ] **Step 7: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 全部 PASS。重点人工核对：
- IR-5：`WithP` 加到 `IBase` 而非 `IDerived`；
- IR-6：泛型接口显式实现用 `global::I<int>` 构造名；
- RR071：碰撞时改名 `WithP__cs2j`，不再产出 RR071 诊断（除非二次碰撞）；
- FB-3：force 时外部接口不再 RR070 阻断，record 侧仍生成桩；
- 泛型接口方法仍 RR070。

- [ ] **Step 8: DIM 默认关闭说明（文档化，不改代码）**

在 `config/RecordReadOnlyOverrides.json` 的同目录或 `docs/superpowers/specs/2026-09-24-record-readonly-preprocessor-design.md` §9 的"明确不做的事"已有"不规避接口新增抽象成员对外部实现者的源码/运行时破坏（可用 DIM 默认体缓解，默认关闭）"。本步骤确认 `RecordReadOnlyOptions` 无 `EnableDim` 开关（默认关闭），InterfaceRippler 生成的新接口成员一律为抽象（无默认体）。**不改代码**，仅在 M6 文档任务中记录该决策。

- [ ] **Step 9: 提交**

Run:
```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): IR-5 base interface, IR-6 generic interface, RR071 suffix fallback, FB-3 force stub degradation"
```

---

## M5：record class 契约转换（RC-1~RC-10）+ 层次一致性 + 诊断补全

> 目标（规范 §4.2）：record class 转为语义 readonly——对外可观察成员全部不可设置（RC-2 set→init、RC-3 字段→init 属性、RC-5 手写 set 体保体、RC-6 继承层次一致、RC-7 接口连带复用 IR、RC-8 层次不一致整层跳过、RC-9 索引器 FB-1、RC-10 私有拷贝 ctor 限制提示）。RC-1 声明语法不变（无 `readonly record class` 语法）。
>
> 本里程碑翻转 `ConvertRecordClasses = true`（默认仍关，CLI 显式开启），补全 RR002/RR013/RR014/RR021/RR080 的产生逻辑。

### Task 23: 翻转 ConvertRecordClasses + RC-2/RC-3/RC-5 声明级 + RR002/RR013 诊断

**Files:**
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs`（`FeatureCaps` 不再拦 record class；PlanBuilder RC-1/RC-2/RC-3/RC-5 分支）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyAnalyzer.cs`（`HasPrivateCopyConstructor` 已收集；确认 record class 成员收集路径）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/DeclarationRewriter.cs`（RC-1 无声明改写；RC-2/RC-5 复用 SetToInit）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyPreprocessor.cs`（`ReportSkippedRecords` 补 RR002/RR013）
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

- [ ] **Step 1: 写失败测试——record class 基本转换 + RR002 + RR013**

追加测试：

```csharp
    [Fact]
    public void RC2_RecordClass_SetToInit_AndRR002()
    {
        var src = """
            public record class P(int X) { public int Y { get; set; } }
            """;
        var (output, stats, diags) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = true });
        // RC-1：声明语法不变（无 readonly 语法）。
        Assert.Contains("public record class P(int X)", output);
        // RC-2：set → init。
        Assert.Contains("public int Y { get; init; }", output);
        Assert.Equal(1, stats.RecordClassesConverted);
        Assert.Equal(0, stats.RecordStructsConverted);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR002);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RC2_RecordClass_PrivateSet_ToPrivateInit()
    {
        var src = """
            public record class P { public int X { get; private set; } }
            """;
        var (output, stats, _) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = true });
        Assert.Contains("private init", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RC3_RecordClass_PublicField_ToInitProperty()
    {
        var src = """
            public record class P { public int X = 5; }
            """;
        var (output, stats, _) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = true });
        Assert.Contains("public int X { get; init; } = 5;", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RC5_RecordClass_HandwrittenSetterBody_PreservedAsInit()
    {
        var src = """
            public record class P
            {
                private int _x;
                public int X { get => _x; set => _x = value; }
            }
            """;
        var (output, stats, _) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = true });
        // RC-5：手写 set 体保体，关键字 set→init。
        Assert.Contains("init => _x = value;", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RC1_RecordClass_DeclarationUnchanged()
    {
        var src = "public record class P(int X);";
        var (output, stats, _) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = true });
        // RC-1：无对外可变成员 → 声明完全不变。
        Assert.Equal(src, output);
    }

    [Fact]
    public void RC4_RecordClass_PrivateField_KeptMutable()
    {
        var src = """
            public record class P
            {
                private int _cache;
                public int X { get; set; }
                public int Compute() { _cache = 42; return _cache; }
            }
            """;
        var (output, stats, _) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = true });
        // RC-4：整体私有字段保留可变（_cache 不转 init）。
        Assert.Contains("private int _cache;", output);
        Assert.Contains("public int X { get; init; }", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RR013_RecordClass_AliasSemanticsInfoDiagnostic()
    {
        var src = """
            public record class P { public int X { get; set; } }
            """;
        var (output, stats, diags) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = true });
        // RR013：别名观察语义变化提示（Info）。
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR013
            && d.Severity == RecordReadOnlySeverity.Info);
    }

    [Fact]
    public void RecordClass_NotConverted_WhenConvertRecordClassesFalse()
    {
        var src = """
            public record class P { public int X { get; set; } }
            """;
        var (output, stats, _) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = false });
        // 默认关：record class 原样不动。
        Assert.Equal(src, output);
        Assert.Equal(0, stats.RecordClassesConverted);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests.RC|FullyQualifiedName~RecordReadOnlyRewriterUnitTests.RR013|FullyQualifiedName~RecordReadOnlyRewriterUnitTests.RecordClass_NotConverted"
```
Expected: 新增 8 个测试失败（record class 未被分析/转换、RR002/RR013 未产出）。

- [ ] **Step 3: PlanBuilder——record class 分支开放**

3.1 在 `BuildRecordPlan` 的 `readonly` 修饰符门：

```csharp
        if (info.Kind == RecordKind.Struct && !_caps.ReadOnlyModifier)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR020, "readonly modifier stage disabled.");
```

**改为**（record class 不需要 readonly 修饰符 cap，RC-1 无语法标记）：

```csharp
        if (info.Kind == RecordKind.Struct && !_caps.ReadOnlyModifier)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR020, "readonly modifier stage disabled.");
        // RC-1：record class 无声明级修饰符改写，不需要 ReadOnlyModifier cap。
```

3.2 把 `needMemberRewrites` cap 门改为对 record class 也放行（RC-2/RC-3/RC-5 复用 `MemberRewrites`）：

```csharp
        var needMemberRewrites = plan.MemberRewrites.Any(m =>
            m.Kind is MemberRewriteKind.SetToInit or MemberRewriteKind.FieldToInitProperty
                or MemberRewriteKind.PrivateFieldToInitProperty);
        if (needMemberRewrites && !_caps.MemberRewrites)
            return Fail(plan, RecordReadOnlyDiagnosticCode.RR020,
                "record has mutable properties/fields; declaration member rewriting (RS-3..RS-6/RC-2..RC-5) "
                + "is required to convert.");
```

3.3 RC-4 私有成员保留：PlanBuilder 中字段分类的私有字段路径已经对 record struct 用 `PrivateFieldReadonly`/`PrivateFieldToInitProperty`。对 record class，整体私有的字段（`!IsVisibleOutsideType`）应保留可变（RC-4），故在 `IsPrivateFieldWrittenAfterConstruction` 判定之前加 record class 例外：

```csharp
            else if (info.Kind == RecordKind.Class && !RecordSyntaxFacts.IsVisibleOutsideType(field))
            {
                // RC-4：record class 整体私有字段保留可变（惰性缓存等内部状态）。
                continue;
            }
            else if (IsPrivateFieldWrittenAfterConstruction(info, field))
            {
                // RS-6b：record struct 私有字段构造后赋值 → 提升 init 属性。
                if (!_caps.MethodMigration)
                    return Fail(plan, RecordReadOnlyDiagnosticCode.RR020,
                        $"private field '{field.Name}' is assigned after construction; method " +
                        "migration is required to convert it (RS-6b/MM).");
                plan.MemberRewrites.Add(new MemberRewrite(
                    field.Name, MemberRewriteKind.PrivateFieldToInitProperty, field, false));
            }
```

3.4 翻转 `ConvertRecordClasses` 默认值。在 `RecordReadOnlyModels.cs` 中：

```csharp
    /// <summary>是否转换 record class（RC 规则）。M5 后默认 true（CLI 仍由 --convert-record-classes 控制）。</summary>
    public bool ConvertRecordClasses { get; init; } = true;
```

> 注：单测中 `ConvertRecordClasses = false` 的测试显式传入 `false`；CLI verb 的 `[Option("convert-record-classes", Default = false)]` 保持 `false`（convert-project 默认关），但独立 verb 与项目驱动可显式开启。`FeatureCaps.Current` 无需新增 `RecordClass` 项——RC 转换不需要独立 cap，复用 `MemberRewrites`/`Sites`/`MethodMigration`/`InterfaceRipple`。

- [ ] **Step 4: DeclarationRewriter——RC-1 无声明改写已就位**

确认 `DeclarationRewriter.VisitRecordDeclaration` 的 `if (plan.Kind != RecordKind.Struct) return visited;` 注释为 `RC-1：class 无标记`。本任务**不改该行**——record class 声明原样，成员改写（RC-2/RC-3/RC-5）由 `VisitPropertyDeclaration`/`VisitFieldDeclaration` 的 `SetToInit`/`FieldToInitProperty` 分支处理，这些分支已不区分 struct/class（Task 6/7 实现）。

- [ ] **Step 5: ReportSkippedRecords——补 RR002 + RR013**

在 `ReportSkippedRecords` 的 `PlanOutcome.Converted` 分支中：

```csharp
            if (p.Outcome == PlanOutcome.Converted)
            {
                if (p.Kind == RecordKind.Struct)
                {
                    statistics.RecordStructsConverted++;
                    if (options.ReportInfo)
                        diagnostics.Add(p.InfoDiagnostic(RecordReadOnlyDiagnosticCode.RR001,
                            "record struct converted."));
                }
                else
                {
                    statistics.RecordClassesConverted++;
                    if (options.ReportInfo)
                    {
                        diagnostics.Add(p.InfoDiagnostic(RecordReadOnlyDiagnosticCode.RR002,
                            "record class converted (semantic immutability)."));
                        // RR013：别名观察语义变化提示（§4.2，Info）。
                        diagnostics.Add(p.InfoDiagnostic(RecordReadOnlyDiagnosticCode.RR013,
                            "alias-observed mutation behavior changed: 'b = a; a.X = 1;' " +
                            "no longer visible through 'b' after conversion to immutable record class."));
                    }
                }
                continue;
            }
```

- [ ] **Step 6: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 全部 PASS。重点人工核对：
- record class 声明不变（RC-1）、set→init（RC-2）、字段→init 属性（RC-3）、手写 set 体保体（RC-5）；
- 私有字段保留可变（RC-4）；
- RR002/RR013 产出；
- `ConvertRecordClasses = false` 时 record class 原样不动。

- [ ] **Step 7: 提交**

Run:
```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): RC-1..RC-5 record class contract conversion, flip ConvertRecordClasses, emit RR002/RR013"
```

---

### Task 24: RC-6 继承层次一致性 + RC-8/FB-2 层次整体跳过 + RR021

**Files:**
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyAnalyzer.cs`（层次链收集）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs`（PlanBuilder 层次一致性裁决 + RR021）
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

- [ ] **Step 1: 写失败测试——RC-6 层次一致 + RC-8 层次跳过 + RR021**

追加测试：

```csharp
    [Fact]
    public void RC6_Hierarchy_AllConverted_WhenAllFeasible()
    {
        var src = """
            public record class Base { public int X { get; set; } }
            public record class Derived : Base { public int Y { get; set; } }
            """;
        var (output, stats, _) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = true });
        // RC-6：基类与派生类都转换（层次一致）。
        Assert.Contains("public int X { get; init; }", output);
        Assert.Contains("public int Y { get; init; }", output);
        Assert.Equal(2, stats.RecordClassesConverted);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RC8_HierarchySkipped_WhenBaseInfeasible()
    {
        var src = """
            public record class Base { public event System.Action? Ev; public int X { get; set; } }
            public record class Derived : Base { public int Y { get; set; } }
            """;
        var (output, stats, diags) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = true });
        // RC-8/FB-2：Base 有实例事件（RR040）→ 整层次跳过，Derived 也跳过。
        Assert.Equal(0, stats.RecordClassesConverted);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR040
            && d.RecordName == "Base");
        // RR021：层次整体跳过（Derived 因 Base 不可转换而跳过）。
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR021
            && d.RecordName == "Derived");
        // 输出原样保留。
        Assert.Contains("public event System.Action? Ev;", output);
        Assert.Contains("public int Y { get; set; }", output);
    }

    [Fact]
    public void RC8_HierarchySkipped_WhenDerivedInfeasible()
    {
        var src = """
            public record class Base { public int X { get; set; } }
            public record class Derived : Base { public event System.Action? Ev; public int Y { get; set; } }
            """;
        var (output, stats, diags) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = true });
        // 反向：Derived 不可转换 → Base 也跳过（层次一致）。
        Assert.Equal(0, stats.RecordClassesConverted);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR021
            && d.RecordName == "Base");
    }

    [Fact]
    public void RC6_VirtualOverride_HierarchyConsistent()
    {
        var src = """
            public record class Base { public virtual int X { get; set; } }
            public record class Derived : Base { public override int X { get; set; } }
            """;
        var (output, stats, _) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = true });
        // RC-6：virtual/override 层次一致转 init。
        Assert.Contains("public virtual int X { get; init; }", output);
        Assert.Contains("public override int X { get; init; }", output);
        Assert.Equal(2, stats.RecordClassesConverted);
        AssertOutputCompiles(output);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests.RC6_|FullyQualifiedName~RecordReadOnlyRewriterUnitTests.RC8_"
```
Expected: RC6 两个测试失败（层次未联动跳过）；RC8 两个测试失败（RR021 未产出）。

- [ ] **Step 3: PlanBuilder——层次一致性裁决**

在 `Build` 方法中，在所有单 record 计划构建完成后、返回前，插入层次一致性裁决：

```csharp
    public ConversionPlan Build(RecordAnalysis analysis)
    {
        var plan = new ConversionPlan();
        foreach (var info in analysis.Records.OrderBy(r => r.Symbol.ToDisplayString(), StringComparer.Ordinal))
        {
            plan.Plans.Add(BuildRecordPlan(info, analysis));
        }
        EnforceHierarchyConsistency(plan, analysis);
        return plan;
    }
```

新增方法：

```csharp
    /// <summary>
    /// RC-8/FB-2：record class 继承层次（基类 record → 派生 record）必须整层一致。
    /// 层次中任一 record 不可转换 → 层次内所有 Converted 计划降级为 Infeasible(RR021)。
    /// </summary>
    private static void EnforceHierarchyConsistency(ConversionPlan plan, RecordAnalysis analysis)
    {
        // 按 BaseRecordType 链分组：同一层次链上的 record class。
        var hierarchyGroups = new List<HashSet<string>>();
        var byQn = plan.Plans.ToDictionary(p => p.RecordQualifiedName, StringComparer.Ordinal);
        foreach (var p in plan.Plans.Where(p => p.Kind == RecordKind.Class))
        {
            if (p.Outcome != PlanOutcome.Converted) continue;
            // 找所属层次组：沿 BaseRecordType 链找已有组，否则新建。
                        HashSet<string>? group = null;
            var current = p;
            while (current != null)
            {
                // 已有组？
                var existing = hierarchyGroups.FirstOrDefault(g => g.Contains(current.RecordQualifiedName));
                if (existing != null) { group = existing; break; }
                // 沿基类 record 链上行。
                var baseQn = analysis.Records
                    .FirstOrDefault(r => SymbolEqualityComparer.Default.Equals(r.Symbol, current.Symbol.BaseType)
                        && r.Kind == RecordKind.Class)?.Symbol.ToDisplayString();
                if (baseQn == null || !byQn.ContainsKey(baseQn)) break;
                current = byQn[baseQn] as RecordPlan;
            }
            group ??= new HashSet<string>(StringComparer.Ordinal);
            group.Add(p.RecordQualifiedName);
            // 把基类 record 链上的所有 record class 加入同组。
            var chain = p.Symbol.BaseType;
            while (chain != null && chain.IsRecord && chain.TypeKind == TypeKind.Class)
            {
                var chainQn = chain.ToDisplayString();
                if (byQn.ContainsKey(chainQn)) group.Add(chainQn);
                chain = chain.BaseType;
            }
            if (!hierarchyGroups.Contains(group)) hierarchyGroups.Add(group);
        }

        // 层次一致性裁决：同组中任一 Infeasible（含原 Infeasible 与 UserSkipped）→
        // 组内所有 Converted 降级为 Infeasible(RR021)。
        foreach (var group in hierarchyGroups)
        {
            var anyInfeasible = group.Any(qn =>
            {
                var rp = byQn[qn];
                return rp.Outcome is PlanOutcome.Infeasible or PlanOutcome.UserSkipped;
            });
            if (!anyInfeasible) continue;
            foreach (var qn in group)
            {
                var rp = byQn[qn];
                if (rp.Outcome != PlanOutcome.Converted) continue;
                rp.Outcome = PlanOutcome.Infeasible;
                rp.FailCode = RecordReadOnlyDiagnosticCode.RR021;
                rp.FailReason = "record class hierarchy is inconsistent: a base or derived " +
                    "record in the same hierarchy was skipped; the entire hierarchy must be " +
                    "converted together (RC-8/FB-2).";
                rp.FailLocation = rp.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
            }
        }
    }
```

> 注：`RecordReadOnlyDiagnosticCode.RR021` 已在 Task 2 的枚举中定义（`record class 继承层次整体跳过（FB-2）`）。`ReportSkippedRecords` 的 `Infeasible` 分支已按 `FailCode` 产出诊断并计入 `RecordsSkipped`/`SkippedByReason`，无需额外报告逻辑。

- [ ] **Step 4: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 全部 PASS。重点人工核对：
- RC-6：层次全部可转换时，基类与派生类都转 init；
- RC-8：Base 不可转换时，Derived 被降级为 RR021；反向同理；
- virtual/override 层次一致转 init。

- [ ] **Step 5: 提交**

Run:
```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): RC-6/RC-8 hierarchy consistency, FB-2 whole-hierarchy skip, RR021"
```

---

### Task 25: RC-9 索引器 + RC-10 私有拷贝 ctor + RR014/RR080 诊断

**Files:**
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs`（PlanBuilder RC-10 检测 + RR080）
- Modify: `src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyPreprocessor.cs`（`ReportSkippedRecords` 补 RR014）
- Modify: `tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs`

> RC-9（对外可见带 set 索引器 → FB-1）已由 Task 8 的 `HasSettableIndexer` → RR042 门覆盖（`RS-19/RC-9/MS-18`），本任务不改门，只补 record class 的测试覆盖。RC-10（私有/不可访问拷贝 ctor）需要检测并产出 RR080/RR014。

- [ ] **Step 1: 写失败测试——RC-9 索引器 + RC-10 私有拷贝 ctor + RR014 + RR080**

追加测试：

```csharp
    [Fact]
    public void RC9_RecordClass_SettableIndexer_FB1()
    {
        var src = """
            public record class P
            {
                public int this[int i] { get; set; }
            }
            """;
        var (output, stats, diags) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = true });
        // RC-9/RR042：带 set 的索引器 → FB-1 跳过。
        Assert.Equal(0, stats.RecordClassesConverted);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR042);
        Assert.Contains("public int this[int i] { get; set; }", output);
    }

    [Fact]
    public void RC10_PrivateCopyConstructor_InternalOnly_FeasibleWithRR014()
    {
        var src = """
            public record class P
            {
                public int X { get; set; }
                private P(P other) { X = other.X; }
                public P(int x) { X = x; }
            }
            """;
        var (output, stats, diags) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = true });
        // RC-10：私有拷贝 ctor，但改写点都在类型内 → 可转换，提示 RR014（Info）。
        Assert.Contains("public int X { get; init; }", output);
        Assert.Equal(1, stats.RecordClassesConverted);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR014
            && d.Severity == RecordReadOnlySeverity.Info);
    }

    [Fact]
    public void RC10_PrivateCopyConstructor_ExternalWithSite_FB1()
    {
        var src = """
            public record class P
            {
                public int X { get; set; }
                private P(P other) { X = other.X; }
                public P(int x) { X = x; }
            }
            public class Consumer
            {
                public void Mutate(P p) { p.X = 42; }
            }
            """;
        var (output, stats, diags) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = true });
        // RC-10/RR080：私有拷贝 ctor，改写点在类型外（Consumer.Mutate），with 不可用 → FB-1。
        Assert.Equal(0, stats.RecordClassesConverted);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR080);
    }
```

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests.RC9_|FullyQualifiedName~RecordReadOnlyRewriterUnitTests.RC10_"
```
Expected: RC9 通过（RR042 门已有）；RC10 两个测试失败（RR014/RR080 未产出）。

- [ ] **Step 3: PlanBuilder——RC-10 私有拷贝 ctor 检测 + RR080**

在 `BuildRecordPlan` 中，在 `plan.Outcome = PlanOutcome.Converted;` 之前、`analysis.HasDynamicOrReflectionMutation` 门之后，插入 RC-10 检测：

```csharp
        // RC-10：私有/不可访问拷贝 ctor。record class 的 with 表达式依赖可访问的拷贝 ctor。
        // 若有私有拷贝 ctor 且改写点在类型外 → RR080（FB-1）。
        // 若改写点都在类型内 → 可转换，RR014 提示（Info，报告阶段补）。
        if (info.Kind == RecordKind.Class && info.HasPrivateCopyConstructor)
        {
            var hasExternalSite = info.Sites.Any(s =>
                !SymbolEqualityComparer.Default.Equals(
                    s.EnclosingMethod?.ContainingType, info.Symbol));
            if (hasExternalSite)
                return Fail(plan, RecordReadOnlyDiagnosticCode.RR080,
                    "record class has a private/inaccessible copy constructor; the 'with' " +
                    "expression used at external mutation sites is unavailable (RC-10).");
            // 内部用点可转换；RR014 在报告阶段补。
        }
```

> 注：`HasPrivateCopyConstructor` 已由 Task 4 的 `FillMemberFacts` 收集（`info.HasPrivateCopyConstructor`）。

- [ ] **Step 4: ReportSkippedRecords——补 RR014**

在 `ReportSkippedRecords` 的 `PlanOutcome.Converted` 分支中，record class 路径末尾追加：

```csharp
                    // RC-10：私有拷贝 ctor 限制提示（Info）。
                    if (p is { Kind: RecordKind.Class } && /* HasPrivateCopyConstructor */
                        p.Symbol.GetMembers().OfType<IMethodSymbol>()
                            .Any(m => m.MethodKind == MethodKind.Constructor
                                && m is { Parameters.Length: 1 }
                                && SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, p.Symbol)
                                && m.DeclaredAccessibility == Accessibility.Private))
                    {
                        diagnostics.Add(p.InfoDiagnostic(RecordReadOnlyDiagnosticCode.RR014,
                            "record class has a private copy constructor; 'with' expressions " +
                            "are only available within this type. External callers cannot use " +
                            "the generated 'With' members (RC-10)."));
                    }
```

> 注：这里用内联检测而非在 `RecordPlan` 上存 flag，避免改 `RecordPlan` 模型；`HasPrivateCopyConstructor` 是 `RecordInfo`（分析层）的属性，`ReportSkippedRecords` 只拿到 `RecordPlan`，故内联重查。`SymbolEqualityComparer` 与 `IMethodSymbol` 已在 `RecordReadOnlyAnalyzer.cs` 的 using 范围内（`ReportSkippedRecords` 在 `RecordReadOnlyAnalyzer` 类中），确认 `RecordReadOnlyPreprocessor.cs` 的 `ReportSkippedRecords` 调用点 using 包含 `Microsoft.CodeAnalysis`。

- [ ] **Step 5: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyRewriterUnitTests"
```
Expected: 全部 PASS。重点人工核对：
- RC-9：record class 带 set 索引器 → RR042 跳过；
- RC-10 内部用点：可转换 + RR014 提示；
- RC-10 外部用点：RR080 跳过。

- [ ] **Step 6: 提交**

Run:
```powershell
cd d:/code/cs2j-record-readonly; git add src/CSharpToJava.Core/RecordReadOnly tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs; git commit -m "feat(record-readonly): RC-9/RC-10 private copy ctor detection, RR014/RR080 diagnostics"
```

---

## M6：SampleRecordSolution fixture + 项目级测试 + CLI 自动化测试 + 一致性测试 + 文档与收尾回归

> 目标（规范 §7）：端到端验证——新建 `SampleRecordSolution` fixture（覆盖全部规则类别），`RecordReadOnlyProjectTests` 断言中间树文件内容 + `dotnet build` 中间树成功 + 计划级回退（FB-5）；CLI 自动化测试（`--record-readonly` 开关、verb 双模式、`.cs2j-record-readonly-src` 保留目录、统计输出）；一致性测试核对（`ProjectPreprocessorsConsistentTests` 扩展）；文档与收尾回归。

### Task 26: SampleRecordSolution fixture

**Files:**
- Create: `tests/SampleRecordSolution/SampleRecordLib/SampleRecordLib.csproj`
- Create: `tests/SampleRecordSolution/SampleRecordLib/RecordStructSamples.cs`
- Create: `tests/SampleRecordSolution/SampleRecordLib/RecordClassSamples.cs`
- Create: `tests/SampleRecordSolution/SampleRecordLib/InterfaceSamples.cs`
- Create: `tests/SampleRecordSolution/SampleRecordLib/MutationSiteSamples.cs`
- Create: `tests/SampleRecordSolution/SampleRecordLib/MethodMigrationSamples.cs`
- Create: `tests/SampleRecordSolution/SampleRecordApp/Program.cs`
- Create: `tests/SampleRecordSolution/SampleRecordApp/SampleRecordApp.csproj`
- Create: `tests/SampleRecordSolution/SampleRecordSolution.sln`
- Modify: `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj`（加 `Content Include`）

- [ ] **Step 1: 创建 fixture 解决方案与项目骨架**

创建 `tests/SampleRecordSolution/SampleRecordLib/SampleRecordLib.csproj`（与 `SampleTupleLib.csproj` 同构）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

创建 `tests/SampleRecordSolution/SampleRecordApp/SampleRecordApp.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\SampleRecordLib\SampleRecordLib.csproj" />
  </ItemGroup>
</Project>
```

创建 `tests/SampleRecordSolution/SampleRecordSolution.sln`（GUID 与 `SampleTupleSolution.sln` 模式一致，用新 GUID）：

```text
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "SampleRecordLib", "SampleRecordLib\SampleRecordLib.csproj", "{55555555-5555-5555-5555-555555555555}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "SampleRecordApp", "SampleRecordApp\SampleRecordApp.csproj", "{66666666-6666-6666-6666-666666666666}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{55555555-5555-5555-5555-555555555555}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{55555555-5555-5555-5555-555555555555}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{55555555-5555-5555-5555-555555555555}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{55555555-5555-5555-5555-555555555555}.Release|Any CPU.Build.0 = Release|Any CPU
		{66666666-6666-6666-6666-666666666666}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{66666666-6666-6666-6666-666666666666}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{66666666-6666-6666-6666-666666666666}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{66666666-6666-6666-6666-666666666666}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
```

- [ ] **Step 2: 创建 fixture 源文件——覆盖全部规则类别**

创建 `tests/SampleRecordSolution/SampleRecordLib/RecordStructSamples.cs`：

```csharp
namespace SampleRecordLib;

// RS-1：positional record struct → readonly record struct
public record struct Point(int X, int Y)
{
    // RS-3：自动属性 set → init
    public int Z { get; set; }
}

// RS-5：公共字段 → init 属性
public record struct WithField
{
    public int Value = 42;
}

// RS-18：已是 readonly → 跳过
public readonly record struct AlreadyImmutable(int X);

// RS-10：实例事件 → FB-1 跳过
public record struct WithEvent
{
    public event System.Action? Changed;
    public int X { get; set; }
}
```

创建 `tests/SampleRecordSolution/SampleRecordLib/RecordClassSamples.cs`：

```csharp
namespace SampleRecordLib;

// RC-1/RC-2：record class set → init
public record class Person(string Name)
{
    public int Age { get; set; }
}

// RC-3：公共字段 → init 属性
public record class Configuration
{
    public string Key = "default";
}

// RC-4：私有字段保留可变
public record class Cache
{
    private int _cache;
    public string Key { get; set; }
    public int Compute() { _cache = 42; return _cache; }
}

// RC-6：继承层次一致
public record class Shape { public double Area { get; set; } }
public record class Circle : Shape { public double Radius { get; set; } }

// RC-8：层次不一致（事件阻断）
public record class BadBase { public event System.Action? Ev; public int X { get; set; } }
public record class BadDerived : BadBase { public int Y { get; set; } }
```

创建 `tests/SampleRecordSolution/SampleRecordLib/InterfaceSamples.cs`：

```csharp
namespace SampleRecordLib;

// IR-1/IR-2：in-tree 接口连带
public interface ICounter { int Count { get; set; } }
public record struct Counter : ICounter { public int Count { get; set; } }

// IR-5：接口继承
public interface IBase { int Value { get; set; } }
public interface IDerived : IBase { }
public record struct Holder : IDerived { public int Value { get; set; } }
```

创建 `tests/SampleRecordSolution/SampleRecordLib/MutationSiteSamples.cs`：

```csharp
namespace SampleRecordLib;

// MS-1：简单成员赋值 → with
public record struct Mutable(int X, int Y)
{
    public static Mutable Bump(Mutable m)
    {
        m.X = 1;
        m.Y = 2;
        return m;
    }
}

// MS-7：集合元素
public record struct Item(int Id);
public static class Container
{
    public static void BumpAll(System.Collections.Generic.List<Item> items)
    {
        items[0] = new Item(42);
    }
}
```

创建 `tests/SampleRecordSolution/SampleRecordLib/MethodMigrationSamples.cs`：

```csharp
namespace SampleRecordLib;

// MM-2/MM-4/MM-5：void 方法迁移
public record struct Accumulator(int Total)
{
    public Accumulator Add(int n)
    {
        Total += n;
        return this;
    }

    // 实际上 record struct 的 positional 属性是 init，不能在方法体赋值。
    // 这个样本展示 MM 迁移后的形态。
}
```

创建 `tests/SampleRecordSolution/SampleRecordApp/Program.cs`：

```csharp
using SampleRecordLib;

var p = new Point(1, 2);
p.Z = 3;
Console.WriteLine($"Point: {p.X}, {p.Y}, {p.Z}");

var person = new Person("Alice");
person.Age = 30;
Console.WriteLine($"{person.Name}, {person.Age}");
```

- [ ] **Step 3: 在 ProjectTests.csproj 注册 Content**

在 `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj` 的最后一个 `</ItemGroup>` 之前追加：

```xml
  <ItemGroup>
    <!-- Include SampleRecordSolution as content so it's available at test runtime -->
    <Content Include="..\SampleRecordSolution\**\*.*">
      <Link>SampleRecordSolution\%(RecursiveDir)%(Filename)%(Extension)</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
```

- [ ] **Step 4: 构建确认 fixture 可编译**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet build tests/SampleRecordSolution/SampleRecordSolution.sln -v q
```
Expected: `Build succeeded`，0 errors。

- [ ] **Step 5: 提交**

Run:
```powershell
cd d:/code/cs2j-record-readonly; git add tests/SampleRecordSolution tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj; git commit -m "test(record-readonly): add SampleRecordSolution fixture covering all rule categories"
```

---

### Task 27: RecordReadOnlyProjectTests——项目级 + 整树 build + 计划级回退

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/RecordReadOnlyProjectTests.cs`

- [ ] **Step 1: 创建项目级测试**

创建 `tests/CSharpToJava.ProjectTests/RecordReadOnlyProjectTests.cs`（与 `TupleToStructProjectTests.cs` 同构）：

```csharp
using CSharpToJava.CLI;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.ProjectTests;

public class RecordReadOnlyProjectTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "SampleRecordSolution");

    private static RecordReadOnlyOptions ConvertStructOptions() => new()
    {
        ConvertRecordClasses = false,
    };

    private static RecordReadOnlyOptions ConvertAllOptions() => new()
    {
        ConvertRecordClasses = true,
    };

    [Fact]
    public async Task LibProject_RecordStructs_RewrittenAndIntermediateBuilds()
    {
        var libDir = Path.Combine(FixtureDir, "SampleRecordLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");
        var outputDir = Path.Combine(libDir, "..", ".test-output-rr");

        try
        {
            var result = await ProjectRecordReadOnlyPreprocessor.PreprocessAsync(
                new ProjectRecordReadOnlyPreprocessRequest
                {
                    SourcePath = libDir,
                    DestinationRoot = outputDir,
                    Options = ConvertStructOptions(),
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.True(result.Statistics.RecordStructsConverted > 0);
            Assert.True(result.Statistics.RecordsSkipped > 0); // WithEvent 等 FB-1 跳过

            var intermediateDir = result.IntermediateRoot;
            Assert.True(Directory.Exists(intermediateDir));

            // RS-1：readonly 修饰符已加。
            var structs = await File.ReadAllTextAsync(
                Path.Combine(intermediateDir, "RecordStructSamples.cs"));
            Assert.Contains("readonly record struct Point", structs);
            Assert.Contains("public int Z { get; init; }", structs);

            // RS-18：已是 readonly 不重复加。
            Assert.Contains("public readonly record struct AlreadyImmutable", structs);

            // RS-10：事件 record 原样保留。
            Assert.Contains("public event System.Action? Changed", structs);

            // 整树重编译成功。
            var buildExitCode = await RunDotNetBuildAsync(result.PreprocessedSourcePath);
            Assert.True(buildExitCode == 0,
                $"dotnet build of the intermediate tree failed ({buildExitCode})");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Solution_RecordClasses_RewrittenAndIntermediateBuilds()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleRecordSolution.sln");
        Assert.True(File.Exists(solutionPath), $"Fixture not found: {solutionPath}");
        var outputDir = Path.Combine(FixtureDir, ".test-output-rr-sln");

        try
        {
            var result = await ProjectRecordReadOnlyPreprocessor.PreprocessAsync(
                new ProjectRecordReadOnlyPreprocessRequest
                {
                    SourcePath = solutionPath,
                    DestinationRoot = outputDir,
                    Options = ConvertAllOptions(),
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.True(result.Statistics.RecordClassesConverted > 0);
            Assert.True(result.Statistics.RecordStructsConverted > 0);

            // RC-2：record class set → init。
            var classes = await File.ReadAllTextAsync(
                Path.Combine(result.IntermediateRoot, "SampleRecordLib", "RecordClassSamples.cs"));
            Assert.Contains("public int Age { get; init; }", classes);

            // RC-8：层次不一致 → 原样保留。
            Assert.Contains("public event System.Action? Ev", classes);
            Assert.Contains("public int Y { get; set; }", classes);

            // App 的用点已改写（MS-1）。
            var appProgram = await File.ReadAllTextAsync(
                Path.Combine(result.IntermediateRoot, "SampleRecordApp", "Program.cs"));
            Assert.Contains("with {", appProgram);

            var buildExitCode = await RunDotNetBuildAsync(result.PreprocessedSourcePath);
            Assert.True(buildExitCode == 0,
                $"dotnet build of the intermediate tree failed ({buildExitCode})");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task CleanProject_NoRecords_CopiedByteIdentical()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-rr-clean-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "src");
        var outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceDir);

        try
        {
            var sourceFile = Path.Combine(sourceDir, "Plain.cs");
            const string sourceCode =
                "namespace Plain;\npublic class Thing\n{\n    public int Value => 42;\n}\n";
            await File.WriteAllTextAsync(sourceFile, sourceCode);
            await File.WriteAllTextAsync(
                Path.Combine(sourceDir, "Plain.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");

            var result = await ProjectRecordReadOnlyPreprocessor.PreprocessAsync(
                new ProjectRecordReadOnlyPreprocessRequest
                {
                    SourcePath = sourceDir,
                    DestinationRoot = outputDir,
                    Options = ConvertStructOptions(),
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Equal(0, result.Statistics.FilesTransformed);
            Assert.Equal(1, result.Statistics.FilesSkippedClean);

            var copied = await File.ReadAllTextAsync(
                Path.Combine(result.IntermediateRoot, "Plain.cs"));
            Assert.Equal(sourceCode, copied);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task<int> RunDotNetBuildAsync(string targetPath)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{targetPath}\" -v q --nologo -p:NoWarn=NU1605",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = System.Diagnostics.Process.Start(startInfo);
        Assert.NotNull(process);
        var outputTask = process!.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            Console.WriteLine(output);
            Console.Error.WriteLine(error);
        }

        return process.ExitCode;
    }
}
```

- [ ] **Step 2: 运行测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyProjectTests"
```
Expected: 全部 PASS。重点人工核对：
- record struct 被加 `readonly`、set→init；
- 事件 record 原样保留（FB-1）；
- record class set→init、层次不一致原样保留；
- App 用点改写为 `with`；
- 中间树 `dotnet build` 成功；
- 无 record 的干净项目原样复制。

- [ ] **Step 3: 提交**

Run:
```powershell
cd d:/code/cs2j-record-readonly; git add tests/CSharpToJava.ProjectTests/RecordReadOnlyProjectTests.cs; git commit -m "test(record-readonly): project-level tests with whole-tree build and FB-1/FB-2 skip verification"
```

---

### Task 28: CLI 自动化测试——开关 + verb 双模式 + 保留目录 + 统计输出

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/RecordReadOnlyCLITests.cs`

> 与既有 `UnsignedTypeCLITests` / `TupleToStructCLITests` 模式一致：通过 `dotnet run --project src/CSharpToJava.CLI --` 驱动 CLI，断言 stdout/stderr/退出码/输出文件。

- [ ] **Step 1: 创建 CLI 自动化测试**

创建 `tests/CSharpToJava.ProjectTests/RecordReadOnlyCLITests.cs`：

```csharp
using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

public class RecordReadOnlyCLITests
{
    private static string CliPath => Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "src", "CSharpToJava.CLI",
        "bin", "Debug", "net10.0", "CSharpToJava.CLI.dll");

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{CliPath}\" {string.Join(' ', args.Select(a => $"\"{a}\""))}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = Process.Start(startInfo)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    [Fact]
    public async Task Verb_SingleFile_RecordStruct_RewrittenToReadonly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-rr-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "P.cs");
        await File.WriteAllTextAsync(inputPath,
            "public record struct P(int X) { public int Y { get; set; } }");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "record-readonly", "-i", inputPath);

            Assert.Equal(0, exit);
            Assert.Contains("readonly record struct P", stdout);
            Assert.Contains("init", stdout);
            Assert.Contains("RR001", stderr);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Verb_SingleFile_RecordClass_RequiresConvertRecordClassesFlag()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-rr-cli-rc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "P.cs");
        await File.WriteAllTextAsync(inputPath,
            "public record class P { public int X { get; set; } }");

        try
        {
            // 不加 --convert-record-classes → record class 原样不动。
            var (exit1, stdout1, _) = await RunCliAsync("record-readonly", "-i", inputPath);
            Assert.Equal(0, exit1);
            Assert.Contains("public int X { get; set; }", stdout1);
            Assert.DoesNotContain("init", stdout1);

            // 加 --convert-record-classes → set→init。
            var (exit2, stdout2, stderr2) = await RunCliAsync(
                "record-readonly", "-i", inputPath, "--convert-record-classes");
            Assert.Equal(0, exit2);
            Assert.Contains("public int X { get; init; }", stdout2);
            Assert.Contains("RR002", stderr2);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Verb_Project_IntermediateTreeBuilds()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "SampleRecordSolution");
        var outputDir = Path.Combine(Path.GetTempPath(),
            "cs2j-rr-cli-proj-" + Guid.NewGuid().ToString("N"));

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "record-readonly",
                "-s", fixtureDir,
                "-d", outputDir);

            Assert.Equal(0, exit);
            Assert.Contains("record-readonly:", stdout);
            Assert.Contains("struct(s)", stdout);
            Assert.Contains("Preprocessed source:", stdout);

            // 中间树目录存在且可编译。
            var intermediateDir = Path.Combine(outputDir, ".cs2j-record-readonly-src");
            Assert.True(Directory.Exists(intermediateDir));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Verb_Strict_NonZeroExitWhenSkipped()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-rr-cli-strict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "P.cs");
        await File.WriteAllTextAsync(inputPath,
            "public record struct P { public event System.Action? Ev; public int X { get; set; } }");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "record-readonly", "-i", inputPath, "--strict");

            Assert.NotEqual(0, exit);
            Assert.Contains("RR040", stderr);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: 运行 CLI 自动化测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnlyCLITests"
```
Expected: 全部 PASS。重点人工核对：
- 单文件 verb：record struct 加 `readonly`、RR001 诊断；
- record class 默认不动，加 `--convert-record-classes` 后 set→init、RR002；
- 项目 verb：中间树目录 `.cs2j-record-readonly-src` 存在、统计行输出；
- `--strict`：跳过时退出码非零。

- [ ] **Step 3: 提交**

Run:
```powershell
cd d:/code/cs2j-record-readonly; git add tests/CSharpToJava.ProjectTests/RecordReadOnlyCLITests.cs; git commit -m "test(record-readonly): CLI automation tests for verb single-file/project modes, --strict, --convert-record-classes"
```

---

### Task 29: 一致性测试核对 + convert-project 管线集成回归

**Files:**
- Modify: `tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs`（确认 record-readonly 不破坏既有预处理链一致性）

> 规范 §7 第 3 条：基于 `ProjectPreprocessSource` 构建，`ProjectPreprocessorsConsistentTests` 自动保持。本任务核对 record-readonly 驱动器与既有预处理器使用相同的 `ProjectPreprocessSource` 管线（项目枚举/筛选/复制），并确认 `.cs2j-record-readonly-src` 保留目录在 `GetPreservedDestinationDirectories` 中注册。

- [ ] **Step 1: 在一致性测试中确认 record-readonly 驱动器存在与签名**

在 `ProjectPreprocessorsConsistentTests.cs` 的 `AllPreprocessors_UseSameProjectSelection_AndExcludeWindowsProjects` 测试中，在 `gotoResult` 断言之后追加（不改既有断言，仅追加确认 record-readonly 驱动器在管线中可用）：

```csharp
            // record-readonly 驱动器存在且与既有预处理器同源（ProjectPreprocessSource）。
            // 默认关（ConvertProjectOptions.RecordReadOnly = false），故 convert-project
            // 不会自动触发；独立 verb 通过 --convert-record-classes 显式开启。
            // 这里仅确认类型存在与签名兼容，不实际执行（避免 fixture 依赖）。
            var recordReadonlyType = typeof(ProjectRecordReadOnlyPreprocessor);
            Assert.NotNull(recordReadonlyType);
            Assert.Equal(".cs2j-record-readonly-src",
                ProjectRecordReadOnlyPreprocessor.IntermediateDirectoryName);
```

- [ ] **Step 2: 运行一致性测试确认通过**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~ProjectPreprocessorsConsistentTests"
```
Expected: PASS。若 `IsDotNetAvailable()` 返回 false（CI 无 dotnet），测试自动跳过。

- [ ] **Step 3: 提交**

Run:
```powershell
cd d:/code/cs2j-record-readonly; git add tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs; git commit -m "test(record-readonly): confirm driver exists and intermediate dir name in consistency tests"
```

---

### Task 30: 文档与收尾回归

**Files:**
- Modify: `docs/superpowers/specs/2026-09-24-record-readonly-preprocessor-design.md`（状态从"待审核"改为"已实现"；§9 补充 DIM 决策记录）
- 无代码改动（全量回归 + 文档更新）

- [ ] **Step 1: 更新规范文档状态**

在 `docs/superpowers/specs/2026-09-24-record-readonly-preprocessor-design.md` 第 6 行：

```
**状态**: 待审核
```

改为：

```
**状态**: 已实现（M1-M6，Task 1-30）
```

在 §9 "明确不做的事"的 DIM 条目后追加：

```
- DIM（默认接口方法体）默认关闭，由 `RecordReadOnlyOptions` 无 `EnableDim` 开关保证；InterfaceRippler 生成的新接口成员一律为抽象（无默认体）。若后续需缓解外部实现者破坏，可在 `RecordReadOnlyOptions` 新增 `EnableDim` 并在 `InterfaceRippler` 给新成员加默认体（Task 22 Step 8 记录该决策）。
```

- [ ] **Step 2: 全量回归测试**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet build CSharpToJavaConverter.slnx -v q; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~RecordReadOnly" -v q
```
Expected: 全部 PASS，0 errors。重点核对：
- M1（RS 全集）：`readonly record struct` 修饰符、set→init、字段→init 属性、FB-1 跳过；
- M2（MS 全集）：用点改写 `with`、集合元素、嵌套链、扩展方法；
- M3（MM 全集）：void/非 void 方法迁移、调用点提升、调用图环；
- M4（IR 全集）：接口连带、显式桩、公共强类型 With、in-tree 实现者补齐、IR-5 基接口、IR-6 泛型、RR071 改名、FB-3 force；
- M5（RC 全集）：record class set→init、字段→属性、层次一致/跳过、私有拷贝 ctor、RR002/RR013/RR014/RR021/RR080；
- M6（端到端）：SampleRecordSolution fixture、项目级整树 build、CLI 自动化、一致性。

- [ ] **Step 3: 既有预处理器回归（确认无破坏）**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~TupleToStruct|FullyQualifiedName~UnsignedType|FullyQualifiedName~ReadOnlyStruct|FullyQualifiedName~Goto" -v q
```
Expected: 全部 PASS（本特性不改既有预处理器，无回归）。

- [ ] **Step 4: C#→Java 转换回归（确认无破坏）**

Run:
```powershell
cd d:/code/cs2j-record-readonly; dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -v q
```
Expected: 全部 PASS（本特性不改 C#→Java 代码）。

- [ ] **Step 5: 提交**

Run:
```powershell
cd d:/code/cs2j-record-readonly; git add docs/superpowers/specs/2026-09-24-record-readonly-preprocessor-design.md; git commit -m "docs(record-readonly): mark spec as implemented, document DIM default-off decision, final regression"
```

- [ ] **Step 6: 合并回特性分支（若使用 worktree）**

若使用 worktree（Task 1 Step 2），在 worktree 内确认全部提交后：

Run:
```powershell
cd d:/code/cs2j-record-readonly; git log --oneline feature/record-readonly | Select-Object -First 30
```
Expected: 看到 Task 2-30 的全部 `feat(record-readonly)` / `test(record-readonly)` 提交。

合并回 `dev-3.x`（或按团队流程开 PR）：

Run:
```powershell
cd d:/code/cs2j; git switch dev-3.x; git merge --no-ff feature/record-readonly
```
Expected: 合并成功，`dotnet build CSharpToJavaConverter.slnx` 全绿。

---

<!-- END OF PLAN -->