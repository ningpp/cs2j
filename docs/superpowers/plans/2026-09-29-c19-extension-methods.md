# C19 扩展方法显式化 Implementation Plan

> **For agentic workers:** 每个里程碑一个独立提交（实现一部分、提交一部分）；所有改动只在本 worktree（`.worktrees/preproc-c19-extension-methods`）内完成，禁止触碰主工作区。步骤用 checkbox（`- [ ]`）跟踪。

**Goal:** 把注册表 stub `extension-methods`（catalog C19）翻转为 `Implemented`（**默认关**），新增 Core 规则与 CLI 适配器：把绑定为 `MethodKind.ReducedExtension` 的调用 `r.M(a)` 显式化为 `global::Ns.Host.M(r, a)`；LINQ host（Enumerable/Queryable）糖零触碰；不改转换器、不建支撑库（设计 [2026-09-29-c19-extension-methods-design.md](../specs/2026-09-29-c19-extension-methods-design.md)）。

**Architecture:** 复刻 generic-deepen 骨架：整树一次 `CSharpCompilation`（Bind→Analyze）→ 逐文件 Rewrite（标准 spine，Changed=false 字节级复制、保 BOM）→ AfterFiles residual 再编译扫描；**无跨文件生成产物**（无 holder/无新文件），适配器比 C02 薄。Core 全量代码在 `src/CSharpToJava.Core/ExtensionMethodResolve/`（命名空间 `CSharpToJava.Core.ExtensionMethodResolve`）；唯一真相是 `SemanticModel.GetSymbolInfo`，不重建候选决议。

**Tech Stack:** .NET SDK 10.0.300（`CSharpToJavaConverter.slnx`）、Roslyn 5.0.0（`Microsoft.CodeAnalysis.CSharp`，`CSharpParseOptions(LanguageVersion.Latest)`）、xUnit。全部命令 `-c Release`。

**Working directory（每条命令）:** `D:\code\cs2j\.worktrees\preproc-c19-extension-methods`（分支 `feature/preproc-c19-extension-methods`，base `46bdeeca`）

**Gate（红线，全程绑定）:** `DefaultEnabled=false`，过设计 §8 全部门槛前绝不翻 true；不改转换器既有代码（ExtensionMethodIndex / ProjectPasses / InvocationExpressionTransformer 等一律不动）；不建支撑库；不改 linq-channel stub 次序（C19→C26 注册序，糖所有权经 EM-LINQ 排除而非换序）；不静默吞错；新增诊断只走设计 §6 码表 CS2J12xx/EM001。

**提交策略:** M0 三个提交（计划文档独立 / 骨架+适配器+注册表翻转含测试 / 探针结论回填设计）；M1-M4 每里程碑 build/test 全绿后各自提交；提交信息 `feat(preprocessing): C19 M<n> …`。不 push。

**基线漂移处置（2026-10-03，commit 0，已完成）:** base `46bdeeca` 的 C24 提交 `9da92d8d` 把未解决的 C23/C24 合并冲突标记带入树（Release build 15 个 CS8300、测试工程无法编译）。已按 union 解决并单独提交 `fix(preprocessing): 解决 C23/C24 expression-desugar 合并遗留冲突标记`（392fd1d6），作为 C19 全部验证的前置。

---

## M0 骨架 + 注册表翻转 + 探针（本计划首批三个提交）

**产物**：pass 注册可见（Implemented/off）；启用时规则全空、所有输入文件字节级复制（Changed=false，保 BOM），中间树零差异；四个探针结论回填设计。

### M0-T1 Core 骨架（commit 2）

**Create（全文件空壳，目录 `src/CSharpToJava.Core/ExtensionMethodResolve/`，11 个文件）:**
- `ExtensionMethodResolveOptions.cs`：options record（Strict/ReportInfo/RunResidualScan=true/RewriteExternalMetadataHosts=true/LinqHostSet 默认 Enumerable+Queryable/ExpressionTreePolicy/OverridesPath；M0 值就位、不被规则消费）
- `ExtensionMethodResolveStatistics.cs`：设计 §4.1 全部计数器 + `SitesSkippedByReason`/`ResidualsByRule` 字典 + Merge + SummaryLine（M0 恒 0）
- `ExtensionMethodResolveDiagnostics.cs`：`ExtensionSeverity`/`ExtensionRule`（Bind/Gen/Ref/Cond/Group/Ext14/Linq/External/ExprTree/Residual/Infra）/`ExtensionDiagnostic`/码表 CS2J1200-1231、1290、EM001（§6）
- `ExtensionCallBinding.cs`：`ExtensionCallBinding` record + `ReceiverClassification` 枚举（InCompilationUser/InCompilationSupport/ExternalMetadata/LinqOwned）
- `ExtensionCallBinder.cs`：空分析（M0 返回空 binding 列表，不产诊断）
- `ExtensionHostSyntaxFactory.cs`：空壳（M1 用 FullyQualifiedFormat 构造 host NameSyntax）
- `ExtensionCallRewriter.cs`：空 rewriter（M0 不选任何节点）
- `ExtensionExpressionTreeFacts.cs`：空壳（M2 实现；若 C02 共享 ExpressionTreeFacts 已先合入则改复用，注释登记）
- `ExtensionMethodResidualScanner.cs`：`Scan` 签名就位，M0 返回空字典
- `ExtensionMethodResolveResult.cs`：`ExtensionFileResult(OutputCode, Changed, Diagnostics, Statistics)`
- `ExtensionMethodResolvePreprocessor.cs`：`BindCompilation`/`Analyze`/`RewriteFile`/`ResidualScan` 接缝；M0 RewriteFile 恒返回 Changed=false（输出 = 原树文本）

- [ ] T1.1 11 个文件落盘，命名空间统一 `CSharpToJava.Core.ExtensionMethodResolve`
- [ ] T1.2 码表常量与 §6 一一对齐（1200/1201/1202/1203/1204/1205/1206/1207/1210/1220/1230/1231/1290/EM001）
- [ ] T1.3 空转单测：任意输入 → RewriteFile Changed=false、输出等于输入、统计全 0、诊断为空；二次运行零 diff

### M0-T2 适配器（commit 2）

**Create:** `src/CSharpToJava.CLI/Preprocessing/Adapters/ExtensionMethodResolvePassAdapter.cs`

- [ ] T2.1 形态贴 GenericDeepenPassAdapter，去掉生成文件分发：`IntermediateDirectoryName=".cs2j-extension-methods-src"`；构造六参（options/verbose/filterUnsupportedProjects/compatNamespacePrefixes/verifyBuild/verifyTests）
- [ ] T2.2 显式 `override CreateParseOptions => new CSharpParseOptions(LanguageVersion.Latest)`；`VerifyBuild/VerifyTests=true`；`DeleteBuildOutputsAfterRestore=true`
- [ ] T2.3 `CompilationReadyAsync`=Bind+Analyze；`RewriteFileAsync` 调 RewriteFile、Changed=false 返回 null（runner 走 File.Copy，BOM 天然保留）；`AfterFilesAsync` 仅 residual 扫描（M0 零发现）
- [ ] T2.4 OnRestoreFailed/OnBuildFailed/OnTestsFailed → CS2J1230/1231；HasErrors/BuildChainSummaryLine/BuildChainDiagnosticLines（Info 仅 verbose）映射到位

### M0-T3 注册表翻转 Stub→Implemented（commit 2）

- [ ] T3.1 `PassCatalog.cs`：`PassIds` 增 `ExtensionMethods="extension-methods"`；在 IteratorLowering 描述符之后、EliminateGoto 之前插 Implemented 描述符（Display "Extension method explicitness"、目录 `.cs2j-extension-methods-src`、Stage=Expression、DefaultEnabled=false、RunAfter=delegate/tuple/ref-out-in/unsigned/array-method/make-readonly/iterator-lowering（五锚+EM-ORDER，附录裁决1）、SupportLibrary.None、NeedsSemanticModel=true、VerifyBuild/Tests=true、CatalogItems="C19"）
- [ ] T3.2 `PassStubCatalog.cs`：删 extension-methods stub 条目；**先**把 linq-channel 与 extension-members(C49) 两条 stub 的 RunAfter 引用 `Ids.ExtensionMethods` 改为 `PassIds.ExtensionMethods`，再删常量（迁移后 `PassStubCatalog.Ids` 不留 ExtensionMethods）
- [ ] T3.3 `PassChainFactory.cs`：CreateAdapter switch 增 `PassIds.ExtensionMethods => new ExtensionMethodResolvePassAdapter(new ExtensionMethodResolveOptions(), …)`；**不进 BuildLegacyToggles**
- [ ] T3.4 测试真值更新：`PassChainOrderTests.LockedChain` 在 IteratorLowering 与 EliminateGoto 之间插 PassIds.ExtensionMethods（EnabledByDefault 自动不含：默认关项需进排除清单——按 lexical/partial/closure 同法显式排除）；新增 C19 注册表锁测试（仿 GenericDeepenRegistryTests：C19/off/目录/CatalogItems/RunAfter 七锚/Factory 建适配器）；`ProjectPreprocessorsConsistentTests` 追加 `.cs2j-extension-methods-src` 段；stub 计数无硬编码断言（现测试无计数断言，2026-09-29 设计提到的 L24 断言 22 已不在当前基线，复核确认）
- [ ] T3.5 CLI 面：`--list-passes` 见 Implemented/off 行（测试锁定行内容：id/stage/status/default/run-after 含七锚/rules=C19）；翻转前 stub CS2J9001 路径随删除自然消失（无测试引用 extension-methods stub，grep 确认）
- [ ] T3.6 单文件 verb 模式保守路径占位：CS2J1290 常量/文案就位，实际接线可后补（M3），M0 仅骨架

### M0-T4 四个探针（commit 2 固化 ①②④ 为 xUnit；commit 3 回填设计）

- [ ] T4.1 **探针①**：xUnit 内联最小编译（LanguageVersion.Latest）——`r?.M()`（M 为扩展方法）是否合法；合法则 `GetSymbolInfo` 绑定的符号 MethodKind/ReducedFrom 形态、节点语法结构（ConditionalAccessExpression + Invocation of MemberBinding）固化为断言
- [ ] T4.2 **探针②**：xUnit 内联编译——C#14 `extension` 块在 Latest 与 Preview 下的接受度、parse 诊断码、块节点 SyntaxKind、扩展属性/静态 extension 成员的声明符号形态；固化"Latest 下 CS2J1206 检测以什么为证据"的结论
- [ ] T4.3 **探针③**：对 `D:\csharp-sdk`（只读；json/regular-expressions/uri/xml-tiny 四个有效样本，xml 为空目录）跑 M0 测量轮：合并编译（TPA 基础引用、Latest、NETCOREAPP）逐 invocation 取 GetSymbolInfo，统计 reduced 站点总数与图内/metadata/LINQ/未绑定占比、Ambiguous（1201 候选）、reduced 方法组（1207 候选）、null 条件站点（1205 候选）；测量以 env 门控（`CS2J_RUN_SDK_PROBE=1` + 目录存在），不拖慢常规测试；方法论与原始数据写入设计
- [ ] T4.4 **探针④**：xUnit 最小复现 LinqRewriter 对静态形态失配——reduced 形 `src.Where(x=>…).Select(…)` vs 静态形 `Enumerable.Where(src,x=>…).Select(Enumerable?…)`：复现 [LinqRewriter.cs:126](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L126) 的 member-access cast + [L552-L576](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L552-L576) lambda-only 实参规则 + [L212-L219](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.cs#L212-L219) 链回溯，证明静态形被 IsSupportedMethod 判 false 且链回溯立即中断；固化 EM-LINQ 必要性
- [ ] T4.5 结论回填设计 §2 决策表（EM-COND/EM-EXT14 行）、§8（测量数）与文末"M0 探针结论（2026-10-03）"小节；commit 3 只动文档

### M0 验证（worktree 根目录）

```powershell
dotnet build CSharpToJavaConverter.slnx -c Release
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release
# 探针③（需要本机 D:\csharp-sdk）：
$env:CS2J_RUN_SDK_PROBE='1'; dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release --filter "FullyQualifiedName~ExtensionMethodProbe"
```

- 启用本 pass 跑一个最小样例（preprocess --pass extension-methods 或 Core 空转单测）：输出与输入逐字节一致、无新增目录外副作用、summary 零计数
- 验收：build 零错误；测试全绿（不新增红灯）；默认关时默认链零变化

---

## M1 核心显式化（commit 4）

EM-BIND / EM-CALL / EM-GEN / EM-GEN-HOST / EM-IDEAL：
- Binder 消费 GetSymbolInfo 产 binding（只认 ReducedExtension + ReducedFrom）；HostSyntaxFactory 用 FullyQualifiedFormat 造 host 名（嵌套泛型宿主构造实参）；Rewriter 自底向上改 `r.M(a)`→`global::H.M(r,a)`（泛型实参源码省略则省略、显式则复制；receiver 原节点复用保求值序）；residual 1/3/6 项
- Core 单测：using 层级（普通/alias/global using/嵌套命名空间/外层类型）、泛型（推断省略/显式实参/嵌套泛型 host）、同名优先（实例优先、不适用回落）、幂等二次零 diff
- fixture 骨架 `tests/SampleExtensionMethodsSolution/`（Lib/App/Tests 三工程），中间树 build/test 全绿；clean 文件字节复制+BOM 用例

**验证**：slnx Release build 零错误；Tests + ProjectTests 相关 filter 全绿；fixture `dotnet build/test -c Release` 全绿。

---

## M2 边界规则（commit 5）

EM-REF（this ref/in/scoped）+ EM-GROUP（reduced 方法组→1207）+ EM-COND（按探针①收编临时提升，裁决3 v1 收编；不可提升形留 1205）+ EM-EXPRTREE（表达式树内改写 + 重绑定 OriginalDefinition 同一性断言，失败 1210 单站回退）；fixture 补 null receiver 运行时不抛断言；ExtensionExpressionTreeFacts 落地（或合并去 C02 共享件）。

**验证**：同 M1；ref/方法组/条件/表达式树用例全绿。

---

## M3 外部 host + 多工程（commit 6）

EM-EXTERNAL：metadata host 同样改写；适配器注入 host 可映射性判定（CLI TypeMappings，v1 可恒 Unknown→1203 全量报告）；1203 与 CS2J_EXT001 历史告警 triage 对账。按 M0 探针③绑定率（附录裁决2：<99% 必须做）决定是否经 ProjectGraph 补 restore 程序集 MetadataReference。多工程 fixture 完整版；与 E12/E09/C01 支撑库 fixture 串行三样本锁五锚可见性。CS2J1290 单文件模式接线（若 M0 未接）。

**验证**：sdk 预处理轮 build/test；新增失败为零；测量轮对比。

---

## M4 链策略 + 收尾门（commit 7）

EM-LINQ 混合链策略定稿（探针④基础上：用户扩展夹 LINQ 链的保守跳过/Info 策略）；E16 procedural/prefer-stream 双模式开/关本 pass 结果零 diff；E13/E14 链位用例（make-readonly/iterator-lowering 先消费糖）；回退实战（CS2J1200 全回退、产物字节一致）；strict/overrides（force/skip 留 Info）；residual 全六项闸门；member-desugar 混合 fixture（裁决6，硬验收）；sdk 基线比对 + Warning triage 完毕。候选默认开评审材料齐备。

**验证**：设计 §8 六门槛证据齐备；三测试工程 Release 全绿；fixture/sdk 零新增失败。

---

## M5 默认开 + 旧资产退役（后续 PR，本特性 PR 不含代码）

descriptor 翻 DefaultEnabled=true；转换器侧退役清单：ProjectExtensionMethodCheckPass/CS2J_EXT001、ExtensionMethodIndex 及跨项目预填、InvocationExpressionTransformer 逐 host 补丁（MemoryExtensions 等）、RewriteExtensionMethods 选项；C49/C52 跟踪关系登记。

---

## 验证命令（每里程碑一致）

```powershell
dotnet build CSharpToJavaConverter.slnx -c Release
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Release --filter "FullyQualifiedName~ExtensionMethod"
```
