# C18 / C38 联合 M0 探针报告（async-normalize：轻量归一 + 异步流 + M4 联合异步迭代器）

| 项 | 值 |
|---|---|
| 日期 | 2026-10-04（测试复验落在本机时间 2026-10-05 凌晨） |
| 作者 | cs2j 预处理波次 |
| 基线 | `e50d987470bde644137e07094d93fd1467c6ca9c`（2026-10-04 13:59） |
| 分支 / worktree | `feature/preproc-c18-c38-async-normalize` / `.worktrees/preproc-c18-c38-async-normalize` |
| HEAD | `95474dcb311166eb44591961770a5931566d969b` |
| 文档性质 | **竣工回填（as-built）**：实现系按用户直接指令先行，本报告在 M1–M4 全部落地后补写；数字以撰写时实跑命令/实测文件为准，不以设计文档预期为准 |
| 关联设计 | [2026-09-29-c18-async-normalize-design.md](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md)、[2026-09-29-c38-async-stream-design.md](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md) |

---

## 1. 背景与门控说明（为何先实施后回填）

C18 设计 D2（[c18 设计 L64](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L64)）与 §9 M0（[L349](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L349)）规定：**M0 形态原型探针先行、过闸才准开 M1**，且 M0「不写生产代码」。

实际执行偏离了该门控顺序：用户直接指令先行实施，2026-10-04 14:53 至 2026-10-05 02:50 连续落地 9 个提交（C18 M1 → 注册表翻转 → C38 M2/M3 → .NET 真值 oracle → D6 翻转 → Java 桥 → C38 M4 联合状态机）。本报告为用户明确要求补交的 M0 文档，按竣工回填性质如实标注：

- M0 的三件交付（①计数、②双原型结论、③HEAD 测试红绿基线）在本文**按实测补齐**，但不作为 M1 的前置闸门——它们是对既成工程的事后核对；
- 回填核对的结论总体**支持但修正** D1：轻量路线对普通 async 方法成立（P-Heavy 免做），唯一真实重量触发点是 C38 async yield 生产者（D11③），已以 M4 子域形态落地；
- 回填核对也**推翻**了 C38 D6 的一条关键语义假设（见第 6 章），该推翻已由提交 `a9743f21` 吸收进产品默认行为；
- csharp-sdk / EF Core 训练画像在本机不可用，M0① 的 sdk 语义计数**未取得**（见 2.4），这直接影响「默认开启」决策但不影响 opt-in 状态下已交付功能的正确性。

---

## 2. M0 交付①：形态计数（三个来源口径，严格分列、不得混用）

### 2.1 口径 a：catalog/设计文档已记载的二手画像数（csharp-sdk 训练集，本机不可复跑）

唯一一手出处是 [preprocessor-catalog.md §5.5 Layer 4 表 L1265](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/prompts/preprocessor-catalog.md#L1265)，原文：

> `async/await；Task</ConfigureAwait/ValueTask | await 1297 / Task< 311 / 789 / 11 | C18`

C18 设计 [§1.1 L16](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L16) 的转引原文：

> 「**await 1297 / `Task<` 311 / `Task` 789 / ConfigureAwait 11**；lambda 总量 4966（C45，同表 L1259），其中 async lambda 的占比**尚无计数**」

C38 侧三个构造在 catalog 中只有占位符「—」（[catalog L1266](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/prompts/preprocessor-catalog.md#L1266)；[c38 设计 L27](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md#L27) 明示「无数字」）。

| 构造 | 二手画像数 | 正确口径（源文档原文） |
|---|---|---|
| await 站点 | **1297** | await |
| `Task<` | **311** | `Task<`（泛型 Task 文本频次），**不是** ConfigureAwait |
| `Task` | **789** | `Task`（含泛型形态的文本频次/非泛型 Task），**不是** async lambda |
| ConfigureAwait | **11** | ConfigureAwait，**不是** lock+await |
| lambda 总量 | 4966 | C45 同表；其中 **async lambda 占比源文档明确写「尚无计数」** |
| `await foreach` / `IAsyncEnumerable` / `IAsyncDisposable` | —（无数字） | C38 行占位 |
| lock+await | 未取得画像数 | lock 总量 32（catalog L1254）；lock+await 在 C# 基线即 CS1996 非法，无画像计数 |

> **重要勘误**：本次任务简报曾把这组数字标注为「async lambda 789、ConfigureAwait 311、lock+await 11」。逐字核对两份设计文档与 catalog 原文后确认该标注与出处不符——**789 是 `Task`、311 是 `Task<`、11 是 ConfigureAwait**，全仓没有任何一处记载「async lambda 789」或「lock+await 11」；async lambda 占比在设计文档中本就是 M0 待回答的开放问题。详见第 9 章更正清单。

### 2.2 口径 b：本仓可得语料的正则近似计数（非语义计数）

命令：在 worktree 根用 ripgrep 对 `src` 与 `tests`（`--glob '*.cs'`、排除 `**/obj/**`、`**/bin/**`）做文本扫描。**这是正则文本命中，不是语义计数**；且本分支本身新增了大量 async 特性的生产代码（生成模板/诊断字符串）与测试夹具输入字符串，命中存在严重的「自污染」，数字仅反映本仓文本规模，不能与 2.1 的 sdk 画像横比。

| 模式（正则近似） | 命中文件数 | 命中行数 | 其中 src（文件/行） | 其中 tests（文件/行） |
|---|---|---|---|---|
| `async\s*\(`（async lambda 起手的**下界**近似；同时混入 `await Task.Run(async ()`、`async ()=>` 等） | 6 | 27 | 0 / 0 | 6 / 27 |
| `ConfigureAwait\(` | 16 | 53 | 10 / 20 | 6 / 33 |
| `await foreach` | 29 | 135 | 16 / 53 | 13 / 82 |
| `await using` | 24 | 112 | 15 / 52 | 9 / 60 |
| `yield return` | 113 | 488 | 64 / 188 | 49 / 300 |
| `IAsyncEnumerable` | 21 | 89 | 10 / 25 | 11 / 64 |
| `async\s+void` | 11 | 22 | 8 / 15 | 3 / 7 |

计数限制：①`async\s*\(` 会漏 `async x =>`、`async delegate`，会误纳注释/字符串与 `FooAsync(...)` 之外的任意 `async (` 文本；②src 的 await foreach/using 命中主要来自 pass 自己的生成模板与测试用内联源码，不是转换输入语料；③未做 lock+await、自定义 awaiter、`await dynamic`、Task.Yield 的扫描（设计 §9 M0① 清单项），需要 sdk 快照才有画像意义。

### 2.3 口径 c：pass 在 SampleAsyncSolution 上的实测计数（唯一语义口径）

来源：[AsyncStreamFixtureProjectTests.cs L356-L378](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ProjectTests/AsyncStreamFixtureProjectTests.cs#L356-L378) 对 adapter Statistics 的固化断言（fixture = 7 个 Lib 源文件 + 1 个测试文件）：

| 统计桶 | 实测值 |
|---|---|
| FilesScanned | **8** |
| FilesTransformed | **7**（[HandwrittenAsyncEnumerable.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/SampleAsyncSolution/SampleAsyncLib/HandwrittenAsyncEnumerable.cs) 为纯手写枚举器、无归一站点） |
| AwaitForeachExpanded | **8**（ChannelProbe×2、Handwritten×2、CancellationProbe×1、DoubleFaultProbe×2、YieldProbe 联合体内×1，注释见 [L345-L347](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ProjectTests/AsyncStreamFixtureProjectTests.cs#L345-L347)） |
| AsyncEnumeratorsEmitted | **8** |
| AsyncDisposeGuardsEmitted | **7**（第 8 个 await foreach 在 M4 迭代器生产者体内，释放并入 DisposeAsync 区域） |
| WithCancellationResolved | **3** |
| AwaitUsingStatementsExpanded / DeclarationsExpanded | **5 / 2**（即「using 5+2」） |
| AsyncIteratorsLowered / ClassesEmitted / Rejected | **3 / 6 / 0**（YieldProbe 三个生产者，各降为枚举器+包装两个类） |
| AsyncStreamResidualCount / CS2J184x 门诊断 | **0 / 0** |

### 2.4 csharp-sdk 训练快照不可用声明

csharp-sdk 训练快照（json/regex/uri/xml-tiny 四子集）与 EF Core 画像在本机**不可用**（未挂载、无路径），因此 M0① 要求的 sdk 语义计数——async lambda 四型分布（D3(a)/(b)/(c)/(d)）、ConfigureAwait bool/Options 拆分、async void/自定义 awaiter 命中率、await foreach/using/IAsync* 命中率、归一率——**全部未取得/画像不可用**。

影响评估（对照 C18 [§8 门槛 L339](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L339) 第 3 条）：**不影响 opt-in 状态下已交付功能**（产品默认关闭，启用时的正确性由 117 过滤集 + fixture 14/14 + 全量 1702 保证），但使「默认开启」所需的**归一率/skip 率画像决策无数据支撑**，第 8 章据此维持 defaultEnabled=false 建议。

---

## 3. M0 交付②：P-Light / P-Heavy 双原型结论

### 3.1 P-Light：未做手改原型，直接作为产品落地

设计 §9 设想 P-Light 是「手改 3–5 个代表文件」的抛针原型；实际直接以产品形态落地，证据链完整：

- `42f50f22`（C18 M1 骨架：Options/Diagnostics/Classifier/Naming + ConfigureAwait(bool) 归一，不翻转注册表）：11 文件 +1470；
- `28683ffa`（async lambda 提升：零捕获→`private static async`、this-only→实例 async、void 委托→async Task + 同步 `_w` wrapper 三型；残余 async 局部函数兜底提升；CS2J1802/1804/1807）：11 文件 +2261/-58；
- `0eb10863`（stub→Implemented 翻转、**默认关**、CLI 适配器、链槽位锁定）：9 文件 +382/-31。

产品代码（HEAD 物理行数，`Get-Content | Measure-Object -Line`）：

- [ConfigureAwaitStripper.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/Await/ConfigureAwaitStripper.cs) 38 行 + [AwaitShapeRewriter.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/Await/AwaitShapeRewriter.cs) 43 行；
- lambda 提升三件套：[AsyncLambdaLifter.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/Lambda/AsyncLambdaLifter.cs) 382 行、[AsyncLambdaCaptureAnalysis.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/Lambda/AsyncLambdaCaptureAnalysis.cs) 126 行、[LambdaLiftPlan.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/Lambda/LambdaLiftPlan.cs) 83 行；
- CLI 适配器 [AsyncNormalizePassAdapter.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.CLI/Preprocessing/Adapters/AsyncNormalizePassAdapter.cs) 199 行；
- 注册表 [PassCatalog.cs L397-L417](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L397-L417)：Status=Implemented、**defaultEnabled=false**、runAfter make-readonly+closure-lift、catalogItems=`C18, C38 (C44 subtopic)`。

单测实测（`[Fact]`/`[Theory]` 计数，非任务简报口径）：三个 P-Light 提交**净增**测试方法 **48 个**（`git show` 新增行计数：27+16+5），其中两个专用文件 [AsyncNormalizeUnitTests.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ProjectTests/AsyncNormalizeUnitTests.cs)（27 个/539 行）与 [AsyncNormalizeLambdaUnitTests.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ProjectTests/AsyncNormalizeLambdaUnitTests.cs)（16 个/538 行），另有 [AsyncNormalizeOrderTests.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.Tests/Preprocessing/AsyncNormalizeOrderTests.cs) 4 个。任务简报所称「92+ 单测」按提交净增/文件口径**未能复现**（全特性 8 个特征测试文件到 HEAD 共 125 个测试方法、ProjectTests 过滤集实测执行 117，见第 4 章；若简报按断言条数或含全链回归计数则口径不同，此处以实测为准）。

### 3.2 P-Heavy：经证普通 async 方法**不需要**，设计雏形未建文件

D11 五个重启条件（[c18 设计 L73](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L73)）在本波次的核对结论：

- **③被满足且仅此一条**：async yield 生产者（`async IAsyncEnumerable` + yield）无法回避跨调用状态持久化；
- ①无数据（sdk 不可用，2.4），但工程证据侧：捕获型 lambda 在 closure-lift 已 Implemented/default-on 的现状下（[PassCatalog.cs L371-L384](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L371-L384)）不再构成「C45 无期」前提；
- ②未触发：Java 半区未转 continuation，仍是 join 阻塞模型，C# 侧先消成无 await 状态机零收益（D1 论证③原样成立）；
- ④⑤无证据。

P-Heavy 的设计雏形文件（[c18 设计 L186](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L186) 预留的 `AsyncStateMachine/AsyncCfgBuilder.cs` 等）**从未创建**；`AsyncNormalizeMode.Heavyweight` 作为选中即抛 `NotSupportedException` 的保留枚举存在（[AsyncNormalizeMode.cs L9-L13](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncNormalizeMode.cs#L9-L13)）。**普通 async 方法的 await 全保留轻量路线在 2943+679+1702 全量回归下成立，P-Heavy 免做**；重量能力按 D11③ 严格收敛在 C38 子域，即下节 M4。

### 3.3 M4 联合形态专节（兼作 C38 §11.3 要求的「M4 联合设计单开文档」）

C38 设计 [§11 第 3 条 L390](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md#L390) 要求「异步迭代器 M4……联合设计在 M4 启动时单开文档（输入：C04 §4 状态机 + C18 §4.8 雏形 + D7）」。实施时未另立 spec，本节以竣工记录身份补齐该输入-产物对照。落地提交 `95474dcb`（19 文件 +3781/-165），新文件三剑客：

| 文件 | HEAD 行数 | 职责 |
|---|---|---|
| [AsyncIteratorPlan.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncStreams/AsyncIterator/AsyncIteratorPlan.cs) | 47 | 降级计划记录（形参提升表、CT 形参、yield 计数、冻结成员表） |
| [AsyncIteratorPlanner.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncStreams/AsyncIterator/AsyncIteratorPlanner.cs) | 467 | 识别/冻结/Validate（v1 拒绝面）、活性分析输入 |
| [AsyncIteratorLowering.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncStreams/AsyncIterator/AsyncIteratorLowering.cs) | 1470 | 区域表 + CFG 扁平化 + 类/工厂发射 |

**生成形态（实测代码）**：

1. **包装类 + 枚举器类**：`IAsyncEnumerable<T>` 方法生成 `Cs2jAsyncEnumerable_<宿主>_<n>`（工厂+GetAsyncEnumerator）与 `Cs2jAsyncIterator_<宿主>_<n>`；`IAsyncEnumerator<T>` 直返形态只生成枚举器类。命名表见 [AsyncStreamNaming.cs L21-L35](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncStreams/AsyncStreamNaming.cs#L21-L35)。
2. **状态字段 / goto 标签 / current**：`__cs2jAsItState_<n>`（0=初始，-1=终结）、`__cs2jAsItCurrent_<n>`、重入标签 `__cs2jResume_<state>`、终结标签 `__cs2jEnd_<n>`、层外重入块标签 `__cs2jL_<id>`；if/switch/循环线性化为同块级 goto 流（遵守 C# goto 不可跳入 try/循环/if/switch section 的作用域规则），交链上 eliminate-goto 后处理（端到端断言见 [AsyncStreamsConversionTests.cs L284-L287](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ConversionTests/Categories/AsyncStreamsConversionTests.cs#L284-L287)）。
3. **async MoveNextAsync**：枚举器方法仍是 `public async ValueTask<bool> MoveNextAsync()`——**真实 await 全部保留**（异步挂起由 async 方法自身完成），只有 yield 跨调用经 state/goto 持久化（类注释 [AsyncIteratorLowering.cs L9-L23](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncStreams/AsyncIterator/AsyncIteratorLowering.cs#L9-L23)）。
4. **async DisposeAsync 区域分派**：try-finally（含 await foreach/await using 展开产物与同步 using/foreach 区域）保留为真 try 节点，采用「层外重入标签 + 层内边界 switch + 链式 goto 分发 + run 标志位 `__cs2jAsItRun_<n>_<r>`」；挂起时活跃区域链 run 标志置 false（finally 跳过释放），重入逐层置 true。fixture T13/T14 锁正常耗尽/早释两路径 finally 各恰好一次。
5. **CT 三元合并**：`[EnumeratorCancellation]` 形参提升为 `__cs2jAsItCt_<n>` 字段，包装类 GetAsyncEnumerator 实参发 `__cs2jItCt_n.CanBeCanceled ? __cs2jItCt_n : __cs2jAsItCt_<n>`（[AsyncIteratorLowering.cs L1538-L1545](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncStreams/AsyncIterator/AsyncIteratorLowering.cs#L1538-L1545)）；fixture T11 以预取消 token 锁 OperationCanceledException 从首个 MoveNextAsync 可见。
6. **工厂表达式体方法**：宿主方法替换为**非 async**、剥除 `[EnumeratorCancellation]` 特性（避免 CS8424）的表达式体工厂 `=> new Cs2jAsync…(…)`（[L1605-L1619](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncStreams/AsyncIterator/AsyncIteratorLowering.cs#L1605-L1619)），透传 this/形参/CT。

**v1 拒绝面（CS2J1844 已从「全拒 async iterator」收窄为局部函数/不支持形态/kill-switch）**：

- kill-switch 两选项：[AsyncNormalizeOptions.cs L60](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncNormalizeOptions.cs#L60) `RejectAsyncIterator=false`（显式拒绝）、[L69](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncNormalizeOptions.cs#L69) `EnableAsyncIteratorLowering=true`（能力子开关），任一关闭落 CS2J1844（[AsyncIteratorPlanner.cs L94-L101](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncStreams/AsyncIterator/AsyncIteratorPlanner.cs#L94-L101)）；
- Validate 冻结形态（[L219-L322](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncStreams/AsyncIterator/AsyncIteratorPlanner.cs#L219-L322)）：残余局部函数、自定义 builder 返回类型、表达式体成员、struct 宿主、ref/in/out/scoped 形参、用户 goto/label、`base.`、unsafe/fixed、ref 局部、内联 out var/解构/模式变量、声明形态同步 using、pattern switch 标签/goto case、解构 foreach；try-catch 内 yield 等基线非法形态由 UnsupportedYieldShape 统一拒绝（[L297-L298 注释](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncStreams/AsyncIterator/AsyncIteratorPlanner.cs#L297-L298)）；
- 冻结成员**整成员 skip、不部分改写**（[AsyncIteratorPlan.cs L44-L51](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncStreams/AsyncIterator/AsyncIteratorPlan.cs#L44-L51)）。

**与 C04 的关系（实测）**：无编译期依赖、无共享文件；复用仅限方法论（plan/naming/捕获、保守活性提升、基线快照、skip 先行）。CFG **未照抄** C04——C04 的 BodyTransformRewriter 产嵌套块标签文本（源码不可编译、仅文本断言），M4 产**真扁平、可编译的 async MoveNextAsync/DisposeAsync**（[AsyncIteratorLowering.cs L20-L22](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncStreams/AsyncIterator/AsyncIteratorLowering.cs#L20-L22)）。fixture 三个 YieldProbe 生产者实测 3 lowered / 6 classes / 0 rejected（2.3），T11–T14 在原树与中间树双 14/14（第 4 章）。

**门控偏差留痕**：C38 [§8 第 2 条 L356](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md#L356) 与 D7 曾要求 M4 凭画像命中数据启动、零命中则书面关闭。实际 M4 在 sdk/EF Core 画像均不可用（2.4）的情况下，由本仓自建 fixture（YieldProbe）证伪「零命中不可降级」并落地——范围严格限制在 D11③ 子域、pass 整体仍默认关闭，风险可接受，但「画像驱动」这一门槛条款事实上未满足，登记于此供评审追认。

---

## 4. M0 交付③：HEAD 测试基线与最终测试矩阵

环境：net10.0 / SDK `10.0.300`（`dotnet --version` 实测）；以下全量为 **Debug 配置**、`dotnet test`（全量复验使用 warm `--no-build`，机器 2026-10-05 03:40–03:56）。Release 全绿为实施期里程碑记录，本次回填**未独立复跑 Release**（见第 8 章门槛 2）。

| 工程 / 集 | 命令（摘要） | 实测结果 | 证据 |
|---|---|---|---|
| CSharpToJava.Tests 全量 | `dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --logger trx` | **2943 passed / 0 failed / 0 skipped**，3m11s | 本机 trx 运行日志；退出码 0 |
| CSharpToJava.ConversionTests 全量 | 同上，ConversionTests.csproj | **679 passed / 0 failed / 0 skipped**，8s | 同上；[AsyncStreamsConversionTests.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ConversionTests/Categories/AsyncStreamsConversionTests.cs) 4 例在内 |
| CSharpToJava.ProjectTests 全量 | 同上，ProjectTests.csproj | **1702 passed / 0 failed / 20 skipped（总 1722）**，11m41s | trx `…03_44_17_net10.0.trx`；20 skipped 均为既存项目夹具（ForeachSource/ClosureLift/StructValueSemantics 等），**无任何 Async* 用例** |
| SampleAsyncSolution 原树 | `dotnet test tests/SampleAsyncSolution/SampleAsyncSolution.sln` | **14/14 passed**，512ms | 控制台汇总（T1–T9、T3a/b、T11–T14；编号无 T10） |
| SampleAsyncSolution 中间树 | ProjectTests 夹具内显式 trx 驱动 | **14/14 passed / 0 failed** | [AsyncStreamFixtureProjectTests.cs L285-L300](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ProjectTests/AsyncStreamFixtureProjectTests.cs#L285-L300)（断言 total/passed/failed=14/14/0），该夹具在上述 1702 全量中通过；基线原树 14/14 断言在 [L451-L454](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ProjectTests/AsyncStreamFixtureProjectTests.cs#L451-L454) |
| AsyncNormalize\|AsyncStream 过滤集 | `dotnet test ProjectTests --filter "FullyQualifiedName~AsyncNormalize|FullyQualifiedName~AsyncStream"` | **117 passed / 0 failed**，19s | 控制台汇总（02:20 带构建运行）；= 六个特性文件 27+16+30+19+21+4 |
| C04 Iterator 回归（ProjectTests 侧） | `--filter FullyQualifiedName~Iterator` | **56 passed / 0 failed**，11s | trx `…03_44_03…`：IteratorLoweringUnitTests 28（C04）+ AsyncStreamIteratorUnitTests 21（M4）+ AsyncStreamAwaitForeachUnitTests 4 + 其余 3 |
| Iterator 回归（Tests 侧） | 同上，CSharpToJava.Tests | **7 passed / 0 failed**，660ms | trx `…03_44_14…`：3 个链排序 + D4Ir/ErrorDoc/Goto/LocalFunctionLift 各 1；注：[IteratorBridgeTests.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.Tests/IteratorBridgeTests.cs) 是空壳类（0 测试） |
| Java 桥 JUnit | `mvn -f java/csharptojava-compat/pom.xml test -Dtest=AsyncStreamsBridgeTest`（JAVA_HOME=`D:\Java\jdk-28-ea-11`） | **2/2 passed**（JDK 28-ea+11、Maven 3.9.9） | surefire `io.github.ningpp.compat.AsyncStreamsBridgeTest.txt` |
| Java compat 全量 | `mvn test`（同环境） | **1606 total / 1 failure / 0 error / 0 skipped**；唯一红 = `ByRefTest.byRefSpanElementPassesThroughAndAliases` | surefire 汇总；该用例文件自基线 `e50d9874` 以来零改动（末次修改 `53cf4304`），失败实测 `expected: <20> but was: <10>`（[ByRefTest.java:114](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/java/csharptojava-compat/src/test/java/io/github/ningpp/compat/ByRefTest.java#L114)），与本特性无关 |

中间树额外结构断言（同夹具）：全树零裸 `await foreach`/`await using`/`yield return`/`yield break`、无 `__cs2jAsOk_` 守卫标志临时量（朴素 finally 默认）、存在 `Cs2jAsyncIterator_`/`Cs2jAsyncEnumerable_` 合成类（[L257-L283](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ProjectTests/AsyncStreamFixtureProjectTests.cs#L257-L283)）。

---

## 5. Java 半区红绿盘点与阻断清单（M0③ 核心）

| # | 条目（C18 §1.2/§8④ 出处） | 现状 | 证据 | 后续 owner 建议 |
|---|---|---|---|---|
| ① | ConfigureAwait camelCase 漏网（§1.2-4，`t.configureAwait(false).join()` 不可编译） | **绿（C# 侧剥离收口）** | [ConfigureAwaitStripper.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/Await/ConfigureAwaitStripper.cs)（38 行）在预处理期整体替换为 receiver；转换门 [AsyncStreamsConversionTests.cs L130-L132](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ConversionTests/Categories/AsyncStreamsConversionTests.cs#L130-L132) 与 L217-L218 同时断言大写/小写 `configureAwait` 零残留（3 个测试方法） | 无；保持 C# 侧收口，Java 侧不加映射 |
| ② | `Task.Delay→delayedExecutor` 形状错误（§1.2-7、D7、§8④） | **红，挂账，未修** | [TypeMappings.json L6857-L6876](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/config/TypeMappings.json#L6857-L6876) 两条 `Delay→delayedExecutor` 为既存映射（`git log -L` 归因旧提交 `29a9d3be`，本波次 9 提交未触碰）；形状三重不对：`Task.Delay(int)` 是静态返回 Task，Java `Executor.delayedExecutor(long,TimeUnit)` 是实例风格、参数表不同、**返回 Executor 而非 future**，产物不可编译 | compat/转换器侧：落 `CSharpTasks.delay(ms,ct)` 类薄封装并改映射；画像确认命中前可维持现状但默认开启前必须签收 |
| ③ | 非泛型 ValueTask 无映射（D7） | **绿，`d24000ff` 已补** | 该提交对 [TypeMappings.json](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/config/TypeMappings.json) 的 hunk 新增 `System.Threading.Tasks.ValueTask`→`CompletableFuture`（现 L162-L168），同 hunk 还含 `ValueTask`1`（L155-L161）与三个 async-streams 桥类型（L169-L189） | 无 |
| ④ | Boolean join 拆箱（while 条件 `Boolean`→`boolean`） | **绿，既有拆箱表已覆盖**（非本波次生产改动） | [ControlFlowTransformer.cs L390](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L390) 既有 `"Boolean" => "boolean"`（await-join cast 路径 L362-L394）；产物 `(boolean) (…moveNextAsync().join())` 由 [AsyncStreamsConversionTests.cs L119-L122](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ConversionTests/Categories/AsyncStreamsConversionTests.cs#L119-L122) 文本锁定；`d24000ff` 仅加配置/Java/测试（7 文件），**零生产 .cs 改动** | 无 |
| ⑤ | `CancellationToken.None` 映射缺失 | **绿，`d24000ff` 已补** | [TypeMappings.json L6902-L6911](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/config/TypeMappings.json#L6902-L6911) 补 `get_None`/`None`→`getNone`；compat [CancellationToken.java L16-L26](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CancellationToken.java#L16-L26) 提供 `getNone()`/`isCancellationRequested()`；转换断言 `.getNone()` 命中（L111、L306） | 无 |
| ⑥ | CompletionException 解包策略（§1.2-8、D9、§8④） | **未定，挂账** | join 抛 `CompletionException` 包装真实异常，Java catch 类型不匹配（设计 [L27](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L27)）；桥 JUnit 暂以 `caught.getCause()` 取真因作同构锁定（[AsyncStreamsBridgeTest.java L83-L86](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/java/csharptojava-compat/src/test/java/io/github/ningpp/compat/AsyncStreamsBridgeTest.java#L83-L86)），**不是产品解包策略** | 转换器侧独立决策：join 包 try/catch 解 cause，或 compat 提供 unwrap 助手；默认开启前需定论 |
| ⑦ | CT 取消传播（Register/ThrowIfCancellationRequested/CancelAfter） | **红，compat 不支持，挂账** | TypeMappings.json 全文件 grep `Register`/`ThrowIfCancellationRequested` **零命中**；compat CancellationToken 仅 getNone/isCancellationRequested 两个面（D8 预告，[c18 设计 L70](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L70)）；fixture T3a/T3b/T11 锁的是 token 数据流可见性，非 Java 半区传播 | compat 工作项：注册回调/取消检查点 API；画像（Task.Run(…,ct) 频率）支撑优先级 |
| ⑧ | 返回包装死路径（§1.2-3，置位点 `9f09f426` 被删） | **HEAD 实测仍是死路（未恢复）** | 全 src grep `IsInAsyncContext` 仅 7 处：4 个消费分支 [StatementTransformer.ExpressionAndReturn.cs L101-L142](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L101-L142)（`return CompletableFuture.completedFuture(...)` 四形）、声明 [ConversionContext.cs L235](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/Context/ConversionContext.cs#L235)、置 false 两处（[ConversionContext.cs L617](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/Context/ConversionContext.cs#L617)、[PropertyTransformer.cs L9](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L9)），**全仓无置 true 点**；`9f09f426` 是基线 `e50d9874` 的祖先（已核），本波次未触碰上述文件。即 async Task 方法的 return 仍发裸 `return expr;`，当前 join 阻塞模型下已测形状侥幸可过，Task<T> 返回包装承诺未兑现 | 转换器侧：要么恢复置位与 completedFuture 包装，要么书面删除四分支改走 join 模型单一形状；默认开启前必须二选一（§8④ 原文要求） |

净盘点：8 条中 **①③④⑤ 已绿（4）**、**②⑥⑦⑧ 仍红/挂账（4）**；其中 §8 门槛 4 原文点名的四项（⑧死路径、①camelCase、②Delay、CompletionException）目前为 2 绿 2 红。

---

## 6. D6 真值更正专章（本轮最重要的探针发现）

### 6.1 被检验的假设

C38 D6（[c38 设计 L67](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md#L67)）断言：await foreach 存在刻意区别于同步 foreach 的「**C# 8 特例**」——循环体/MoveNextAsync 已有在途异常时，DisposeAsync 的异常**不得屏蔽主异常**（吞掉释放异常、主异常传播）；据此 AS-1 默认发射带成功标志位的保真守卫。佐证文本还有 §2.1 示意注释 [L107](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md#L107)（`/* 保真：在途主异常优先，释放异常丢弃，C# 8 规则 */`）、§4.6 时序表 [L246](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md#L246)（「主异常传播；DisposeAsync 仍执行，其异常丢弃不屏蔽（D6 守卫）」）、风险 2 [L376](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md#L376)。

### 6.2 实验

- 装置：[HandwrittenAsyncEnumerable.cs L99-L130](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/SampleAsyncSolution/SampleAsyncLib/HandwrittenAsyncEnumerable.cs#L99-L130) 纯手写枚举器（无状态机糖），注入双故障：首次 MoveNextAsync 以 `ValueTask.FromException<bool>(new InvalidOperationException("body-in-flight"))` 失败（L106-L107），DisposeAsync 以 `ValueTask.FromException(new ObjectDisposedException("dispose"))` 失败（L124-L127），并计数 DisposeAsync 调用次数。
- 用例：T4 在途双故障（[AsyncStreamTruthTests.cs L127-L156](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/SampleAsyncSolution/SampleAsyncTests/AsyncStreamTruthTests.cs#L127-L156)）、T5 正常耗尽后释放故障（[L163-L178](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/SampleAsyncSolution/SampleAsyncTests/AsyncStreamTruthTests.cs#L163-L178)）、T8 await using 双故障（[L212-L227](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/SampleAsyncSolution/SampleAsyncTests/AsyncStreamTruthTests.cs#L212-L227)）。
- 环境：net10.0，SDK **10.0.300**（本机 `dotnet --version` 实测）。T4 注释记载完整矩阵：「MoveNext 抛 vs 体抛 × Dispose 抛/不抛 × 同步/异步故障」8 格全部同结论（[L139-L141](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/SampleAsyncSolution/SampleAsyncTests/AsyncStreamTruthTests.cs#L139-L141)）。

### 6.3 结果（net10 实测真值）

1. **T4**：await foreach 表面抛出的是 **ObjectDisposedException("dispose")**——DisposeAsync 异常屏蔽在途主异常；DisposeAsync 恰好运行 1 次。断言 `Assert.ThrowsAsync<ObjectDisposedException>` + `ObjectName=="dispose"` + `DisposeCount==1` 通过。
2. **T5**：序列正常耗尽后 DisposeAsync 抛错，无主异常在途，dispose 异常照常上抛（同为 ObjectDisposedException("dispose")）。
3. **T8**：await using 双故障同构——finally 的 DisposeAsync 异常屏蔽体异常（朴素 try/finally 语义）。

### 6.4 结论

**await foreach == 朴素 try/finally + `await DisposeAsync()`，不存在 D6 假设的「C# 8 在途异常优先」特例**；await using 同构（本就是普通 finally）。C# 编译器对异步流的故障处理与同步 foreach 的 finally 屏蔽语义一致。设计 D6、§2.1 守卫示意、§4.6 时序表、风险 2 四处文本均需更正（第 9 章）。

### 6.5 处置（已吸收进产品）

- `a9743f21`（2026-10-04 21:58，5 文件 +90/-70）把 [AsyncNormalizeOptions.FaithfulAsyncDisposeGuard](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncNormalizeOptions.cs#L51) 由 `true` 翻为 **`false`**（默认发单层朴素 try/finally），XML doc 改写为「true 仅作显式语义选择，注意该语义与真实 .NET 行为不同，不要作为保真映射使用」；
- 翻转前的中间树为 **9 passed / 1 failed**（守卫发散，唯一红即 T4），翻转后中间树与原树 **10/10 全绿**（该 9/1 史实在夹具类注释 [L39-L46](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ProjectTests/AsyncStreamFixtureProjectTests.cs#L39-L46) 留痕）；M4 加入 T11–T14 后当前为原树/中间树双 **14/14**（第 4 章）。

### 6.6 对 Java 半区的含义

- **Java try-with-resources 反而与朴素 finally 不同**：try-with-resources 在主异常+关闭异常时采「主异常优先、关闭异常挂 suppressed」，恰好接近 D6 假想的旧规则——因此 C38 决策（await-using/foreach **不映 try-with-resources**、映手写 try/finally）在 D6 更正后被证明是**正确方向**，手写 `finally { x.disposeAsync().join(); }` 的 join 模型与 .NET 朴素 finally 同构；
- JUnit [AsyncStreamsBridgeTest.java L54-L87](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/java/csharptojava-compat/src/test/java/io/github/ningpp/compat/AsyncStreamsBridgeTest.java#L54-L87) 以 `FailingEnumerator`（MoveNext 第 3 跳抛 `IllegalStateException("in-flight")`、Dispose 抛 `RuntimeException("dispose")`）同构锁定：捕获到的 CompletionException 的 cause 必须是 "dispose" 且不是 IllegalStateException——结构同 T4（差别仅在 Java 多一层 CompletionException 包装，即挂账⑥）。

---

## 7. 交付物清单

### 7.1 提交序列（`git log --oneline e50d9874..HEAD`，9 个；总 diff：58 文件 +13940/-43）

| # | hash | 日期 | 主题与关键内容 | 规模 |
|---|---|---|---|---|
| 1 | `42f50f22` | 10-04 14:53 | C18 M1 骨架：Options/Diagnostics/Classifier/Naming、ConfigureAwait(bool) 归一；不翻转注册表 | 11 文件 +1470 |
| 2 | `28683ffa` | 10-04 16:36 | C18 M1 async lambda 提升（零捕获/this/wrapper 三型）+ 局部函数兜底；CS2J1802/1804/1807 | 11 文件 +2261/-58 |
| 3 | `0eb10863` | 10-04 17:06 | stub→Implemented（默认关）+ CLI 适配器 + 链槽位锁定（iterator-lowering→async-normalize→extension-methods→eliminate-goto） | 9 文件 +382/-31 |
| 4 | `63a2b9fe` | 10-04 18:12 | C38 M2 await using→try/finally+DisposeAsync；鸭子型 CS2J1842 门；无注册表改动 | 9 文件 +2017/-19 |
| 5 | `adf75c18` | 10-04 20:23 | C38 M3 await foreach 展开（当时含保真守卫）+ CS2J1844 async-iterator 门；catalogItems 加宽回 C18,C38 | 17 文件 +2461/-89 |
| 6 | `19efb52b` | 10-04 21:46 | SampleAsyncSolution .NET 真值 oracle（顺序/取消/在途与正常双故障/await-using LIFO）+ 预处理驱动夹具 | 14 文件 +1335 |
| 7 | `a9743f21` | 10-04 21:58 | **D6 翻转**：FaithfulAsyncDisposeGuard 默认 false（net10 真值） | 5 文件 +90/-70 |
| 8 | `d24000ff` | 10-04 22:29 | Java 桥三接口（AsyncEnumerable/AsyncEnumerator/AsyncDisposable，删除孤本 IAsyncDisposable）+ TypeMappings 桥条目/非泛型 ValueTask/CT None + 4 个转换门测试 | 7 文件 +544/-12 |
| 9 | `95474dcb` | 10-05 02:50 | **C38 M4 联合异步迭代器 lowering**（yield 区域 × async MoveNextAsync、async DisposeAsync 区域、CT 合并）；CS2J1844 收窄为局部函数/不支持形态/kill-switch | 19 文件 +3781/-165 |

### 7.2 生产代码（HEAD 实测物理行数；目录 [src/CSharpToJava.Core/AsyncNormalize/](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/)，23 文件 / 6421 行）

- 根目录 9 文件 1791 行：[AsyncNormalizeAnalyzer.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncNormalizeAnalyzer.cs) 827、[AsyncNormalizePreprocessor.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncNormalizePreprocessor.cs) 526、[AsyncShapeClassifier.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncShapeClassifier.cs) 144、[AsyncNormalizeStatistics.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncNormalizeStatistics.cs) 81、[AsyncNormalizeDiagnostic.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncNormalizeDiagnostic.cs) 74、[AsyncNormalizeOptions.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncNormalizeOptions.cs) 64、[AsyncNormalizeNaming.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncNormalizeNaming.cs) 45、AsyncNormalizeAnnotations 18、AsyncNormalizeMode 12；
- AsyncStreams/ 6 文件 1974 行：AsyncStreamAnalyzer 812、AwaitUsingRewriter 383、AsyncForeachRewriter 350、AsyncTypeResolver 248、AsyncStreamNaming 153、AsyncForeachSite 28；
- AsyncStreams/AsyncIterator/ 3 文件 1984 行：Lowering 1470、Planner 467、Plan 47；
- Await/ 2 文件 81 行（43+38）；Lambda/ 3 文件 591 行（382+126+83）；
- CLI 适配器 [AsyncNormalizePassAdapter.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.CLI/Preprocessing/Adapters/AsyncNormalizePassAdapter.cs) 199 行（生产侧合计 24 文件 / 6620 行）。

### 7.3 测试与 Java/夹具（HEAD 实测）

- 特性测试 8 文件 / 4192 行：[AsyncStreamAwaitForeachUnitTests.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ProjectTests/AsyncStreamAwaitForeachUnitTests.cs) 949（30 测试）、[AsyncStreamIteratorUnitTests.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ProjectTests/AsyncStreamIteratorUnitTests.cs) 799（21）、[AsyncStreamAwaitUsingUnitTests.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ProjectTests/AsyncStreamAwaitUsingUnitTests.cs) 595（19）、[AsyncStreamFixtureProjectTests.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ProjectTests/AsyncStreamFixtureProjectTests.cs) 409（4）、AsyncNormalizeUnitTests 539（27）、AsyncNormalizeLambdaUnitTests 538（16）、[AsyncStreamsConversionTests.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ConversionTests/Categories/AsyncStreamsConversionTests.cs) 293（4）、AsyncNormalizeOrderTests 70（4）；
- [tests/SampleAsyncSolution/](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/SampleAsyncSolution/) 全树（排除 bin/obj）12 文件：sln 27 行、.gitignore 8；SampleAsyncLib 7 个 .cs（YieldProbe 105、ChannelProbe 110、HandwrittenAsyncEnumerable 114、AsyncDisposalProbe 98、CancellationProbe 68、DoubleFaultProbe 59、LifoScope 38）+ csproj 9；SampleAsyncTests [AsyncStreamTruthTests.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/SampleAsyncSolution/SampleAsyncTests/AsyncStreamTruthTests.cs) 317 + csproj 16；
- Java：3 桥接口（[AsyncEnumerable.java](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/AsyncEnumerable.java) 26、[AsyncEnumerator.java](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/AsyncEnumerator.java) 34、[AsyncDisposable.java](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/AsyncDisposable.java) 25；删除基线孤本 IAsyncDisposable.java 9 行）+ JUnit [AsyncStreamsBridgeTest.java](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/java/csharptojava-compat/src/test/java/io/github/ningpp/compat/AsyncStreamsBridgeTest.java) 125 行 2 测试；
- 注册表配套：[PassCatalog.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) +21、[PassChainFactory.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs) +14、[PassStubCatalog.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) +17/-；[config/TypeMappings.json](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/config/TypeMappings.json) +63。

诊断码实测在码：CS2J1801-1805、1807、1808、1810、1820、1830、1840-1846、1849（[AsyncNormalizeDiagnostic.cs L16-L69](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.Core/AsyncNormalize/AsyncNormalizeDiagnostic.cs#L16-L69)）；设计列过的 **CS2J1806/1809/1847/1848 全仓零实现**（见第 9 章）。

---

## 8. 挂账与默认开启建议（逐条对照 C18 §8 五门槛）

| 门槛（[c18 设计 L337-L341](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L337-L341)） | 实测达成度 | 结论 |
|---|---|---|
| ① M0 探针闭环且未推翻 D1；M1–M3 出口达成、两半区结论成文 | 本报告即回填探针；D1 未被推翻（P-Heavy 对普通 async 免做），且 D11③ 由 M4 承接；M1–M4 全落地、测试成文。**但 sdk 计数缺口（2.4）使「闭环」打折**，另探针门是事后补行 | 部分达成（追认性） |
| ② 全仓 Release build/test 全绿；residual=0；skip 站站有诊断 | Debug 全量 2943/679/1702(20 skip 无 Async*) 本次实测全绿；fixture residual=0、CS2J184x=0 有断言；Release 本次**未独立复跑**（实施期里程碑记录为绿）；skip 分类在 Planner Validate/UnsupportedYieldShape 成文 | 大部分达成；Release 待复验 |
| ③ sdk 四子集归一率（lambda 四型/ConfigureAwait/async void/awaiter） | **无数据**：快照本机不可用（2.4） | 未达成 |
| ④ Java 阻断清单清零或显式签收 | 8 条盘点 4 绿 4 红：①③④⑤ 绿；**②Task.Delay 形状、⑥CompletionException 策略、⑦CT 传播、⑧返回包装死路径仍挂账**；无「目标画像不触发」的数据证据（画像不可用） | 未达成（4/8） |
| ⑤ opt-in 观察期跨一次主版本回归窗口 | 分支未合入 dev，观察期尚未开始计时 | 未达成 |

**建议：维持 `defaultEnabled=false`。** 理由：

1. 门槛 3/4/5 客观未满足，且画像数据缺失使「不触发即签收」无路可走——在阻塞 join 模型 + Delay/CompletionException/CT/返回包装四笔 Java 半区债务未清前，默认开只扩大「可编译面」而不扩大「可正确运行面」（与设计 [L343](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L343) 原文判断一致）；
2. 现状与链上先例一致且自洽：PassCatalog 37 个 Implemented 中 36 个 default-on，**async-normalize 是唯一 default-off**（实测计数），其适配器 verifyBuild/verifyTests 默认开，opt-in 用户拿到的是强验证形态；
3. 翻 true 的最小前置建议：取得 sdk/EF Core 画像（门槛③）、Java 四红至少以画像证据签收或修复（门槛④）、Release 复验（门槛②）、合入后观察一个回归窗口（门槛⑤）。

---

## 9. 对两份设计文档的事实性更正清单（供后续合入 dev 时改 spec）

1. **【最重要】C38 D6 守卫假设错误**：[c38 设计 L67（D6 全文）](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md#L67)、[L107 守卫注释](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md#L107)、[L246 §4.6 时序表](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md#L246)、[L376 风险 2](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md#L376) 的「在途主异常优先、吞 DisposeAsync 异常的 C# 8 特例」**不存在**；net10/SDK 10.0.300 实测 await foreach 等价朴素 try/finally（dispose 屏蔽主异常）。默认已随 `a9743f21` 翻为朴素 finally，FaithfulAsyncDisposeGuard=true 应改述为「**与 .NET 不同的**显式语义选项」。
2. **catalog 数字的文字标注更正**：[c18 设计 L16](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L16) 原文「await 1297 / `Task<` 311 / `Task` 789 / ConfigureAwait 11」应在合入时保留原口径并加醒目说明，防止后续读者再误标成「async lambda 789 / ConfigureAwait 311 / lock+await 11」（本波次任务简报即发生此误读）；async lambda「尚无计数」的开放项状态应更新为「M0 回填时 sdk 仍不可用，继续未取得」。
3. **closure-lift（C32/C45）已翻转**：两份设计多处假设 C45 为 stub（如 [c18 L65 D3(d)](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L65)、[L74 D12](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L74)、[c38 L395 第 8 条](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md#L395)）；实测 closure-lift 已 Implemented/**default-on**（[PassCatalog.cs L371-L384](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L371-L384)，catalogItems=C32,C45），async-normalize 的 runAfterIds 已含 ClosureLift（[L412](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L412)）。D3(d)「捕获型 async lambda 一律 CS2J1802 defer 等 C45」的前提需重评：C45 已在位但本 pass 当前仍对捕获型发 CS2J1802（合流演练 M2 项未做），spec 应改写为「两 pass 已同链、display-class 合流仍待验证」。
4. **PassCatalog 描述文本过时**：[PassCatalog.cs L416](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L416) 仍写 await foreach 带「faithful in-flight-exception dispose guard」，与 D6 更正后的默认（朴素 try/finally；守卫为非 .NET opt-in）相反，建议合入时改述。
5. **stub 计数文字**：C38 [L394](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md#L394)「stub 计数断言（C18 翻转后 23→22）」在现仓无对应硬编码——[PassStubCatalogTests.cs](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs) 用结构断言（全部 default-off、目录唯一、LinqChannel 作示例 stub）；实测当前 PassStubCatalog 7 个 Stub、PassCatalog 37 个 Implemented（36 默认开 + async-normalize 默认关）。
6. **IAsyncDisposable 孤本重塑时点**：C38 附录裁决 4（[L422](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md#L422)）把重塑列入 M2，实际 M2（`63a2b9fe`）未动 Java；重塑发生在 M3 提交 `d24000ff`（删 IAsyncDisposable.java、新增 AsyncDisposable/AsyncEnumerable/AsyncEnumerator）。
7. **未实现的设计诊断码**：CS2J1806（ref struct 跨 await，D10）在码表中不存在；CS2J1809/1847/1848 亦零实现。设计 [L244-L248](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L244-L248) 的码表应标注「未发/后补」。
8. **M4 触发依据与里程碑文本**：C38 M4 原门控（画像命中才启动、零命中书面关闭，[L371](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md#L371)、[L423 第 5 条](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md#L423)）实际由自建 fixture 驱动而非画像（3.3 末已登记偏差）；CS2J1844 语义从「async iterator 一律拒绝」收窄为「局部函数/不支持形态/kill-switch」，D7/AS-6 相关段落需按 `95474dcb` 实际形态改写（本报告 3.3 可作输入）。
9. **§4.7 预留文件与实际落点不符**：P-Heavy 的 `AsyncStateMachine/*` 未创建；M4 落于 `AsyncStreams/AsyncIterator/{AsyncIteratorPlan,AsyncIteratorPlanner,AsyncIteratorLowering}.cs`（C18 [L186](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md#L186)、C38 文件所有权段建议同步订正）。
10. **轻微代码注释漂移（非 spec）**：[AsyncStreamFixtureProjectTests.cs L39-L46、L232、L243、L426](file:///D:/code/cs2j/.worktrees/preproc-c18-c38-async-normalize/tests/CSharpToJava.ProjectTests/AsyncStreamFixtureProjectTests.cs#L39-L46) 仍写「T1–T9（10/10）」，M4 后真值已是 T1–T9+T11–T14 的 14/14（编号无 T10），建议顺手修注释。
