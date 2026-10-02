# C32 Lambda 可变捕获前置化（closure-lift 第一阶段：CaptureHolder）— Implementation Plan

> **For agentic workers:** 按任务顺序实施本计划；步骤用 checkbox（`- [ ]`）跟踪。执行环境：分支 `dev-3.x`，**禁止 worktree**（用户 2026-10-02 强制约束，覆盖设计文档 09-29 版 §12），直接在当前工作区逐文件 `git add` 提交。推荐使用 superpowers:subagent-driven-development 或 superpowers:executing-plans 逐 Task 执行；**任何执行者都不得创建 worktree/特性分支**。

**Goal:** 翻转注册表 stub `closure-lift` 为 Implemented（**默认关**），在 C# 源码层把「被 lambda 捕获且可变」的局部/参数/foreach 迭代变量提升为显式 holder（host 类型内 `private sealed class Cs2jDisplayClass_<member>_<n>` + 实例 `__cs2jClosureN` + public 字段 `_cs2jCap_<name>`，全部读/写/`ref`/`out`/`lock`/循环站点改字段形式），产物是可 Release build/test 的合法 C#、语义逐字节等价；不做 C45（lambda → display class + 命名方法全量 lowering）。

**Architecture:** Core 新目录 `ClosureLift/`：Analyze（语义模型两遍：先全文件收集再统一重写）→ Plan（按声明作用域分组的 HolderGroupPlan）→ Generate（嵌套 holder 类 + 实例声明）→ Rewrite（单个 `CSharpSyntaxRewriter` + 冻结站点表 + `cs2j_closure_lift` annotation 幂等）。捕获「怎么找、怎么判写」全树只有一份实现——M1 第一步先从 E02（local-function-lift）提取 `Analysis/CaptureClassification.cs` 公共原语，E02 改消费方且其测试零行为回归，然后才许开发 C32 规则（设计 §4.2/§12 硬纪律）。CLI 单适配器 `ClosureLiftPassAdapter` 镜像 `LocalFunctionLiftPassAdapter`（整树语义、build/test 双 verify、residual 闸门）；M1 阶段适配器以私有 descriptor 挂接但**不翻转注册表**，M2 才完成 §3.2 五文件翻转。转换器侧（`StatementTransformer*`/`LambdaTransformer`/`MethodConversionState` 等）本期一行不改。

**Tech Stack:** C# / .NET 10 / Roslyn 5.0（`Microsoft.CodeAnalysis.CSharp` 5.0.0，含 `CSharpCompilation`/`SemanticModel`/`Formatter`+`AdhocWorkspace`，parse 锁 `LanguageVersion.Latest`、`allowUnsafe:true`）/ xUnit 2.9.3 / CommandLine parser。

**设计**：[2026-09-29-c32-capture-lift-design.md](../specs/2026-09-29-c32-capture-lift-design.md)（2026-10-02 第二次校订定稿，锚点 HEAD `ff63b20f`；决策 D1-D16、诊断段 CS2J32xx、§7 测试计划、§8 六门槛以该文档为准）

---

## 0. 基线裁决（开工前确认）

1. 开工 HEAD：**`ff63b20f`**（分支 `dev-3.x`）。先跑 `git branch --show-current`，输出必须为 `dev-3.x`；否则停止。再跑 `git fetch` 与 `git pull --ff-only`；若非 fast-forward 则停止并回报，不 rebase、不 merge。
2. 锚点口径：设计文档 §3.1/§3.2/§3.3 已按 `ff63b20f` 重核（2026-10-02 第二次校订）；本计划 §2 行号同为该 HEAD 实测。实施中每步仍以 Read 实际内容为准；若 `pull --ff-only` 再拉入新提交，按新 HEAD 重核 §2 与设计 §3.2 热文件清单（只允许在那五个文件冲突）。
3. 开工时点工作区在途改动（2026-10-02 实测，均与本任务无关，**不裹挟**）：
   - ` M docs/superpowers/specs/2026-09-29-c20-partial-merge-design.md`——C20 设计稿，非本任务；
   - ` M docs/superpowers/specs/2026-09-29-c43-lexical-literal-design.md`——C43 设计稿，非本任务；
   - ` M docs/superpowers/specs/2026-09-29-c32-capture-lift-design.md`——**本特性设计稿**，可随 M1 首个提交或单独提交，逐文件 `git add`；
   - `?? docs/superpowers/plans/2026-10-02-c20-partial-merge.md`——C20 计划，非本任务；
   - `?? prompts/preprocessor-completion-audit-and-order.md`——非本任务。
   **本计划文档本身**（`docs/superpowers/plans/2026-10-02-c32-capture-lift.md`）可在 Task 1 开工时单独一次提交：`docs(preprocessing): C32 closure-lift 设计校订＋实施计划`（逐文件 add 设计稿+计划两个文件）。
4. 所有提交必须**逐文件 `git add <本任务文件>`**，严禁 `git add -A`/`git add .`；提交前以 `git status` 复核暂存内容仅含本特性文件。不 push、不建标签（除非用户另行授权）。
5. 不新建支撑库工程；不注入资源；不改 csproj 包引用（fixture 工程只用既有 NET.Test.Sdk/xunit 版本）；**转换器 `src/CSharpToJava.Core/Transformers/**` 本期一行不改**（设计 §1.2-4/裁决⑥）。
6. 提交节奏：**M1（Tasks 1-7，分析+审计，Changed 恒 false、注册表不翻转）→ M2（Tasks 8-20，holder 提升+stub 翻转默认关）→ M3（Task 21，§8 六门槛决策留痕，不自动翻默认开）**；每个 Task 结束提交，里程碑末必须全仓 Release 验证。
7. 开工顺序硬约束（设计 §12）：**Task 1 的 E02 提取未绿之前，禁止新建 `ClosureLift/` 任何规则代码**。

## 1. 文件结构（新建/修改总览）

```
src/CSharpToJava.Core/Analysis/
  CaptureClassification.cs       # 新建（Task 1）：从 E02 提取的唯一捕获分类原语（public static）
src/CSharpToJava.Core/ClosureLift/                    # 全部新建
  HolderPlan.cs                  # Task 2：TriggerReason 枚举；M2 补 HolderGroupPlan/CapturedSymbolPlan/SiteRef
  ClosureLiftOptions.cs          # Task 2：Strict=true/AuditOnly=false/HolderForm=HolderClass/Verbose
  ClosureLiftStatistics.cs       # Task 2：设计 §4.7 全字段（含 TriggerReason 分桶/SkippedByReason）
  ClosureLiftDiagnostic.cs       # Task 2：Severity/Code(CS2J32xx)/Message/FilePath/Line/Member/Symbol/Reason
  ClosureLiftResult.cs           # Task 2：OutputCode/Changed/Diagnostics/Statistics
  ClosureLiftStage.cs            # Task 2：IClosureLiftStage + ClosureStageInput/Result（C45 预留，§4.8）
  ClosureLiftNaming.cs           # Task 3：类名/实例名/字段名 + 跨文件命名表（实例化、随门面复用）
  CaptureAnalysis.cs             # Task 4：CaptureAnalyzer + 分析记录（MemberCaptureAnalysis 等）
  ClosureLiftPreprocessor.cs     # Task 5（M1 审计版）→ Task 11 升级（Analyze→Plan→Generate→Rewrite）
  HolderSyntaxGenerator.cs       # Task 8（M2）：嵌套 holder 类/实例/字段 + annotation（含数组形态）
  CaptureSiteRewriter.cs         # Task 9-10（M2）：声明点 + 六类站点重写
  HolderLiftStage.cs             # Task 11（M2）：C32 唯一 IClosureLiftStage
src/CSharpToJava.CLI/Preprocessing/Adapters/
  ClosureLiftPassAdapter.cs      # Task 5（M1 审计挂接/私有 descriptor）→ Task 15 升级（residual 闸门/切注册表）
tests/CSharpToJava.ProjectTests/
  ClosureLiftUnitTests.cs        # Task 6（M1 审计断言）→ Task 13/14 扩为设计 §7.1 全 25 条
  ClosureLiftSdkAuditHarness.cs  # Task 7（M1）：env 驱动的 sdk AuditOnly 手动画像（无 env 即通过）
  ClosureLiftProjectTests.cs     # Task 19（M2）：fixture 项目级验收（CliPreprocessChain）
  ClosureLiftCLITests.cs         # Task 19（M2）：CLI 端到端
tests/CSharpToJava.Tests/
  ClosureLiftLinqInteropTests.cs # Task 20（M2）：holder 形态对 E16 LINQ 过程式化透明（设计 §7.3）
tests/SampleClosureLiftSolution/                       # Task 18（M2，新建 fixture：Lib + Tests + sln）
  SampleClosureLiftSolution.sln
  SampleClosureLiftLib/SampleClosureLiftLib.csproj
  SampleClosureLiftLib/*.cs
  SampleClosureLiftTests/SampleClosureLiftTests.csproj
  SampleClosureLiftTests/RuntimeBehaviorTests.cs
# 修改（行内编辑，锚点见 §2）：
  src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs  # Task 1 改消费方（行为零变化）
  src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs                 # Task 16 删 stub 条目、留 Ids 常量
  src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs                     # Task 16 插 Implemented 描述符+iterator 边
  src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs                # Task 16 CreateAdapter 加臂（无 toggle/config 字段）
  tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs        # Task 17 两靶子换 async-normalize
  tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs         # Task 17 五处（含 legacy toggle 排除清单）
  tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj      # Task 19 Content 接线
```

## 2. 集成点行号锚点（2026-10-02 基线 HEAD `ff63b20f`，随 Read 复核）

| 文件 | 位置 |
|------|------|
| LocalFunctionLiftAnalyzer.cs | `using` 区 L1-4（提取后加 `using CSharpToJava.Core.Analysis;`）；`CollectCaptures` **L405-465**（整体替换为消费方）；`IsDeclaredInsideLocalFunction` **L467-489**（删除，逻辑进 CaptureClassification）；`IsCapturedVariableWritten` **L491-529**（改一行消费）；`IsInsideNameOf` 唯一调用点 **L743**；方法本体 **L750-762**（删除）；`IsUnsupportedCaptureType` **L787-797**（改一行消费） |
| PassStubCatalog.cs | closure-lift stub 条目 **L86-95（M2 删）**；`Ids.ClosureLift` 常量 **L30（保留）**；async-normalize stub **L155-164**（`runAfterIds: closure-lift` L162，不动） |
| PassCatalog.cs | unsafe-ffm descriptor **L306-318**；iterator-lowering **L319-330**（`runAfterIds` **L326** 追加 ClosureLift 边）；eliminate-goto **L331-340**（补边先例 L338）；member-desugar **L341-352**（closure 边已在 **L348**，不动）；equality-concat L353-367；java-keywords L368-386；`ById` **L389-393**；`BuildRegistry` **L395-397**（`Implemented.Concat(Stubs)` L397） |
| PassChainFactory.cs | `PassChainConfiguration` **L38-92（不加字段）**；`BuildLegacyToggles` **L103-133（不加键）**；`CreateAdapter` iterator 臂 **L334-341**（ClosureLift 臂插其前，options 内联 `new()` 形态、末参传 `config.Strict`）；default throw **L382** |
| PassDescriptor.cs | 构造器 L67-102（`needsSemanticModel` 默认 true L76）；`PassStage.StateMachine` L23 |
| ProjectPassAdapter.cs | abstract/virtual 面 L117-236：`Descriptor`/`Verbose`/`FilterUnsupportedProjects`/`CompatNamespacePrefixes`/`VerifyBuild`/`VerifyTests`/`DeleteBuildOutputsAfterRestore`/`RewriteFileAsync(PassFileContext)`/`AfterFilesAsync(PassTreeContext)`/`OnRestoreFailed`/`OnBuildFailed`/`OnTestsFailed`/`HasErrors`/`BuildChainSummaryLine`/`BuildChainDiagnosticLines`；`PassFileContext`（SourceCode/SourceFilePath/RelativePath/DestinationFilePath/SemanticModel）L46-53；`PassTreeContext.IntermediateRoot` L61-70 |
| ProjectPassRunner.cs | 直连入口 `RunAsync(adapter, ProjectPassRunRequest)` **L19**（M1 harness 不经注册表直调）；`ProjectPassRunRequest` L11-14（SourcePath/DestinationRoot/Force=true） |
| PassChainOrderTests.cs | `LockedChain` 29 项 **L19-50**（ClosureLift 插 L44 UnsafeFfm 与 L45 IteratorLowering 之间→30 项）；`EnabledByDefault` **L52-54**（加 `&& id != ClosureLift`）；顺序断言 L56-74；UnsafeFfm 隔离槽位先例 **L113-159**；catalog 映射 **L216-234**（加 C32 断言）；适配器全覆盖 **L236-259**（config 工厂集 L239-251 无需新增）；legacy toggle 排除清单 **L351-356**（必须加 `&& id != ClosureLift`，否则编译不过断言） |
| PassStubCatalogTests.cs | 启用计划用例 **L57-69**（靶子 L63/L67）；CS2J9001 用例 **L71-111**（靶子 L97/L109；CliChainRequest 各 OptionsFactory 抛异常列表 L86-95 **无需为 async 新增**——async 仍是 stub，factory 抛前先被 CS2J9001 拦截） |
| CSharpToJava.ProjectTests.csproj | 新 ItemGroup 插 **L255 C28 ItemGroup 之后、L257 `</Project>` 之前**；Content 先例（Exclude bin/obj + Link）SampleEnumDeclarationSolution L142-147、C28 SampleLexicalNormalizeSolution L248-255 |
| E02 回归测试 | `tests/CSharpToJava.Tests/`：LocalFunctionLiftTests / LocalFunctionHoistingTests / LocalFunctionWrittenCaptureTests / HoistedLocalFunctionCaptureScopeTests / LocalFunctionAfterReturnHoistingTests / LocalFunctionExpressionBodySpanAdaptationTests / GenericLocalFunctionRenamedCallSiteTests / LocalFunctionTypeRefPropagationTests / LinqLocalFunctionCaptureTests（过滤器 `LocalFunction` 全覆盖） |
| Core 单测母本 | `tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs`：ParseOptions L465、ParseWithSemantics（TPA 引用）L467-490、Rewrite helper L492-514 |
| 门面/适配器母本 | `LocalFunctionLiftPreprocessor.cs`（106 行：_crossFileNameTable/Preprocess/BuildStandaloneModel/Formatter.Format L71-73）；`LocalFunctionLiftPassAdapter.cs`（155 行：MergeStatistics/AfterFilesAsync residual L74-91/CountResidual... L123-136） |
| 其他共享 | `Analysis/ExpressionTreeFacts.cs`（19 行，直接引用）；`LocalFunctionLiftNaming.GetBaseName/ResolveUniqueName`（internal，同程序集可直接调）；解决方案文件根目录 **`CSharpToJavaConverter.slnx`**（无 .sln） |

---

# M1 — 分析 + 审计（Tasks 1-7，只报不改）

**出口（设计 §9）**：E02 全量测试在提取后零行为变化；Options/Statistics/Diagnostic/Naming/Analysis 落地；AuditOnly 对每个「若改写将提升」的符号发 CS2J3207（成员/符号/原因/站点数/foreach 标记/预分配名）；`Changed` 恒 false、树零改动；适配器以私有 descriptor 可直连挂接但注册表三文件零改动；sdk AuditOnly 命中率报告成文。

## Task 1: 从 E02 提取 CaptureClassification（硬纪律第一步，先绿再开发）

**Files:**
- Create: `src/CSharpToJava.Core/Analysis/CaptureClassification.cs`
- Modify: `src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs`

- [ ] **Step 1: 记录 E02 基线绿灯（提取前快照）**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter LocalFunction --nologo`
Expected: `失败: 0`、`0 error`（9 个测试类全部通过；记下通过总数，Step 6 必须一致）。

- [ ] **Step 2: 写共享原语（落 Analysis/，public static 无状态）**

写入 `src/CSharpToJava.Core/Analysis/CaptureClassification.cs`：

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Analysis;

/// <summary>
/// How a captured variable is written at one site.
/// </summary>
public enum CaptureWriteKind
{
    /// <summary>Left side of an assignment expression (declarator initializers excluded by construction).</summary>
    Assignment,

    /// <summary>Prefix/postfix ++ or -- operand.</summary>
    IncrementDecrement,

    /// <summary>Expression of a ref/out argument.</summary>
    RefOutArgument,
}

/// <summary>
/// One external local/parameter/range variable referenced inside a boundary body.
/// Extracted from the E02 local-function analyzer (design C32 §4.2): the single
/// capture-classification implementation for the whole tree.
/// </summary>
public sealed record CapturedSymbolInfo(
    ISymbol Symbol,
    string Name,
    ITypeSymbol Type,
    string TypeDisplayFullyQualified);

/// <summary>One write site of a captured symbol.</summary>
public sealed record CaptureWriteSite(
    IdentifierNameSyntax Identifier,
    CaptureWriteKind Kind);

/// <summary>
/// Stateless capture primitives shared by local-function-lift (E02) and
/// closure-lift (C32). Strategy (what triggers a lift, grouping, foreach
/// conservatism) belongs to each pass; these only answer
/// "which symbols are referenced from outside the boundary" and
/// "where is a symbol written".
/// </summary>
public static class CaptureClassification
{
    /// <summary>
    /// Enumerates distinct local/parameter/range-variable symbols referenced
    /// inside <paramref name="bodyRoot"/> but declared outside it.
    /// Shape extracted verbatim from LocalFunctionLiftAnalyzer.CollectCaptures:
    /// NameColon labels, the boundary's own parameters and symbols declared
    /// inside the body are skipped; ErrorType symbols are skipped.
    /// </summary>
    /// <param name="model">Semantic model for the tree.</param>
    /// <param name="bodyRoot">The boundary body (local function body, lambda body/expression).</param>
    /// <param name="boundaryMethod">
    /// The boundary's own method symbol (local function/lambda); parameters it
    /// owns are skipped. Pass null when no parameter exclusion is needed.
    /// </param>
    /// <param name="descendPredicate">
    /// Optional DescendantNodes predicate. E02 passes "do not descend into
    /// nested local functions"; C32 passes the same (nested local functions are
    /// a residual CS2J3204 boundary) while nested lambdas are descended.
    /// </param>
    public static IReadOnlyList<CapturedSymbolInfo> CollectCapturedSymbols(
        SemanticModel model,
        SyntaxNode bodyRoot,
        IMethodSymbol? boundaryMethod,
        Func<SyntaxNode, bool>? descendPredicate = null)
    {
        var captures = new List<CapturedSymbolInfo>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var identifier in bodyRoot
            .DescendantNodes(descendPredicate)
            .OfType<IdentifierNameSyntax>())
        {
            if (identifier.Parent is NameColonSyntax)
                continue;

            var symbol = model.GetSymbolInfo(identifier).Symbol;
            if (symbol is not (ILocalSymbol or IParameterSymbol or IRangeVariableSymbol))
                continue;

            // Skip references to the boundary's own parameters.
            if (boundaryMethod != null
                && SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, boundaryMethod))
                continue;

            // Skip symbols declared inside the boundary body itself
            // (lambda parameters, variables declared in nested blocks).
            if (IsDeclaredInside(symbol, bodyRoot))
                continue;

            if (!seen.Add(symbol))
                continue;

            var type = symbol switch
            {
                ILocalSymbol ls => ls.Type,
                IParameterSymbol ps => ps.Type,
                _ => null,
            };

            if (type == null || type.TypeKind == TypeKind.Error)
                continue;

            var typeDisplay = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            captures.Add(new CapturedSymbolInfo(symbol, symbol.Name, type, typeDisplay));
        }

        return captures;
    }

    /// <summary>
    /// Enumerates every write site of <paramref name="symbol"/> inside
    /// <paramref name="root"/>: assignment left values (declarator
    /// initializers are EqualsValueClause, not assignments, and therefore
    /// never matched), prefix/postfix ++/-- operands, and ref/out arguments.
    /// Shape extracted verbatim from LocalFunctionLiftAnalyzer.IsCapturedVariableWritten.
    /// </summary>
    public static IReadOnlyList<CaptureWriteSite> FindWriteSites(
        SemanticModel model,
        ISymbol symbol,
        SyntaxNode root,
        Func<SyntaxNode, bool>? descendPredicate = null)
    {
        var sites = new List<CaptureWriteSite>();

        foreach (var identifier in root
            .DescendantNodes(descendPredicate)
            .OfType<IdentifierNameSyntax>())
        {
            if (identifier.Parent is NameColonSyntax)
                continue;

            var resolved = model.GetSymbolInfo(identifier).Symbol;
            if (!SymbolEqualityComparer.Default.Equals(resolved, symbol))
                continue;

            var parent = identifier.Parent;
            if (parent is AssignmentExpressionSyntax assignment
                && assignment.Left == identifier)
            {
                sites.Add(new CaptureWriteSite(identifier, CaptureWriteKind.Assignment));
                continue;
            }

            if (parent is PrefixUnaryExpressionSyntax prefix
                && prefix.Operand == identifier
                && (prefix.IsKind(SyntaxKind.PreIncrementExpression)
                    || prefix.IsKind(SyntaxKind.PreDecrementExpression)))
            {
                sites.Add(new CaptureWriteSite(identifier, CaptureWriteKind.IncrementDecrement));
                continue;
            }

            if (parent is PostfixUnaryExpressionSyntax postfix
                && postfix.Operand == identifier
                && (postfix.IsKind(SyntaxKind.PostIncrementExpression)
                    || postfix.IsKind(SyntaxKind.PostDecrementExpression)))
            {
                sites.Add(new CaptureWriteSite(identifier, CaptureWriteKind.IncrementDecrement));
                continue;
            }

            if (parent is ArgumentSyntax argument
                && (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                    || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
            {
                sites.Add(new CaptureWriteSite(identifier, CaptureWriteKind.RefOutArgument));
            }
        }

        return sites;
    }

    /// <summary>
    /// True when <paramref name="node"/> (normally an identifier) is lexically
    /// inside a <c>nameof(...)</c> invocation. Such references are string
    /// literals, never variable accesses. Extracted from E02.
    /// </summary>
    public static bool IsInsideNameOf(SyntaxNode node)
    {
        SyntaxNode? current = node.Parent;
        while (current != null)
        {
            if (current is InvocationExpressionSyntax invocation
                && invocation.Expression is IdentifierNameSyntax id
                && id.Identifier.Text == "nameof")
                return true;
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// True for capture kinds that cannot be spelled in generated C# source:
    /// anonymous types (unspeakable <c>&lt;anonymous&gt;</c> name), pointer
    /// types (baseline compile error when lambda-captured), ref struct prefix.
    /// Extracted verbatim from E02 IsUnsupportedCaptureType.
    /// </summary>
    public static bool IsUnsupportedCaptureKind(string? typeDisplay)
    {
        if (typeDisplay == null)
            return false;
        if (typeDisplay.Contains("<anonymous"))
            return true;
        if (typeDisplay.Contains("*"))
            return true;
        if (typeDisplay.StartsWith("ref "))
            return true;
        return false;
    }

    private static bool IsDeclaredInside(ISymbol symbol, SyntaxNode body)
    {
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var location = syntaxRef.GetSyntax().GetLocation();
            if (body.SpanStart <= location.SourceSpan.Start
                && location.SourceSpan.Start < body.Span.End)
            {
                return true;
            }
        }
        return false;
    }
}
```

- [ ] **Step 3: E02 改消费方——文件头加 using**

`src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs` L1-4 改为：

```csharp
using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;
```

- [ ] **Step 4: E02 改消费方——CollectCaptures 整体替换（L405-465）**

把现有 `private List<CapturePlan> CollectCaptures(...)` 整个方法（L405-465）替换为：

```csharp
    private List<CapturePlan> CollectCaptures(
        LocalFunctionStatementSyntax localFn,
        IMethodSymbol localFnSymbol,
        bool isStatic)
    {
        if (isStatic)
            return new List<CapturePlan>();

        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return new List<CapturePlan>();

        // C32 §4.2: capture discovery is the single shared primitive.
        var capParamNames = new HashSet<string>(StringComparer.Ordinal);
        return CaptureClassification.CollectCapturedSymbols(
                _model,
                body,
                localFnSymbol,
                d => d is not LocalFunctionStatementSyntax)
            .Select(info =>
            {
                var isWritten = IsCapturedVariableWritten(localFn, info.Symbol);
                var capName = LocalFunctionLiftNaming.GetCaptureParameterName(
                    info.Name, capParamNames);
                return new CapturePlan(
                    info.Symbol,
                    info.Name,
                    capName,
                    info.TypeDisplayFullyQualified,
                    isWritten,
                    isWritten);
            })
            .ToList();
    }
```

- [ ] **Step 5: E02 改消费方——删除私有重复、其余两处改一行**

1. 删除 `IsDeclaredInsideLocalFunction` 整个方法（现 L467-489，含其上方注释 L467-471）——逻辑已进 `CaptureClassification.IsDeclaredInside`。
2. `IsCapturedVariableWritten`（现 L491-529）方法体替换为对原语的一行消费：

```csharp
    private bool IsCapturedVariableWritten(
        LocalFunctionStatementSyntax localFn,
        ISymbol symbol)
    {
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return false;

        // C32 §4.2: write-site discovery is the single shared primitive.
        return CaptureClassification.FindWriteSites(
                _model,
                symbol,
                body,
                d => d is not LocalFunctionStatementSyntax)
            .Count > 0;
    }
```

3. `IsInsideNameOf` 的调用点（现 L743）`if (IsInsideNameOf(id))` 改为：

```csharp
            if (CaptureClassification.IsInsideNameOf(id))
                continue;
```

   然后删除私有 `IsInsideNameOf` 整个方法（现 L750-762）。
4. `IsUnsupportedCaptureType`（现 L787-797）整体替换为：

```csharp
    private static bool IsUnsupportedCaptureType(CapturePlan cap)
        // C32 §4.2: unsupported-kind classification is the single shared primitive.
        => CaptureClassification.IsUnsupportedCaptureKind(cap.TypeDisplay);
```

- [ ] **Step 6: 构建 + E02 零行为回归**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter LocalFunction --nologo`
Expected: `失败: 0`，通过总数与 Step 1 完全一致（提取是纯搬家，断言结果零变化）。

- [ ] **Step 7: 提交（逐文件 add，仅两个文件）**

```powershell
git add src/CSharpToJava.Core/Analysis/CaptureClassification.cs
git add src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs
git commit -m "refactor(preprocessing): C32 M1 提取 CaptureClassification 共享捕获原语（E02 改消费方、零行为变化）"
```

## Task 2: ClosureLift Models（Options / Statistics / Diagnostic / Result / TriggerReason）

**Files:**
- Create: `src/CSharpToJava.Core/ClosureLift/ClosureLiftOptions.cs`
- Create: `src/CSharpToJava.Core/ClosureLift/ClosureLiftStatistics.cs`
- Create: `src/CSharpToJava.Core/ClosureLift/ClosureLiftDiagnostic.cs`
- Create: `src/CSharpToJava.Core/ClosureLift/ClosureLiftResult.cs`
- Create: `src/CSharpToJava.Core/ClosureLift/HolderPlan.cs`（M1 只放 `TriggerReason`，M2 Task 8 追加 plan 记录）

- [ ] **Step 1: Options + HolderForm（设计 §4.7）**

```csharp
namespace CSharpToJava.Core.ClosureLift;

/// <summary>Generated storage shape for lifted captures (design D1).</summary>
public enum HolderForm
{
    /// <summary>Default and only acceptance shape: private sealed holder class with public fields.</summary>
    HolderClass,

    /// <summary>Non-default audit/regression comparator: single-element T[] arrays (design §4.4 tail).</summary>
    SingleElementArray,
}

/// <summary>
/// Options for the closure-lift pass. Four knobs only (design §4.7); no
/// per-syntax toggles. AuditOnly is the M1 mode; the CLI never exposes a
/// dedicated option — reachable via the manual sdk harness until M3.
/// </summary>
/// <param name="Strict">Residuals/skips are Errors (file kept verbatim) when true; downgraded to Warnings otherwise.</param>
/// <param name="AuditOnly">M1: analyze and emit CS2J3207 rows only; Changed is always false.</param>
/// <param name="HolderForm">HolderClass (default) or the non-default array comparator.</param>
/// <param name="Verbose">Emit per-file Info lines.</param>
public sealed record ClosureLiftOptions(
    bool Strict = true,
    bool AuditOnly = false,
    HolderForm HolderForm = HolderForm.HolderClass,
    bool Verbose = false);
```

- [ ] **Step 2: Statistics（设计 §4.7 全字段）**

```csharp
namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// Mutable accumulator (mirrors LocalFunctionLiftStatistics); merged across
/// files by the adapter. Buckets follow design §4.7.
/// </summary>
public sealed class ClosureLiftStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;

    public int HolderClassesGenerated;
    public int HolderInstancesGenerated;

    /// <summary>Total captured symbols lifted; per-reason breakdown in <see cref="CapturesByReason"/>.</summary>
    public int CapturesLifted;
    public Dictionary<TriggerReason, int> CapturesByReason = new();

    public int SitesRewritten;
    public int ForeachPerIterationGroups;

    /// <summary>CS2J3201-3206 rows keyed by code; residual gate rows counted separately.</summary>
    public Dictionary<string, int> SkippedByReason = new(StringComparer.Ordinal);

    /// <summary>Mutable captures still present in the re-parsed intermediate tree (must be 0 in M2 strict).</summary>
    public int ResidualMutableCaptures;

    public void AddSkipped(string code)
        => SkippedByReason[code] = SkippedByReason.TryGetValue(code, out var count) ? count + 1 : 1;
}
```

- [ ] **Step 3: Diagnostic + 诊断码常量（设计 §4.7 表）**

```csharp
namespace CSharpToJava.Core.ClosureLift;

public enum ClosureLiftSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One CS2J32xx row. Every row carries file/line/member/symbol/reason
/// (design §6-4); the adapter fills FilePath.
/// </summary>
public sealed record ClosureLiftDiagnostic(
    ClosureLiftSeverity Severity,
    string Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    string? MemberName = null,
    string? SymbolName = null,
    TriggerReason? Reason = null)
{
    public ClosureLiftDiagnostic WithFilePath(string? filePath) => this with { FilePath = filePath };
}

/// <summary>CS2J32xx code constants (design §4.7 / §6).</summary>
internal static class ClosureLiftCodes
{
    /// <summary>Mutable capture of a ref local or by-ref parameter before C01 shape arrives (D6).</summary>
    public const string RefCapture = "CS2J3201";

    /// <summary>Symbol unresolvable / ErrorType; the complete site set cannot be guaranteed (D15).</summary>
    public const string UnresolvableSymbol = "CS2J3202";

    /// <summary>Residual anonymous method (E05 output missing) (D14).</summary>
    public const string ResidualAnonymousMethod = "CS2J3203";

    /// <summary>Residual local function cross-boundary capture / lambda in field-or-property initializer (D14, D16).</summary>
    public const string UnsupportedBoundary = "CS2J3204";

    /// <summary>Pointer/anonymous capture kind that cannot be spelled in source (D15).</summary>
    public const string UnsupportedCaptureKind = "CS2J3205";

    /// <summary>Mutable symbol referenced by both a statement lambda and a quoted expression-tree lambda (D11).</summary>
    public const string ExpressionTreeMix = "CS2J3206";

    /// <summary>AuditOnly report row: member/symbol/reason/site count/foreach/prospective names.</summary>
    public const string AuditRow = "CS2J3207";
}
```

- [ ] **Step 4: Result**

```csharp
namespace CSharpToJava.Core.ClosureLift;

public sealed record ClosureLiftResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<ClosureLiftDiagnostic> Diagnostics,
    ClosureLiftStatistics Statistics);
```

- [ ] **Step 5: HolderPlan.cs（M1 仅 TriggerReason；记录体 Task 8 追加）**

```csharp
namespace CSharpToJava.Core.ClosureLift;

// M1: trigger classification only (used by analysis/statistics/diagnostics).
// M2 (Task 8) appends HolderGroupPlan / CapturedSymbolPlan / SiteRef records
// to this file — design §4.3 freezes their shape.
/// <summary>Why a symbol is lifted (design D2). Primary reason with deterministic precedence.</summary>
public enum TriggerReason
{
    ExternalWrite,
    InsideLambdaWrite,
    RefOutArgument,
    ForeachIteration,
}
```

- [ ] **Step 6: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`（新命名空间未被引用亦不报错）。

- [ ] **Step 7: 提交**

```powershell
git add src/CSharpToJava.Core/ClosureLift/ClosureLiftOptions.cs
git add src/CSharpToJava.Core/ClosureLift/ClosureLiftStatistics.cs
git add src/CSharpToJava.Core/ClosureLift/ClosureLiftDiagnostic.cs
git add src/CSharpToJava.Core/ClosureLift/ClosureLiftResult.cs
git add src/CSharpToJava.Core/ClosureLift/HolderPlan.cs
git commit -m "feat(preprocessing): C32 M1 closure-lift Models/Options/Statistics/诊断码/TriggerReason"
```

## Task 3: ClosureLiftNaming（确定性命名 + 跨文件表）+ C45 阶段接口

**Files:**
- Create: `src/CSharpToJava.Core/ClosureLift/ClosureLiftNaming.cs`
- Create: `src/CSharpToJava.Core/ClosureLift/ClosureLiftStage.cs`

- [ ] **Step 1: 写命名工具（设计 §4.6；实例化、随门面跨文件复用）**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// Deterministic naming (design §4.6):
///   class    Cs2jDisplayClass_&lt;member&gt;_&lt;n&gt;  — n per host type, assigned in
///            the syntax order of groups (SpanStart); cross-file table makes
///            partial parts of one host type share the reservation set.
///   instance __cs2jClosure&lt;n&gt;                   — n per enclosing routine;
///            a user-local collision gets _2, _3 ... (ResolveUniqueName style).
///   field    _cs2jCap_&lt;originalName&gt;           — _2, _3 ... within one class.
/// No randomness/hashes: every decision derives from syntax order + symbols.
/// One instance is reused across files in project mode (mirrors E02).
/// </summary>
internal sealed class ClosureLiftNaming
{
    // hostTypeKey (fully qualified display) -> reserved class names across files.
    private readonly Dictionary<string, HashSet<string>> _classNamesByHost = new(StringComparer.Ordinal);

    /// <summary>Reserve Cs2jDisplayClass_&lt;member&gt;_&lt;n&gt; for one group inside a host type.</summary>
    public string ReserveClassName(string hostTypeKey, string hostMemberName)
    {
        if (!_classNamesByHost.TryGetValue(hostTypeKey, out var used))
        {
            used = new HashSet<string>(StringComparer.Ordinal);
            _classNamesByHost[hostTypeKey] = used;
        }

        var memberPart = SanitizeMemberName(hostMemberName);
        for (var n = 0; ; n++)
        {
            var candidate = $"Cs2jDisplayClass_{memberPart}_{n}";
            if (used.Add(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Reserve the per-routine instance name. <paramref name="localNamesInRoutine"/>
    /// starts with every existing local/parameter name of the routine and gains
    /// each generated name, so user-named __cs2jClosure* locals cannot collide.
    /// </summary>
    public static string ReserveInstanceName(int groupIndex, ISet<string> localNamesInRoutine)
    {
        var candidate = $"__cs2jClosure{groupIndex}";
        if (localNamesInRoutine.Add(candidate))
            return candidate;

        for (var suffix = 2; ; suffix++)
        {
            var suffixed = $"{candidate}_{suffix}";
            if (localNamesInRoutine.Add(suffixed))
                return suffixed;
        }
    }

    /// <summary>Reserve the public field name inside one holder class.</summary>
    public static string GetFieldName(string originalName, ISet<string> usedFieldNames)
    {
        var candidate = "_cs2jCap_" + TrimEscape(originalName);
        if (usedFieldNames.Add(candidate))
            return candidate;

        for (var suffix = 2; ; suffix++)
        {
            var suffixed = $"{candidate}_{suffix}";
            if (usedFieldNames.Add(suffixed))
                return suffixed;
        }
    }

    /// <summary>Non-default array-shape local name (design §4.4): __cs2jArr_&lt;name&gt;.</summary>
    public static string GetArrayLocalName(string originalName)
        => "__cs2jArr_" + TrimEscape(originalName);

    /// <summary>
    /// The class-name segment keeps the Pascal spelling of the host member.
    /// A member literally named with a C# reserved keyword (e.g. <c>@class</c>)
    /// gets a deterministic Member suffix. Contextual keywords stay verbatim
    /// (they are legal identifiers, same policy as E02 GetBaseName).
    /// </summary>
    internal static string SanitizeMemberName(string name)
    {
        var stripped = TrimEscape(name);
        return SyntaxFacts.GetKeywordKind(stripped) != SyntaxKind.None
            ? stripped + "Member"
            : stripped;
    }

    private static string TrimEscape(string name)
        => name.StartsWith("@", StringComparison.Ordinal) && name.Length > 1 ? name[1..] : name;
}
```

- [ ] **Step 2: 写 C45 预留阶段接口（设计 §4.8；C32 只注册一个 HolderLiftStage）**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// Input shared by every closure-lift stage. C32 registers exactly one stage
/// (HolderLiftStage); C45 appends DisplayClassConsolidation / NamedMethodLift /
/// DelegateSiteRewrite stages to the same ordered pipeline without a new pass
/// id or registry change (design §3.4/§4.8).
/// </summary>
internal sealed record ClosureStageInput(
    CompilationUnitSyntax Root,
    SemanticModel Model,
    ClosureLiftOptions Options,
    ClosureLiftNaming Naming,
    ClosureLiftStatistics Statistics,
    List<ClosureLiftDiagnostic> Diagnostics);

internal sealed record ClosureStageResult(
    CompilationUnitSyntax Root,
    bool Changed);

internal interface IClosureLiftStage
{
    string Id { get; }

    ClosureStageResult Apply(ClosureStageInput input);
}
```

- [ ] **Step 3: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 4: 提交**

```powershell
git add src/CSharpToJava.Core/ClosureLift/ClosureLiftNaming.cs
git add src/CSharpToJava.Core/ClosureLift/ClosureLiftStage.cs
git commit -m "feat(preprocessing): C32 M1 确定性命名工具（跨文件表）＋C45 阶段接口预留"
```

## Task 4: CaptureAnalyzer（D2/D4/D6/D9/D11/D14/D15/D16 纯分析，不改树）

**Files:**
- Create: `src/CSharpToJava.Core/ClosureLift/CaptureAnalysis.cs`

说明：本文件只产分析记录与诊断，不碰 SyntaxTree。捕获「怎么找/怎么判写」全部调 Task 1 的 `CaptureClassification`；本文件只拥有 C32 策略（D2 触发、foreach 保守、引号混合、边界防线、宿主/作用域解析）。

- [ ] **Step 1: 写 CaptureAnalysis.cs 完整代码**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Analysis;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>How a non-quoted reference site is treated by the rewriter.</summary>
internal enum ClosureSiteKind
{
    Read,
    Write,
    Lock,
}

/// <summary>
/// One reference site of a captured symbol. Rewrite-eligible sites never
/// include nameof (a string literal) or quoted expression-tree lambdas (D11).
/// </summary>
internal sealed record ClosureSiteRef(
    SyntaxReference Reference,
    ClosureSiteKind Kind,
    CaptureWriteKind? WriteKind,
    bool InsideLambda,
    bool InsideQuotedLambda);

/// <summary>Analysis record for one candidate symbol inside one routine.</summary>
internal sealed class CapturedSymbolAnalysis
{
    public required ISymbol Symbol { get; init; }
    public required string Name { get; init; }
    public required string TypeDisplay { get; init; }
    public required SyntaxReference DeclaringReference { get; init; }
    public required MemberDeclarationSyntax EnclosingMember { get; init; }
    public required TypeDeclarationSyntax HostType { get; init; }
    public required string RoutineName { get; init; }

    /// <summary>Block the instance declaration belongs to (null for non-block foreach bodies wrapped in M2).</summary>
    public BlockSyntax? ScopeBlock { get; init; }

    public required TriggerReason Reason { get; init; }
    public bool IsForeachIteration { get; init; }
    public ForEachStatementSyntax? ForeachLoop { get; init; }
    /// <summary>Set when the symbol is a for-loop induction variable (instance goes right before the loop).</summary>
    public ForStatementSyntax? ForLoop { get; init; }
    public VariableDeclaratorSyntax? Declarator { get; init; }
    public ParameterSyntax? Parameter { get; init; }

    /// <summary>True when a CS2J3201-3206 row fired; the file must stay verbatim for this symbol's routine.</summary>
    public bool Skipped { get; init; }
    public string? SkipCode { get; init; }

    public List<ClosureSiteRef> Sites { get; init; } = new();
    public List<ClosureSiteRef> QuotedSites { get; init; } = new();
}

internal sealed record MemberCaptureAnalysis(
    TypeDeclarationSyntax HostType,
    MemberDeclarationSyntax Routine,
    string RoutineName,
    List<CapturedSymbolAnalysis> Symbols);

internal sealed record ClosureAnalysisResult(
    List<MemberCaptureAnalysis> Members,
    List<CapturedSymbolAnalysis> AllSymbols);

/// <summary>
/// First pass of design §4.1: whole CompilationUnit, read-only.
/// Residual anonymous methods/local-function boundaries and initializer
/// lambdas are diagnosed before any rewrite (skip/error set is frozen first).
/// </summary>
internal sealed class CaptureAnalyzer
{
    private static readonly Func<SyntaxNode, bool> DescendNotIntoLocalFunctions
        = static d => d is not LocalFunctionStatementSyntax;

    private readonly SemanticModel _model;
    private readonly ClosureLiftOptions _options;
    private readonly List<ClosureLiftDiagnostic> _diagnostics;

    public CaptureAnalyzer(
        SemanticModel model,
        ClosureLiftOptions options,
        List<ClosureLiftDiagnostic> diagnostics)
    {
        _model = model;
        _options = options;
        _diagnostics = diagnostics;
    }

    public ClosureAnalysisResult Analyze(CompilationUnitSyntax root)
    {
        var all = new List<CapturedSymbolAnalysis>();
        var byRoutine = new Dictionary<MemberDeclarationSyntax, MemberCaptureWork>();

        // D14 residual: anonymous methods should have been lowered by E05.
        foreach (var anon in root.DescendantNodes().OfType<AnonymousMethodExpressionSyntax>())
        {
            Emit(ClosureLiftCodes.ResidualAnonymousMethod,
                "Residual anonymous method cannot be capture-lifted (delegate pass output expected).",
                anon.GetLocation());
        }

        var lambdas = root.DescendantNodes().OfType<LambdaExpressionSyntax>().ToList();
        var quoted = new HashSet<LambdaExpressionSyntax>();
        foreach (var lambda in lambdas)
        {
            if (ExpressionTreeFacts.IsExpressionTreeLambda(_model, lambda))
                quoted.Add(lambda);
        }

        // Candidate symbols discovered from statement lambdas, keyed by routine.
        var candidateByRoutine = new Dictionary<MemberDeclarationSyntax, Dictionary<ISymbol, LambdaExpressionSyntax>>();

        foreach (var lambda in lambdas)
        {
            var routine = ResolveRoutine(lambda, out var boundaryCode, out var boundaryNode);
            if (routine == null)
            {
                if (boundaryCode != null)
                    Emit(boundaryCode, BoundaryMessage(boundaryCode), boundaryNode!.GetLocation());
                continue;
            }

            // D11: quoted lambdas contribute no mutable candidates of their own.
            if (quoted.Contains(lambda))
                continue;

            // D12: static lambdas capture nothing.
            if (lambda.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)))
                continue;

            var lambdaBody = lambda is ParenthesizedLambdaExpressionSyntax parenthesized
                ? (SyntaxNode?)parenthesized.Block ?? parenthesized.ExpressionBody
                : ((SimpleLambdaExpressionSyntax)lambda).Block is { } block2
                    ? block2
                    : ((SimpleLambdaExpressionSyntax)lambda).ExpressionBody;
            if (lambdaBody == null)
                continue;

            var lambdaSymbol = (IMethodSymbol?)_model.GetSymbolInfo(lambda).Symbol
                ?? _model.GetDeclaredSymbol(lambda);

            foreach (var info in CaptureClassification.CollectCapturedSymbols(
                _model, lambdaBody, lambdaSymbol, DescendNotIntoLocalFunctions))
            {
                if (!candidateByRoutine.TryGetValue(routine, out var map))
                {
                    map = new Dictionary<ISymbol, LambdaExpressionSyntax>(SymbolEqualityComparer.Default);
                    candidateByRoutine[routine] = map;
                }

                map.TryAdd(info.Symbol, lambda);
            }
        }

        foreach (var (routineNode, candidateMap) in candidateByRoutine)
        {
            var bodyRoot = GetBodyRoot(routineNode);
            if (bodyRoot == null)
                continue;

            var hostType = routineNode.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (hostType is not (ClassDeclarationSyntax or StructDeclarationSyntax
                or RecordDeclarationSyntax))
            {
                // No insertable host type (interface default method / top-level statements).
                foreach (var symbol in candidateMap.Keys)
                {
                    Emit(ClosureLiftCodes.UnsupportedBoundary,
                        $"Captured variable '{symbol.Name}' has no holder host type (interface/top-level boundary is unsupported in v1).",
                        symbol.Locations.FirstOrDefault());
                }
                continue;
            }

            var routineName = GetRoutineName(routineNode, hostType);
            if (!byRoutine.TryGetValue(routineNode, out var work))
            {
                work = new MemberCaptureWork(hostType, routineNode, routineName);
                byRoutine[routineNode] = work;
            }

            foreach (var symbol in candidateMap.Keys)
            {
                // Idempotency: never re-lift our own generated instances (D8/§4.1).
                if (symbol.Name.StartsWith("__cs2jClosure", StringComparison.Ordinal)
                    || symbol.Name.StartsWith("__cs2jArr_", StringComparison.Ordinal))
                    continue;

                var analysis = AnalyzeSymbol(symbol, routineNode, bodyRoot, hostType, routineName, quoted);
                if (analysis == null)
                    continue;

                work.Symbols.Add(analysis);
                all.Add(analysis);
            }
        }

        // Deterministic order: routines by first symbol span; symbols by declaring span (§4.3).
        var members = byRoutine.Values
            .Select(w => new MemberCaptureAnalysis(
                w.HostType, w.Routine, w.RoutineName,
                w.Symbols.OrderBy(s => s.DeclaringReference.Span.Start).ToList()))
            .OrderBy(m => m.Symbols.First().DeclaringReference.Span.Start)
            .ToList();

        return new ClosureAnalysisResult(members, all);
    }

    private CapturedSymbolAnalysis? AnalyzeSymbol(
        ISymbol symbol,
        MemberDeclarationSyntax routine,
        SyntaxNode bodyRoot,
        TypeDeclarationSyntax hostType,
        string routineName,
        ISet<LambdaExpressionSyntax> quoted)
    {
        var declaringRef = symbol.DeclaringSyntaxReferences.FirstOrDefault(r => r.SyntaxTree == bodyRoot.SyntaxTree);
        if (declaringRef == null)
        {
            Emit(ClosureLiftCodes.UnresolvableSymbol,
                $"Captured symbol '{symbol.Name}' has no declarator in this tree; the complete site set cannot be guaranteed.",
                symbol.Locations.FirstOrDefault());
            return null;
        }

        var declaringNode = declaringRef.GetSyntax();

        ParameterSyntax? parameter = declaringNode as ParameterSyntax;
        VariableDeclaratorSyntax? declarator = declaringNode as VariableDeclaratorSyntax;
        if (parameter == null && declarator == null)
            return null;

        // D6: by-ref parameter (RefKind != None; C01 ByRef<T> has RefKind.None and is ordinary).
        if (parameter != null)
        {
            var parameterSymbol = (IParameterSymbol)symbol;
            if (parameterSymbol.RefKind != RefKind.None)
            {
                return Skip(symbol, routine, bodyRoot, hostType, routineName, declaringRef,
                    ClosureLiftCodes.RefCapture,
                    $"By-ref parameter '{symbol.Name}' captured by a lambda cannot be field-backed before C01 normalization (D6).",
                    parameter, null);
            }
        }

        ForEachStatementSyntax? foreachLoop = null;
        ForStatementSyntax? forLoop = null;
        BlockSyntax? scopeBlock = null;

        if (declarator != null && declarator.Parent is VariableDeclarationSyntax declaration)
        {
            switch (declaration.Parent)
            {
                case ForEachStatementSyntax fe when fe.DescendantNodes().Contains(declarator):
                    foreachLoop = fe;
                    scopeBlock = fe.Statement as BlockSyntax;
                    break;
                case ForStatementSyntax fs when fs.DescendantNodes().Contains(declarator):
                    forLoop = fs;
                    scopeBlock = fs.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
                    break;
                case LocalDeclarationStatementSyntax local:
                    // D6: ref local.
                    if (local.Declaration.Type is Microsoft.CodeAnalysis.CSharp.Syntax.RefTypeSyntax)
                    {
                        return Skip(symbol, routine, bodyRoot, hostType, routineName, declaringRef,
                            ClosureLiftCodes.RefCapture,
                            $"ref local '{symbol.Name}' captured by a lambda cannot be field-backed (D6).",
                            null, declarator);
                    }
                    scopeBlock = local.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
                    break;
                default:
                    // catch/using/argument-declaration locals are never mutable-captured
                    // in baseline-valid code; treat conservatively as no trigger below.
                    break;
            }
        }

        if (parameter != null)
            scopeBlock = (routine as BaseMethodDeclarationSyntax)?.Body
                ?? (routine as AccessorDeclarationSyntax)?.Body;

        // D15: ErrorType / pointer / anonymous kinds that cannot be spelled.
        var typeDisplay = symbol switch
        {
            ILocalSymbol ls => ls.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            IParameterSymbol ps => ps.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            _ => null,
        };

        if (typeDisplay == null
            || (symbol is ILocalSymbol { Type.TypeKind: TypeKind.Error })
            || (symbol is IParameterSymbol { Type.TypeKind: TypeKind.Error }))
        {
            return Skip(symbol, routine, bodyRoot, hostType, routineName, declaringRef,
                ClosureLiftCodes.UnresolvableSymbol,
                $"Captured symbol '{symbol.Name}' has an unresolvable type (D15).",
                parameter, declarator);
        }

        if (CaptureClassification.IsUnsupportedCaptureKind(typeDisplay))
        {
            return Skip(symbol, routine, bodyRoot, hostType, routineName, declaringRef,
                ClosureLiftCodes.UnsupportedCaptureKind,
                $"Captured symbol '{symbol.Name}' has pointer/anonymous kind '{typeDisplay}' that cannot be field-backed in source (D15).",
                parameter, declarator);
        }

        // Site enumeration across the whole routine (external + lambda sites).
        var writeSpans = CaptureClassification
            .FindWriteSites(_model, symbol, bodyRoot, DescendNotIntoLocalFunctions)
            .ToDictionary(s => s.Identifier.Span.Start, s => s.Kind);

        var sites = new List<ClosureSiteRef>();
        var quotedSites = new List<ClosureSiteRef>();

        foreach (var identifier in bodyRoot
            .DescendantNodes(DescendNotIntoLocalFunctions)
            .OfType<IdentifierNameSyntax>())
        {
            if (identifier.Parent is NameColonSyntax)
                continue;
            if (!SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(identifier).Symbol, symbol))
                continue;
            if (CaptureClassification.IsInsideNameOf(identifier))
                continue;

            var enclosingLambda = identifier.Ancestors().OfType<LambdaExpressionSyntax>().FirstOrDefault();
            var insideQuoted = enclosingLambda != null && quoted.Contains(enclosingLambda);
            writeSpans.TryGetValue(identifier.Span.Start, out var writeKind);

            var isLock = identifier.Parent is LockStatementSyntax lockStatement
                && lockStatement.Expression == identifier;

            var kind = writeKind.HasValue ? ClosureSiteKind.Write
                : isLock ? ClosureSiteKind.Lock
                : ClosureSiteKind.Read;

            var siteRef = new ClosureSiteRef(
                identifier.GetReference(),
                kind,
                writeKind,
                enclosingLambda != null,
                insideQuoted);

            if (insideQuoted)
                quotedSites.Add(siteRef);
            else
                sites.Add(siteRef);
        }

        // D2 trigger predicate.
        TriggerReason? reason = null;
        if (foreachLoop != null)
        {
            // D9 conservative: every foreach iteration variable captured by a lambda lifts.
            reason = TriggerReason.ForeachIteration;
        }
        else
        {
            var writes = sites.Where(s => s.WriteKind.HasValue).ToList();
            if (writes.Count > 0)
            {
                if (writes.Any(s => s.WriteKind == CaptureWriteKind.RefOutArgument))
                    reason = TriggerReason.RefOutArgument;
                else if (writes.Any(s => s.InsideLambda))
                    reason = TriggerReason.InsideLambdaWrite;
                else
                    reason = TriggerReason.ExternalWrite;
            }
        }

        if (reason == null)
        {
            // D3: read-only capture — effectively final, no holder in C32.
            return null;
        }

        var analysis = new CapturedSymbolAnalysis
        {
            Symbol = symbol,
            Name = symbol.Name,
            TypeDisplay = typeDisplay!,
            DeclaringReference = declaringRef,
            EnclosingMember = routine,
            HostType = hostType,
            RoutineName = routineName,
            ScopeBlock = scopeBlock,
            Reason = reason.Value,
            IsForeachIteration = foreachLoop != null,
            ForeachLoop = foreachLoop,
            ForLoop = forLoop,
            Declarator = declarator,
            Parameter = parameter,
            Sites = sites,
            QuotedSites = quotedSites,
        };

        // D11: a mutable symbol also referenced inside a quoted lambda — rewrite would
        // dangle the in-tree reference; fail the symbol and keep the file verbatim.
        if (quotedSites.Count > 0)
        {
            Emit(ClosureLiftCodes.ExpressionTreeMix,
                $"Mutable captured symbol '{symbol.Name}' is also referenced inside an expression-tree lambda (D11).",
                symbol.Locations.FirstOrDefault(), routineName, symbol.Name, reason);
            return analysis with { Skipped = true, SkipCode = ClosureLiftCodes.ExpressionTreeMix };
        }

        return analysis;
    }

    private CapturedSymbolAnalysis Skip(
        ISymbol symbol,
        MemberDeclarationSyntax routine,
        SyntaxNode bodyRoot,
        TypeDeclarationSyntax hostType,
        string routineName,
        SyntaxReference declaringRef,
        string code,
        string message,
        ParameterSyntax? parameter,
        VariableDeclaratorSyntax? declarator)
    {
        Emit(code, message, symbol.Locations.FirstOrDefault(), routineName, symbol.Name, null);
        return new CapturedSymbolAnalysis
        {
            Symbol = symbol,
            Name = symbol.Name,
            TypeDisplay = symbol switch
            {
                ILocalSymbol ls => ls.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                IParameterSymbol ps => ps.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                _ => "?",
            },
            DeclaringReference = declaringRef,
            EnclosingMember = routine,
            HostType = hostType,
            RoutineName = routineName,
            Reason = TriggerReason.ExternalWrite,
            Parameter = parameter,
            Declarator = declarator,
            Skipped = true,
            SkipCode = code,
        };
    }

    private MemberDeclarationSyntax? ResolveRoutine(
        LambdaExpressionSyntax lambda,
        out string? boundaryCode,
        out SyntaxNode? boundaryNode)
    {
        boundaryCode = null;
        boundaryNode = null;

        for (var current = lambda.Parent; current != null; current = current.Parent)
        {
            switch (current)
            {
                case AccessorDeclarationSyntax accessor:
                    return accessor;
                case BaseMethodDeclarationSyntax method:
                    return method;
                case LocalFunctionStatementSyntax:
                    boundaryCode = ClosureLiftCodes.UnsupportedBoundary;
                    boundaryNode = current;
                    return null;
                case PropertyDeclarationSyntax:
                case IndexerDeclarationSyntax:
                case BaseFieldDeclarationSyntax:
                    boundaryCode = ClosureLiftCodes.UnsupportedBoundary;
                    boundaryNode = current;
                    return null;
            }
        }

        return null;
    }

    private static string BoundaryMessage(string code) => code switch
    {
        ClosureLiftCodes.UnsupportedBoundary =>
            "Lambda crosses a residual local-function boundary or lives in a field/property initializer; unsupported in v1 (D14/D16).",
        _ => "Unsupported lambda boundary.",
    };

    private static SyntaxNode? GetBodyRoot(MemberDeclarationSyntax member) => member switch
    {
        BaseMethodDeclarationSyntax bm => (SyntaxNode?)bm.Body ?? bm.ExpressionBody,
        AccessorDeclarationSyntax accessor => accessor.Body ?? accessor.ExpressionBody,
        _ => null,
    };

    private string GetRoutineName(MemberDeclarationSyntax routine, TypeDeclarationSyntax hostType)
    {
        if (routine is AccessorDeclarationSyntax accessor)
        {
            string containerName;
            if (accessor.Ancestors().OfType<IndexerDeclarationSyntax>().FirstOrDefault() != null)
                containerName = "Indexer";
            else
            {
                containerName = accessor.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault()?.Identifier.Text
                    ?? accessor.Ancestors().OfType<EventDeclarationSyntax>().FirstOrDefault()?.Identifier.Text
                    ?? "Accessor";
            }

            return $"{containerName}_{accessor.Keyword.Text}";
        }

        var declared = routine is BaseMethodDeclarationSyntax bm ? _model.GetDeclaredSymbol(bm) : null;
        return declared?.Name ?? routine switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text,
            DestructorDeclarationSyntax d => d.Identifier.Text,
            _ => hostType.Identifier.Text,
        };
    }

    private void Emit(
        string code,
        string message,
        Location? location,
        string? memberName = null,
        string? symbolName = null,
        TriggerReason? reason = null)
    {
        var severity = code switch
        {
            ClosureLiftCodes.RefCapture or ClosureLiftCodes.UnsupportedBoundary
                => _options.Strict ? ClosureLiftSeverity.Error : ClosureLiftSeverity.Warning,
            ClosureLiftCodes.AuditRow => ClosureLiftSeverity.Info,
            _ => ClosureLiftSeverity.Error,
        };

        var line = location?.GetLineSpan().StartLinePosition.Line + 1 ?? 0;
        _diagnostics.Add(new ClosureLiftDiagnostic(
            severity, code, message, null, line, memberName, symbolName, reason));
    }

    private sealed record MemberCaptureWork(
        TypeDeclarationSyntax HostType,
        MemberDeclarationSyntax Routine,
        string RoutineName)
    {
        public List<CapturedSymbolAnalysis> Symbols { get; } = new();
    }
}
```

- [ ] **Step 2: 编译确认（分析器暂无调用方，仅编译）**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

若编译器提示 `SimpleLambdaExpressionSyntax.Block/ExpressionBody` 属性名在当前 Roslyn 版本不同，以 IDE 自动补全为准（二者必有 Block 与 ExpressionBody 两个属性，意图不变），同步修正本计划执行记录。

- [ ] **Step 3: 提交**

```powershell
git add src/CSharpToJava.Core/ClosureLift/CaptureAnalysis.cs
git commit -m "feat(preprocessing): C32 M1 CaptureAnalyzer（D2触发/foreach保守/引号混合/边界防线，纯分析）"
```

## Task 5: ClosureLiftPreprocessor（M1 审计门面）+ ClosureLiftPassAdapter（M1 直连版）

**Files:**
- Create: `src/CSharpToJava.Core/ClosureLift/ClosureLiftPreprocessor.cs`
- Create: `src/CSharpToJava.CLI/Preprocessing/Adapters/ClosureLiftPassAdapter.cs`

M1 纪律（设计 §3.1）：门面**只审计、不改写**，任何选项组合下 `Changed` 恒为 `false`，输出与输入字节相同；M2 Task 12 才把审计尾替换为 Analyze→Plan→Generate→Rewrite。适配器 Task 16 前**不接入** `PassChainFactory`，用文件内私有 descriptor 直连（`ProjectPassRunner` 不查 `Status`，已核实）。

- [ ] **Step 1: 写 M1 审计门面**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// Entry point for the closure-lift preprocessor.
/// M1 (this task): audit only. It runs <see cref="CaptureAnalyzer"/> over each
/// file and emits CS2J3207 rows carrying the *prospective* holder class /
/// instance / field names; <see cref="ClosureLiftResult.Changed"/> is always
/// false and the output equals the input byte for byte. M2 Task 12 replaces
/// the audit tail with Analyze → Plan → Generate → Rewrite.
/// In project mode one instance is reused across files; the deterministic
/// cross-file class-name table lives on <see cref="_naming"/> (mirrors E02).
/// </summary>
public sealed class ClosureLiftPreprocessor
{
    private readonly ClosureLiftNaming _naming = new();

    public ClosureLiftResult Preprocess(
        string sourceCode,
        SemanticModel? model = null,
        ClosureLiftOptions? options = null)
    {
        options ??= new ClosureLiftOptions();
        var stats = new ClosureLiftStatistics { FilesScanned = 1 };
        var diagnostics = new List<ClosureLiftDiagnostic>();

        SemanticModel? resolvedModel = model;
        if (resolvedModel == null)
        {
            resolvedModel = BuildStandaloneModel(sourceCode, out var parseError);
            if (parseError != null)
            {
                stats.FilesSkippedClean = 1;
                return new ClosureLiftResult(
                    sourceCode, false, Array.Empty<ClosureLiftDiagnostic>(), stats);
            }
        }

        var tree = resolvedModel.SyntaxTree;
        var root = (CompilationUnitSyntax)tree.GetRoot();

        // Fast path: no lambdas and no residual anonymous methods (E05 output).
        if (!root.DescendantNodes().Any(n =>
                n is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax))
        {
            stats.FilesSkippedClean = 1;
            return new ClosureLiftResult(
                sourceCode, false, Array.Empty<ClosureLiftDiagnostic>(), stats);
        }

        var analysis = new CaptureAnalyzer(resolvedModel, options, diagnostics).Analyze(root);

        // Analyzer owns CS2J3201-3206 rows; bucket them for statistics here.
        foreach (var d in diagnostics)
        {
            if (d.Code != ClosureLiftCodes.AuditRow)
                stats.AddSkipped(d.Code);
        }

        if (analysis.AllSymbols.Count == 0)
        {
            stats.FilesSkippedClean = 1;
            return new ClosureLiftResult(sourceCode, false, diagnostics, stats);
        }

        // M1 audit-only tail. No counter named *Generated and no FilesTransformed
        // ever move here — nothing is generated or transformed. Task 12 replaces
        // this with the real pipeline gated on !options.AuditOnly.
        EmitAuditRows(analysis, resolvedModel, stats, diagnostics);
        return new ClosureLiftResult(sourceCode, false, diagnostics, stats);
    }

    /// <summary>
    /// Emit one CS2J3207 Info row per liftable symbol. Prospective naming uses
    /// the exact M2 grouping key preview (design §4.3: host member + declaring
    /// scope block; foreach per-iteration groups carry their own block) and the
    /// same naming API Task 11 will call, so audit names == generated names.
    /// Skipped symbols already carry their CS2J3201-3206 rows and never reserve names.
    /// </summary>
    private void EmitAuditRows(
        ClosureAnalysisResult analysis,
        SemanticModel model,
        ClosureLiftStatistics stats,
        List<ClosureLiftDiagnostic> diagnostics)
    {
        foreach (var member in analysis.Members)
        {
            var hostKey = model.GetDeclaredSymbol(member.HostType) is { } hostSymbol
                ? hostSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                : member.HostType.Identifier.Text;

            var liftable = member.Symbols.Where(s => !s.Skipped).ToList();
            var groups = liftable
                .GroupBy(s => (object?)s.ScopeBlock ?? s.EnclosingMember)
                .OrderBy(g => g.Min(s => s.DeclaringReference.Span.Start))
                .ToList();

            // Same helper the M2 planner (Task 8) uses; do not diverge.
            var routineNames = CollectRoutineLocalNames(member.Routine);
            var groupIndex = 0;
            foreach (var group in groups)
            {
                var className = _naming.ReserveClassName(hostKey, member.RoutineName);
                var instanceName = ClosureLiftNaming.ReserveInstanceName(groupIndex, routineNames);
                groupIndex++;

                var usedFieldNames = new HashSet<string>(StringComparer.Ordinal);
                if (group.Any(s => s.IsForeachIteration))
                    stats.ForeachPerIterationGroups++;

                foreach (var symbol in group)
                {
                    var fieldName = ClosureLiftNaming.GetFieldName(symbol.Name, usedFieldNames);
                    stats.CapturesByReason[symbol.Reason] =
                        stats.CapturesByReason.TryGetValue(symbol.Reason, out var n) ? n + 1 : 1;

                    var lineSpan = symbol.DeclaringReference.SyntaxTree
                        .GetLineSpan(symbol.DeclaringReference.Span);
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var shape = symbol.IsForeachIteration ? "foreach-per-iteration" : "shared";

                    diagnostics.Add(new ClosureLiftDiagnostic(
                        ClosureLiftSeverity.Info,
                        ClosureLiftCodes.AuditRow,
                        $"[audit] {member.RoutineName}.{symbol.Name}: {symbol.Reason} ({shape}), " +
                        $"sites={symbol.Sites.Count}, quoted={symbol.QuotedSites.Count}, " +
                        $"holder={className}, instance={instanceName}, field={fieldName}, type={symbol.TypeDisplay}",
                        null, line, member.RoutineName, symbol.Name, symbol.Reason));
                }
            }
        }
    }

    /// <summary>
    /// Seed set for per-routine instance reservation: the routine parameters plus
    /// every declarator / foreach iteration variable / local function name inside
    /// the routine body. Seeding nested-scope names is conservative (it can only
    /// force a _2 suffix); the M2 planner must call this identical helper.
    /// </summary>
    private static HashSet<string> CollectRoutineLocalNames(MemberDeclarationSyntax routine)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        if (routine is BaseMethodDeclarationSyntax method)
        {
            foreach (var parameter in method.ParameterList.Parameters)
            {
                var text = parameter.Identifier.Text;
                if (text.Length > 0)
                    names.Add(text);
            }
        }

        foreach (var declarator in routine.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            names.Add(declarator.Identifier.Text);
        foreach (var foreachLoop in routine.DescendantNodes().OfType<ForEachStatementSyntax>())
            names.Add(foreachLoop.Identifier.Text);
        foreach (var localFunction in routine.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            names.Add(localFunction.Identifier.Text);

        return names;
    }

    private static SemanticModel BuildStandaloneModel(string sourceCode, out string? parseError)
    {
        parseError = null;
        var tree = CSharpSyntaxTree.ParseText(sourceCode);

        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            parseError = string.Join("; ", errors.Select(e => e.GetMessage()));
        }

        var compilation = CSharpCompilation.Create("closure-lift-standalone",
            new[] { tree },
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            },
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));

        return compilation.GetSemanticModel(tree);
    }
}
```

- [ ] **Step 2: 写 M1 直连适配器**

```csharp
using CSharpToJava.Core.ClosureLift;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the closure-lift pass.
/// M1 (this task): deliberately NOT wired into PassChainFactory — the registry
/// flip is Task 16. The descriptor is file-private so the Stub entry in
/// PassStubCatalog stays the single registry truth until then; ProjectPassRunner
/// never consults PassStatus (verified), so the SDK harness / tests can already
/// drive this adapter directly. M1 processing is audit-only: every file is
/// copied verbatim and CS2J3207 rows land in Diagnostics.
/// </summary>
internal sealed class ClosureLiftPassAdapter : ProjectPassAdapter
{
    private static readonly PassDescriptor M1Descriptor = new(
        PassStubCatalog.Ids.ClosureLift,
        "Closure/display class lift",
        ".cs2j-closure-lift-src",
        PassStage.StateMachine,
        PassStatus.Implemented,
        defaultEnabled: false,
        runAfterIds: new[] { PassIds.RefOutIn },
        verifyBuildByDefault: true,
        verifyTestsByDefault: true,
        description: "M1 audit-only: report mutable lambda captures and prospective holder names; no rewrite yet.",
        catalogItems: "C32");

    private readonly ClosureLiftOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly ClosureLiftPreprocessor _preprocessor = new();
    private readonly List<ClosureLiftDiagnostic> _diagnostics = new();
    private readonly ClosureLiftStatistics _statistics = new();

    public ClosureLiftPassAdapter(
        ClosureLiftOptions options,
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

    public override PassDescriptor Descriptor => M1Descriptor;
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public ClosureLiftStatistics Statistics => _statistics;
    public IReadOnlyList<ClosureLiftDiagnostic> Diagnostics => _diagnostics;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.Preprocess(context.SourceCode, context.SemanticModel!, _options);
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
            _diagnostics.Add(diagnostic.WithFilePath(context.RelativePath));

        if (_verbose)
        {
            var auditRows = result.Diagnostics.Count(d => d.Code == ClosureLiftCodes.AuditRow);
            Console.WriteLine(
                $"[closure-lift] audit: {auditRows} candidate capture(s): {context.RelativePath}");
        }

        // M1: always verbatim. Task 12 returns a PassFileRewrite once rewriting exists.
        return Task.FromResult<PassFileRewrite?>(null);
    }

    // M1: the tree is a verbatim copy, so there is no residual gate yet.
    // Task 15 adds re-parse + residual mutable-capture counting here.
    public override Task AfterFilesAsync(PassTreeContext tree) => Task.CompletedTask;

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ClosureLiftDiagnostic(
            ClosureLiftSeverity.Warning,
            ClosureLiftCodes.UnsupportedBoundary,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new ClosureLiftDiagnostic(
            ClosureLiftSeverity.Error,
            ClosureLiftCodes.UnsupportedBoundary,
            $"dotnet build failed with exit code {exitCode} during closure-lift audit."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new ClosureLiftDiagnostic(
            ClosureLiftSeverity.Error,
            ClosureLiftCodes.UnsupportedBoundary,
            $"dotnet test failed with exit code {exitCode} during closure-lift audit."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == ClosureLiftSeverity.Error);

    public override string? BuildChainSummaryLine()
    {
        var candidates = _statistics.CapturesByReason.Values.Sum();
        var blocked = _statistics.SkippedByReason.Values.Sum();
        // M1 wording; Task 15 switches to the transform/holders/residual line.
        return $"closure-lift (audit): {_statistics.FilesScanned} scanned, " +
               $"{candidates} mutable captures reported, {blocked} blocked, " +
               $"{_statistics.ForeachPerIterationGroups} foreach group(s)";
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => verbose || d.Severity != ClosureLiftSeverity.Info)
            .Select(d => $"[{d.Severity}] {d.FilePath}:{d.Line}: {d.Message}")
            .ToList();

    private void MergeStatistics(ClosureLiftStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.HolderClassesGenerated += source.HolderClassesGenerated;
        _statistics.HolderInstancesGenerated += source.HolderInstancesGenerated;
        _statistics.CapturesLifted += source.CapturesLifted;
        _statistics.SitesRewritten += source.SitesRewritten;
        _statistics.ForeachPerIterationGroups += source.ForeachPerIterationGroups;
        _statistics.ResidualMutableCaptures += source.ResidualMutableCaptures;

        foreach (var kvp in source.CapturesByReason)
        {
            if (!_statistics.CapturesByReason.TryAdd(kvp.Key, kvp.Value))
                _statistics.CapturesByReason[kvp.Key] += kvp.Value;
        }

        foreach (var kvp in source.SkippedByReason)
        {
            if (!_statistics.SkippedByReason.TryAdd(kvp.Key, kvp.Value))
                _statistics.SkippedByReason[kvp.Key] += kvp.Value;
        }
    }
}
```

说明：`OnRestoreFailed/OnBuildFailed/OnTestsFailed` 的 Code 列复用 `UnsupportedBoundary` 仅为让行进入 `SkippedByReason` 同类桶；M2 Task 15 若引入独立 infra 码再调整（设计 §4.7 未为基础设施失败占号，不新造诊断码）。

- [ ] **Step 3: 编译 Core**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 4: 编译 CLI（适配器暂无调用方，仅编译）**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 5: 提交**

```powershell
git add src/CSharpToJava.Core/ClosureLift/ClosureLiftPreprocessor.cs
git add src/CSharpToJava.CLI/Preprocessing/Adapters/ClosureLiftPassAdapter.cs
git commit -m "feat(preprocessing): C32 M1 审计门面＋私有 descriptor 直连适配器（Changed 恒 false，不接注册表）"
```

## Task 6: M1 审计单测（Changed 恒 false + CS2J3201-3207 行为冻结）

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/ClosureLiftUnitTests.cs`

helpers 照抄 `IteratorLoweringUnitTests` L465-514（`LanguageVersion.Latest` + `TRUSTED_PLATFORM_ASSEMBLIES`）。本任务冻结 M1 契约：**任何输入输出字节不变**；触发原因、诊断码、候选命名全部可断言。

- [ ] **Step 1: 写完整测试文件**

```csharp
using CSharpToJava.Core.ClosureLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.ProjectTests;

public sealed class ClosureLiftUnitTests
{
    // ── M1 invariant: never rewrite ──

    [Fact]
    public void Audit_NoLambda_FastPath_IsCleanAndUnchanged()
    {
        var src = "class C { void M() { int x = 1; } }";
        var r = Audit(src);

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        Assert.Equal(1, r.Statistics.FilesScanned);
        Assert.Equal(1, r.Statistics.FilesSkippedClean);
        Assert.Empty(r.Diagnostics);
    }

    [Fact]
    public void Audit_ReadOnlyCapture_NoRow_Unchanged()
    {
        var src = @"
class C {
    int M() {
        int x = 41;
        System.Func<int> f = () => x + 1;
        return f();
    }
}";
        var r = Audit(src);

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "CS2J3207");
        Assert.Equal(1, r.Statistics.FilesSkippedClean);
    }

    // ── D2 trigger reasons ──

    [Fact]
    public void Audit_ExternalWrite_ReasonAndProspectiveNames()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { System.Console.WriteLine(x); };
        x = 1;
    }
}";
        var r = Audit(src);

        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J3207"));
        Assert.Equal(ClosureLiftSeverity.Info, row.Severity);
        Assert.Equal("x", row.SymbolName);
        Assert.Equal(TriggerReason.ExternalWrite, row.Reason);
        Assert.Contains("Cs2jDisplayClass_M_0", row.Message);
        Assert.Contains("__cs2jClosure0", row.Message);
        Assert.Contains("_cs2jCap_x", row.Message);
        Assert.Equal(1, r.Statistics.CapturesByReason[TriggerReason.ExternalWrite]);
        Assert.Equal(0, r.Statistics.FilesTransformed);
    }

    [Fact]
    public void Audit_InsideLambdaWrite_Reason()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { x++; };
    }
}";
        var r = Audit(src);

        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J3207"));
        Assert.Equal(TriggerReason.InsideLambdaWrite, row.Reason);
        Assert.Contains("shared", row.Message);
        Assert.Contains("sites=1", row.Message);
    }

    [Fact]
    public void Audit_RefOutArgument_ReasonTakesPrecedence()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { int.TryParse(""1"", out x); };
    }
}";
        var r = Audit(src);

        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J3207"));
        Assert.Equal(TriggerReason.RefOutArgument, row.Reason);
    }

    [Fact]
    public void Audit_ForeachIteration_PerIterationShapeAndStat()
    {
        var src = @"
class C {
    void M() {
        foreach (var x in new[] { 1, 2, 3 })
        {
            System.Action a = () => { System.Console.WriteLine(x); };
        }
    }
}";
        var r = Audit(src);

        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J3207"));
        Assert.Equal(TriggerReason.ForeachIteration, row.Reason);
        Assert.Contains("foreach-per-iteration", row.Message);
        Assert.Equal(1, r.Statistics.ForeachPerIterationGroups);
    }

    [Fact]
    public void Audit_ParameterWithExternalWrite_RowForParameter()
    {
        var src = @"
class C {
    void M(int p) {
        System.Action a = () => { System.Console.WriteLine(p); };
        p = 2;
    }
}";
        var r = Audit(src);

        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J3207"));
        Assert.Equal("p", row.SymbolName);
        Assert.Equal(TriggerReason.ExternalWrite, row.Reason);
        Assert.Contains("_cs2jCap_p", row.Message);
    }

    // ── grouping / naming ──

    [Fact]
    public void Audit_TwoSymbolsSameScope_OneHolderTwoFields()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        int y = 0;
        System.Action a = () => { x++; y++; };
    }
}";
        var r = Audit(src);

        var rows = r.Diagnostics.Where(d => d.Code == "CS2J3207").ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, d => Assert.Contains("Cs2jDisplayClass_M_0", d.Message));
        Assert.All(rows, d => Assert.Contains("__cs2jClosure0", d.Message));
        Assert.Contains(rows, d => d.Message.Contains("_cs2jCap_x"));
        Assert.Contains(rows, d => d.Message.Contains("_cs2jCap_y"));
        Assert.Equal(2, r.Statistics.CapturesByReason[TriggerReason.InsideLambdaWrite]);
    }

    [Fact]
    public void Audit_CrossFileNaming_SameHostContinuesIndex_DifferentHostRestarts()
    {
        var pp = new ClosureLiftPreprocessor();

        var a1 = @"
class A {
    void M() {
        int x = 0;
        System.Action a = () => { x++; };
    }
}";
        var a2 = @"
class A {
    void M() {
        int y = 0;
        System.Action b = () => { y++; };
    }
}";
        var b1 = @"
class B {
    void M() {
        int z = 0;
        System.Action c = () => { z++; };
    }
}";

        var r1 = Audit(pp, a1);
        var r2 = Audit(pp, a2);
        var r3 = Audit(pp, b1);

        Assert.Contains(r1.Diagnostics, d => d.Message.Contains("Cs2jDisplayClass_M_0"));
        Assert.Contains(r2.Diagnostics, d => d.Message.Contains("Cs2jDisplayClass_M_1"));
        Assert.Contains(r3.Diagnostics, d => d.Message.Contains("Cs2jDisplayClass_M_0"));
    }

    // ── CS2J3201 / 3203 / 3204 / 3205 / 3206 diagnostics ──

    [Fact]
    public void Audit_ByRefParameter_CS2J3201_ErrorInStrict()
    {
        var src = @"
class C {
    void M(ref int p) {
        System.Action a = () => { System.Console.WriteLine(p); };
    }
}";
        var r = Audit(src);

        var d = Assert.Single(r.Diagnostics.Where(x => x.Code == "CS2J3201"));
        Assert.Equal(ClosureLiftSeverity.Error, d.Severity);
        Assert.Equal("p", d.SymbolName);
        Assert.DoesNotContain(r.Diagnostics, x => x.Code == "CS2J3207");
        Assert.Equal(1, r.Statistics.SkippedByReason["CS2J3201"]);
    }

    [Fact]
    public void Audit_RefLocal_CS2J3201_ErrorInStrict()
    {
        var src = @"
class C {
    void M() {
        int y = 0;
        ref int p = ref y;
        System.Action a = () => { var z = p; };
    }
}";
        var r = Audit(src);

        Assert.Contains(r.Diagnostics, d =>
            d.Code == "CS2J3201" && d.Severity == ClosureLiftSeverity.Error);
    }

    [Fact]
    public void Audit_NonStrict_RefCaptureDowngradedToWarning()
    {
        var src = @"
class C {
    void M(ref int p) {
        System.Action a = () => { System.Console.WriteLine(p); };
    }
}";
        var r = Audit(src, new ClosureLiftOptions(Strict: false));

        var d = Assert.Single(r.Diagnostics.Where(x => x.Code == "CS2J3201"));
        Assert.Equal(ClosureLiftSeverity.Warning, d.Severity);
    }

    [Fact]
    public void Audit_ResidualAnonymousMethod_CS2J3203()
    {
        var src = @"
class C {
    void M() {
        System.Comparison<int> c = delegate(int a, int b) { return a - b; };
    }
}";
        var r = Audit(src);

        Assert.Contains(r.Diagnostics, d =>
            d.Code == "CS2J3203" && d.Severity == ClosureLiftSeverity.Error);
        Assert.False(r.Changed);
    }

    [Fact]
    public void Audit_LambdaInsideLocalFunction_CS2J3204()
    {
        var src = @"
class C {
    void M() {
        void Local() {
            System.Action a = () => { System.Console.WriteLine(1); };
        }
        Local();
    }
}";
        var r = Audit(src);

        Assert.Contains(r.Diagnostics, d =>
            d.Code == "CS2J3204" && d.Severity == ClosureLiftSeverity.Error);
    }

    [Fact]
    public void Audit_LambdaInFieldInitializer_CS2J3204()
    {
        var src = @"
class C {
    System.Func<int> F = () => 1;
}";
        var r = Audit(src);

        Assert.Contains(r.Diagnostics, d =>
            d.Code == "CS2J3204" && d.Severity == ClosureLiftSeverity.Error);
    }

    [Fact]
    public void Audit_PointerCapture_CS2J3205()
    {
        var src = @"
unsafe class C {
    void M() {
        int* p = null;
        System.Action a = () => { var z = p; };
    }
}";
        var r = Audit(src);

        Assert.Contains(r.Diagnostics, d =>
            d.Code == "CS2J3205" && d.Severity == ClosureLiftSeverity.Error);
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "CS2J3207");
    }

    [Fact]
    public void Audit_ExpressionTreeMix_CS2J3206()
    {
        var src = @"
using System;
using System.Linq.Expressions;
class C {
    void M() {
        int x = 0;
        Expression<Func<int>> q = () => x;
        Action a = () => { x++; };
    }
}";
        var r = Audit(src);

        Assert.Contains(r.Diagnostics, d =>
            d.Code == "CS2J3206" && d.Severity == ClosureLiftSeverity.Error);
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "CS2J3207");
        Assert.Equal(1, r.Statistics.SkippedByReason["CS2J3206"]);
    }

    // ── site filtering / idempotence / parse errors ──

    [Fact]
    public void Audit_NameOfSite_ExcludedFromSiteCount()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Func<string> f = () => nameof(x);
        x = 1;
    }
}";
        var r = Audit(src);

        // Only the outer assignment LVS counts; nameof(x) is a string literal site.
        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J3207"));
        Assert.Contains("sites=1", row.Message);
    }

    [Fact]
    public void Audit_RepeatedRuns_ByteIdenticalAndDeterministic()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { x++; };
    }
}";
        var pp = new ClosureLiftPreprocessor();
        var r1 = Audit(pp, src);
        var r2 = Audit(pp, src);
        var r3 = Audit(new ClosureLiftPreprocessor(), src);

        Assert.False(r1.Changed);
        Assert.False(r2.Changed);
        Assert.False(r3.Changed);
        Assert.Equal(src, r1.OutputCode);
        Assert.Equal(src, r2.OutputCode);
        Assert.Equal(src, r3.OutputCode);

        var m1 = string.Join("\n", r1.Diagnostics.Select(d => d.ToString()));
        var m2 = string.Join("\n", r2.Diagnostics.Select(d => d.ToString()));
        var m3 = string.Join("\n", r3.Diagnostics.Select(d => d.ToString()));
        Assert.Equal(m1, m2);
        Assert.Equal(m1, m3);
    }

    [Fact]
    public void Audit_StandaloneModel_ParseError_ReturnsUnchangedClean()
    {
        var broken = "class C { void M() { ";
        var r = new ClosureLiftPreprocessor().Preprocess(broken);

        Assert.False(r.Changed);
        Assert.Equal(broken, r.OutputCode);
        Assert.Equal(1, r.Statistics.FilesSkippedClean);
        Assert.Empty(r.Diagnostics);
    }

    // ── Helpers (mirror IteratorLoweringUnitTests L465-514) ──

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "closure-audit-test",
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
                if (File.Exists(assembly))
                    yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static ClosureLiftResult Audit(string src, ClosureLiftOptions? options = null)
        => Audit(new ClosureLiftPreprocessor(), src, options);

    private static ClosureLiftResult Audit(
        ClosureLiftPreprocessor preprocessor,
        string src,
        ClosureLiftOptions? options = null)
        => preprocessor.Preprocess(src, ParseWithSemantics(src), options);
}
```

- [ ] **Step 2: 跑本任务全部测试**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~ClosureLiftUnitTests" --nologo -v q`
Expected: `失败: 0`，20 个测试全部通过。

- [ ] **Step 3: E02/旧回归抽验（审计代码路径与 E02 共享 CaptureClassification，确认未被污染）**

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~LocalFunction" --nologo -v q`
Expected: `失败: 0`，通过总数与 Task 1 Step 1 快照一致。

- [ ] **Step 4: 提交**

```powershell
git add tests/CSharpToJava.ProjectTests/ClosureLiftUnitTests.cs
git commit -m "test(preprocessing): C32 M1 审计单测 20 条（触发原因/3201-3207/命名分组/零变化幂等）"
```

## Task 7: SDK 命中率审计 harness（env 驱动，永不 Skip）＋ M1 里程碑全量验证

**Files:**
- Create: `tests/CSharpToJava.ProjectTests/ClosureLiftSdkAuditHarness.cs`
- 执行时才生成（**本计划不预建、不提交**）：`docs/superpowers/specs/2026-10-02-c32-sdk-capture-hitrate.md`

harness 是一个 xUnit Fact：设置 `CS2J_CLOSURE_AUDIT_ROOT` 时审计真实 SDK 源树并落盘命中率报告；未设置时跑**内置冒烟目录**（无 skip、永远确定性通过）。任何模式下都断言 `Changed==false` 且输出字节相同（M1 红线）。

- [ ] **Step 1: 写 harness 完整代码**

```csharp
using System.Text;
using CSharpToJava.Core.ClosureLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// M1 SDK hit-rate audit (design §9 milestone M1).
/// Env CS2J_CLOSURE_AUDIT_ROOT: audit that root; project groups are csproj
/// directories (fallback: the root itself), each compiled as one CSharpCompilation
/// with trusted-platform references. Without the env var a deterministic temp
/// smoke tree is audited — this test NEVER skips.
/// In env mode a markdown report is written to docs/superpowers/specs/
/// 2026-10-02-c32-sdk-capture-hitrate.md (execution artifact; not pre-created).
/// </summary>
public sealed class ClosureLiftSdkAuditHarness
{
    private const string RootEnv = "CS2J_CLOSURE_AUDIT_ROOT";
    private const string ReportFileName = "2026-10-02-c32-sdk-capture-hitrate.md";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);
    private static readonly ClosureLiftOptions AuditOptions = new(Strict: false);

    private readonly ITestOutputHelper _output;

    public ClosureLiftSdkAuditHarness(ITestOutputHelper output) => _output = output;

    [Fact]
    public void AuditOnly_NeverChangesFiles_AndReportsHitRate()
    {
        var envRoot = Environment.GetEnvironmentVariable(RootEnv);
        string auditRoot;
        bool envMode;
        string? tempRoot = null;

        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            auditRoot = Path.GetFullPath(envRoot);
            Assert.True(Directory.Exists(auditRoot), $"{RootEnv} does not exist: {auditRoot}");
            envMode = true;
        }
        else
        {
            tempRoot = Directory.CreateDirectory(Path.Combine(
                Path.GetTempPath(), "cs2j-closure-audit-" + Guid.NewGuid().ToString("N"))).FullName;
            SeedSmokeTree(tempRoot);
            auditRoot = tempRoot;
            envMode = false;
        }

        try
        {
            var bag = new AuditBag();
            foreach (var projectRoot in FindProjectRoots(auditRoot))
                AuditProject(projectRoot, bag);

            _output.WriteLine(bag.Summary());

            // M1 hard red line, in every mode.
            Assert.Equal(0, bag.FilesChanged);
            Assert.True(bag.FilesScanned > 0, "no .cs files found under audit root");

            if (envMode)
            {
                var reportPath = WriteReport(auditRoot, bag);
                _output.WriteLine($"report: {reportPath}");
            }
            else
            {
                Assert.Equal(4, bag.FilesScanned);
                Assert.Equal(2, bag.FilesClean);
                Assert.Equal(2, bag.RowsByCode.GetValueOrDefault("CS2J3207"));
                Assert.True(bag.RowsByReason.ContainsKey(TriggerReason.ExternalWrite));
                Assert.True(bag.RowsByReason.ContainsKey(TriggerReason.ForeachIteration));
            }
        }
        finally
        {
            if (tempRoot != null && Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void AuditProject(string projectRoot, AuditBag bag)
    {
        var files = EnumerateCsFiles(projectRoot)
            .Where(f => bag.SeenFiles.Add(Path.GetFullPath(f)))
            .ToList();
        if (files.Count == 0)
            return;

        var trees = files
            .Select(f => CSharpSyntaxTree.ParseText(
                File.ReadAllText(f), ParseOptions, path: f))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "closure-audit-" + Guid.NewGuid().ToString("N"),
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new ClosureLiftPreprocessor();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var content = tree.ToString();
            var relative = Path.GetRelativePath(projectRoot, tree.FilePath);

            var result = preprocessor.Preprocess(content, model, AuditOptions);
            bag.FilesScanned++;
            bag.FilesChanged += result.Changed ? 1 : 0;
            bag.FilesClean += result.Statistics.FilesSkippedClean;
            bag.FilesTransformed += result.Statistics.FilesTransformed;
            foreach (var kvp in result.Statistics.SkippedByReason)
                bag.RowsByCode[kvp.Key] = bag.RowsByCode.GetValueOrDefault(kvp.Key) + kvp.Value;
            foreach (var kvp in result.Statistics.CapturesByReason)
                bag.RowsByReason[kvp.Key] = bag.RowsByReason.GetValueOrDefault(kvp.Key) + kvp.Value;

            foreach (var d in result.Diagnostics)
            {
                bag.RowsByCode[d.Code] = bag.RowsByCode.GetValueOrDefault(d.Code) + 1;
                if (d.Reason.HasValue)
                    bag.RowsByReason[d.Reason.Value] = bag.RowsByReason.GetValueOrDefault(d.Reason.Value) + 1;
                bag.Rows.Add((relative, d.Code, d.Severity.ToString(), d.Line, d.Message));
            }

            // M1 contract, per file.
            Assert.False(result.Changed, $"audit rewrote {tree.FilePath}");
            Assert.Equal(content, result.OutputCode);
        }
    }

    private static List<string> FindProjectRoots(string root)
    {
        var csprojDirs = Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Select(d => Path.GetFullPath(Path.GetDirectoryName(d)!))
            .Distinct(StringComparer.Ordinal)
            .OrderList();
        return csprojDirs.Count == 0
            ? new List<string> { Path.GetFullPath(root) }
            : csprojDirs;
    }

    private static List<string> EnumerateCsFiles(string dir)
        => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(seg =>
                seg.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                seg.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            .OrderList();

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                    yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static void SeedSmokeTree(string root)
    {
        File.WriteAllText(Path.Combine(root, "Counter.cs"), @"
class SampleCounter
{
    void Run()
    {
        int c = 0;
        System.Action a = () => System.Console.WriteLine(c);
        c++;
    }
}");
        File.WriteAllText(Path.Combine(root, "ForeachCase.cs"), @"
class SampleForeach
{
    void Run()
    {
        foreach (var item in new[] { 1, 2, 3 })
        {
            System.Action a = () => System.Console.WriteLine(item);
        }
    }
}");
        File.WriteAllText(Path.Combine(root, "ReadOnly.cs"), @"
class SampleReadOnly
{
    int Run()
    {
        var x = 41;
        System.Func<int> f = () => x + 1;
        return f();
    }
}");
        File.WriteAllText(Path.Combine(root, "Plain.cs"), "class SamplePlain { int Run() => 1; }");
    }

    private static string WriteReport(string root, AuditBag bag)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# C32 M1 SDK capture hit-rate audit");
        sb.AppendLine();
        sb.AppendLine($"- Root: `{root}`");
        sb.AppendLine($"- Generated (UTC): {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine("- Mode: AuditOnly (M1; output byte-identical, nothing rewritten)");
        sb.AppendLine("- Options: Strict=false; references=TRUSTED_PLATFORM_ASSEMBLIES; compile group = csproj dir");
        sb.AppendLine();
        sb.AppendLine("## Totals");
        sb.AppendLine();
        sb.AppendLine("| Metric | Count |");
        sb.AppendLine("| --- | ---: |");
        sb.AppendLine($"| .cs files scanned | {bag.FilesScanned} |");
        sb.AppendLine($"| files clean (no liftable candidate) | {bag.FilesClean} |");
        sb.AppendLine($"| files changed (must be 0 in M1) | {bag.FilesChanged} |");
        sb.AppendLine($"| CS2J3207 audit rows (liftable symbols) | {bag.RowsByCode.GetValueOrDefault("CS2J3207")} |");
        sb.AppendLine();
        sb.AppendLine("## Rows by code");
        sb.AppendLine();
        sb.AppendLine("| Code | Count |");
        sb.AppendLine("| --- | ---: |");
        foreach (var kvp in bag.RowsByCode.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.AppendLine($"| {kvp.Key} | {kvp.Value} |");
        sb.AppendLine();
        sb.AppendLine("## Rows by trigger reason (CS2J3207)");
        sb.AppendLine();
        sb.AppendLine("| Reason | Count |");
        sb.AppendLine("| --- | ---: |");
        foreach (var kvp in bag.RowsByReason.OrderBy(k => k.Key.ToString(), StringComparer.Ordinal))
            sb.AppendLine($"| {kvp.Key} | {kvp.Value} |");
        sb.AppendLine();
        sb.AppendLine("## First 200 rows");
        sb.AppendLine();
        foreach (var (relative, code, severity, line, message) in bag.Rows.Take(200))
            sb.AppendLine($"- `{relative}:{line}` [{severity}] {code}: {message}");
        if (bag.Rows.Count > 200)
            sb.AppendLine($"- … {bag.Rows.Count - 200} more rows omitted");
        sb.AppendLine();
        sb.AppendLine("## Methodology caveats");
        sb.AppendLine();
        sb.AppendLine("- Cross-project symbols are not linked; ErrorType symbols may surface as CS2J3202 noise.");
        sb.AppendLine("- Generators, #line-mapped files and MSBuild items are not modeled.");
        sb.AppendLine("- Hit rate = CS2J3207 rows per scanned file; the file is an M1 planning input, not a quality verdict.");
        sb.AppendLine();

        var repoRoot = FindRepoRoot();
        var reportPath = Path.Combine(
            repoRoot, "docs", "superpowers", "specs", ReportFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, sb.ToString());
        return reportPath;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CSharpToJavaConverter.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("repo root (CSharpToJavaConverter.slnx) not found.");
    }

    private sealed class AuditBag
    {
        public int FilesScanned;
        public int FilesChanged;
        public int FilesClean;
        public int FilesTransformed;
        public HashSet<string> SeenFiles = new(StringComparer.Ordinal);
        public Dictionary<string, int> RowsByCode = new(StringComparer.Ordinal);
        public Dictionary<TriggerReason, int> RowsByReason = new();
        public List<(string Relative, string Code, string Severity, int Line, string Message)> Rows = new();

        public string Summary()
            => $"[closure-lift] scanned={FilesScanned}, clean={FilesClean}, changed={FilesChanged}, " +
               $"auditRows={RowsByCode.GetValueOrDefault("CS2J3207")}, blocked=" +
               $"{RowsByCode.Where(kv => kv.Key is not "CS2J3207").Sum(kv => kv.Value)}";
    }
}

internal static class OrderListExtensions
{
    public static List<string> OrderList(this IEnumerable<string> source)
        => source.OrderBy(p => p, StringComparer.Ordinal).ToList();
}
```

- [ ] **Step 2: 跑冒烟模式（无 env，必须通过）**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~ClosureLiftSdkAuditHarness" --nologo -v q`
Expected: `失败: 0`；输出含 `[closure-lift] scanned=4, clean=2, changed=0, auditRows=2`。

- [ ] **Step 3: M1 里程碑——解决方案 Release 全量构建**

Run: `dotnet build CSharpToJavaConverter.slnx -c Release --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 4: M1 里程碑——三个测试工程 Release 全绿**

Run（逐条执行，每条都 `失败: 0`）：

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release --nologo -v q
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --nologo -v q
dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj -c Release --nologo -v q
```

重点人工核对：旧回归 `LambdaExternalReassignmentTests`、`LinqRewriterVariableCaptureTests` 所在工程全绿（转换器旧代码一行未改）。

- [ ] **Step 5:（人工闸门，按需执行）真实 SDK 源命中率**

```powershell
$env:CS2J_CLOSURE_AUDIT_ROOT = "D:\path\to\some-sdk-style-repo"
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj `
  --filter "FullyQualifiedName~ClosureLiftSdkAuditHarness" --nologo -v q
Remove-Item Env:\CS2J_CLOSURE_AUDIT_ROOT
```

执行后人工检视 `docs/superpowers/specs/2026-10-02-c32-sdk-capture-hitrate.md`（harness 生成，**本任务不 git add、不提交**；M2 结束 Task 20 重新生成并在 Task 21 引用结论）。无外部源时跳过本步，不阻塞 M1。

- [ ] **Step 6: 提交 harness（仅此一个文件）**

```powershell
git add tests/CSharpToJava.ProjectTests/ClosureLiftSdkAuditHarness.cs
git commit -m "test(preprocessing): C32 M1 SDK 命中率审计 harness（env 驱动/无 env 冒烟/Changed 恒零）"
```

**M1 出口检查单（设计 §9）**

- [ ] ClosureLift 目录 7 个文件（Options/Statistics/Diagnostic/Result/HolderPlan/Naming/Stage/CaptureAnalysis/Preprocessor）＋ Analysis/CaptureClassification.cs 齐备，Core/CLI Release 0 错误。
- [ ] 注册表、PassChainFactory、转换器旧代码（StatementTransformer*/LambdaTransformer/MethodConversionState/ArgumentTransformer.Ref）零改动；`git status --porcelain` 中无这些路径。
- [ ] ClosureLiftUnitTests 20/20、LocalFunction 旧回归数与 Task 1 Step 1 一致、三测试工程 Release 全绿。
- [ ] 全链路 default-off：未显式 `--enable-pass closure-lift` 时 `convert-project` 行为与基线逐字节一致（M1 适配器未入厂，结构上保证）。
- [ ] M1 结束点 tag 为本地提交序列，不打 git tag、不翻 `defaultEnabled`（Task 16 才翻注册表，且翻后仍 default-off）。

# M2 — holder 提升 + stub 翻转（Tasks 8-20，默认关）

**出口（设计 §9）**：class 形态全规则（D1-D16）落地；输出 C# Release 可 build/test、行为与原程序等价；§7.1 全 25 条 Core 单测绿；residual 闸门为 0；§3.2 五文件翻转一次完成（PassStubCatalog/PassCatalog/PassChainFactory＋两个排序测试），翻转后仍 **default-off**，默认链逐字节不变；SampleClosureLiftSolution fixture Release build/test 绿；转换器旧代码一行不改。

**M2 行号基线重核（2026-10-02，HEAD `aef9436a` 实测）**：开工后 dev-3.x 已从 M1 锚点 `ff63b20f` 前进（C20 partial-merge、C43 系列落地），§2 表中五个热文件行号整体下移；**M2 各 Task 的行号以本节为准**（实施时仍逐文件 Read 复核；若再 `pull --ff-only` 拉入新提交，按新 HEAD 重核并回填本节）：

| 文件 | M2 实测位置（HEAD aef9436a） |
|------|------|
| PassStubCatalog.cs | closure-lift stub 条目 **L77-86（Task 16 删）**；`Ids.ClosureLift` 常量 **L30（保留）**；async-normalize stub **L146-155**（`runAfterIds: Ids.ClosureLift` **L153**，不动） |
| PassCatalog.cs | unsafe-ffm descriptor **L319-331**；iterator-lowering **L332-343**（`runAfterIds` **L339** 追加 ClosureLift 边）；eliminate-goto **L344-353**（补边先例 **L351**）；member-desugar **L354-365**（closure 边已在 **L361**，不动）；equality-concat **L366-380**；java-keywords **L381-399**；`ById` **L402-403** |
| PassChainFactory.cs | `PassChainConfiguration` **L39-93（不加字段）**；`BuildLegacyToggles` **L104-134（不加键）**；unsafe-ffm 臂 **L327-333**；partial-merge 臂 **L335-341**；iterator 臂 **L343-350**（ClosureLift 臂插其前，options 内联 `new()`、末参传 `config.Strict`）；default throw **L391** |
| PassChainOrderTests.cs | `LockedChain` 29 项 **L19-51**（ClosureLift 插 **L45 UnsafeFfm 与 L46 IteratorLowering 之间**→30 项）；`EnabledByDefault` **L54-57**（加 `&& id != ClosureLift`）；catalog 映射 **L227-246**（加 C32 断言）；适配器全覆盖 **L249-271**（config 工厂集 L251-263 **无需新增**——options 内联 `new()`）；legacy toggle 排除清单 **L363-369**（必须加 ClosureLift） |
| PassStubCatalogTests.cs | 启用计划用例 **L57-69**（靶子 **L63/L67**）；CS2J9001 用例 **L71-111**（靶子 **L97/L109**；CliChainRequest 各 OptionsFactory 抛异常列表 L86-95 **无需新增**——async 仍是 stub，factory 抛前先被 CS2J9001 拦截） |
| CSharpToJava.ProjectTests.csproj | 新 ItemGroup 插 **C20 ItemGroup（L257-264）之后、L266 `</Project>` 之前**；Content 先例：SampleEnumDeclaration L142-147（Exclude bin/obj）、C28 SampleLexicalNormalize L248-255、C20 SamplePartial L257-264 |

**M2 形态裁决（实施细节，设计 §4.4 已授权）**：实例存储一律采用设计 §4.4 允许的两种形态之二——**实例声明 + 紧随字段赋值**（`var __cs2jClosureN = new C();` 然后 `__cs2jClosureN._cs2jCap_x = init;`），不用对象初始化器形态：单一代码路径天然正确处理同组初始值交叉引用（`int y = x;` 两符号同组）、无初始值（省略赋值，字段零初始化与合法 C# 程序的明确赋值规则等价）、多声明器。foreach/参数/for 三类站点的初始赋值位置见 Task 10。

## Task 8: HolderPlan 记录体 + HolderSyntaxGenerator（class/数组双形态 SyntaxFactory）

**Files:**
- Modify: `src/CSharpToJava.Core/ClosureLift/HolderPlan.cs`（M1 仅有 TriggerReason；本任务补全 §4.3 记录）
- Create: `src/CSharpToJava.Core/ClosureLift/HolderSyntaxGenerator.cs`

- [ ] **Step 1: HolderPlan.cs 整体替换为 M2 完整版**

保留 M1 的 `TriggerReason` 枚举不动，文件头补两个 using，末尾追加记录体：

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>Why a symbol is lifted (design D2). Primary reason with deterministic precedence.</summary>
public enum TriggerReason
{
    ExternalWrite,
    InsideLambdaWrite,
    RefOutArgument,
    ForeachIteration,
}

/// <summary>
/// One frozen lift unit (design §4.3): all symbols sharing one holder class
/// (or one array-form storage set) and one instance. Group key is
/// (host member, declaration scope, foreach-per-iteration loop). Built by
/// HolderPlanner (Task 11) from CapturedSymbolAnalysis; the audit tail and
/// the transform path consume the SAME plans, so audit names == generated names.
/// </summary>
internal sealed class HolderGroupPlan
{
    /// <summary>Type declaration the nested holder class is inserted into.</summary>
    public required TypeDeclarationSyntax HostType { get; init; }

    /// <summary>Routine the captures belong to (naming anchor + analysis root).</summary>
    public required MemberDeclarationSyntax HostMember { get; init; }

    /// <summary>Fully qualified display key of the host type (cross-file table key).</summary>
    public required string HostTypeKey { get; init; }

    /// <summary>Cs2jDisplayClass_&lt;member&gt;_&lt;n&gt; (unused textually in array form; still reserved).</summary>
    public required string ClassName { get; init; }

    /// <summary>__cs2jClosure&lt;n&gt;; array form uses per-symbol __cs2jArr_ names instead.</summary>
    public required string InstanceName { get; init; }

    /// <summary>True only for foreach iteration groups (instance created per iteration, D9).</summary>
    public required bool PerIteration { get; init; }

    /// <summary>Non-default comparator shape (design §4.4 tail / D1): one T[] per symbol.</summary>
    public required bool ArrayForm { get; init; }

    /// <summary>The foreach loop when <see cref="PerIteration"/>; null otherwise.</summary>
    public ForEachStatementSyntax? PerIterationLoop { get; init; }

    /// <summary>Lifted symbols in deterministic order (DeclaringNode.SpanStart), Task 11 freezes it.</summary>
    public List<CapturedSymbolPlan> Symbols { get; } = new();
}

/// <summary>One lifted symbol within a <see cref="HolderGroupPlan"/>.</summary>
internal sealed record CapturedSymbolPlan(
    ISymbol Symbol,
    string OriginalName,
    /// <summary>Holder-class public field: _cs2jCap_&lt;name&gt; (collision-suffixed).</summary>
    string FieldName,
    /// <summary>Array-form storage local: __cs2jArr_&lt;name&gt;; equals FieldName in holder form.</summary>
    string StorageName,
    string TypeDisplayFullyQualified,
    SyntaxNode DeclaringNode,
    TriggerReason Reason,
    bool IsForeachIteration,
    ForEachStatementSyntax? ForeachLoop,
    ForStatementSyntax? ForLoop,
    VariableDeclaratorSyntax? Declarator,
    ParameterSyntax? Parameter,
    /// <summary>Frozen non-quoted sites (read/write/lock); the rewriter only touches these nodes.</summary>
    IReadOnlyList<ClosureSiteRef> Sites);
```

说明：`ClosureSiteRef`（Task 4 已落 `CaptureAnalysis.cs`，含 `SyntaxReference Reference/ClosureSiteKind Kind/CaptureWriteKind? WriteKind/InsideLambda/InsideQuoted`）即设计 §4.3 的 `SiteRef`，不另建第二份记录。

- [ ] **Step 2: 写 HolderSyntaxGenerator.cs（纯 SyntaxFactory，无 SemanticModel）**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// Annotation helpers (design §4.1 idempotence). Annotations live on in-memory
/// nodes only and never survive the text round-trip; the durable re-entry
/// guards are the reserved name prefixes (__cs2jClosure* / __cs2jArr_ /
/// Cs2jDisplayClass_* / _cs2jCap_), which CaptureAnalyzer already skips.
/// </summary>
internal static class ClosureLiftAnnotations
{
    public const string Kind = "cs2j_closure_lift";

    public static SyntaxAnnotation HolderClass(string className) => new(Kind, "class:" + className);

    public static SyntaxAnnotation HolderInstance(string instanceName) => new(Kind, "instance:" + instanceName);
}

/// <summary>
/// Builds the holder shape (design §4.4): private sealed nested class with
/// public mutable fields; instance declaration + field-assignment statements;
/// and the non-default single-element T[] comparator shape.
/// No method/property members are emitted — the class is the strict
/// field-only subset of a C45 display class (design §3.4).
/// </summary>
internal sealed class HolderSyntaxGenerator
{
    /// <summary>
    /// private sealed class Cs2jDisplayClass_&lt;member&gt;_&lt;n&gt;
    /// {
    ///     public &lt;T&gt; _cs2jCap_&lt;name&gt;;
    /// }
    /// </summary>
    public ClassDeclarationSyntax BuildHolderClass(HolderGroupPlan group)
    {
        var fields = group.Symbols
            .Select(s => (MemberDeclarationSyntax)SyntaxFactory.FieldDeclaration(
                SyntaxFactory.List(new[] { SyntaxFactory.Token(SyntaxKind.PublicKeyword) }),
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.ParseTypeName(s.TypeDisplayFullyQualified),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(s.FieldName)))))
            .ToArray();

        return SyntaxFactory.ClassDeclaration(group.ClassName)
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword))
            .WithMembers(SyntaxFactory.List(fields))
            .WithAdditionalAnnotations(ClosureLiftAnnotations.HolderClass(group.ClassName));
    }

    /// <summary>var __cs2jClosureN = new Cs2jDisplayClass_&lt;member&gt;_&lt;n&gt;();</summary>
    public LocalDeclarationStatementSyntax BuildHolderInstanceDeclaration(HolderGroupPlan group)
    {
        var objectCreation = SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.ParseTypeName(group.ClassName))
            .WithArgumentList(SyntaxFactory.ArgumentList());

        var declarator = SyntaxFactory.VariableDeclarator(group.InstanceName)
            .WithInitializer(SyntaxFactory.EqualsValueClause(objectCreation));

        return SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(VarType(), SyntaxFactory.SingletonSeparatedList(declarator)))
            .WithAdditionalAnnotations(ClosureLiftAnnotations.HolderInstance(group.InstanceName));
    }

    /// <summary>__cs2jClosureN._cs2jCap_x = value;</summary>
    public ExpressionStatementSyntax BuildFieldAssignment(
        string instanceName,
        string fieldName,
        ExpressionSyntax value)
        => SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                BuildFieldAccess(instanceName, fieldName),
                value));

    /// <summary>__cs2jClosureN._cs2jCap_x — the only site shape C45 must keep (design §3.4).</summary>
    public MemberAccessExpressionSyntax BuildFieldAccess(string instanceName, string fieldName)
        => SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName(instanceName),
            SyntaxFactory.IdentifierName(fieldName));

    /// <summary>
    /// Array form (non-default): T[] __cs2jArr_x = new T[1];
    /// One storage per symbol even within one scope (design D1② tradeoff note).
    /// </summary>
    public LocalDeclarationStatementSyntax BuildArrayStorageDeclaration(CapturedSymbolPlan symbol)
    {
        var elementType = SyntaxFactory.ParseTypeName(symbol.TypeDisplayFullyQualified);

        var newExpression = SyntaxFactory.ArrayCreationExpression(
            SyntaxFactory.ArrayType(elementType)
                .WithRankSpecifiers(SyntaxFactory.SingletonList(
                    // new T[1]: a fixed one-slot box. The slot zero-inits like
                    // an uninitialized local; Task 9/10 append an explicit [0]
                    // assignment at the declaration site when an initializer
                    // exists. (new T[] without size or initializer is illegal.)
                    SyntaxFactory.ArrayRankSpecifier(SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(1)))))));

        var declarator = SyntaxFactory.VariableDeclarator(symbol.StorageName)
            .WithInitializer(SyntaxFactory.EqualsValueClause(newExpression));

        return SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(VarType(), SyntaxFactory.SingletonSeparatedList(declarator)));
    }

    /// <summary>__cs2jArr_x[0] = value;</summary>
    public ExpressionStatementSyntax BuildArrayAssignment(string storageName, ExpressionSyntax value)
        => SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                BuildArrayElementAccess(storageName),
                value));

    /// <summary>__cs2jArr_x[0] — array-form read/write/ref site shape.</summary>
    public ElementAccessExpressionSyntax BuildArrayElementAccess(string storageName)
        => SyntaxFactory.ElementAccessExpression(SyntaxFactory.IdentifierName(storageName))
            .WithArgumentList(SyntaxFactory.BracketedArgumentList(
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(0))))));

    private static IdentifierNameSyntax VarType()
        => SyntaxFactory.IdentifierName(SyntaxFactory.Token(SyntaxKind.VarKeyword));
}
```

要点（评审对照设计）：
- 字段只 `public`、类 `private sealed`、无方法/属性/`Equals`——字段段 display class（设计 §4.4）。
- 类型一律 `ParseTypeName(TypeDisplayFullyQualified)`：M1 已过滤 ErrorType/指针/匿名/ref 前缀（CS2J3202/3205/3201），可拼写性由分析闸门保证。
- 数组形态一律发 `new T[1]`（单槽盒，槽位零初始化；有初始值时由 Task 9/10 在声明点补发 `__cs2jArr_x[0] = init;`）；站点 `[0]` 由 Task 9 rewriter 统一替换。**不得**发无尺寸无初始化器的 `new T[]`（非法 C#）。
- 生成节点全部带 `cs2j_closure_lift` annotation；跨文本重入靠名字前缀（CaptureAnalyzer M1 已跳过 `__cs2jClosure*`/`__cs2jArr_`，Task 11 再补 holder 类/字段的生成识别说明）。

- [ ] **Step 3: 编译确认（暂无调用方，仅编译）**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`（internal 未使用类型最多 CS 默认以外的提示；该工程未开 warnings-as-errors，以实测输出为准）。

- [ ] **Step 4: 提交（逐文件 add，两个文件）**

```powershell
git add src/CSharpToJava.Core/ClosureLift/HolderPlan.cs
git add src/CSharpToJava.Core/ClosureLift/HolderSyntaxGenerator.cs
git commit -m "feat(preprocessing): C32 M2 HolderPlan 记录体＋HolderSyntaxGenerator（holder 类/实例/字段/数组双形态）"
```

## Task 9: CaptureSiteRewriter（冻结站点表 + 标识符/声明点/参数/holder 类插入）

**Files:**
- Create: `src/CSharpToJava.Core/ClosureLift/CaptureSiteRewriter.cs`

本任务实现设计 §4.5 改写口径的**非循环部分**：① 标识符读/写/`ref`/`out`/`lock` 全部经同一张冻结站点表双射到 `instance.Field`（或数组 `[0]`）——赋值左值、`++`/`--`、`ref/out` 实参、lock 表达式都只需替换内层表达式，父节点形态天然保持；② 局部声明点移除被提升声明器、就地插入实例声明与字段赋值；③ 参数捕获（D4）在体首插入实例＋快照赋值；④ holder 类插入宿主类型。for/foreach（D9）在 Task 10 扩展。

- [ ] **Step 1: 写 CaptureSiteRewriter.cs 完整代码（Task 9 形态）**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// The single site rewriter (design §4.5). Input is the FROZEN plan set:
/// identifiers are mapped by original node identity (never by name), so
/// shadowing same-named locals cannot be hit by mistake. Quoted-lambda and
/// nameof sites never enter the plan (CaptureAnalyzer excluded them).
///
/// Two structural steps, mirroring E02:
///   1. InsertHolderClasses(root) — nested holder types (ReplaceNodes).
///   2. Visit(root)                 — statement splices + identifier sites in
///                                    one bottom-up traversal.
/// Moved initializer expressions keep their original node identity, so the
/// site table still resolves them after relocation.
/// Task 10 extends statement registration for for/foreach (D9).
/// </summary>
internal sealed class CaptureSiteRewriter : CSharpSyntaxRewriter
{
    private readonly HolderSyntaxGenerator _generator = new();
    private readonly ClosureLiftStatistics? _statistics;

    // Identifier node (original tree) → replacement expression.
    private readonly Dictionary<IdentifierNameSyntax, ExpressionSyntax> _siteTargets = new();

    // Lifted declarator → its plan (for statement rebuilds).
    private readonly Dictionary<VariableDeclaratorSyntax, CapturedSymbolPlan> _liftedDeclarators = new();

    // Original statement → statements it becomes (empty list = remove).
    private readonly Dictionary<StatementSyntax, List<StatementSyntax>> _statementReplacements = new();

    // Original block → statements inserted at its top (parameter groups, D4).
    private readonly Dictionary<BlockSyntax, List<StatementSyntax>> _blockPrepends = new();

    // Original host type → holder classes appended to its members.
    private readonly Dictionary<TypeDeclarationSyntax, List<ClassDeclarationSyntax>> _holderClasses = new();

    public CaptureSiteRewriter(
        IEnumerable<HolderGroupPlan> groups,
        ClosureLiftStatistics? statistics = null)
    {
        _statistics = statistics;

        foreach (var group in groups)
            RegisterGroup(group);

        BuildLocalDeclarationReplacements();
    }

    /// <summary>Number of identifier sites actually replaced during <see cref="Visit"/>.</summary>
    public int SitesRewritten { get; private set; }

    /// <summary>Step A: insert every holder class into its host type.</summary>
    public CompilationUnitSyntax InsertHolderClasses(CompilationUnitSyntax root)
    {
        if (_holderClasses.Count == 0)
            return root;

        return root.ReplaceNodes(
            _holderClasses.Keys,
            (original, _) => original.WithMembers(
                original.Members.AddRange(_holderClasses[original])));
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (_siteTargets.TryGetValue(node, out var replacement))
        {
            SitesRewritten++;
            _statistics?.SitesRewritten++;
            // Assignment LHS / ++-- operand / ref-out argument / lock expression
            // all stay legal with a field or array-element expression in place.
            return replacement.WithTriviaFrom(node);
        }

        return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
        => RebuildStatementList(node, node.Statements, block => block.WithStatements(block));

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
    {
        var statements = VisitStatementList(node.Statements);
        return node.WithStatements(SyntaxFactory.List(statements));
    }

    private BlockSyntax? RebuildStatementList(
        BlockSyntax original,
        SyntaxList<StatementSyntax> originalStatements,
        Func<BlockSyntax, BlockSyntax, BlockSyntax> attach)
    {
        var visitedStatements = VisitStatementList(originalStatements);
        if (!_blockPrepends.TryGetValue(original, out var prepend))
        {
            var rebuilt = attach(original, original.WithStatements(SyntaxFactory.List(visitedStatements)));
            return ReferenceEquals(rebuilt, original) ? null : rebuilt;
        }

        var all = new List<StatementSyntax>(prepend);
        all.AddRange(visitedStatements);
        return attach(original, original.WithStatements(SyntaxFactory.List(all)));
    }

    private List<StatementSyntax> VisitStatementList(SyntaxList<StatementSyntax> statements)
    {
        var result = new List<StatementSyntax>();
        foreach (var statement in statements)
        {
            if (_statementReplacements.TryGetValue(statement, out var replaced))
            {
                foreach (var candidate in replaced)
                {
                    if (Visit(candidate) is StatementSyntax visited)
                        result.Add(visited);
                }
                continue;
            }

            if (Visit(statement) is StatementSyntax visitedStatement)
                result.Add(visitedStatement);
        }

        return result;
    }

    private void RegisterGroup(HolderGroupPlan group)
    {
        if (!group.ArrayForm && !_holderClasses.ContainsKey(group.HostType))
            _holderClasses[group.HostType] = new List<ClassDeclarationSyntax>();

        foreach (var symbol in group.Symbols)
            RegisterSites(group, symbol);

        if (!group.ArrayForm)
            _holderClasses[group.HostType].Add(_generator.BuildHolderClass(group));

        var parameters = group.Symbols.Where(s => s.Parameter != null).ToList();
        if (parameters.Count > 0)
        {
            // D4: instance at body first statement, then per-parameter snapshot
            // assignments; the RHS identifier is a FRESH node, so it reads the
            // original parameter once and is never itself site-rewritten.
            var body = (group.HostMember as BaseMethodDeclarationSyntax)?.Body
                ?? (group.HostMember as AccessorDeclarationSyntax)?.Body;
            // Planner (Task 11) guarantees a block body; expression-bodied
            // routines with mutable captures are rejected there (CS2J3204).
            var prepend = new List<StatementSyntax>();
            if (!group.ArrayForm)
                prepend.Add(_generator.BuildHolderInstanceDeclaration(group));
            foreach (var parameter in parameters)
            {
                prepend.Add(BuildInitialization(
                    group,
                    parameter,
                    SyntaxFactory.IdentifierName(parameter.OriginalName)));
            }

            if (body != null)
            {
                if (!_blockPrepends.TryGetValue(body, out var list))
                {
                    list = new List<StatementSyntax>();
                    _blockPrepends[body] = list;
                }
                list.AddRange(prepend);
            }
        }
    }

    private void RegisterSites(HolderGroupPlan group, CapturedSymbolPlan symbol)
    {
        foreach (var site in symbol.Sites)
        {
            if (site.Reference.GetSyntax() is not IdentifierNameSyntax identifier)
                continue; // Defensive: stale reference never silently rewrites.

            var target = group.ArrayForm
                ? (ExpressionSyntax)_generator.BuildArrayElementAccess(symbol.StorageName)
                : _generator.BuildFieldAccess(group.InstanceName, symbol.FieldName);
            _siteTargets[identifier] = target;
        }

        if (symbol.Declarator != null)
            _liftedDeclarators[symbol.Declarator] = symbol;
    }

    /// <summary>
    /// Materialize per-declaration splices. Within one declaration statement
    /// declarators are walked in original order: lifted declarators become
    /// storage/assignment statements (array form: storage + assignment),
    /// consecutive un-lifted declarators stay as one declaration. The group
    /// instance declaration (holder form, non-parameter groups) is emitted at
    /// the earliest lifted declaration statement.
    /// </summary>
    private void BuildLocalDeclarationReplacements()
    {
        // statement → lifted symbols in declarator order; populated first so
        // the anchor decision can observe every statement.
        var liftedByStatement =
            new Dictionary<LocalDeclarationStatementSyntax, List<CapturedSymbolPlan>>();
        foreach (var symbol in _liftedDeclarators.Values)
        {
            if (symbol.Declarator!.Parent is not VariableDeclarationSyntax declaration
                || declaration.Parent is not LocalDeclarationStatementSyntax statement)
                continue;

            if (!liftedByStatement.TryGetValue(statement, out var list))
            {
                list = new List<CapturedSymbolPlan>();
                liftedByStatement[statement] = list;
            }
            list.Add(symbol);
        }

        // Anchor: holder instance goes before the earliest lifted local
        // declaration of each non-parameter group (Task 10 adds for-loops).
        var anchoredGroups = new HashSet<HolderGroupPlan>();
        foreach (var group in _holderClasses
                     .SelectMany(kvp => kvp.Value)
                     .Select(_ => (HolderGroupPlan?)null))
        {
            // Unused indirection avoided below; groups tracked separately.
        }
        foreach (var group in GroupsWithLocalAnchor())
            anchoredGroups.Add(group);

        foreach (var (statement, lifted) in liftedByStatement)
        {
            var generated = new List<StatementSyntax>();

            // One instance at most per statement (the group's earliest anchor).
            foreach (var group in anchoredGroups)
            {
                if (group.ArrayForm || GroupHasParameters(group))
                    continue;
                if (EarliestLocalStatement(group) == statement)
                    generated.Add(_generator.BuildHolderInstanceDeclaration(group));
            }

            var liftedSet = new HashSet<VariableDeclaratorSyntax>(
                lifted.Select(s => s.Declarator)!);

            // Walk every declarator in source order, interleaving generated
            // statements with the rebuilt declaration of un-lifted runs.
            var run = new List<VariableDeclaratorSyntax>();
            foreach (var declarator in statement.Declaration.Variables)
            {
                if (!liftedSet.Contains(declarator))
                {
                    run.Add(declarator);
                    continue;
                }

                EmitRunDeclaration();

                var symbol = lifted.Single(s => s.Declarator == declarator);
                var group = GroupOf(symbol);
                if (group.ArrayForm)
                {
                    generated.Add(_generator.BuildArrayStorageDeclaration(symbol));
                    if (declarator.Initializer is { Value: var init })
                        generated.Add(_generator.BuildArrayAssignment(symbol.StorageName, init));
                }
                else if (declarator.Initializer is { Value: var init })
                {
                    generated.Add(BuildInitialization(group, symbol, init));
                }
                // No initializer: nothing emitted — field zero-init matches the
                // definite-assignment rules every legal input already obeys.
            }

            EmitRunDeclaration();

            _statementReplacements[statement] = generated;

            void EmitRunDeclaration()
            {
                if (run.Count == 0)
                    return;
                generated.Add(statement.Declaration.WithVariables(
                    SyntaxFactory.SeparatedList(run)) is VariableDeclarationSyntax varDecl
                        ? statement.WithDeclaration(varDecl)
                        : throw new InvalidOperationException("unreachable"));
                run.Clear();
            }
        }
    }

    private StatementSyntax BuildInitialization(
        HolderGroupPlan group,
        CapturedSymbolPlan symbol,
        ExpressionSyntax value)
        => group.ArrayForm
            ? _generator.BuildArrayAssignment(symbol.StorageName, value)
            : _generator.BuildFieldAssignment(group.InstanceName, symbol.FieldName, value);

    // Group lookup helpers. Registration order is the plan order; a symbol
    // belongs to exactly one group.
    private readonly Dictionary<CapturedSymbolPlan, HolderGroupPlan> _groupBySymbol = new();

    private HolderGroupPlan GroupOf(CapturedSymbolPlan symbol) => _groupBySymbol[symbol];

    private static bool GroupHasParameters(HolderGroupPlan group)
        => group.Symbols.Any(s => s.Parameter != null);

    private LocalDeclarationStatementSyntax? EarliestLocalStatement(HolderGroupPlan group)
        => group.Symbols
            .Where(s => s.Declarator?.Parent?.Parent is LocalDeclarationStatementSyntax)
            .OrderBy(s => s.Declarator!.SpanStart)
            .Select(s => (LocalDeclarationStatementSyntax)s.Declarator!.Parent!.Parent!)
            .FirstOrDefault();

    private IEnumerable<HolderGroupPlan> GroupsWithLocalAnchor() => _groupBySymbol.Values.Distinct();

    private void TrackGroup(HolderGroupPlan group, CapturedSymbolPlan symbol)
        => _groupBySymbol[symbol] = group;
}
```

⚠️ **实施前修正（写计划时自查发现的两处注册缺口，落码时按此修正，勿照抄上面骨架的错误形状）**：

1. `RegisterGroup` 的 `foreach (var symbol in group.Symbols) RegisterSites(...)` 必须在 `RegisterSites` 内补 `_groupBySymbol[symbol] = group`（即调 `TrackGroup`）；上面私有方法 `TrackGroup` 已给，但 `RegisterSites` 漏了一行调用——落码时在 `RegisterSites` 首行加 `_groupBySymbol[symbol] = group;`（需把 group 传进去）。
2. `BuildLocalDeclarationReplacements` 中 `foreach (var group in _holderClasses.SelectMany(...) ...)` 那个空循环是草稿残留，**整段删除**；锚定集合直接由 `_groupBySymbol.Values.Distinct()` 推导（保留 `GroupsWithLocalAnchor()`），实例插入判断已用 `EarliestLocalStatement(group) == statement` 完成，去重天然成立（同组最早语句唯一）。
3. `statement.Declaration.WithVariables(...) is VariableDeclarationSyntax varDecl ? ... : throw` 三元写法是多余的——`WithVariables` 静态返回 `VariableDeclarationSyntax`，落码时直接写：
   ```csharp
   var kept = statement.Declaration.WithVariables(SyntaxFactory.SeparatedList(run));
   generated.Add(statement.WithDeclaration(kept));
   ```
4. 数组形态 group 不建 holder 类，但构造器里 `RegisterGroup` 对数组 group 不应触碰 `_holderClasses`——上面代码的 `ContainsKey` 守卫 + 仅 holder 形态 `Add` 已正确；`GroupsWithLocalAnchor()` 对数组 group 也会返回，实例插入分支靠 `group.ArrayForm` 跳过，保持此形状。

落码完成后代码必须满足：`dotnet build` 0 错误；构造函数无副作用外的可观察行为；类尚未被任何生产代码调用（Task 11 接线）。

- [ ] **Step 2: 编译确认**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 3: 提交**

```powershell
git add src/CSharpToJava.Core/ClosureLift/CaptureSiteRewriter.cs
git commit -m "feat(preprocessing): C32 M2 CaptureSiteRewriter（冻结站点表/声明点/参数快照/holder 类插入，非循环部分）"
```

## Task 10: CaptureSiteRewriter 扩展 for/foreach（D9 循环二分 + 表达式体边界硬化）

**Files:**
- Modify: `src/CSharpToJava.Core/ClosureLift/CaptureSiteRewriter.cs`（Task 9 产物）

本任务落地设计 D9 的循环二分：**for** 归纳变量的 holder 实例在循环**之前**构造、for 声明摘除被提升声明器（initializer 值变为字段赋值）、condition/incrementor/体内站点零特殊处理（原节点在冻结站点表内，遍历时就地双射到字段）；**foreach** 迭代变量在循环体**首句每轮新建实例**并以本轮值快照初始化，非块体包块。另把参数捕获分支的「体首插入」从静默跳过硬化为 fail-loud。

**改写前后对照（设计 §4.4，本 pass 用「实例声明＋紧随赋值」形态，非对象初始化器）**：

```csharp
// for：循环前一个实例；condition/incrementor 走字段，单存储=单一循环变量
var __cs2jClosure0 = new Cs2jDisplayClass_Work_0();
__cs2jClosure0._cs2jCap_i = 0;
for (; __cs2jClosure0._cs2jCap_i < n; __cs2jClosure0._cs2jCap_i++)
    acts.Add(() => __cs2jClosure0._cs2jCap_i);

// foreach：每轮首句新建实例，本轮委托捕获本轮对象（C# 5+ 每轮独立）
foreach (var item in source)
{
    var __cs2jClosure1 = new Cs2jDisplayClass_Work_1();
    __cs2jClosure1._cs2jCap_item = item;
    acts.Add(() => __cs2jClosure1._cs2jCap_item);
}
```

**前置契约（Task 11 HolderPlanner 必须先拦住；rewriter 只 fail-loud，不自己发诊断）**：

1. for 归纳组的锚点展开是「一语句变多语句」，只能拼进 block/switch-section 的语句表。`ForStatement.Parent` 不是 `BlockSyntax`/`SwitchSectionSyntax`（如 `while (c) for (…) {}` 裸嵌）→ Planner 对该 routine 报 **CS2J3204** 边界，计划不下发。
2. 参数捕获要求块体；表达式体成员（`void F(int x) => G(() => x++);` 这类无块可放实例的形态）→ Planner 报 **CS2J3204**。
3. 解构 foreach（`ForEachVariableStatementSyntax`，`foreach (var (x,y) in …)`）M1 分析器不识别为 foreach 迭代变量；v1 不收编：若其中变量被 lambda 内可变捕获，Planner 对该 routine 报 **CS2J3204**（审计行仍可见，留待 C45）。本任务假设 `PerIterationLoop` 永远是经典 `ForEachStatementSyntax`。
4. for 前声明的普通局部（`int i=0; for(;i<n;i++)`）走 Task 9 普通局部路径（锚点即其声明语句，天然在循环前），本任务不为它做任何事；condition/incrementor 站点凭节点身份在表内自动改写。

- [ ] **Step 1: 新增两个注册字典（字段区，紧随 `_holderClasses` 之后）**

```csharp
// for-statement (original node) → lifted induction symbols, declarator order.
private readonly Dictionary<ForStatementSyntax, List<CapturedSymbolPlan>> _liftedForDeclarators = new();

// foreach loops whose embedded statement is NOT a block: the loop is rebuilt
// with a block opening with the per-iteration instance + snapshot (D9).
private readonly Dictionary<ForEachStatementSyntax, List<StatementSyntax>> _foreachWraps = new();
```

- [ ] **Step 2: 构造器追加 for 展开构建调用**

Task 9 修正 1/2 落码后构造器形如 `RegisterGroup` 循环 + `BuildLocalDeclarationReplacements()`；在其后**追加一行**：

```csharp
foreach (var group in groups)
    RegisterGroup(group);

BuildLocalDeclarationReplacements();
BuildForLoopExpansions();   // ← 本任务新增
```

- [ ] **Step 3: RegisterGroup 增加 foreach 每轮注册与统计**

在 `RegisterGroup` 首行（holder 类守卫之前）加统计；末尾（参数块处理之后）加 foreach 分派：

```csharp
private void RegisterGroup(HolderGroupPlan group)
{
    if (group.PerIteration)
        _statistics?.ForeachPerIterationGroups++;

    if (!group.ArrayForm && !_holderClasses.ContainsKey(group.HostType))
        _holderClasses[group.HostType] = new List<ClassDeclarationSyntax>();

    foreach (var symbol in group.Symbols)
        RegisterSites(group, symbol);

    if (!group.ArrayForm)
        _holderClasses[group.HostType].Add(_generator.BuildHolderClass(group));

    // …（Task 9 既有的 parameters 块体首插入逻辑保持不动；本任务 Step 6 硬化其 null 分支）…

    if (group.PerIteration && group.PerIterationLoop is { } foreachLoop)
        RegisterForeachSnapshot(group, foreachLoop);
}
```

新增 `RegisterForeachSnapshot`（放在 `RegisterGroup` 之后）：

```csharp
/// <summary>
/// D9 foreach side: every iteration opens with a fresh holder instance (or
/// fresh array storage) snapshotted from THIS iteration's value. The RHS
/// identifier is a FRESH SyntaxFactory node — identity-based site lookup can
/// never match it, so it reads the original iteration variable exactly once;
/// every body/lambda reference of the symbol is still in the frozen site
/// table and rewrites to the field/element.
/// </summary>
private void RegisterForeachSnapshot(HolderGroupPlan group, ForEachStatementSyntax loop)
{
    var prepend = new List<StatementSyntax>();

    if (group.ArrayForm)
    {
        foreach (var symbol in group.Symbols)
        {
            prepend.Add(_generator.BuildArrayStorageDeclaration(symbol));
            prepend.Add(_generator.BuildArrayAssignment(
                symbol.StorageName,
                SyntaxFactory.IdentifierName(symbol.OriginalName)));
        }
    }
    else
    {
        prepend.Add(_generator.BuildHolderInstanceDeclaration(group));
        foreach (var symbol in group.Symbols)
        {
            prepend.Add(BuildInitialization(
                group,
                symbol,
                SyntaxFactory.IdentifierName(symbol.OriginalName)));
        }
    }

    if (loop.Statement is BlockSyntax block)
    {
        // Block body: reuse the Task 9 prepend channel; VisitBlock splices.
        if (!_blockPrepends.TryGetValue(block, out var list))
        {
            list = new List<StatementSyntax>();
            _blockPrepends[block] = list;
        }
        list.AddRange(prepend);
    }
    else
    {
        // Embedded non-block body: VisitForEachStatement wraps it in a block.
        _foreachWraps[loop] = prepend;
    }
}
```

说明：经典 foreach 每组只有一个迭代符号（设计 §4.3 迭代变量单独成组）；`foreach` 泛化循环是防御形状。块体与非块体两条通道覆盖 `foreach (…) ;`（空语句体）等全部嵌入形态。

- [ ] **Step 4: RegisterSites 收集 for 声明器**

Task 9 修正 1 已要求 `RegisterSites(HolderGroupPlan group, CapturedSymbolPlan symbol)` 首行落 `_groupBySymbol[symbol] = group;`。在同方法既有的 `if (symbol.Declarator != null) _liftedDeclarators[…] = …;` **之后**追加 for 收集：

```csharp
if (symbol.ForLoop is { } forLoop)
{
    if (!_liftedForDeclarators.TryGetValue(forLoop, out var forList))
    {
        forList = new List<CapturedSymbolPlan>();
        _liftedForDeclarators[forLoop] = forList;
    }
    forList.Add(symbol);
}
```

注意：for 声明器同时也在 `_liftedDeclarators` 中，**不要**从该表移除——`BuildLocalDeclarationReplacements` 的入口守卫要求 `declarator.Parent.Parent is LocalDeclarationStatementSyntax`，for 声明器（祖父是 `ForStatementSyntax`）天然被跳过，无副作用。

- [ ] **Step 5: 新增 BuildForLoopExpansions + StripForDeclaration**

```csharp
/// <summary>
/// D9 for side. Builds the statement-list expansion per lifted for-loop:
/// [group instance?][field/array initializers…][rebuilt for]. The rebuilt
/// for keeps its ORIGINAL condition/incrementors/body nodes, so their site
/// identifiers rewrite in place when the expansion is visited; the loop
/// declaration only loses lifted declarators.
/// </summary>
private void BuildForLoopExpansions()
{
    foreach (var (loop, lifted) in _liftedForDeclarators)
    {
        // Precondition contract #1: planner must diagnose CS2J3204 earlier.
        // Fail loud — dropping the instance here would silently split state.
        if (loop.Parent is not (BlockSyntax or SwitchSectionSyntax))
            throw new InvalidOperationException(
                "C32: a for-loop with a lifted induction variable anchored " +
                "outside a block/switch section must be rejected with " +
                "CS2J3204 before rewriting (D9 boundary).");

        var ordered = lifted
            .OrderBy(s => s.Declarator!.SpanStart)
            .ToList();

        var generated = new List<StatementSyntax>();

        // Instance before the loop ONLY when this loop is the group's earliest
        // anchor. Parameter groups anchor at body top; local-anchor groups are
        // handled by BuildLocalDeclarationReplacements; array groups have no
        // holder instance.
        foreach (var group in ordered.Select(GroupOf).Distinct())
        {
            if (group.ArrayForm || GroupHasParameters(group))
                continue;
            if (EarliestAnchorStatement(group) == loop)
                generated.Add(_generator.BuildHolderInstanceDeclaration(group));
        }

        // Initializers relocate verbatim (original node identity → sites in
        // the table still rewrite after the move). No initializer: the field
        // zero-inits, matching a declarator-less for iteration variable.
        foreach (var symbol in ordered)
        {
            if (symbol.Declarator!.Initializer is { Value: var init })
                generated.Add(BuildInitialization(GroupOf(symbol), symbol, init));
        }

        generated.Add(StripForDeclaration(loop, ordered));

        _statementReplacements[loop] = generated;
    }
}

/// <summary>
/// Remove lifted declarators from the for declaration. All declarators in one
/// for declaration share its type token: surviving declarators keep it; when
/// none survive the declaration becomes null (for (; cond; incr)).
/// </summary>
private static ForStatementSyntax StripForDeclaration(
    ForStatementSyntax loop,
    IReadOnlyCollection<CapturedSymbolPlan> lifted)
{
    if (loop.Declaration is not { } declaration)
        return loop; // Expression-form initializers carry no declarator.

    var liftedSet = new HashSet<VariableDeclaratorSyntax>(
        lifted.Select(s => s.Declarator!));

    var remaining = declaration.Variables
        .Where(d => !liftedSet.Contains(d))
        .ToList();

    if (remaining.Count == declaration.Variables.Count)
        return loop; // Defensive: nothing to strip.

    VariableDeclarationSyntax? rebuiltDeclaration = remaining.Count == 0
        ? null
        : declaration.WithVariables(SyntaxFactory.SeparatedList(remaining));

    return loop.WithDeclaration(rebuiltDeclaration);
}
```

要点：
- `WithDeclaration(null)` 在 Roslyn 合法，产出 `for (; …; …)`。
- 展开结果登记进 Task 9 既有的 `_statementReplacements`（键是 `StatementSyntax`，for 语句可直接做键）；`VisitStatementList` 已统一做「先替换、再 Visit 候选」，无需重写 `VisitForStatement`——condition/incrementor/体内站点在候选被 Visit 时凭原节点身份命中站点表。
- `for (int i = 0, j = 10; i < j; i++)` 仅提升 i 时，摘除 i 保留 `int j = 10`；j 的引用不是站点，原样不动。

- [ ] **Step 6: 统一锚点判定 + 参数体 null 硬化**

把 Task 9 的 `EarliestLocalStatement` **整体替换**为统一锚点方法（调用点 `BuildLocalDeclarationReplacements` 内的 `EarliestLocalStatement(group) == statement` 同步改名；语义不变——候选语句是 `LocalDeclarationStatementSyntax`，仅当锚点正是该语句时相等）：

```csharp
/// <summary>
/// Instance anchor of a non-parameter group: the earliest source statement
/// among lifted local declarations (Task 9) and enclosing for-loops (D9).
/// Parameter groups anchor at routine body top and never query this.
/// </summary>
private StatementSyntax? EarliestAnchorStatement(HolderGroupPlan group)
{
    StatementSyntax? earliest = group.Symbols
        .Where(s => s.Declarator?.Parent?.Parent is LocalDeclarationStatementSyntax)
        .OrderBy(s => s.Declarator!.SpanStart)
        .Select(s => (StatementSyntax)s.Declarator!.Parent!.Parent!)
        .FirstOrDefault();

    foreach (var loop in group.Symbols
                 .Where(s => s.ForLoop != null)
                 .Select(s => s.ForLoop!)
                 .Distinct())
    {
        if (earliest is null || loop.SpanStart < earliest.SpanStart)
            earliest = loop;
    }

    return earliest;
}
```

Task 9 参数分支末尾的 `if (body != null) { …_blockPrepends… }` 包裹**去掉条件、改为前置 fail-loud**（前置契约 #2；落码后该分支直接操作 `_blockPrepends[body]`）：

```csharp
var body = (group.HostMember as BaseMethodDeclarationSyntax)?.Body
    ?? (group.HostMember as AccessorDeclarationSyntax)?.Body
    ?? throw new InvalidOperationException(
        "C32: parameter capture in a routine without a block body must be " +
        "rejected with CS2J3204 before rewriting (expression-bodied/D16 boundary).");
```

- [ ] **Step 7: 新增 VisitForEachStatement（非块体包块）**

块体 foreach 经 `_blockPrepends` + `VisitBlock` 已闭环，无需访问器；只有 `_foreachWraps` 命中的非块体需要重写。加在 `VisitSwitchSection` 之后：

```csharp
public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
{
    if (!_foreachWraps.TryGetValue(node, out var prepend))
        return base.VisitForEachStatement(node);

    // Bottom-up: visit the original loop (embedded statement + all site
    // hits) first, then wrap the VISITED embedded statement in a block that
    // opens with the per-iteration instance + snapshot.
    var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;

    var wrapped = new List<StatementSyntax>();
    foreach (var statement in prepend)
    {
        if (Visit(statement) is StatementSyntax visitedPrepend)
            wrapped.Add(visitedPrepend);
    }
    wrapped.Add(visited.Statement);

    // Trivia/indentation is canonicalized by the Formatter pass (Task 11,
    // same AdhocWorkspace shape as E02).
    return visited.WithStatement(SyntaxFactory.Block(SyntaxFactory.List(wrapped)));
}
```

注意：`foreach (var item in source) acts.Add(() => item);` 重写后循环体为 `{ var __cs2jClosure1 = new …(); __cs2jClosure1._cs2jCap_item = item; acts.Add(() => __cs2jClosure1._cs2jCap_item); }`——实例声明/快照/原语句严格按序，快照在任何用户语句之前，等价性论证（设计 §4.5④）成立。

- [ ] **Step 8: 自查清单（落码后逐条核对，再编译）**

- [ ] `_statementReplacements[loop]` 的展开中，被移动的 initializer 是**原节点**（不得 `SyntaxFactory.ParseExpression` 重建），保证跨引用站点（`for (int i = start; …)` 且 start 同组）移动后仍按身份改写。
- [ ] 同一 for 语句在 `_liftedForDeclarators` 中的符号按 `Declarator.SpanStart` 排序；同一组的实例声明在整文件内只出现一次（参数组体首 / 最早局部 / 最早 for 三锚点互斥）。
- [ ] 不新增 `VisitForStatement`：展开在语句表层完成；裸嵌 for 由构造器抛异常闸门拦截（Step 5）。
- [ ] foreach 快照 RHS 全部是 `SyntaxFactory.IdentifierName(symbol.OriginalName)` fresh 节点；全文搜不到把快照 RHS 放进 `_siteTargets` 的路径。
- [ ] `ForeachPerIterationGroups` 只在 transform 路径的 rewriter 构造期按组加一次；Task 11 Stage 汇总时以 rewriter 计数为准，分析/审计侧（AuditOnly）不构造 rewriter，不会重复计数。
- [ ] holder 类插入（`InsertHolderClasses`）对 PerIteration 组照常生效（类在宿主类型内一份；实例每轮 new）。

- [ ] **Step 9: 编译确认（仍无生产调用方；Task 11 接线）**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 10: 提交（单文件）**

```powershell
git add src/CSharpToJava.Core/ClosureLift/CaptureSiteRewriter.cs
git commit -m "feat(preprocessing): C32 M2 CaptureSiteRewriter for/foreach 二分（D9 循环前实例/每轮快照/非块体包块/边界硬化）"
```

<!-- APPEND-MARK-TASK11 -->

## Task 11: HolderPlanner + HolderLiftStage + Preprocessor 升级（Analyze→Plan→Generate→Rewrite 接通）

**Files:**
- Create: `src/CSharpToJava.Core/ClosureLift/HolderPlanner.cs`
- Create: `src/CSharpToJava.Core/ClosureLift/HolderLiftStage.cs`
- Modify: `src/CSharpToJava.Core/ClosureLift/ClosureLiftPreprocessor.cs`（替换 M1 审计尾；AuditOnly 路径行为冻结不变）
- Modify: `src/CSharpToJava.Core/ClosureLift/CaptureAnalyzer.cs`（无块体例程补 CS2J3204，一处）

设计 §4.1 三段式在此接通：**Analyze**（M1 已落地，纯只读）→ **Plan**（本任务 HolderPlanner：分组/命名/边界冻结，纯只读产记录）→ **Generate＋Rewrite**（HolderSyntaxGenerator 经 CaptureSiteRewriter 落地）。skip/报错集必须在任何树改动前冻结：**任何** CS2J3201-3206 行存在则该文件原样返回（与 Strict 无关——`Strict=false` 只把严重级别降为 Warning 继续跑其他文件，绝不半改写，catalog 第一原则）。

- [ ] **Step 1: 写 HolderPlanner.cs（分组/命名冻结 + 审计行发射器迁移至此）**

把 M1 门面私有的 `EmitAuditRows`/`CollectRoutineLocalNames` **迁移**到本文件（方法体逐行保留，仅分组键改走共享 `BuildGroupKey`），保证审计预命名与转换命名同一代码路径（设计 §4.3「audit names == generated names」）。

```csharp
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// Design §4.3 frozen planner. Pure (never edits trees): turns the read-only
/// ClosureAnalysisResult into the ordered HolderGroupPlan set, freezing group
/// keys, class/instance/field/storage names and the Task 10 rewriter
/// precondition boundaries. CS2J3207 prospective rows run through the SAME
/// key/naming calls, so audit names equal generated names.
/// </summary>
internal static class HolderPlanner
{
    public static IReadOnlyList<HolderGroupPlan> BuildPlans(
        ClosureAnalysisResult analysis,
        SemanticModel model,
        ClosureLiftOptions options,
        ClosureLiftNaming naming,
        List<ClosureLiftDiagnostic> diagnostics)
    {
        var liftableByMember = analysis.Members
            .Select(m => (Member: m, Liftable: m.Symbols.Where(s => !s.Skipped).ToList()))
            .Where(t => t.Liftable.Count > 0)
            .ToList();

        // Pass 1: validate EVERY member first so non-strict runs report all
        // boundary rows in one pass; any boundary freezes the whole file.
        var blocked = false;
        foreach (var (member, liftable) in liftableByMember)
        {
            if (HasBlockingBoundary(member, liftable, options, diagnostics))
                blocked = true;
        }
        if (blocked)
            return Array.Empty<HolderGroupPlan>();

        // Pass 2: group + freeze names.
        var plans = new List<HolderGroupPlan>();
        var arrayForm = options.HolderForm == HolderForm.SingleElementArray;

        foreach (var (member, liftable) in liftableByMember)
        {
            var hostKey = HostTypeKey(model, member.HostType);
            var routineNames = CollectRoutineLocalNames(member.Routine);

            var grouped = liftable
                .GroupBy(BuildGroupKey)
                .OrderBy(g => g.Min(s => s.DeclaringReference.Span.Start))
                .ToList();

            var groupIndex = 0;
            foreach (var group in grouped)
            {
                var className = naming.ReserveClassName(hostKey, member.RoutineName);
                var instanceName = ClosureLiftNaming.ReserveInstanceName(groupIndex, routineNames);
                groupIndex++;

                var plan = new HolderGroupPlan
                {
                    HostType = member.HostType,
                    HostMember = member.Routine,
                    HostTypeKey = hostKey,
                    ClassName = className,
                    InstanceName = instanceName,
                    PerIteration = group.Any(s => s.IsForeachIteration),
                    ArrayForm = arrayForm,
                    PerIterationLoop = group
                        .Select(s => s.ForeachLoop)
                        .FirstOrDefault(loop => loop != null),
                };

                var usedFieldNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var symbol in group.OrderBy(s => s.DeclaringReference.Span.Start))
                {
                    var fieldName = ClosureLiftNaming.GetFieldName(symbol.Name, usedFieldNames);
                    var storageName = arrayForm
                        ? ReserveArrayStorageName(symbol.Name, routineNames)
                        : fieldName;

                    plan.Symbols.Add(new CapturedSymbolPlan(
                        symbol.Symbol,
                        symbol.Name,
                        fieldName,
                        storageName,
                        symbol.TypeDisplay,
                        symbol.DeclaringReference.GetSyntax(),
                        symbol.Reason,
                        symbol.IsForeachIteration,
                        symbol.ForeachLoop,
                        symbol.ForLoop,
                        symbol.Declarator,
                        symbol.Parameter,
                        symbol.Sites));
                }

                plans.Add(plan);
            }
        }

        return plans;
    }

    /// <summary>
    /// Group key (design §4.3): routine + declaring scope block + foreach
    /// per-iteration identity. for-induction symbols carry the loop's
    /// enclosing block (the M1 analyzer mapping) — do not re-scope them to
    /// the method body; the audit preview froze names against this mapping.
    /// Syntax nodes are compared by REFERENCE inside the key (never by
    /// SyntaxNode virtual equality).
    /// </summary>
    private sealed class GroupKey : IEquatable<GroupKey>
    {
        private readonly object _scope;
        private readonly bool _perIteration;
        private readonly ForEachStatementSyntax? _loop;

        public GroupKey(object scope, bool perIteration, ForEachStatementSyntax? loop)
        {
            _scope = scope;
            _perIteration = perIteration;
            _loop = loop;
        }

        public bool Equals(GroupKey? other)
            => other is not null
               && ReferenceEquals(_scope, other._scope)
               && _perIteration == other._perIteration
               && ReferenceEquals(_loop, other._loop);

        public override bool Equals(object? obj) => Equals(obj as GroupKey);

        public override int GetHashCode()
            => HashCode.Combine(
                RuntimeHelpers.GetHashCode(_scope),
                _perIteration,
                RuntimeHelpers.GetHashCode(_loop!));
    }

    private static GroupKey BuildGroupKey(CapturedSymbolAnalysis symbol)
        => new(
            (object?)symbol.ScopeBlock ?? symbol.EnclosingMember,
            symbol.IsForeachIteration,
            symbol.ForeachLoop);

    /// <summary>Task 10 rewriter preconditions (CS2J3204 v1 boundaries).</summary>
    private static bool HasBlockingBoundary(
        MemberCaptureAnalysis member,
        IReadOnlyList<CapturedSymbolAnalysis> liftable,
        ClosureLiftOptions options,
        List<ClosureLiftDiagnostic> diagnostics)
    {
        var blocked = false;

        foreach (var symbol in liftable)
        {
            // Contract #1: for anchor must splice into a statement list.
            if (symbol.ForLoop is { } loop
                && loop.Parent is not (BlockSyntax or SwitchSectionSyntax))
            {
                EmitBoundary(options, diagnostics,
                    $"for-loop with a captured induction variable in '{member.RoutineName}' is an embedded statement; v1 boundary.",
                    loop.GetLocation(), member.RoutineName, symbol.Name);
                blocked = true;
            }

            // Contract #3: deconstruction foreach iteration variables are not
            // recognized as foreach variables by the M1 analyzer.
            var declaring = symbol.DeclaringReference.GetSyntax();
            if (declaring.Ancestors().OfType<ForEachVariableStatementSyntax>().Any())
            {
                EmitBoundary(options, diagnostics,
                    $"Deconstruction foreach variable '{symbol.Name}' mutably captured in '{member.RoutineName}' is unsupported in v1.",
                    symbol.Symbol.Locations.FirstOrDefault(), member.RoutineName, symbol.Name);
                blocked = true;
            }
        }

        // Contract #2 (defensive; the analyzer already rejects body-less
        // routines before candidates exist): parameter capture needs a block.
        var body = (member.Routine as BaseMethodDeclarationSyntax)?.Body
            ?? (member.Routine as AccessorDeclarationSyntax)?.Body;
        if (body is null && liftable.Any(s => s.Parameter != null))
        {
            EmitBoundary(options, diagnostics,
                $"Routine '{member.RoutineName}' has no block body to host a parameter holder instance.",
                member.Routine.GetLocation(), member.RoutineName, null);
            blocked = true;
        }

        return blocked;
    }

    private static void EmitBoundary(
        ClosureLiftOptions options,
        List<ClosureLiftDiagnostic> diagnostics,
        string message,
        Location? location,
        string? memberName,
        string? symbolName)
    {
        var severity = options.Strict
            ? ClosureLiftSeverity.Error
            : ClosureLiftSeverity.Warning;
        var line = location?.GetLineSpan().StartLinePosition.Line + 1 ?? 0;
        diagnostics.Add(new ClosureLiftDiagnostic(
            severity, ClosureLiftCodes.UnsupportedBoundary, message,
            null, line, memberName, symbolName));
    }

    private static string ReserveArrayStorageName(string originalName, ISet<string> routineNames)
    {
        var candidate = ClosureLiftNaming.GetArrayLocalName(originalName);
        if (routineNames.Add(candidate))
            return candidate;

        for (var suffix = 2; ; suffix++)
        {
            var suffixed = $"{candidate}_{suffix}";
            if (routineNames.Add(suffixed))
                return suffixed;
        }
    }

    internal static string HostTypeKey(SemanticModel model, TypeDeclarationSyntax hostType)
        => model.GetDeclaredSymbol(hostType) is { } hostSymbol
            ? hostSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : hostType.Identifier.Text;

    // ── Audit tail (migrated verbatim from the M1 preprocessor except the
    //    shared BuildGroupKey; CS2J3207 rows only ever run in AuditOnly). ──

    public static void EmitAuditRows(
        ClosureAnalysisResult analysis,
        SemanticModel model,
        ClosureLiftNaming naming,
        ClosureLiftStatistics stats,
        List<ClosureLiftDiagnostic> diagnostics)
    {
        foreach (var member in analysis.Members)
        {
            var hostKey = HostTypeKey(model, member.HostType);
            var liftable = member.Symbols.Where(s => !s.Skipped).ToList();
            var groups = liftable
                .GroupBy(BuildGroupKey)
                .OrderBy(g => g.Min(s => s.DeclaringReference.Span.Start))
                .ToList();

            var routineNames = CollectRoutineLocalNames(member.Routine);
            var groupIndex = 0;
            foreach (var group in groups)
            {
                var className = naming.ReserveClassName(hostKey, member.RoutineName);
                var instanceName = ClosureLiftNaming.ReserveInstanceName(groupIndex, routineNames);
                groupIndex++;

                var usedFieldNames = new HashSet<string>(StringComparer.Ordinal);
                if (group.Any(s => s.IsForeachIteration))
                    stats.ForeachPerIterationGroups++;

                foreach (var symbol in group)
                {
                    var fieldName = ClosureLiftNaming.GetFieldName(symbol.Name, usedFieldNames);
                    stats.CapturesByReason[symbol.Reason] =
                        stats.CapturesByReason.TryGetValue(symbol.Reason, out var n) ? n + 1 : 1;

                    var lineSpan = symbol.DeclaringReference.SyntaxTree
                        .GetLineSpan(symbol.DeclaringReference.Span);
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var shape = symbol.IsForeachIteration ? "foreach-per-iteration" : "shared";

                    diagnostics.Add(new ClosureLiftDiagnostic(
                        ClosureLiftSeverity.Info,
                        ClosureLiftCodes.AuditRow,
                        $"[audit] {member.RoutineName}.{symbol.Name}: {symbol.Reason} ({shape}), " +
                        $"sites={symbol.Sites.Count}, quoted={symbol.QuotedSites.Count}, " +
                        $"holder={className}, instance={instanceName}, field={fieldName}, type={symbol.TypeDisplay}",
                        null, line, member.RoutineName, symbol.Name, symbol.Reason));
                }
            }
        }
    }

    /// <summary>
    /// Seed set for per-routine instance reservation (M1 shape, unchanged):
    /// parameters + every declarator / classic foreach iteration variable /
    /// local function name inside the routine. Seeding nested-scope names is
    /// conservative; the audit emitter and the transform planner must call
    /// this identical helper.
    /// </summary>
    internal static HashSet<string> CollectRoutineLocalNames(MemberDeclarationSyntax routine)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        if (routine is BaseMethodDeclarationSyntax method)
        {
            foreach (var parameter in method.ParameterList.Parameters)
            {
                var text = parameter.Identifier.Text;
                if (text.Length > 0)
                    names.Add(text);
            }
        }

        foreach (var declarator in routine.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            names.Add(declarator.Identifier.Text);
        foreach (var foreachLoop in routine.DescendantNodes().OfType<ForEachStatementSyntax>())
            names.Add(foreachLoop.Identifier.Text);
        foreach (var localFunction in routine.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            names.Add(localFunction.Identifier.Text);

        return names;
    }
}
```

⚠️ 与 M1 审计的唯一行为差异：非块体 foreach 迭代变量在 M1 预分组里并入例程键，现按设计 §4.3 独立成 per-iteration 组（预命名修正为独立 holder/实例）；M1 20 条单测的 foreach 用例是块体（分组本来就独立），不受影响。

- [ ] **Step 2: 写 HolderLiftStage.cs（C32 唯一 IClosureLiftStage）**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// The single C32 stage (design §4.8; C45 appends more stages without a new
/// pass id). Applies design §4.1 on one CompilationUnit:
/// Analyze → [AuditOnly: rows, stop] → freeze → Plan → Generate/Rewrite.
/// Statistics are mutated in place; diagnostics accumulate on the shared
/// list. The stage never formats — the facade runs Formatter (E02 shape).
/// </summary>
internal sealed class HolderLiftStage : IClosureLiftStage
{
    public string Id => "holder-lift";

    public ClosureStageResult Apply(ClosureStageInput input)
    {
        var (root, model, options, naming, statistics, diagnostics) = input;

        // 1. Analyze — read only. M1 behavior preserved exactly, including
        //    FilesSkippedClean when no candidate exists.
        var analysis = new CaptureAnalyzer(model, options, diagnostics).Analyze(root);

        if (analysis.AllSymbols.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return new ClosureStageResult(root, false);
        }

        // 2. AuditOnly — prospective rows, no plans, no tree edits (M1 parity).
        if (options.AuditOnly)
        {
            HolderPlanner.EmitAuditRows(analysis, model, naming, statistics, diagnostics);
            return new ClosureStageResult(root, false);
        }

        // 3. Plan (freezes names; emits CS2J3204 boundary rows if any).
        var groups = HolderPlanner.BuildPlans(analysis, model, options, naming, diagnostics);

        // 4. Freeze: ANY blocking row keeps the file verbatim regardless of
        //    Strict; Strict=false only downgrades row severity (analyzer side).
        if (diagnostics.Any(d => d.Code != ClosureLiftCodes.AuditRow))
            return new ClosureStageResult(root, false);

        if (groups.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return new ClosureStageResult(root, false);
        }

        // 5. Generate + Rewrite. Holder classes first (ReplaceNodes preserves
        //    every unrelated node identity, so the frozen site/statement keys
        //    still resolve), then the single bottom-up site traversal.
        var rewriter = new CaptureSiteRewriter(groups, statistics);
        var withHolderClasses = rewriter.InsertHolderClasses(root);
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(withHolderClasses)!;

        // Fail loud rather than emitting an unreferenced holder class.
        if (rewriter.SitesRewritten == 0)
            throw new InvalidOperationException(
                "C32: plans were frozen but no capture site was rewritten; " +
                "refusing to emit a dangling holder class.");

        statistics.HolderClassesGenerated += groups.Count(g => !g.ArrayForm);
        statistics.HolderInstancesGenerated += groups.Count(g => !g.ArrayForm);
        statistics.CapturesLifted += groups.Sum(g => g.Symbols.Count);

        foreach (var reason in groups.SelectMany(g => g.Symbols).Select(s => s.Reason))
        {
            statistics.CapturesByReason[reason] =
                statistics.CapturesByReason.TryGetValue(reason, out var n) ? n + 1 : 1;
        }
        // SitesRewritten and ForeachPerIterationGroups are counted by the
        // rewriter itself (one rewriter per file → exact, no double counting).

        return new ClosureStageResult(rewritten, true);
    }
}
```

计数口径备忘（评审用）：`HolderClassesGenerated == HolderInstancesGenerated == holder 形态组数`——foreach 组的实例声明在语法树里只有一个声明点（位于循环体首，运行时每轮 new），按语法点计数；`CapturesLifted` 按符号；数组形态不产生 holder 类/实例计数（数组存储声明不是 holder）。

- [ ] **Step 3: CaptureAnalyzer 补无块体例程诊断（一处小改）**

M1 `Analyze` 对 `GetBodyRoot(routineNode) == null` 直接 `continue`（无任何行），表达式体成员中的可变参数捕获因此静默漏网。把 `candidateByRoutine` 循环开头改为先取 host 类型并报码：

```csharp
foreach (var (routineNode, candidateMap) in candidateByRoutine)
{
    var hostType = routineNode.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();

    var bodyRoot = GetBodyRoot(routineNode);
    if (bodyRoot == null)
    {
        // No statement body to place a holder instance (Task 10 contract #2;
        // expression-bodied routines and the D16 boundary family).
        Emit(ClosureLiftCodes.UnsupportedBoundary,
            "Mutably captured variable in a routine without a block body cannot be closure-lifted in v1.",
            routineNode.GetLocation(),
            hostType is null ? null : GetRoutineName(routineNode, hostType),
            candidateMap.Keys.FirstOrDefault()?.Name);
        continue;
    }
    // …（后续既有的 hostType 可插入性检查保持不动；删除原先循环内重复的
    //    var hostType = …FirstOrDefault() 声明，复用上面的变量。）…
```

- [ ] **Step 4: 升级 ClosureLiftPreprocessor（替换审计尾，AuditOnly 不变）**

文件头补 `using Microsoft.CodeAnalysis.Formatting;`；把 `Preprocess` 中从 `var analysis = new CaptureAnalyzer(...)` 到方法尾的整段替换为：

```csharp
        var stage = new HolderLiftStage();
        var stageResult = stage.Apply(new ClosureStageInput(
            (CompilationUnitSyntax)resolvedModel.SyntaxTree.GetRoot(),
            resolvedModel,
            options,
            _naming,
            stats,
            diagnostics));

        // Analyzer/planner own CS2J3201-3206 rows; bucket them for statistics.
        foreach (var d in diagnostics)
        {
            if (d.Code != ClosureLiftCodes.AuditRow)
                stats.AddSkipped(d.Code);
        }

        if (!stageResult.Changed)
            return new ClosureLiftResult(sourceCode, false, diagnostics, stats);

        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(stageResult.Root, workspace);
        var code = formatted.ToFullString();
        stats.FilesTransformed = 1;

        return new ClosureLiftResult(code, true, diagnostics, stats);
```

同时**删除**门面中已迁移的两个私有方法 `EmitAuditRows` 与 `CollectRoutineLocalNames`（改走 `HolderPlanner` 静态）；`BuildStandaloneModel` 与 `_naming` 跨文件表字段保持不动。类注释中「M1 audit only / Changed always false / Task 12 replaces…」一段更新为：

```csharp
/// <summary>
/// Entry point for the closure-lift preprocessor. Owns the per-run naming
/// table (one instance reused across files in project mode, mirrors E02) and
/// the Formatter step; semantics live in HolderLiftStage (Analyze →
/// AuditOnly rows, or Plan → Generate/Rewrite). AuditOnly keeps every M1
/// guarantee: Changed is false and output equals input byte for byte.
/// </summary>
```

注意：门面仍保留两道快路径（standalone parse 错误、无 lambda/匿名方法），它们在任何模式下逐字节直返。

- [ ] **Step 5: 编译 Core**

Run: `dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj --nologo -v q`
Expected: `0 个错误`。

- [ ] **Step 6: M1 审计单测回归（AuditOnly 行为冻结，必须全绿）**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~ClosureLiftUnitTests" --nologo -v q`
Expected: `失败: 0`（M1 20/20）。重点核对：
- `Audit_*` 全部行文本/统计不变（含 `Audit_ForeachIteration_PerIterationShapeAndStat`）；
- `Audit_RepeatedRuns_ByteIdenticalAndDeterministic` 仍绿（AuditOnly 不动树，天然成立）。
- 若 `Audit_LambdaInsideLocalFunction_CS2J3204` / `Audit_LambdaInFieldInitializer_CS2J3204` 因 Step 3 改动多出行：对照输入确认行只来自既有边界，不得来自块体正常方法。

- [ ] **Step 7: 手工冒烟（临时，不落测试文件）——转换路径已可达但链上仍 stub**

用 `dotnet run --project src/CSharpToJava.CLI` 对默认链跑任一既有样例工程（不带 `--enable-pass closure-lift`），确认输出与基线一致（stub 未翻，Task 16 才翻）。转换路径只经门面直连可达，默认链结构上不可达。

- [ ] **Step 8: 提交（四个文件逐个 add）**

```powershell
git add src/CSharpToJava.Core/ClosureLift/HolderPlanner.cs
git add src/CSharpToJava.Core/ClosureLift/HolderLiftStage.cs
git add src/CSharpToJava.Core/ClosureLift/CaptureAnalyzer.cs
git add src/CSharpToJava.Core/ClosureLift/ClosureLiftPreprocessor.cs
git commit -m "feat(preprocessing): C32 M2 HolderPlanner/HolderLiftStage 接通 Analyze→Plan→Generate→Rewrite（AuditOnly 冻结/边界 CS2J3204/Formatter 落地）"
```

<!-- APPEND-MARK-TASK12 -->

## Task 12: 转换单测 harness + 基础转换组（设计 §7.1 第 2-9 条）

**Files:**
- Modify: `tests/CSharpToJava.ProjectTests/ClosureLiftUnitTests.cs`（M1 20 条审计测试保留不动，本任务追加转换 helper 与 8 条转换测试）

纪律（设计 §7.1）：Core 单测只做**文本断言 + 输出树可编译断言**；可观察行为（计数结果/每轮值/调用时序）归 Task 18 fixture。转换入口为 `new ClosureLiftOptions()`（Strict=true、AuditOnly=false、HolderClass 默认形态）。

- [ ] **Step 1: helpers 区追加转换工具**

文件头 using 补一行：

```csharp
using Microsoft.CodeAnalysis.CSharp.Syntax;
```

在既有 `Audit(...)` 帮助方法之后追加：

```csharp
    // ── M2 transform helpers ──

    private static ClosureLiftResult Transform(string src, ClosureLiftOptions? options = null)
        => new ClosureLiftPreprocessor().Preprocess(
            src, ParseWithSemantics(src), options ?? new ClosureLiftOptions());

    /// <summary>Design §7.1: every transformed tree must compile (LanguageVersion.Latest, unsafe allowed).</summary>
    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "closure-transformed",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(
            errors.Count == 0,
            "Transformed output failed to compile:\n" + string.Join("\n", errors.Select(e => e.GetMessage())));
    }

    /// <summary>All IdentifierName nodes spelling <paramref name="name"/> (declarators/parameters are NOT included).</summary>
    private static IReadOnlyList<IdentifierNameSyntax> BareIdentifiers(string code, string name)
    {
        var root = (SyntaxNode)CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot();
        return root.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.Text == name)
            .ToList();
    }

    private static IReadOnlyList<string> HolderClassNames(string code)
    {
        var root = (SyntaxNode)CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot();
        return root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(c => c.Identifier.Text.StartsWith("Cs2jDisplayClass_", StringComparison.Ordinal))
            .Select(c => c.Identifier.Text)
            .ToList();
    }
```

- [ ] **Step 2: §7.1-2 只读捕获在转换模式下同样零变化**

```csharp
    [Fact]
    public void Transform_ReadOnlyCapture_UnchangedAndClean()
    {
        var src = @"
class C {
    int M() {
        int x = 41;
        System.Func<int> f = () => x + 1;
        return f();
    }
}";
        var r = Transform(src);

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        Assert.Equal(1, r.Statistics.FilesSkippedClean);
        Assert.Equal(0, r.Statistics.FilesTransformed);
    }
```

- [ ] **Step 3: §7.1-3 外部再赋值：holder/实例/字段/裸名清零**

```csharp
    [Fact]
    public void Transform_ExternalWrite_AllSitesFieldBacked_NoBareName()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { System.Console.WriteLine(x); };
        x = 1;
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Equal(1, r.Statistics.HolderClassesGenerated);
        Assert.Equal(1, r.Statistics.HolderInstancesGenerated);
        Assert.Equal(1, r.Statistics.CapturesLifted);
        Assert.Equal(1, r.Statistics.CapturesByReason[TriggerReason.ExternalWrite]);

        Assert.Contains("private sealed class Cs2jDisplayClass_M_0", r.OutputCode);
        Assert.Contains("public int _cs2jCap_x;", r.OutputCode);
        Assert.Contains("var __cs2jClosure0 = new Cs2jDisplayClass_M_0();", r.OutputCode);
        Assert.Contains("__cs2jClosure0._cs2jCap_x = 0;", r.OutputCode);
        Assert.Contains("__cs2jClosure0._cs2jCap_x = 1;", r.OutputCode);
        Assert.DoesNotContain("int x =", r.OutputCode);
        Assert.Empty(BareIdentifiers(r.OutputCode, "x"));
        AssertOutputCompiles(r.OutputCode);
    }
```

- [ ] **Step 4: §7.1-4 lambda 内写 + 外部读：同组单实例单字段**

```csharp
    [Fact]
    public void Transform_InsideLambdaWrite_ExternalRead_OneHolderOneField()
    {
        var src = @"
class C {
    void M() {
        int count = 0;
        System.Action inc = () => count++;
        inc();
        System.Console.WriteLine(count);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Single(HolderClassNames(r.OutputCode));
        Assert.Contains("public int _cs2jCap_count;", r.OutputCode);
        Assert.Contains("() => __cs2jClosure0._cs2jCap_count++", r.OutputCode);
        Assert.Contains("System.Console.WriteLine(__cs2jClosure0._cs2jCap_count)", r.OutputCode);
        Assert.Empty(BareIdentifiers(r.OutputCode, "count"));
        AssertOutputCompiles(r.OutputCode);
    }
```

- [ ] **Step 5: §7.1-5 内赋 + 外赋并存：holder 仅一个**

```csharp
    [Fact]
    public void Transform_InternalAndExternalWrites_SingleHolder()
    {
        var src = @"
class C {
    void M() {
        int count = 0;
        System.Action inc = () => count += 2;
        count = 5;
        inc();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Single(HolderClassNames(r.OutputCode));
        Assert.Single(HolderClassNames(r.OutputCode).Where(n => n == "Cs2jDisplayClass_M_0"));
        Assert.Contains("__cs2jClosure0._cs2jCap_count = 5;", r.OutputCode);
        Assert.Contains("() => __cs2jClosure0._cs2jCap_count += 2", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);
    }
```

- [ ] **Step 6: §7.1-6 多 lambda 共享：字段/实例共享**

```csharp
    [Fact]
    public void Transform_MultipleLambdas_ShareOneInstanceAndField()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action reader = () => System.Console.WriteLine(x);
        System.Action writer = () => { x = 9; };
        reader(); writer();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Single(HolderClassNames(r.OutputCode));
        Assert.Equal(1, r.Statistics.HolderInstancesGenerated);
        Assert.Contains("() => System.Console.WriteLine(__cs2jClosure0._cs2jCap_x)", r.OutputCode);
        Assert.Contains("() => { __cs2jClosure0._cs2jCap_x = 9; }", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);
    }
```

- [ ] **Step 7: §7.1-7 同作用域两个变量：一个类/实例、两字段顺序确定、跨运行字节一致**

```csharp
    [Fact]
    public void Transform_TwoSymbolsSameScope_OneHolderOrderedFields_Deterministic()
    {
        var src = @"
class C {
    void M() {
        int a = 1, b = 2;
        System.Action t = () => { a++; b += a; };
        t();
    }
}";
        var r1 = Transform(src);
        var r2 = Transform(src);

        Assert.True(r1.Changed);
        Assert.Equal(r1.OutputCode, r2.OutputCode);
        Assert.Single(HolderClassNames(r1.OutputCode));
        Assert.Equal(1, r1.Statistics.HolderInstancesGenerated);
        Assert.Equal(2, r1.Statistics.CapturesLifted);

        var holderIndex = r1.OutputCode.IndexOf("class Cs2jDisplayClass_M_0", StringComparison.Ordinal);
        var fieldA = r1.OutputCode.IndexOf("public int _cs2jCap_a;", StringComparison.Ordinal);
        var fieldB = r1.OutputCode.IndexOf("public int _cs2jCap_b;", StringComparison.Ordinal);
        Assert.True(holderIndex >= 0 && fieldA > holderIndex && fieldB > fieldA);

        // Cross-reference inside the initializer-free world: b += a reads the field.
        Assert.Contains("__cs2jClosure0._cs2jCap_b += __cs2jClosure0._cs2jCap_a", r1.OutputCode);
        Assert.Empty(BareIdentifiers(r1.OutputCode, "a"));
        Assert.Empty(BareIdentifiers(r1.OutputCode, "b"));
        AssertOutputCompiles(r1.OutputCode);
    }
```

- [ ] **Step 8: §7.1-8 参数捕获：体首实例 + 快照，签名不变**

```csharp
    [Fact]
    public void Transform_ParameterCapture_BodyFirstSnapshot_SignatureUnchanged()
    {
        var src = @"
class C {
    void Run(int seed, System.Action trigger) {
        seed++;
        System.Action a = () => seed += 2;
        a(); trigger();
        System.Console.WriteLine(seed);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("void Run(int seed, System.Action trigger)", r.OutputCode);
        var bodyIndex = r.OutputCode.IndexOf("void Run", StringComparison.Ordinal);
        var instanceIndex = r.OutputCode.IndexOf(
            "var __cs2jClosure0 = new Cs2jDisplayClass_Run_0();", StringComparison.Ordinal);
        var snapshotIndex = r.OutputCode.IndexOf(
            "__cs2jClosure0._cs2jCap_seed = seed;", StringComparison.Ordinal);
        var incrementIndex = r.OutputCode.IndexOf(
            "__cs2jClosure0._cs2jCap_seed++", StringComparison.Ordinal);
        Assert.True(instanceIndex > bodyIndex);
        Assert.True(snapshotIndex > instanceIndex && incrementIndex > snapshotIndex);

        // Exactly one bare 'seed': the one-time snapshot RHS (parameter token
        // in the signature is a parameter syntax, not an IdentifierName).
        Assert.Single(BareIdentifiers(r.OutputCode, "seed"));
        AssertOutputCompiles(r.OutputCode);
    }
```

- [ ] **Step 9: §7.1-9 嵌套 lambda：外层变量一层 holder，内层不生成第二套**

```csharp
    [Fact]
    public void Transform_NestedLambdas_SingleHolderLayer()
    {
        var src = @"
class C {
    void M() {
        int outer = 0;
        System.Action a = () => {
            System.Action b = () => { outer++; };
            b();
        };
        a();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Single(HolderClassNames(r.OutputCode));
        Assert.Equal(1, r.Statistics.HolderClassesGenerated);
        Assert.Contains("() => { __cs2jClosure0._cs2jCap_outer++; }", r.OutputCode);
        Assert.Empty(BareIdentifiers(r.OutputCode, "outer"));
        AssertOutputCompiles(r.OutputCode);
    }
```

- [ ] **Step 10: 跑本任务测试（M1 20 条须同时保持全绿）**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~ClosureLiftUnitTests" --nologo -v q`
Expected: `失败: 0`，28 个测试全部通过（20 审计 + 8 转换）。

若 `AssertOutputCompiles` 报编译错误，优先排查顺序：① Formatter 输出语法（生成语句的 trivia/分号）；② 字段类型全限定名（M1 TypeDisplay 已含 global:: 形态时 `ParseTypeName` 是否可解析）；③ 多声明器残余声明重建。不得通过改断言迁就错误输出。

- [ ] **Step 11: 提交（单文件）**

```powershell
git add tests/CSharpToJava.ProjectTests/ClosureLiftUnitTests.cs
git commit -m "test(preprocessing): C32 M2 转换单测基础组（§7.1 2-9：外赋/内赋/共享/同作用域/参数/嵌套＋可编译与裸名断言）"
```

<!-- APPEND-MARK-TASK13 -->

## Task 13: 转换单测循环/互操作/边界组（设计 §7.1 第 10-19 条）

**Files:**
- Modify: `tests/CSharpToJava.ProjectTests/ClosureLiftUnitTests.cs`（追加 10 条）

- [ ] **Step 1: §7.1-10 for 归纳变量：循环前实例、condition/incrementor 走字段**

```csharp
    [Fact]
    public void Transform_ForInduction_InstanceBeforeLoop_ConditionAndIncrementFieldBacked()
    {
        var src = @"
class C {
    void M(int n, System.Collections.Generic.List<System.Action> acts) {
        for (int i = 0; i < n; i++)
            acts.Add(() => System.Console.WriteLine(i));
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Equal(0, r.Statistics.ForeachPerIterationGroups);
        Assert.Contains("for (; __cs2jClosure0._cs2jCap_i < n; __cs2jClosure0._cs2jCap_i++)", r.OutputCode);
        Assert.Contains("acts.Add(() => System.Console.WriteLine(__cs2jClosure0._cs2jCap_i))", r.OutputCode);
        Assert.DoesNotContain("int i", r.OutputCode);

        var initIndex = r.OutputCode.IndexOf("__cs2jClosure0._cs2jCap_i = 0;", StringComparison.Ordinal);
        var forIndex = r.OutputCode.IndexOf("for (;", StringComparison.Ordinal);
        Assert.True(initIndex >= 0 && initIndex < forIndex);
        Assert.Empty(BareIdentifiers(r.OutputCode, "i"));
        AssertOutputCompiles(r.OutputCode);
    }
```

- [ ] **Step 2: §7.1-11 foreach 每轮语义：块体首句实例 + 非块体包块**

可观察的「循环后调用得每轮值」归 Task 18 fixture（运行时断言）；本测试锁定语法形态：每轮实例必须在循环体第一句、快照第二句。

```csharp
    [Fact]
    public void Transform_ForeachIteration_InstanceFirstInBody_AndEmbeddedBodyWrapped()
    {
        var src = @"
class C {
    void M(System.Collections.Generic.List<System.Action> acts) {
        foreach (var item in new[] { 1, 2, 3 })
        {
            acts.Add(() => System.Console.WriteLine(item));
        }
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Equal(1, r.Statistics.ForeachPerIterationGroups);

        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(r.OutputCode, ParseOptions).GetRoot();
        var loop = Assert.Single(root.DescendantNodes().OfType<ForEachStatementSyntax>());
        var body = Assert.IsType<BlockSyntax>(loop.Statement);
        Assert.Equal(3, body.Statements.Count);
        Assert.Equal("__cs2jClosure0",
            ((LocalDeclarationStatementSyntax)body.Statements[0]).Declaration.Variables[0].Identifier.Text);
        var snapshot = Assert.IsType<ExpressionStatementSyntax>(body.Statements[1]);
        Assert.IsType<AssignmentExpressionSyntax>(snapshot.Expression);
        Assert.Equal("item",
            ((IdentifierNameSyntax)((AssignmentExpressionSyntax)snapshot.Expression).Right).Identifier.Text);

        // The snapshot RHS is the single surviving bare 'item'; lambda + body sites are fields.
        Assert.Single(BareIdentifiers(r.OutputCode, "item"));
        AssertOutputCompiles(r.OutputCode);

        // Non-block embedded body: wrapped with instance + snapshot + original statement.
        var embedded = @"
class C {
    void M(System.Collections.Generic.List<System.Action> acts) {
        foreach (var v in new[] { 1, 2 }) acts.Add(() => System.Console.WriteLine(v));
    }
}";
        var r2 = Transform(embedded);
        var root2 = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(r2.OutputCode, ParseOptions).GetRoot();
        var loop2 = Assert.Single(root2.DescendantNodes().OfType<ForEachStatementSyntax>());
        var wrapped = Assert.IsType<BlockSyntax>(loop2.Statement);
        Assert.Equal(3, wrapped.Statements.Count);
        AssertOutputCompiles(r2.OutputCode);
    }
```

- [ ] **Step 3: §7.1-12 静态 lambda / 无捕获 lambda：零生成物**

```csharp
    [Fact]
    public void Transform_StaticAndCapturelessLambdas_NoHolder()
    {
        var src = @"
class C {
    void M() {
        System.Action a = static () => System.Console.WriteLine(1);
        System.Func<int> b = () => 42;
        a();
        System.Console.WriteLine(b());
    }
}";
        var r = Transform(src);

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        Assert.Empty(HolderClassNames(r.OutputCode));
        Assert.Equal(0, r.Statistics.CapturesLifted);
    }
```

- [ ] **Step 4: §7.1-13 async lambda 内可变捕获：holder 正常、await 不被破坏**

```csharp
    [Fact]
    public void Transform_AsyncLambdaMutableCapture_AwaitPreservedAndCompiles()
    {
        var src = @"
class C {
    async System.Threading.Tasks.Task M() {
        int x = 0;
        System.Func<System.Threading.Tasks.Task> f = async () => {
            await System.Threading.Tasks.Task.Yield();
            x++;
        };
        await f();
        System.Console.WriteLine(x);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("await System.Threading.Tasks.Task.Yield();", r.OutputCode);
        Assert.Contains("__cs2jClosure0._cs2jCap_x++", r.OutputCode);
        Assert.Empty(BareIdentifiers(r.OutputCode, "x"));
        AssertOutputCompiles(r.OutputCode);
    }
```

- [ ] **Step 5: §7.1-14 表达式树：纯引号不改；混合引用 CS2J3206 且文件原样**

```csharp
    [Fact]
    public void Transform_ExpressionTree_PureUnchanged_MixFreezesFile()
    {
        var pure = @"
using System;
using System.Linq.Expressions;
class C {
    void M() {
        Expression<Func<int, int>> e = x => x + 1;
    }
}";
        var rp = Transform(pure);
        Assert.False(rp.Changed);
        Assert.Equal(pure, rp.OutputCode);

        var mix = @"
using System;
using System.Linq.Expressions;
class C {
    void M() {
        int x = 0;
        Expression<Func<int>> q = () => x;
        Action a = () => { x++; };
    }
}";
        var rm = Transform(mix);
        Assert.False(rm.Changed);
        Assert.Equal(mix, rm.OutputCode);
        Assert.Contains(rm.Diagnostics, d =>
            d.Code == "CS2J3206" && d.Severity == ClosureLiftSeverity.Error);
        Assert.DoesNotContain(rm.Diagnostics, d => d.Code == "CS2J3207");
    }
```

- [ ] **Step 6: §7.1-15 普通局部 ref 实参改 `ref instance.Field`；ref 形参/ref 局部冻结**

```csharp
    [Fact]
    public void Transform_RefArgument_FieldBacked_RefKindsFreeze()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { x = 2; };
        Mutate(ref x);
        a();
    }
    static void Mutate(ref int z) { z++; }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("Mutate(ref __cs2jClosure0._cs2jCap_x)", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);

        var byRefParameter = @"
class C {
    void M(ref int p) {
        System.Action a = () => { p++; };
    }
}";
        var rp = Transform(byRefParameter);
        Assert.False(rp.Changed);
        Assert.Equal(byRefParameter, rp.OutputCode);
        Assert.Contains(rp.Diagnostics, d => d.Code == "CS2J3201");

        var refLocal = @"
class C {
    void M() {
        int z = 0;
        ref int p = ref z;
        System.Action a = () => { p++; };
    }
}";
        var rl = Transform(refLocal);
        Assert.False(rl.Changed);
        Assert.Equal(refLocal, rl.OutputCode);
        Assert.Contains(rl.Diagnostics, d => d.Code == "CS2J3201");
    }
```

- [ ] **Step 7: §7.1-16 残余匿名方法 3203 / 残余局部函数边界 3204 / 指针 3205：转换模式文件原样**

```csharp
    [Theory]
    [InlineData("CS2J3203", @"
class C {
    void M() {
        System.Comparison<int> c = delegate(int a, int b) { return a - b; };
    }
}")]
    [InlineData("CS2J3204", @"
class C {
    void M() {
        void Local() {
            System.Action a = () => { System.Console.WriteLine(1); };
        }
        Local();
    }
}")]
    [InlineData("CS2J3205", @"
unsafe class C {
    void M() {
        int* p = null;
        System.Action a = () => { var z = p; };
    }
}")]
    public void Transform_BlockingDiagnostic_FileVerbatim(string code, string src)
    {
        var r = Transform(src);

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        Assert.Empty(HolderClassNames(r.OutputCode));
        Assert.Contains(r.Diagnostics, d =>
            d.Code == code && d.Severity == ClosureLiftSeverity.Error);
    }
```

说明：CS2J3202（坏符号/ErrorType）为防御性分支——同树符号必有同树声明引用，合成输入构造不出来；M1 亦无该码的直连用例，评审以代码走查覆盖，不伪造输入。

- [ ] **Step 8: §7.1-17 与 E02 共存：已提升私有方法的 `_cs2jCap_*` 形参按普通参数提升**

```csharp
    [Fact]
    public void Transform_E02LiftedShape_CaptureParameterLiftsNormally()
    {
        var src = @"
class C {
    static void M_cs2jLifted(C @this, int _cs2jCap_count) {
        System.Action a = () => { _cs2jCap_count++; };
        a();
        System.Console.WriteLine(_cs2jCap_count);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Single(HolderClassNames(r.OutputCode));
        Assert.Contains("(C @this, int _cs2jCap_count)", r.OutputCode); // 签名不动
        // 唯二裸名扫描：快照 RHS 一处；另两处（lambda/WriteLine）已是字段访问。
        Assert.Single(BareIdentifiers(r.OutputCode, "_cs2jCap_count"));
        AssertOutputCompiles(r.OutputCode);
    }
```

- [ ] **Step 9: §7.1-18 与 E05 共存：标准链产物（无匿名方法）统计正确、无 3203**

```csharp
    [Fact]
    public void Transform_E05OutputShape_NoResidualRow_StatsCorrect()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { x++; };
        System.Func<int> b = () => 42;
        a();
        System.Console.WriteLine(x + b());
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "CS2J3203");
        Assert.True(r.Statistics.SitesRewritten >= 3);
        Assert.Equal(1, r.Statistics.CapturesLifted);
        AssertOutputCompiles(r.OutputCode);
    }
```

- [ ] **Step 10: §7.1-19 lock 同步；using 局部/const 不提升**

```csharp
    [Fact]
    public void Transform_LockExpressionFieldBacked_UsingAndConstNotLifted()
    {
        var src = @"
class C {
    private readonly object _gate = new object();
    void M() {
        object locker = new object();
        System.Action a = () => { System.Console.WriteLine(locker); };
        lock (locker) { System.Console.WriteLine(locker); }
        locker = _gate;
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("lock (__cs2jClosure0._cs2jCap_locker)", r.OutputCode);
        Assert.Contains("System.Console.WriteLine(__cs2jClosure0._cs2jCap_locker)", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);

        var readOnly = @"
using System.IO;
class C {
    void M(MemoryStream u) {
        const int c = 3;
        System.Func<int> f = () => c + (int)u.Length;
        System.Console.WriteLine(f());
    }
}";
        var rr = Transform(readOnly);
        Assert.False(rr.Changed);
        Assert.Equal(readOnly, rr.OutputCode);
        Assert.DoesNotContain("_cs2jCap_c", rr.OutputCode);
    }
```

注意：`using 局部`不可再赋值（基线即错）、`const` 无写站点，二者按 D10 永不提升；lock 站点由 M1 分析器以 `ClosureSiteKind.Lock` 冻结，rewriter 仅替换内层表达式，`lock(…)` 父形态保持。

- [ ] **Step 11: 跑全部 ClosureLift 单测**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~ClosureLiftUnitTests" --nologo -v q`
Expected: `失败: 0`，38 个测试全部通过（20 审计 + 8 基础转换 + 本任务 10）。

- [ ] **Step 12: 提交**

```powershell
git add tests/CSharpToJava.ProjectTests/ClosureLiftUnitTests.cs
git commit -m "test(preprocessing): C32 M2 转换单测循环/互操作/边界组（§7.1 10-19：for/foreach 二分/async/表达式树/ref/3203-3205/E02/E05/lock）"
```

<!-- APPEND-MARK-TASK14 -->

## Task 14: 转换单测收尾组（设计 §7.1 第 20-25 条）＋三工程回归收口

**Files:**
- Modify: `tests/CSharpToJava.ProjectTests/ClosureLiftUnitTests.cs`（追加 6 条，§7.1 全 25 条至此齐备）

- [ ] **Step 1: §7.1-20 this 捕获（含实例字段读写）：不动**

```csharp
    [Fact]
    public void Transform_ThisAndFieldCapture_Unchanged()
    {
        var src = @"
class C {
    private int _n;
    void Use() { }
    void M() {
        System.Action a = () => { Use(); _n++; };
        a();
    }
}";
        var r = Transform(src);

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        Assert.Empty(HolderClassNames(r.OutputCode));
    }
```

- [ ] **Step 2: §7.1-21 多声明器仅提升被捕获者**

```csharp
    [Fact]
    public void Transform_MultiDeclarator_OnlyLiftedSymbolRemoved()
    {
        var src = @"
class C {
    void M() {
        int a = 1, b = 2;
        System.Action w = () => a++;
        System.Console.WriteLine(b);
        w();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("int b = 2;", r.OutputCode);
        Assert.DoesNotContain("int a =", r.OutputCode);
        Assert.Empty(BareIdentifiers(r.OutputCode, "a"));
        Assert.Single(BareIdentifiers(r.OutputCode, "b")); // WriteLine(b) 原样保留
        AssertOutputCompiles(r.OutputCode);
    }
```

- [ ] **Step 3: §7.1-22 幂等：第二次 Changed=false 且字节相同（跨文本重入靠名字前缀，不依赖内存 annotation）**

```csharp
    [Fact]
    public void Transform_Idempotent_SecondRunUnchangedByteIdentical()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => x++;
        x = 1;
        a();
    }
}";
        var r1 = Transform(src);
        Assert.True(r1.Changed);

        // Fresh preprocessor + freshly parsed semantic model: reentry relies on
        // the reserved name prefixes only, exactly like the project pipeline.
        var r2 = new ClosureLiftPreprocessor().Preprocess(
            r1.OutputCode, ParseWithSemantics(r1.OutputCode), new ClosureLiftOptions());

        Assert.False(r2.Changed);
        Assert.Equal(r1.OutputCode, r2.OutputCode);
        Assert.Equal(0, r2.Statistics.HolderClassesGenerated);
        Assert.DoesNotContain("CS2J3207", string.Join("\n", r2.Diagnostics.Select(d => d.Code)));
    }
```

- [ ] **Step 4: §7.1-23 AuditOnly 矩阵：Changed=false、统计/CS2J3207 报告保留**

```csharp
    [Fact]
    public void Transform_AuditOnly_StillVerbatimWithRows()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => x++;
    }
}";
        var r = Transform(src, new ClosureLiftOptions(AuditOnly: true));

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        Assert.Equal(0, r.Statistics.FilesTransformed);
        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J3207"));
        Assert.Equal("x", row.SymbolName);
        Assert.Contains("Cs2jDisplayClass_M_0", row.Message);
    }
```

- [ ] **Step 5: §7.1-24 非默认数组形态：`T[1]` + `[0]` 站点、可编译、幂等**

```csharp
    [Fact]
    public void Transform_SingleElementArrayForm_SlotShapeCompilesAndIsIdempotent()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { System.Console.WriteLine(x); };
        x = 1;
        a();
    }
}";
        var options = new ClosureLiftOptions(HolderForm: HolderForm.SingleElementArray);
        var r = Transform(src, options);

        Assert.True(r.Changed);
        Assert.DoesNotContain("Cs2jDisplayClass", r.OutputCode);
        Assert.Contains("var __cs2jArr_x = new int[1];", r.OutputCode);
        Assert.Contains("__cs2jArr_x[0] = 0;", r.OutputCode);
        Assert.Contains("__cs2jArr_x[0] = 1;", r.OutputCode);
        Assert.Contains("System.Console.WriteLine(__cs2jArr_x[0])", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);

        var r2 = new ClosureLiftPreprocessor().Preprocess(
            r.OutputCode, ParseWithSemantics(r.OutputCode), options);
        Assert.False(r2.Changed);
        Assert.Equal(r.OutputCode, r2.OutputCode);
    }
```

数组形态只做 Core 文本/编译对照（设计 §4.4 尾），不做默认验收、不进 Task 18 fixture；holder 形态与数组形态的**运行时行为等价**由 fixture（holder 形态）与可编译性共同保证。

- [ ] **Step 6: §7.1-25 nameof / 特性 / XML 注释中的同名标识符不改写**

```csharp
    [Fact]
    public void Transform_NameOfAttributeAndXmlDoc_SameNameIdentifiersPreserved()
    {
        var src = @"
class C {
    /// <param name=""seed"">documented</param>
    [System.Obsolete(nameof(seed))]
    void M(int seed) {
        seed = 1;
        System.Action a = () => seed++;
        var n = nameof(seed);
        System.Console.WriteLine(n);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("/// <param name=\"seed\">documented</param>", r.OutputCode);
        Assert.Contains("[System.Obsolete(nameof(seed))]", r.OutputCode);
        Assert.Contains("var n = nameof(seed);", r.OutputCode);
        Assert.DoesNotContain("nameof(__cs2jClosure", r.OutputCode);

        // Snapshot RHS (1) + the two nameof(seed) IdentifierNames (2) = 3;
        // every other seed site is field-backed.
        Assert.Equal(3, BareIdentifiers(r.OutputCode, "seed").Count);
        AssertOutputCompiles(r.OutputCode);
    }
```

- [ ] **Step 7: ClosureLift 全单测（审计 20 + 转换 24）**

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~ClosureLiftUnitTests" --nologo -v q`
Expected: `失败: 0`，44 个测试全部通过。

§7.1 覆盖矩阵自查（25 条无遗漏）：1 命名/Options 由 M1 第 1/9/10 条审计用例锁定；2-25 由 Task 12-14 转换用例逐条覆盖；3202 防御码走代码走查（Task 13 已说明）。

- [ ] **Step 8: 转换器直连回归（旧代码一行未改，必须零改动全绿）**

Run（逐条）：

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~LambdaExternalReassignment" --nologo -v q
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~LinqRewriterVariableCapture" --nologo -v q
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --nologo -v q
dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj --nologo -v q
```

Expected: 四条全部 `失败: 0`。前两条是转换器旧路径直连回归（不经预处理链）；后两条证明 M2 新增代码在默认关下不产生任何外溢。

- [ ] **Step 9: 提交**

```powershell
git add tests/CSharpToJava.ProjectTests/ClosureLiftUnitTests.cs
git commit -m "test(preprocessing): C32 M2 转换单测收尾（§7.1 20-25：this/多声明器/幂等/AuditOnly 矩阵/数组形态/nameof 保留），25 条齐备"
```

<!-- APPEND-MARK-TASK15 -->

## Task 15: 适配器升级——真实改写 + 中间树 residual 闸门（设计 §5、§7.3）

M1 的 `ClosureLiftPassAdapter` 永远返回 `null`（逐字拷贝）、`AfterFilesAsync` 空转。本任务接通两件事：Changed 文件返回 `PassFileRewrite`；`AfterFilesAsync` 重解析中间树全量 .cs 做 AuditOnly 口径再分析，残余可变捕获 >0 按 strict 定级。

**Files:**
- Modify: `src/CSharpToJava.Core/ClosureLift/ClosureLiftPreprocessor.cs`（暴露 residual 扫描入口）
- Modify: `tests/CSharpToJava.ProjectTests/ClosureLiftUnitTests.cs`（2 条闸门测试）
- Modify: `src/CSharpToJava.CLI/Preprocessing/Adapters/ClosureLiftPassAdapter.cs`（RewriteFileAsync / AfterFilesAsync / 文案 / descriptor 描述）

- [ ] **Step 1: 门面暴露 residual 扫描入口（复用 BuildStandaloneModel，不复制第二套分析）**

在 `ClosureLiftPreprocessor` 的公共 API 区（`Preprocess` 重载旁）新增：

```csharp
    /// <summary>
    /// Tree residual gate (design §7.3): re-runs the read-only capture
    /// analysis on an intermediate-tree file WITHOUT a project compilation
    /// (standalone trusted-assembly model, same construction as the single
    /// file path). Returns the number of mutably captured locals/parameters
    /// still present; holder-lift output is 0 by construction (holder
    /// instances are effectively-final reserved names and field accesses are
    /// member expressions, never candidates).
    /// Parse-broken files are the pass fast-path family and return 0: the
    /// pass never edits them, so they cannot own a closure-lift residual.
    /// </summary>
    public int CountResidualMutableCaptures(string code)
    {
        var model = BuildStandaloneModel(code, out var parseError);
        if (parseError != null)
            return 0;

        var discarded = new List<ClosureLiftDiagnostic>();
        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        var analysis = new CaptureAnalyzer(model, new ClosureLiftOptions(), discarded).Analyze(root);
        return analysis.AllSymbols.Count;
    }
```

要点：
- 诊断列表刻意丢弃——闸门只计数，**不在此重复发码**；3201-3206 的归属行在 `RewriteFileAsync` 已经逐文件发过。
- 走默认 `ClosureLiftOptions()`（`AuditOnly=false` 不影响分析器：AuditOnly 只在 stage 层分流，`CaptureAnalyzer` 无此分支）。
- 生成类内的字段/方法不会成为候选（分析根只取成员体，holder 类是嵌套类，其成员体无 lambda 捕获局部）；`__cs2jClosure*` 实例与 `__cs2jArr_*` 槽位是只读局部，M1 的保留名豁免与「无写站点」双重保证计数为 0。

- [ ] **Step 2: 闸门两条 Core 测试**

追加到 `ClosureLiftUnitTests.cs`：

```csharp
    [Fact]
    public void ResidualGate_TransformedOutput_Zero()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => x++;
        x = 1;
        a();
    }
}";
        var r = Transform(src);
        Assert.True(r.Changed);

        var gate = new ClosureLiftPreprocessor().CountResidualMutableCaptures(r.OutputCode);
        Assert.Equal(0, gate);
    }

    [Fact]
    public void ResidualGate_RawMutableCapture_Counted()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => x++;
    }
}";
        var gate = new ClosureLiftPreprocessor().CountResidualMutableCaptures(src);
        Assert.Equal(1, gate);
    }
```

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~ClosureLiftUnitTests" --nologo -v q`
Expected: `失败: 0`，46 个测试全部通过。

- [ ] **Step 3: 提交 A（Core 闸门 + 测试）**

```powershell
git add src/CSharpToJava.Core/ClosureLift/ClosureLiftPreprocessor.cs
git add tests/CSharpToJava.ProjectTests/ClosureLiftUnitTests.cs
git commit -m "feat(preprocessing): C32 M2 residual 扫描入口 CountResidualMutableCaptures 与闸门单测（设计 §7.3）"
```

- [ ] **Step 4: 适配器 RewriteFileAsync 返回真实改写**

把 `RewriteFileAsync` 从 verbose 行到方法尾的整段（M1 骨架 L1648-1657）替换为：

```csharp
        if (_verbose)
        {
            string status;
            if (_options.AuditOnly)
                status = "audit";
            else if (result.Changed)
                status = "transformed";
            else if (result.Diagnostics.Any(d => d.Code != ClosureLiftCodes.AuditRow))
                status = "blocked (copy)";
            else
                status = "clean (copy)";
            Console.WriteLine($"[closure-lift] {status}: {context.RelativePath}");
        }

        return Task.FromResult(result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true, PreserveBom = true }
            : (PassFileRewrite?)null);
```

`PreserveBom: true` 对齐 E02 之后各语义 pass 的写盘纪律（原文件有 BOM 则保留）；冻结文件返回 `null`，由脊柱逐字拷贝，字节级原样。

- [ ] **Step 5: AfterFilesAsync 中间树闸门（替换 M1 的空转实现 L1659-1661）**

```csharp
    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        // Design §7.3 gate: re-parse every .cs under the intermediate tree in
        // audit terms. Transformed files are 0 by construction; frozen files
        // (any CS2J3201-3206 row) keep their original captures and are owned
        // by their per-file rows — M3 gate §8.4 excludes such owned Warning
        // rows when judging the tree.
        var residual = 0;
        foreach (var csFile in Directory.EnumerateFiles(
                     tree.IntermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var fileCount = _preprocessor.CountResidualMutableCaptures(File.ReadAllText(csFile));
            if (fileCount > 0 && _verbose)
            {
                Console.WriteLine(
                    $"[closure-lift] residual gate: {fileCount} capture(s) in {Path.GetRelativePath(tree.IntermediateRoot, csFile)}");
            }
            residual += fileCount;
        }

        _statistics.ResidualMutableCaptures = residual;

        if (residual > 0)
        {
            var severity = _strict
                ? ClosureLiftSeverity.Error
                : ClosureLiftSeverity.Warning;
            _diagnostics.Add(new ClosureLiftDiagnostic(
                severity,
                ClosureLiftCodes.UnresolvableSymbol,
                $"C32 residual gate: {residual} mutable lambda capture(s) remain in the intermediate tree; " +
                "the complete capture-site set cannot be guaranteed for those file(s)."));
        }

        return Task.CompletedTask;
    }
```

⚠️ 诊断码裁决（冻结 3201-3207，不新造码）：闸门汇总行复用 **CS2J3202**——其语义本就是「无法保证站点全集」，残余可变捕获正是该保证在改写后被破坏的字面含义；闸门行是**树级汇总**，`SkippedByReason` 不因此加桶（桶只由逐文件 `MergeStatistics` 填），M3 画像里可用「3202 汇总行 + 逐文件归属行」配对核对。执行者若评审认为必须独立码，须先走设计文档改码（占号 CS2J3208）再实施，不得在实现期擅自加码。

注意赋值用 `=` 而非 `+=`：闸门以中间树实测为准（同 E02 L77 的 `LocalFunctionsResidual = residualCount`），不与逐文件统计叠加。

- [ ] **Step 6: summary 行与 descriptor 描述换 M2 文案**

`BuildChainSummaryLine` 替换为：

```csharp
    public override string? BuildChainSummaryLine()
    {
        var blocked = _statistics.SkippedByReason.Values.Sum();
        return $"closure-lift: {_statistics.FilesTransformed} transformed, " +
               $"{_statistics.FilesSkippedClean} clean, " +
               $"{_statistics.HolderClassesGenerated} holder class(es), " +
               $"{_statistics.CapturesLifted} capture(s) lifted, " +
               $"{blocked} blocked, " +
               $"{_statistics.ResidualMutableCaptures} residual";
    }
```

AuditOnly 运行时 `FilesTransformed=0/HolderClassesGenerated=0`，行内容自然退化为审计口径（blocked 桶照常），不另发分支文案。

`M1Descriptor` 的 description 换为（Task 16 翻转时整个 descriptor 迁走并删除 `M1Descriptor`，本任务只改描述文本）：

```csharp
        description: "M2 holder-lift: rewrite mutably captured locals/parameters into private holder classes before lambdas (C32).",
```

- [ ] **Step 7: 编译 CLI + 全量回归（默认关，默认计划必须零变化）**

Run（逐条）：

```powershell
dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj --nologo -v q
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --nologo -v q
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --nologo -v q
```

Expected: build `0 个错误`；两个工程 `失败: 0`。此刻注册表仍指向 stub（Task 16 才翻转），CLI 测试零变化；真实改写只能经门面直连到达——已被 46 条 ProjectTests 锁定。

- [ ] **Step 8: 提交 B（适配器）**

```powershell
git add src/CSharpToJava.CLI/Preprocessing/Adapters/ClosureLiftPassAdapter.cs
git commit -m "feat(preprocessing): C32 M2 适配器接通真实改写与中间树 residual 闸门（Changed→PassFileRewrite，CS2J3202 汇总行，strict 定级）"
```

<!-- APPEND-MARK-TASK16 -->

## Task 16: 注册表翻转一次完成（stub→Implemented，仍 default-off）

设计 §3.2 五文件翻转中的注册侧三处（另两处排序/stub 测试在 Task 17）。翻转后 closure-lift 可由 `preprocess --pass closure-lift` / `--enable-pass closure-lift` 显式启用，但**默认计划逐字节不变**（descriptor `defaultEnabled: false`，不加 legacy toggle 键）。

**Files:**
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs`（删 stub 条目，留 Ids 常量）
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs`（插 Implemented descriptor + iterator 补边）
- Modify: `src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs`（using + 工厂臂）
- Modify: `src/CSharpToJava.CLI/Preprocessing/Adapters/ClosureLiftPassAdapter.cs`（删 M1Descriptor，Descriptor 走目录）

- [ ] **Step 1: PassStubCatalog 删除 closure-lift stub 条目**

把 L77-86 的整个 `new(Ids.ClosureLift, …)` 条目替换为翻转注释（同 L75-76 unsafe-ffm 先例）：

```csharp
        // Ids.ClosureLift flipped to Implemented in PassCatalog (C32 M2);
        // the descriptor lives there and no stub entry remains.
```

**L30 `Ids.ClosureLift` 常量保留不动**——PassCatalog 的 descriptor/边、PassChainFactory 工厂臂、member-desugar 既有边都引用它（iterator-lowering 先例 §3.4）。

- [ ] **Step 2: PassCatalog 插 Implemented descriptor（iterator 条目之前）**

在 L332 `new(PassStubCatalog.Ids.IteratorLowering, …)` 条目**之前**插入：

```csharp
        new(
            PassStubCatalog.Ids.ClosureLift,
            "Closure/display class lift",
            ".cs2j-closure-lift-src",
            PassStage.StateMachine,
            PassStatus.Implemented,
            // C32 M2 ships default-off; M3 owns the enable-by-default review (design §8 gate 6).
            defaultEnabled: false,
            runAfterIds: new[] { PassIds.RefOutIn },
            needsSemanticModel: true,
            verifyBuildByDefault: true,
            verifyTestsByDefault: true,
            description: "Lift mutably captured locals/parameters into deterministic private holder classes before lambdas (C32 M2).",
            catalogItems: "C32"),
```

与 M1 适配器内 descriptor 的差异仅此三处：`PassStatus.Implemented`、显式 `needsSemanticModel: true`、description 去掉 M2 前缀字样；`catalogItems` 收敛为 **`"C32"`**（C45 接续时另立设计再扩，设计 §9 里程碑表注）。

同文件 iterator 条目 L339 的 runAfterIds 补 closure-lift 边（声明序与拓扑序双保险，排序测试锁定的是拓扑序）：

```csharp
            runAfterIds: new[] { PassIds.MakeReadonly, PassStubCatalog.Ids.ClosureLift },
```

member-desugar L361 的 `PassStubCatalog.Ids.ClosureLift` 边 M1 已预埋，不动。

- [ ] **Step 3: PassChainFactory 补 using 与工厂臂**

L1-2 之间（字母序，ArrayMethod 之后、DefaultParameter 之前）加：

```csharp
using CSharpToJava.Core.ClosureLift;
```

L343 iterator 臂**之前**插入 closure-lift 臂（options 内联 `new()`；末参透传 `config.Strict`；无 OptionsFactory——同 iterator，不进 `PassChainConfiguration`）：

```csharp
            PassStubCatalog.Ids.ClosureLift => new ClosureLiftPassAdapter(
                new ClosureLiftOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),

```

`PassChainConfiguration`（L39-93）与 `BuildLegacyToggles`（L104-134）**不加字段、不加键**：默认关 pass 无历史 convert-project 开关，启用只走 `--enable-pass`（C28 lexical-normalize 同款）。

- [ ] **Step 4: 适配器删除私有 M1Descriptor**

删除 `ClosureLiftPassAdapter` 内的 `M1Descriptor` 静态字段（Task 15 骨架 L1586-1597），Descriptor 属性改为目录查询（同 E02 L42）：

```csharp
    public override PassDescriptor Descriptor
        => PassCatalog.ImplementedById(PassStubCatalog.Ids.ClosureLift);
```

- [ ] **Step 5: 编译（不跑排序测试——本提交预期两个测试类转红，Task 17 立即修复）**

Run: `dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj --nologo -v q`
Expected: `0 个错误`。

预期转红（先记录、不在本任务修）：`PassChainOrderTests`（LockedChain 仍 29 项且缺 closure-lift 映射/适配器覆盖）、`PassStubCatalogTests`（启用计划用例仍把 closure-lift 当 stub 靶子）。两红由 Task 17 同里程碑紧接着修复，**这两个提交必须连续，不得跨越里程碑边界或单独交付**。

快速确认默认计划未被污染（descriptor defaultEnabled:false 不进默认计划）：

Run: `dotnet run --project src/CSharpToJava.CLI -- preprocess --list-passes | Select-String "closure-lift"`
Expected: 输出一行 closure-lift，状态为 Implemented 且默认关（Enabled 列为否/`default: off` 形态，以现有列头为准）。

- [ ] **Step 6: 提交**

```powershell
git add src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs
git add src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs
git add src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs
git add src/CSharpToJava.CLI/Preprocessing/Adapters/ClosureLiftPassAdapter.cs
git commit -m "feat(preprocessing): C32 M2 closure-lift stub 翻转为 Implemented（StateMachine/默认关/RefOutIn 后/iterator 前），--enable-pass 可达"
```

<!-- APPEND-MARK-TASK17 -->

## Task 17: 排序测试与 stub 测试随翻转修复（五处）+ 全量回归

Task 16 转红的两个测试类在本任务修复；所有改点都是**随翻转的同步断言更新**，不放松任何既有关卡。

**Files:**
- Modify: `tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs`（LockedChain / EnabledByDefault / catalog 映射 / 新增隔离槽位 / legacy 排除清单 / 类注释）
- Modify: `tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs`（两处 stub 靶子换 async-normalize）

- [ ] **Step 1: LockedChain 在 UnsafeFfm 与 IteratorLowering 之间插 closure-lift（29→30 项）**

L45-46 之间插入一行：

```csharp
        PassStubCatalog.Ids.UnsafeFfm,
        PassStubCatalog.Ids.ClosureLift,
        PassStubCatalog.Ids.IteratorLowering,
```

- [ ] **Step 2: EnabledByDefault 排除 closure-lift（第三个默认关的 Implemented pass）**

```csharp
    // lexical-normalize / partial-merge land Implemented/default-off (gated);
    // closure-lift ships M2 default-off pending the M3 enable-by-default review.
    private static readonly string[] EnabledByDefault =
        LockedChain.Where(id =>
            id != PassStubCatalog.Ids.LexicalNormalize
            && id != PassStubCatalog.Ids.PartialMerge
            && id != PassStubCatalog.Ids.ClosureLift).ToArray();
```

类注释 L13-15「As of 2026-09-30 every implemented pass is default-on」一句更新为：

```csharp
/// every implemented pass is default-on except the gated lexical-normalize,
/// partial-merge and the M2 closure-lift (default-off pending M3 review).
```

- [ ] **Step 3: catalog 映射加 C32 断言**

`Implemented_catalog_passes_carry_their_catalog_item_mappings` 末尾（L245 PartialMerge 断言之后）加：

```csharp
        Assert.Equal("C32", byId[PassStubCatalog.Ids.ClosureLift].CatalogItems);
        Assert.False(byId[PassStubCatalog.Ids.ClosureLift].DefaultEnabled);
```

`ChainFactory_creates_an_adapter_for_every_implemented_pass`（L249-271）**无需改**：closure-lift 工厂臂 options 内联 `new()`、不依赖 OptionsFactory，循环自动覆盖；该测试转绿即证明工厂臂对新 descriptor 可达。

- [ ] **Step 4: 新增 closure-lift 隔离槽位测试（仿 UnsafeFfm L123-169）**

在 `UnsafeFfm_when_enabled_runs_in_its_locked_slot_…` 之后追加：

```csharp
    [Fact]
    public void ClosureLift_when_enabled_runs_in_its_locked_slot_after_ref_out_in_before_iterator()
    {
        var registry = PassCatalog.BuildRegistry();
        var allOff = LockedChain.ToDictionary(id => id, _ => false);

        // Default-off: absent from the default plan even with every legacy
        // toggle at its baseline.
        var baseline = PassPlanResolver.Resolve(registry);
        Assert.DoesNotContain(baseline.Enabled, p => p.Id == PassStubCatalog.Ids.ClosureLift);

        // Isolated slot: ref-out-in → closure-lift → iterator-lowering.
        var isolatedToggles = new Dictionary<string, bool>(allOff)
        {
            [PassIds.RefOutIn] = true,
            [PassStubCatalog.Ids.IteratorLowering] = true,
        };
        var plan = PassPlanResolver.Resolve(
            registry,
            isolatedToggles,
            enablePassIds: new[] { PassStubCatalog.Ids.ClosureLift });

        Assert.True(plan.IsValid);
        Assert.Equal(new[]
        {
            PassIds.RefOutIn,
            PassStubCatalog.Ids.ClosureLift,
            PassStubCatalog.Ids.IteratorLowering,
        }, plan.Enabled.Select(p => p.Id).ToArray());
    }
```

- [ ] **Step 5: legacy toggle 排除清单加 closure-lift**

`BuildLegacyToggles_covers_every_registered_pass` 的排除清单（L364-369）追加一项：

```csharp
            Assert.Equal(
            LockedChain.Where(id => id != PassIds.GenericDeepen && id != PassIds.PatternDesugar
                && id != PassIds.EnumDeclaration
                && id != PassStubCatalog.Ids.UnsafeFfm
                && id != PassStubCatalog.Ids.ExpressionDesugar
                && id != PassStubCatalog.Ids.LexicalNormalize
                && id != PassStubCatalog.Ids.PartialMerge
                && id != PassStubCatalog.Ids.ClosureLift)
                .OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            toggles.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray());
```

- [ ] **Step 6: PassStubCatalogTests 两处 stub 靶子换 async-normalize**

实施前先 Read 复核 `PassStubCatalog.Ids.AsyncNormalize` 仍是 `PassStatus.Stub`（HEAD aef9436a L146-155；若已翻转，改选 `Ids.ThreadStatic` 等仍在 stub 列表的 id，不得让断言落空）。

`Enabling_a_stub_resolves_to_an_enabled_plan_entry`（L63、L68）：

```csharp
            enablePassIds: new[] { PassStubCatalog.Ids.AsyncNormalize });

        Assert.True(plan.IsValid);
        var enabled = Assert.Single(plan.Enabled);
        Assert.Equal(PassStubCatalog.Ids.AsyncNormalize, enabled.Id);
        Assert.Equal(PassStatus.Stub, enabled.Status);
```

`Running_an_enabled_stub_fails_with_CS2J9001_and_creates_no_directories`（L97、L109）：

```csharp
                EnablePassIds = new[] { PassStubCatalog.Ids.AsyncNormalize },
```

```csharp
        Assert.Contains(PassStubCatalog.Ids.AsyncNormalize, captured);
```

OptionsFactory 抛异常列表（L86-95）**不动**：async-normalize 仍在 stub 位，CS2J9001 在任何 factory 调用前拦截。

- [ ] **Step 7: 跑两个测试类（Task 16 的两红必须转绿）**

Run:

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~PassChainOrderTests|FullyQualifiedName~PassStubCatalogTests" --nologo -v q
```

Expected: `失败: 0`。重点核对：LockedChain 30 项精确匹配（顺序断言不容错）、默认计划不含 closure-lift、隔离槽位三 pass 精确序列、CS2J9001 仍对 stub 生效。

- [ ] **Step 8: 默认计划逐字节不变的行为佐证**

Run（三测试工程 + 直连回归全量）：

```powershell
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --nologo -v q
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --nologo -v q
dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj --nologo -v q
```

Expected: 三条全部 `失败: 0`。默认关 + 无 legacy 键 ⇒ 不经 `--enable-pass closure-lift` 的任何路径与翻转前完全一致；Task 18 的项目级测试再做显式启用端到端佐证。

- [ ] **Step 9: 提交**

```powershell
git add tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs
git add tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs
git commit -m "test(preprocessing): C32 M2 排序/stub 测试随翻转同步（LockedChain 30 项 closure-lift 槽位/默认关/映射 C32/隔离槽位/legacy 清单/stub 靶子换 async-normalize）"
```

<!-- APPEND-MARK-TASK18 -->

## Task 18: 项目级 fixture SampleClosureLiftSolution（设计 §7.2）

仿 C28 `tests/SampleLexicalNormalizeSolution/`：一个 Lib（全场景可变捕获源码）＋一个 xUnit 测试工程（只断**可观察行为**）＋ classic .sln。该测试工程在**原树**与 closure-lift 中间树上各跑一遍（Task 19 的 VerifyBuild/VerifyTests 驱动后者），即 holder 改写语义等价的 .NET 半区证据。

**Files（全部新建）:**
- `tests/SampleClosureLiftSolution/SampleClosureLiftLib/SampleClosureLiftLib.csproj`
- `tests/SampleClosureLiftSolution/SampleClosureLiftLib/CaptureScenarios.cs`
- `tests/SampleClosureLiftSolution/SampleClosureLiftLib/ConcurrencyScenarios.cs`
- `tests/SampleClosureLiftSolution/SampleClosureLiftLib/CapturelessControls.cs`
- `tests/SampleClosureLiftSolution/SampleClosureLiftRuntimeTests/SampleClosureLiftRuntimeTests.csproj`
- `tests/SampleClosureLiftSolution/SampleClosureLiftRuntimeTests/RuntimeBehaviorTests.cs`
- `tests/SampleClosureLiftSolution/SampleClosureLiftSolution.sln`（命令生成，不手写 GUID）

- [ ] **Step 1: Lib csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

- [ ] **Step 2: CaptureScenarios.cs（9 个场景，每个方法都含真实可变捕获）**

```csharp
namespace SampleClosureLiftLib;

/// <summary>
/// Mutable-capture scenarios for C32 holder lifting (design §7.2). Every value
/// returned here is observable behavior that must be identical before and
/// after the closure-lift intermediate tree is built and tested.
/// </summary>
public static class CaptureScenarios
{
    // External write to a captured local after delegate creation: delegate
    // storage and the local must stay one shared cell.
    public static int ExternalWriteCounter()
    {
        var count = 0;
        Action inc = () => count++;
        count = 10;
        inc();
        inc();
        return count; // 12
    }

    // Two delegates sharing one captured cell with mixed write kinds.
    public static (int First, int Second) SharedDelegates()
    {
        var x = 0;
        Action a = () => x++;
        Action b = () => x += 10;
        Func<int> read = () => x;

        a();
        b();
        a();
        var first = read(); // 12
        b();
        return (first, read()); // (12, 22)
    }

    // A parameter captured and externally reassigned.
    public static int ParameterCapture(int seed)
    {
        Func<int> next = () => ++seed;
        seed += 100;
        next();
        return seed; // 102
    }

    // Nested lambdas: the inner lambda closes over the outer routine's local
    // (outer lambda parameter 'step' is read-only and must NOT be lifted).
    public static int NestedDelegates()
    {
        var outer = 1;
        Func<int, Func<int>> make = step =>
        {
            outer += step;
            return () => outer;
        };

        var reader = make(40);
        return reader(); // 41
    }

    // for induction: one shared cell across all iterations (current C# semantics).
    public static List<int> ForRoundValues(int n)
    {
        var funcs = new List<Func<int>>();
        for (var i = 0; i < n; i++)
        {
            funcs.Add(() => i);
        }

        return funcs.ConvertAll(f => f()); // n=3 -> [3, 3, 3]
    }

    // foreach iteration variable: a fresh cell per iteration.
    public static List<int> ForeachRoundValues(IEnumerable<int> source)
    {
        var funcs = new List<Action>();
        var results = new List<int>();
        foreach (var item in source)
        {
            funcs.Add(() => results.Add(item));
        }

        foreach (var f in funcs)
        {
            f();
        }

        return results; // [1, 2, 3]
    }

    // lock site on a captured object that is also externally reassigned.
    public static int LockedCounter()
    {
        var locker = new object();
        var count = 0;
        Action inc = () =>
        {
            lock (locker)
            {
                count++;
            }
        };

        lock (locker)
        {
            count = 5;
        }

        locker = new object();
        inc();
        inc();
        return count; // 7
    }

    // Generic capture: holder fields must keep T (no object/Object[] erosion).
    public static T[] GenericCapture<T>(T first, T second)
    {
        var current = first;
        Func<T> get = () => current;
        Action swap = () => current = second;

        var before = get();
        swap();
        return new[] { before, get() };
    }

    // Mirrors an E02-lifted private static method: the reserved-shaped
    // parameter name is an ordinary by-value parameter to C32.
    public static int RunCs2jLiftedShape(int _cs2jCap_count)
    {
        Action inc = () => _cs2jCap_count++;
        inc();
        inc();
        return _cs2jCap_count;
    }
}
```

- [ ] **Step 3: ConcurrencyScenarios.cs（确定性的跨线程共享存储对照）**

不用「各跑 N 次最后比总数」那种数据竞争非确定断言；改用事件严格交替：两线程经同一个委托递增同一被捕获存储，计数必须精确。holder 字段与原局部同为无同步共享字段，事件信号本身的内存屏障就是原程序语义里已有的同步，C32 不新增任何同步原语。

```csharp
namespace SampleClosureLiftLib;

/// <summary>
/// Cross-thread shared-storage equivalence (design §7.2 tail): two threads
/// increment the SAME captured cell through one delegate, in a strictly
/// alternating event-driven schedule. The result is deterministic; the events
/// are the test harness's own synchronization — closure-lift must not add any.
/// </summary>
public static class ConcurrencyScenarios
{
    public static int PingPongCounter(int rounds)
    {
        var count = 0;
        Action inc = () => count++;

        using var aTurn = new AutoResetEvent(initialState: false);
        using var bTurn = new AutoResetEvent(initialState: false);

        var a = new Thread(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                aTurn.WaitOne();
                inc();
                bTurn.Set();
            }
        });

        var b = new Thread(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                bTurn.WaitOne();
                inc();
                aTurn.Set();
            }
        });

        a.Start();
        b.Start();
        aTurn.Set();
        a.Join();
        b.Join();

        return count; // exactly 2 * rounds
    }
}
```

- [ ] **Step 4: CapturelessControls.cs（静态/无捕获对照：零生成物、行为不变）**

```csharp
namespace SampleClosureLiftLib;

/// <summary>
/// Static / captureless lambdas (design D12): closure-lift must emit nothing
/// for this file. Behavior coverage guards against accidental rewrites.
/// </summary>
public static class CapturelessControls
{
    private static readonly Func<int, int> Square = static x => x * x;

    public static int Run(int x)
    {
        Func<int, int> addOne = static v => v + 1;
        return addOne(x) + Square(x); // x=3 -> 13
    }
}
```

- [ ] **Step 5: RuntimeTests csproj（版本锁与仓库 fixture 一致）**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\SampleClosureLiftLib\SampleClosureLiftLib.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 6: RuntimeBehaviorTests.cs（只断行为，11 条）**

```csharp
using SampleClosureLiftLib;
using Xunit;

namespace SampleClosureLiftRuntimeTests;

/// <summary>
/// Observable behavior assertions (design §7.2): counts, list contents and
/// call timing — never generated text. These run on the original fixture AND
/// the closure-lift intermediate tree (verifier), so they are the half-side
/// proof that holder lifting preserves C# closure semantics.
/// </summary>
public sealed class RuntimeBehaviorTests
{
    [Fact]
    public void ExternalWriteCounter_StaysOneSharedCell()
        => Assert.Equal(12, CaptureScenarios.ExternalWriteCounter());

    [Fact]
    public void SharedDelegates_ObserveMixedWritesInOrder()
        => Assert.Equal((12, 22), CaptureScenarios.SharedDelegates());

    [Fact]
    public void ParameterCapture_FollowsReassignment()
        => Assert.Equal(102, CaptureScenarios.ParameterCapture(1));

    [Fact]
    public void NestedDelegates_ShareOuterCell()
        => Assert.Equal(41, CaptureScenarios.NestedDelegates());

    [Fact]
    public void ForInduction_IsOneSharedCell()
        => Assert.Equal(new[] { 3, 3, 3 }, CaptureScenarios.ForRoundValues(3));

    [Fact]
    public void ForeachIteration_IsFreshPerRound()
        => Assert.Equal(new[] { 1, 2, 3 },
            CaptureScenarios.ForeachRoundValues(new[] { 1, 2, 3 }));

    [Fact]
    public void LockedCounter_SurvivesLockSitesAndReassignment()
        => Assert.Equal(7, CaptureScenarios.LockedCounter());

    [Fact]
    public void GenericCapture_PreservesIntValues()
        => Assert.Equal(new[] { 1, 2 }, CaptureScenarios.GenericCapture(1, 2));

    [Fact]
    public void GenericCapture_PreservesReferenceValues()
        => Assert.Equal(new[] { "a", "b" }, CaptureScenarios.GenericCapture("a", "b"));

    [Fact]
    public void E02ShapedLiftedMethod_LiftsItsCaptureParameter()
        => Assert.Equal(2, CaptureScenarios.RunCs2jLiftedShape(0));

    [Fact]
    public void CapturelessControl_RunsUnhanged()
        => Assert.Equal(13, CapturelessControls.Run(3));

    [Fact]
    public void TwoThreads_AlternatingThroughOneDelegate_CountExactly()
        => Assert.Equal(200, ConcurrencyScenarios.PingPongCounter(100));
}
```

- [ ] **Step 7: 生成 sln（命令生成，禁止手写 GUID/配置行）**

```powershell
cd d:\code\cs2j\tests\SampleClosureLiftSolution
dotnet new sln -n SampleClosureLiftSolution
dotnet sln SampleClosureLiftSolution.sln add SampleClosureLiftLib\SampleClosureLiftLib.csproj
dotnet sln SampleClosureLiftSolution.sln add SampleClosureLiftRuntimeTests\SampleClosureLiftRuntimeTests.csproj
```

- [ ] **Step 8: 基线验证——原 fixture 必须先 Release build/test 全绿**

Run: `dotnet test SampleClosureLiftSolution.sln -c Release --nologo -v q`（在 fixture 目录）
Expected: `失败: 0`，12 条测试通过（11 场景方法 + 两个泛型用例在同一测试类合计 12 条 `[Fact]`）。这是改写前行为基线；失败则先修 fixture，不得进入 Task 19。

- [ ] **Step 9: 提交（逐文件 add，不加 gitignore 之外的产物；bin/obj 不入库）**

```powershell
cd d:\code\cs2j
git add tests/SampleClosureLiftSolution/SampleClosureLiftSolution.sln
git add tests/SampleClosureLiftSolution/SampleClosureLiftLib/SampleClosureLiftLib.csproj
git add tests/SampleClosureLiftSolution/SampleClosureLiftLib/CaptureScenarios.cs
git add tests/SampleClosureLiftSolution/SampleClosureLiftLib/ConcurrencyScenarios.cs
git add tests/SampleClosureLiftSolution/SampleClosureLiftLib/CapturelessControls.cs
git add tests/SampleClosureLiftSolution/SampleClosureLiftRuntimeTests/SampleClosureLiftRuntimeTests.csproj
git add tests/SampleClosureLiftSolution/SampleClosureLiftRuntimeTests/RuntimeBehaviorTests.cs
git commit -m "test(preprocessing): C32 M2 SampleClosureLiftSolution fixture（外赋/共享/参数/嵌套/for-foreach 每轮值/lock/泛型/E02 共存/静态对照/双线程交替，12 条行为断言）"
```

<!-- APPEND-MARK-TASK19 -->

## Task 19: fixture 接线 + 项目级测试 + CLI 测试（设计 §7.4）

**Files:**
- Modify: `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj`（Content ItemGroup）
- Create: `tests/CSharpToJava.ProjectTests/ClosureLiftProjectTests.cs`
- Create: `tests/CSharpToJava.ProjectTests/ClosureLiftCLITests.cs`

- [ ] **Step 1: csproj Content 接线（插 C20 ItemGroup 之后、`</Project>` 之前）**

在 L264 C20 块的 `</ItemGroup>` 之后、L266 `</Project>` 之前插入：

```xml
  <ItemGroup>
    <!-- C32 closure-lift fixture (bin/obj excluded: may have been built locally). -->
    <Content Include="..\SampleClosureLiftSolution\**\*.*"
             Exclude="..\SampleClosureLiftSolution\**\bin\**;..\SampleClosureLiftSolution\**\obj\**">
      <Link>SampleClosureLiftSolution\%(RecursiveDir)%(Filename)%(Extension)</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
```

- [ ] **Step 2: ClosureLiftProjectTests.cs（进程内链路：改写→build→跑 RuntimeBehaviorTests）**

```csharp
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.ClosureLift;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.EqualityConcat;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.LexicalNormalize;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.StructValueSemantics;
using CSharpToJava.Core.UnsafeFfm;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for closure-lift (C32 M2): the holder-lifted fixture
/// builds and its RuntimeBehaviorTests pass on the intermediate tree, the
/// residual gate is 0 for every file, generated holders keep generic T, and a
/// second run over the intermediate tree is idempotent.
/// </summary>
public sealed class ClosureLiftProjectTests
{
    private const string PassId = "closure-lift";
    private const string IntermediateDir = ".cs2j-closure-lift-src";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleClosureLiftSolution");

    private static async Task<CliChainResult> RunPassAsync(
        string sourcePath,
        string outputRoot,
        bool verify = true)
    {
        var registry = PassCatalog.BuildRegistry();
        return await CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = sourcePath,
            DestinationRoot = outputRoot,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
            EnumDeclarationOptionsFactory = () => new EnumDeclarationOptions(),
            EqualityConcatOptionsFactory = () => new EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () => new UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () => new ExpressionDesugarOptions(),
            StructValueSemanticsOptionsFactory = () => new StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            LexicalNormalizeOptionsFactory = () => new LexicalNormalizeOptions(),
            LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
            EnablePassIds = new[] { PassId },
            VerifyBuild = verify,
            VerifyTests = verify,
        });
    }

    [Fact]
    public async Task Fixture_LiftsCaptures_IntermediateTreeBuildsAndRuntimeTestsPass()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-c32-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await RunPassAsync(
                Path.Combine(FixtureDir, "SampleClosureLiftSolution.sln"), outputRoot);

            Assert.True(result.Success, "closure-lift fixture run failed (see chain diagnostics)");
            Assert.Contains(IntermediateDir, result.ExecutedIntermediateDirectoryNames);

            // Idempotence on the intermediate tree (fresh destination).
            var intermediate = Path.Combine(outputRoot, IntermediateDir);
            var second = await RunPassAsync(
                Path.Combine(intermediate, "SampleClosureLiftSolution.sln"),
                Path.Combine(outputRoot, "second"));
            Assert.True(second.Success);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResidualGate_IsZero_ForEveryIntermediateFile()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-c32-gate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await RunPassAsync(
                Path.Combine(FixtureDir, "SampleClosureLiftSolution.sln"), outputRoot);
            Assert.True(result.Success);

            var scanner = new ClosureLiftPreprocessor();
            var intermediate = Path.Combine(outputRoot, IntermediateDir);
            foreach (var csFile in Directory.EnumerateFiles(
                         intermediate, "*.cs", SearchOption.AllDirectories))
            {
                var residual = scanner.CountResidualMutableCaptures(await File.ReadAllTextAsync(csFile));
                Assert.Equal(0, residual);
            }
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GeneratedHolders_KeepGenericT_AndCapturelessFileIsClean()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-c32-shape-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await RunPassAsync(
                Path.Combine(FixtureDir, "SampleClosureLiftSolution.sln"), outputRoot);
            Assert.True(result.Success);

            var libDir = Path.Combine(outputRoot, IntermediateDir, "SampleClosureLiftLib");
            var scenarios = await File.ReadAllTextAsync(Path.Combine(libDir, "CaptureScenarios.cs"));
            Assert.Contains("Cs2jDisplayClass", scenarios);
            Assert.Contains("__cs2jClosure", scenarios);
            // Generic holder field is typed T — no object/Object[] erosion.
            Assert.Contains("T _cs2jCap_current", scenarios);
            Assert.DoesNotContain("Object[]", scenarios);

            // foreach per-iteration instance sits in the loop body.
            Assert.Contains("foreach", scenarios);

            var controls = await File.ReadAllTextAsync(Path.Combine(libDir, "CapturelessControls.cs"));
            Assert.DoesNotContain("Cs2jDisplayClass", controls);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }
}
```

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~ClosureLiftProjectTests" --nologo -v q`
Expected: `失败: 0`，3 条通过（第一条含中间树 RuntimeBehaviorTests 全绿，即 12 条行为断言在改写后树上真实执行）。

- [ ] **Step 3: 提交 A（接线 + 项目级测试）**

```powershell
git add tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj
git add tests/CSharpToJava.ProjectTests/ClosureLiftProjectTests.cs
git commit -m "test(preprocessing): C32 M2 SampleClosureLiftSolution 接线与项目级测试（中间树 build/test 全绿、residual 逐文件为 0、泛型 T 字段、幂等）"
```

- [ ] **Step 4: ClosureLiftCLITests.cs（独立 verb 端到端 + list-passes 快照）**

```csharp
using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end CLI coverage for closure-lift (design §7.4): the isolated
/// `preprocess --pass closure-lift` verb runs the fixture through build AND
/// test verification, and --list-passes shows it Implemented/default-off.
/// </summary>
public sealed class ClosureLiftCLITests
{
    private const string IntermediateDir = ".cs2j-closure-lift-src";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleClosureLiftSolution");

    private static string CliPath
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null)
            {
                var candidate = Path.Combine(
                    dir, "src", "CSharpToJava.CLI", "bin", "Debug", "net10.0",
                    "CSharpToJava.CLI.dll");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }

            throw new FileNotFoundException("Could not locate CSharpToJava.CLI.dll.");
        }
    }

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
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    [Fact]
    public async Task ListPasses_ShowsClosureLift_ImplementedAndOffByDefault()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        var line = stdout.Split('\n').First(l => l.Contains("closure-lift"));
        Assert.Contains("Implemented", line);
        Assert.Contains(" off ", line);
        Assert.DoesNotContain(" on ", line);
        Assert.Contains("C32", line);
    }

    [Fact]
    public async Task IsolatedPass_FixtureBuildsAndTestsVerify_ExitZero()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-c32-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "closure-lift",
                "-s", Path.Combine(FixtureDir, "SampleClosureLiftSolution.sln"),
                "-d", outputRoot);

            Assert.Equal(0, exit);
            Assert.DoesNotContain("CS2J", stderr);

            var scenarios = await File.ReadAllTextAsync(Path.Combine(
                outputRoot, IntermediateDir, "SampleClosureLiftLib", "CaptureScenarios.cs"));
            Assert.Contains("Cs2jDisplayClass", scenarios);

            // The verifier runs the fixture's RuntimeBehaviorTests on the
            // intermediate tree; success text comes from the dotnet test step.
            Assert.Contains("Passed!", stdout);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }
}
```

⚠️ 两处执行注意：
1. `UseShellValidate` 是笔误陷阱——ProcessStartInfo 的属性名是 **`UseShellExecute`**（母本 LexicalNormalizeCLITests L38 即此名），照抄时以母本为准。
2. `"Passed!"` 是 `dotnet test` 正常完成时的控制台标记；若该 verb 的 stdout 捕获不含此串（MSBuild 节点复用等原因），退而断言 exit 0 + 中间树存在，并**手工在输出根复跑** `dotnet test <intermediate sln> -c Debug` 退出码 0，在提交说明注明；不得删除「中间树测试已验证」这一证据要求。

Run: `dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~ClosureLiftCLITests" --nologo -v q`
Expected: `失败: 0`，2 条通过（端到端用例较慢，同仓 E2E 量级，不加 Skip——它是 §7.4 唯一的独立 verb 证据）。

- [ ] **Step 5: 提交 B（CLI 测试）**

```powershell
git add tests/CSharpToJava.ProjectTests/ClosureLiftCLITests.cs
git commit -m "test(preprocessing): C32 M2 closure-lift CLI 端到端（独立 --pass 跑 fixture build/test 绿，list-passes Implemented/默认关/C32）"
```

<!-- APPEND-MARK-TASK20 -->

## Task 20: E16 LINQ 互操作测试 + M2 Release 全量出口验证（设计 §7.3、§9 M2 出口）

holder 字段访问对转换器 LinqRewrite（E16 过程式化）必须透明：过程式化提取方法时，被提升符号以「holder 实例的字段」形态作为捕获参数传入，转换结果仍是 `ProceduralLinq` 形态。

**Files:**
- Create: `tests/CSharpToJava.Tests/ClosureLiftLinqInteropTests.cs`

- [ ] **Step 1: 写互操作测试（C32 门面输出 → 转换器直连管线）**

```csharp
using CSharpToJava.Core.ClosureLift;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C32 × E16 interop (design §7.3 tail): a mutably captured local referenced
/// inside a LINQ lambda is holder-lifted first; the holder FIELD access is an
/// ordinary member expression and must pass through procedural LINQ rewriting
/// transparently (holder instance captured as an extracted-method parameter).
/// </summary>
public sealed class ClosureLiftLinqInteropTests
{
    [Fact]
    public void LiftedWhereCapture_Proceduralizes_WithHolderField()
    {
        var csharp = @"
using System.Linq;
using System.Collections.Generic;

public class C
{
    public List<int> Filter(List<int> items)
    {
        var threshold = 0;
        threshold = 5;
        return items.Where(v => v > threshold).ToList();
    }
}";

        var preResult = new ClosureLiftPreprocessor().Preprocess(
            csharp, semanticModel: null, new ClosureLiftOptions());
        Assert.True(preResult.Changed);
        Assert.Contains("__cs2jClosure", preResult.OutputCode);

        var result = ConvertProcedural(preResult.OutputCode);

        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode);
        Assert.Contains("_cs2jCap_threshold", result.GeneratedCode);
    }

    [Fact]
    public void LiftedAllCapture_Proceduralizes_WithHolderField()
    {
        var csharp = @"
using System.Linq;
using System.Collections.Generic;

public class C
{
    public bool Check(List<int> items)
    {
        var limit = 0;
        limit = 3;
        return items.All(n => n > limit);
    }
}";

        var preResult = new ClosureLiftPreprocessor().Preprocess(
            csharp, semanticModel: null, new ClosureLiftOptions());
        Assert.True(preResult.Changed);

        var result = ConvertProcedural(preResult.OutputCode);

        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode);
        Assert.Contains("_cs2jCap_limit", result.GeneratedCode);
    }

    private static ConversionResult ConvertProcedural(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
                PreferStreamApi = false,
            },
        });
    }
}
```

若断言发现转换器对 holder 字段生成了意外名（getter 前缀等），先打印 `result.GeneratedCode` 核对**实际**稳定形态再放宽断言（如 `_cs2jCap_threshold` 的 getter 拼写），但 `Success` 与 `ProceduralLinq` 两条不可放宽。

Run: `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~ClosureLiftLinqInteropTests" --nologo -v q`
Expected: `失败: 0`，2 条通过。

- [ ] **Step 2: 提交**

```powershell
git add tests/CSharpToJava.Tests/ClosureLiftLinqInteropTests.cs
git commit -m "test(c32): C32×E16 互操作——holder 字段对 LINQ 过程式化透明（Where/All 提取方法带 holder 捕获）"
```

- [ ] **Step 3: M2 出口 Release 全量验证（设计 §9 M2 行、§8 门槛 1-2 的 M2 部分）**

全部在 dev-3.x 工作区根目录逐条执行（不用 worktree）：

```powershell
dotnet build CSharpToJavaConverter.slnx -c Release --nologo -v q
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release --nologo -v q
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --nologo -v q
dotnet test tests/CSharpToJava.ConversionTests/CSharpToJava.ConversionTests.csproj -c Release --nologo -v q
dotnet test tests/SampleClosureLiftSolution/SampleClosureLiftSolution.sln -c Release --nologo -v q
```

Expected:
1. build `0 个错误`；
2. 三个测试工程全部 `失败: 0`（含 ClosureLift 46+2 单测、PassChainOrder/PassStubCatalog、ClosureLiftProjectTests 3 条、ClosureLiftCLITests 2 条、ClosureLiftLinqInteropTests 2 条，及全部既有用例）；
3. fixture 工程独立 Release build/test `失败: 0`（12 条行为断言）；
4. 转换器两条直连回归在 Release 下零改动全绿（已含在 CSharpToJava.Tests 全量中，失败时优先复核 LambdaExternalReassignment / LinqRewriterVariableCapture 两组）。

任一红 → 停止 M2 收尾，按 systematic-debugging 定位修，修好后重跑本条全序列，不得只跑红组即宣布完成。

- [ ] **Step 4: M2 出口自查清单（人工核对，写入 M3 决策记录引用）**

- [ ] §7.1 全 25 条 Core 用例 + 2 条闸门 + 2 条互操作齐备且绿
- [ ] §7.2 fixture 12 条行为断言在原树与中间树双绿
- [ ] §7.3 residual 闸门：fixture 逐文件 0；转换器直连回归零改动
- [ ] §7.4 排序/stub/独立 verb/list-passes 全绿；默认计划零变化（LockedChain 30 项含 closure-lift 但 EnabledByDefault 不含）
- [ ] 注册表状态：Implemented + default-off + StateMachine + `--enable-pass` 可达；**未翻默认开**
- [ ] 风险 1（站点完备性）：冻结规则 + SitesRewritten==0 fail-loud + 无裸名/闸门断言 + fixture 行为双绿 四层闭环
- [ ] 风险 2（foreach 版本语义）：for/foreach 二分单测 + fixture 每轮值行为断言 + 双线程交替断言
- [ ] 无计划外文件、无 worktree、逐文件 add、未 push、未打 tag

M2 到此结束。M3 不自动开始；默认开评审见 Task 21。

<!-- APPEND-MARK-TASK21 -->

## Task 21（M3 窗口执行）: 默认开六门槛评审与决策留痕——不自动翻默认开

本任务是 M3 里程碑的**评审作业清单**，不在 M2 实施。核心纪律（设计 §8 gate 6 + catalog 风险 10）：默认关必须先稳定至少一个里程碑窗口；六门槛全部成文评审后，**另行批准**才允许把 descriptor 改 `defaultEnabled: true`（届时另起改点 PR：PassCatalog 一处 + LockedChain/EnabledByDefault 两处测试同步）。M3 窗口在 dev-3.x 上作业，禁止 worktree，逐文件 add。

**Files:**
- Create（M3 产出物，评审文档）：`docs/superpowers/reports/2026-10-c32-m3-default-on-gates.md`
- Modify（仅当 gate 5 发现真实交界缺口）：`tests/SampleClosureLiftSolution/SampleClosureLiftLib/IteratorInterop.cs`（新建场景）＋ RuntimeBehaviorTests
- Modify（配套 gate 5 测试）：`tests/CSharpToJava.ProjectTests/ClosureLiftProjectTests.cs`

- [ ] **Step 1: gate 1-2 基线复跑（Release 三工程 + fixture + 转换器直连）**

重跑 Task 20 Step 3 全序列并记录日期/commit SHA/版本号（dotnet sdk 版本）到报告。门槛：零红；转换器两组直连回归确认**仍零改动**。

- [ ] **Step 2: gate 3 sdk 画像（D:\csharp-sdk，两轮数据）**

sdk 仓库不在本工作区，只做只读画像与中间树验证，产出全部数字入报告：

1. **AuditOnly 口径命中率**：沿用 M1 既定方式——临时 harness 直连门面（`ClosureLiftOptions(AuditOnly: true)` 逐文件跑整 CompilationUnit），**不入仓、不加 CLI 选项**（设计 §5 冻结）。记录：扫描文件数 / FilesSkippedClean / 命中文件数 / 候选符号数 / `CapturesByReason` 四桶占比 / `ForeachPerIterationGroups` 占比 / CS2J3201-3206 逐码命中数与代表样例（文件:行）。
2. **开启后基线**：`dotnet run --project src/CSharpToJava.CLI -- preprocess --enable-pass closure-lift -s <sdk sln> -d <tmp>`（基于默认链叠加启用，非 all-disabled），随后 build/test 中间树；记录与未启用基线的通过/失败差异，要求**无新增 Error、build/test 不退化**。
3. **holder 分配增量**：在 sdk 实际热点路径（画像 Top 命中方法）用 BenchmarkDotNet 或 Stopwatch 计时 + GC 计数对比（启用前/后），记录分配增量百分比；门槛阈值由评审在报告上**现场定**（设计 §8 gate 3 明确「审计给数据，门槛评审定阈值」），不得在本计划预填。

每个非零 CS2J32xx 命中必须给出处置归属（上游 pass 缺失 / 真不支持待 C45 / 可修规则缺口），不许留「未知」桶。

- [ ] **Step 3: gate 4 sdk 中间树 residual 闸门**

对 gate 3 第 2 轮中间树逐文件跑 `CountResidualMutableCaptures`，记录非零文件清单。裁决口径（设计 §8 gate 4）：非零文件必须**逐条配对**到已登记的 skip 诊断（3201-3206）且在非 strict 下全为 Warning 级并有 owner；任何「residual 非零但无归属行」的文件 = 门槛失败，回 M2 修规则，不得放行。

- [ ] **Step 4: gate 5 联合开关验证（C04/E02/E05 + foreach-yield 交界）**

1. **closure-lift + iterator-lowering 同开**：在 ClosureLiftProjectTests 加临时（或转正式）用例——`BuildAllDisabledToggles` + `EnablePassIds = [closure-lift, iterator-lowering]` 跑 SampleClosureLiftSolution，断言 Success、两中间目录都执行、幂等再跑 Success。
2. **「lambda 内 foreach-yield」交界 fixture**：在 Lib 新建 `IteratorInterop.cs`：迭代器方法内含 `foreach` + 捕获了迭代变量/外部可变局部的 lambda（典型形态：`yield return` 与 `funcs.Add(() => item)` 同体）。先在**原树**写 RuntimeBehaviorTests 锁定可观察结果，再跑 closure-lift+iterator 双开链，断言中间树 build/test 绿。若该形态被 3204 冻结或行为不符，**记入报告作为默认开阻断项**（这是设计点名的交界，不允许绕过）。
3. **默认链组合**：`BuildLegacyToggles(new ConvertProjectOptions())`（E02/E05 等历史 pass 全在默认态）+ `EnablePassIds=[closure-lift]` 跑 fixture 两轮，断言 Success 且字节幂等；这覆盖 C32 与 E02/E05 的实际共存（Task 13/18 只覆盖形态与单点）。

- [ ] **Step 5: gate 6 稳定窗口与决策**

报告记录 M2 翻转 commit 到评审日的时间窗（≥1 个里程碑窗口）及窗口内 closure-lift 相关回滚/hotfix 次数。结论二选一，由评审人签署：

- **继续 opt-in**：报告列明未达门槛项与 owner；代码零改动，任务结束。
- **批准默认开**：报告引用本任务全部数据；翻 `defaultEnabled: true` 的改点**另起任务**（PassCatalog descriptor、EnabledByDefault/LockedChain 测试、list-passes 断言、报告链接），不在本任务顺手改。

- [ ] **Step 6: M3 提交（仅留痕文件与 gate 5 增补测试；画像脚本/中间树不入仓）**

```powershell
git add docs/superpowers/reports/2026-10-c32-m3-default-on-gates.md
git add tests/SampleClosureLiftSolution/SampleClosureLiftLib/IteratorInterop.cs
git add tests/SampleClosureLiftSolution/SampleClosureLiftRuntimeTests/RuntimeBehaviorTests.cs
git add tests/CSharpToJava.ProjectTests/ClosureLiftProjectTests.cs
git commit -m "docs(c32): M3 默认开六门槛画像与 C04 交界 fixture（决策留痕；默认开未翻转）"
```

（无 gate 5 增补时只 add 报告一个文件；画像数字、sdk 中间树、临时 harness 一律不入库。）

---

## 全文尾注

- 计划范围：M1 审计（Task 1-7，已先期完成）→ M2 holder-lift 全量交付（Task 8-20）→ M3 评审清单（Task 21）。
- 权威依据：[2026-09-29-c32-capture-lift-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c32-capture-lift-design.md)；本计划与设计冲突时以设计为准，骨架代码中的 ⚠️ 标注为实施期必须照改的已知修正点。
- 分支纪律：全程 dev-3.x、禁止 worktree、逐文件 `git add`、不 push、不打 tag；默认关是 M2 交付状态的一部分。
