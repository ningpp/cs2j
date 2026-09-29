# C18 async / await 归一预处理器设计

**日期**: 2026-09-29
**范围**: 新增 C#→C# 预处理器 `async-normalize`（Core `AsyncNormalize\` + CLI 适配器），覆盖 async **方法体**：async lambda / 残余 async 局部函数提升为命名 async 方法、`ConfigureAwait` 形态归一、async void 与不可降级 await 形态显式诊断。**不含**异步流（`await foreach` / `await using` / `IAsyncEnumerable`，属姊妹 C38，未来在同一 pass 内扩展）；不改造 C#→Java 主转换器
**状态**: 设计稿，待评审；**形态选型高不确定**，★建议先做 M0 形态原型探针再定 M1+（见 §2 D1/D2、§9）；翻转后长期 opt-in
**注册表**: stub 已登记于 [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L229-L238)（`Ids.AsyncNormalize = "async-normalize"`，Stage=StateMachine，RunAfter=`closure-lift`，默认关，目录 `.cs2j-async-normalize-src`，catalogItems=`C18, C38 (C44 subtopic)`）

> 命名口径：pass id 一律以代码为准 = `async-normalize`；Core 命名空间/目录用 `AsyncNormalize`。本 pass 是异步族【锚点】：形态选型在本文定稿；翻转时 catalogItems **收窄为 "C18"**；C38 落地时在同一 pass 上扩宽回 `C18, C38`（见 §2 D13、§4.7、§11）。

## 1. 背景与目标

### 1.1 频率证据

[preprocessor-catalog.md](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1069-L1076) §5.5 Layer 4 实测 csharp-sdk 训练集：**await 1297 / `Task<` 311 / `Task` 789 / ConfigureAwait 11**；lambda 总量 4966（C45），其中 async lambda 的占比**尚无计数**——这是 M0 探针必须先回答的问题（C18 条目 471-482 行标注「P2，高不确定」）。对照：yield 2887（C04）是纯 fail-fast 面；async 方法体的**大部分形态今天不报错**（走伪异步映射），硬失败集中在 async lambda 一个面上。

### 1.2 当前转换面（已逐处复核，证据以 HEAD `953293b9` 为准）

1. **async lambda 无条件硬拒绝**：[LambdaTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L49-L53) 对 `async` 修饰符的 lambda 直接抛 `NotSupportedException("... async lambdas must be lowered ... by a preprocessor ...")`；匿名方法 `delegate { }` 同样硬拒绝（[同文件 L38-L39](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L38-L39)，由 E05 消去，非本 pass 范围）。
2. **await → `CompletableFuture.join()` 阻塞特判**：[ControlFlowTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ControlFlowTransformer.cs#L358-L398) 把 `await x` 翻成 `x.join()`（语句位置）或 `(T) x.join()`（表达式位置，含装箱原始类型拆箱表）。这是**阻塞伪异步**：方法签名是 `CompletableFuture`，方法体却同步执行并在每个 await 点阻塞，不产生任何 continuation，并发/调度语义与 C# async 不等价（结果值对「最终已完成」的 Task 等价）。
3. **async 方法返回包装路径在当前 HEAD 已是死代码**：[StatementTransformer.ExpressionAndReturn.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L97-L143) 的 `ctx.IsInAsyncContext ? "return CompletableFuture.completedFuture(...)" : ...` 四个分支依赖 `IsInAsyncContext`，但全仓搜索该标志**只有声明（[ConversionContext.cs:232](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionContext.cs#L232)）与进方法重置为 false（[L611-L614](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionContext.cs#L611-L614)），再无置 true 点**。git 考古：置位点原在 `MethodTransformer.MapReturnType`（`9f09f426~1` 的 MethodTransformer.cs:2415，同时承担 async Task→CompletableFuture 的特判），在 `9f09f426`「clean codes」（2026-09-27）中被删除。[MethodTransformer.cs:31](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L31) 仍落 `method.IsAsync = true` 标记，但该标记除 record-readonly 分析（[RecordReadOnlyAnalyzer.cs:353](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyAnalyzer.cs#L353)）外**无生产消费方**。同理 `MethodConversionState.InAsyncLambdaReturnPosition`（[MethodConversionState.cs:116-L121](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/MethodConversionState.cs#L116-L121)）也是声明后零引用的半拉子设施。→ **Java 半区 async 就绪度弱于 catalog 叙述**，M0 必须实测现有 async 测试簇在 HEAD 的红绿现状（本设计任务受只读约束不跑 build/test，见 §9 M0）。
4. **ConfigureAwait 实际未被剥离**：全 src 无任何 `ConfigureAwait` 特判；普通调用路径按 camelCase 输出（[InvocationExpressionTransformer.cs:224](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs#L224)），`await t.ConfigureAwait(false)` 的产物是 `t.configureAwait(false).join()`——**Java 不可编译**。测试断言只检查大写 `ConfigureAwait` 不出现（[AsyncAwaitTests.cs:35-L41](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/AsyncAwaitTests.cs#L35-L41)），camelCase 漏网，断言强度不足。
5. **async 局部函数已被 E02 提升但 async 语义可能丢**：E02 提升局部函数时保留 async 修饰符（[LocalFunctionLiftRewriter.cs:524-L525](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftRewriter.cs#L524-L525)），但 async 函数不能走 ref 捕获快路（[LocalFunctionLiftAnalyzer.cs:651-L699](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs#L651-L699)，原因串 `"local function is async"`）；而主转换器侧 `TransformLocalFunction`（[MethodTransformer.cs:55-L70](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L55-L70)）**没有 `IsAsync = true` 置位**——提升后的 async 方法是否产出可编译 Java 同样待 M0 实测。
6. **async void 当前静默「转换成功」**：[AsyncAwaitTests.cs:69-L76](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/AsyncAwaitTests.cs#L69-L76) 记录现状——async void 被当成 void 阻塞方法，fire-and-forget 与未观察异常语义静默丢失（无诊断）。
7. **类型/方法映射半就绪**：[TypeMappings.json](file:///d:/code/cs2j/config/TypeMappings.json#L142-L159) 把 `Task`/`Task<T>`/`ValueTask<T>` 都映到 `CompletableFuture`（非泛型 `ValueTask` **无映射**，落到薄 compat [ValueTask.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ValueTask.java)（仅 14 行、空壳））；方法面 `Run→runAsync/supplyAsync`、`WhenAll→allOf`、`WhenAny→anyOf`、`Result/Wait→join`、`ContinueWith→thenApply`、`FromResult→completedFuture`、`CompletedTask→CompletableFuture.completedFuture(null)`、`Delay→delayedExecutor`（[L6619-L6747](file:///d:/code/cs2j/config/TypeMappings.json#L6619-L6747)）。其中 **`Task.Delay→delayedExecutor` 形状就是错的**（`delayedExecutor` 返回 `Executor` 而非 future，`await Task.Delay(x)` 产物不可编译）；`Task.Run(Func<Task>)` 靠 [ArgumentTransformer.Coercion.cs:215-L224](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.Coercion.cs#L215-L224) 强转 `(Runnable)` 回避签名冲突。compat 侧 [Task.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Task.java) / [TaskFactory.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TaskFactory.java) / [CancellationToken.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CancellationToken.java) / [CancellationTokenSource.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CancellationTokenSource.java) 均为薄类（CT 只有 `getNone/isCancellationRequested`，**无 `Register`/`ThrowIfCancellationRequested`/`CancelAfter`**）。
8. **异常语义缺口**：`CompletableFuture.join()` 抛 `CompletionException` 包装真实异常；C# `await` 重抛原始异常。`catch (IOException)` 直译为 Java catch 在 await 点**捕获不到**目标异常。轻量版不解决该缺口（§2 D9 处置）。
9. **测试簇现状**：[AsyncAwaitTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/AsyncAwaitTests.cs) 13 例（文本断言为主）、[ConfigureAwaitConversionTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ConfigureAwaitConversionTests.cs) 7 例（含 async Task 末尾补 `completedFuture(null)` 的可达性回归断言——依赖第 3 条已死路径，红绿待实测）；[GetResultConversionTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/GetResultConversionTests.cs) 仅 1 例（负例：非 awaiter 的 `GetResult()` 不得映成 join）；[StreamAsyncTokenTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/StreamAsyncTokenTests.cs) 与 [TaskRunGenericMappingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/TaskRunGenericMappingTests.cs) **是空占位类**（无 `[Fact]`）。
10. **异步流静默降级（C38，非本 pass 范围，但必须可见）**：`await using` 不检查 `AwaitKeyword`，直接降 try-with-resources（[StatementTransformer.SwitchAndResource.cs:198-L244](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L198-L244)）；`await foreach` 同病（StatementTransformer.Loops.cs:104-155）；`IAsyncEnumerable` 等无类型映射、无 compat（catalog 719-730 行）。

### 1.3 两条候选形态（本设计的核心选型对象）

| | 形态①轻量版（normalize，输入仍含 await） | 形态②重量版（state machine elimination，输入再无 await） |
|---|---|---|
| 做法 | async lambda 提升为命名 async 方法（必要时合成最小 async closure 类）；残余 async 局部函数补提升；`ConfigureAwait(false/true)` 归一剥离；async void / 自定义 awaiter 等显式报错。**产物仍是合法 async C#**，await 原样保留 | 仿 Roslyn lowering：每个 async 方法改写成嵌套状态机类（`IAsyncStateMachine` + `AsyncTaskMethodBuilder<T>` + awaiter 字段 + `MoveNext` 状态 switch + `SetResult/SetException`），方法体变为 `builder.Start(...)`；产物**不含 await** |
| 等价性 | 与 C# 编译器 async 语义在「结果值」层等价；调度/并发/同步上下文不等价（交 Java 半区 join 模型承接，现状即此） | C# 半区可逐行等价；但 Java 半区必须新学 builder/awaiter 全套映射或 compat 基建 |
| 成本 | 中：语法/SemanticModel 重写 + 捕获子集策略，复用 E02/C45 捕获与命名基建思路 | 极高：≈ 重写编译器 async lowering（catalog §4 风险 1 原话）；ExecutionContext/SynchronizationContext/ValueTask 自定义 builder/`Task.Yield`/异常封送全套；try-finally 跨挂起清理同 C04 量级 |
| 与 Java 半区关系 | 零新增映射需求（Task→CompletableFuture、await→join 既有，尽管语义是阻塞版） | 需要 Java 侧新增 AsyncMethodBuilder/TaskAwaiter/YieldAwaitable 映射或 compat，**当前完全空白** |
| 主要受益者 | async lambda（今天 100% fail-fast）、ConfigureAwait 脏产物、async void 静默丢语义 | 追求真正 continuation/`thenCompose` 化、或 C38 async iterator 无法回避状态机时 |

### 1.4 目标与非目标

**目标**：

1. async lambda 不再 fail-fast：按 §2 D3 规则提升为命名 async 方法（v1 覆盖零捕获/this 捕获；局部捕获等 closure-lift）。
2. E02 残余/边界 async 局部函数有确定归宿（提升或显式诊断）。
3. `ConfigureAwait(bool)` 归一为无调用形态；不可等价形态显式报错，不产不可编译 Java。
4. async void 与一切不可降级 async 形态给**显式诊断**（消灭第 6 条静默面）。
5. Stub→Implemented 翻转、默认关、verify 全开、residual 闸门、排序/CLI 测试齐备；为 C38 在同一 pass 内预留扩展位（不改 id）。

**非目标**：

- 不重写 await/Task 的运行时语义（不消除 await、不合成 continuation、不做 CPS）；不解决 join 阻塞与 `CompletionException` 包装（记录为 Java 半区债务，§10 风险 1）。
- 不覆盖 `await foreach` / `await using` / `IAsyncEnumerable` / async iterator（C38）。
- 不补 CancellationToken 的取消可观察语义（compat 债务，只保证提升后 token 数据流不断）。
- 不改造主转换器（沿用 C04「本特性不动转换器」纪律；M0 若实测出转换器侧阻断缺陷，另开配套任务，不在本 pass 内夹带）。
- 不注入支撑库（轻量版目标全是 BCL Task；提升产物落在用户源码内）。

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据 / 备注 |
|---|---|---|---|
| D1 ★ | **形态选型** | **推荐形态①轻量版作为 M1+ 实施对象；形态②否决为本期目标，仅保留设计雏形（§4.8）与再评估触发条件（D11）**。三维度论证：① **频率**——await/Task 站点 1297/789/311 已由现有 join 通道承接（语义弱但不 fail-fast），唯一硬失败面是 async lambda（4966 lambda 的子集，占比待 M0 计数），轻量版恰好消灭该面；② **等价性成本**——重量版 ≈ 手写 Roslyn async lowering（builder/AwaitOnCompleted/ExecutionContext/异常封送/try-finally 跨挂起），catalog §4 风险 1 明确警告，且 C04 的 try-finally 原型方法论尚未在 async 上验证；③ **Java 半区就绪度**——CompletableFuture 映射族已在但仍是阻塞 join 模型，且返回包装路径在 HEAD 已死（§1.2 第 3 条）、Delay/ConfigureAwait 产物不可编译；**在 Java 半区没决定走 continuation 之前，C# 侧先消成无 await 状态机零收益**（状态机最终仍被映回阻塞调用），应先修/验证 Java 半区再谈重量版 | 轻量版不宣称完整 async 语义等价，只承诺：可编译性、fail-fast 消除、结果值等价、形态归一、显式错误边界。重量版的真正驱动力大概率来自 C38（async iterator 必然状态机）而非 C18 |
| D2 ★ | **落地节奏** | **M0 形态原型探针先行，门控后才准开 M1**。M0 不写生产代码，只交探针报告（§9）：sdk 形态分布计数、轻/重两原型各跑通代表性样例、HEAD 测试红绿基线、Java 半区不可编译点清单。M1 开工条件 = 探针报告未推翻 D1 | catalog 对 C18 的原话即「先做原型，见 §4」；与 C04「风险 2 要求原型先行」同纪律 |
| D3 | **async lambda 提升规则（轻量版）** | 按目标委托与捕获分四型：(a) **零捕获** → 提为类型内 `private static async` 命名方法，调用点换显式委托构造（`Task.Run(new Func<Task>(Cs2jAsync_<M>_<n>))`，显式 `new` 规避 `Action`/`Func` 重载歧义）；(b) **仅捕获 this** → 实例 `private async` 方法，无参外的捕获入参；(c) **目标委托返回 void（async void lambda，如事件处理器）** → 方法体提为 `private async Task Cs2jAsync_...(委托参数)`，调用点放一个**同步丢弃 wrapper**（`private void Cs2jAsync_..._w(...) { Cs2jAsync_...(...); }`），wrapper 是全 pass 唯一被 residual 白名单豁免的 async-void 等价物（不直接生成 async void）；(d) **捕获局部/形参（含被读）** → **v1 显式报错 CS2J1802 并原样保留**，等 closure-lift（C45） | C# 闭包捕获的是**变量不是值**：closure-lift 未做 display class 前，私自「拷入字段」会改变调度后可见值语义，属于错误优化，故宁拒不错。M0 须给出四型在 sdk 的比例，若 (d) 占比高且 C45 无期，触发 D11 重评 |
| D4 | **async 局部函数** | 信任 E02：链路上 E02 在最前段（conditional 之后），本 pass 看到的 async 局部函数理论上都已是命名 async 方法（async 修饰符保留，已核实 [LocalFunctionLiftRewriter.cs:524-L525](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftRewriter.cs#L524-L525)）。Analyzer 仍扫描**残余** async local function（E02 skip 门的漏网）：能套 D3 同规则提升则提升，否则 CS2J1804 显式报错（「未被 local-function-lift 提升的 async 局部函数」），禁止其流入 LambdaTransformer 之外的未知路径 | 转换器侧 `TransformLocalFunction` 漏置 `IsAsync`（§1.2 第 5 条）记入 Java 半区债务清单，由 M0 实测确认是否阻断，阻断则开转换器配套小修任务（不改本 pass 边界） |
| D5 | **ConfigureAwait 归一** | `expr.ConfigureAwait(false)` 与 `expr.ConfigureAwait(true)` → 整体替换为 `expr`（保留 await：`await expr`）；语义说明写进诊断 info：Java 无 SynchronizationContext，`ConfigureAwait(false)` 恰是 CompletableFuture 的自然模型，剥离不丢目标语义；`ConfigureAwait(true)` 在输入基线合法但 Java 侧无差异，同样剥离并计 Info（CS2J1830 桶）。**.NET 新形态 `ConfigureAwait(ConfigureAwaitOptions)`（SuppressThrowing 等）v1 显式报错 CS2J1808**；`ConfiguredTaskAwaitable`/`ConfiguredValueTaskAwaitable` 类型的局部/参数/字段 v1 同样显式报错（剥离 awaitable 包装会改类型，需逐案判定） | ConfigureAwait 实测 11 处，几乎全是 bool 形态；residual 闸门把残余 `ConfigureAwait` 标识符按违规计数（白名单=已发诊断的节点） |
| D6 | **async void 拒绝策略** | **用户书写的 `async void` 方法与 async void 局部函数：严格模式（默认）CS2J1803 Error，pass 失败、成员原样保留（树仍可编译）**；非严格降 Warning。诊断附修复建议：改 `async Task` 并在调用点丢弃（`_ = MAsync();`），或事件场景改用 D3(c) wrapper 形态。**唯一豁免**：pass 自己合成的同步 wrapper（带 `Cs2jAsyncNormalize` annotation，residual 白名单） | async void 的未观察异常与 fire-and-forget 在 Java 无对应契约，现状静默成功（§1.2 第 6 条）正是 catalog §4 风险 12 要消灭的「静默错误输出」面。Top-level statements / 旧版 `Main` 的 async void 形态基线即不合法，无需特判 |
| D7 | **Task / ValueTask 返回形态** | **不重写任何返回类型**：`async Task/Task<T>/ValueTask<T>` 原样保留，交既有类型映射（Task 族→CompletableFuture）。非 async 的 TAP 手写方法（返回 `Task.FromResult/CompletedTask/Delay`）本 pass 不触碰。已知缺口登记不解决：非泛型 `ValueTask` 无 CompletableFuture 映射（落薄 compat）、`Task.Delay→delayedExecutor` 形状错误（§1.2 第 7 条）——M0 fixture 在 Java 半区暴露后由转换器/映射侧任务收口 | 不做 `ValueTask→Task` 归一：两者重用/多次 await 语义不同，机械改写有正确性含义；await 单次性约束靠 C# 编译基线保证 |
| D8 | **CancellationToken 透传** | 本 pass 只保证**提升不断数据流**：① 提升后的命名 async 方法/async closure 类把外层 CT 实参作为形参/字段原样传递（按捕获规则走 D3(a)-(d)）；② 不删改任何 `ct` 实参（如 `Task.Run(f, ct)`）；③ 不在 C# 侧发明取消桥接。`Task.Run(…, ct)` 在 Java 半区无对应（`runAsync` 第二参是 `Executor`）等 compat/映射缺口列入 M3 清单 | compat CT 仅 `getNone/isCancellationRequested`，`Register/ThrowIfCancellationRequested/CancelAfter` 全缺；取消**可观察语义**是独立 compat 工作，C18 不背 |
| D9 | **try/catch/finally 中的 await** | **轻量版不改动语句结构**：C# 6 起 await 在 catch/finally 合法，保留即由 C# 基线编译保证；提升 lambda 跨 try 区域时，方法整体外提，区域结构随体搬运，不重写。已知不等价（不在本 pass 修复）：join 抛 `CompletionException`，Java catch 类型不匹配。处置：M3 fixture 在 Java 半区做「异常透传」对照并记录差异；是否在转换器侧加 unwrap（`join()` 包 try/catch 解 cause）是**转换器侧独立决策**，本文只登记需求 | 轻量版不在 C# 树插入任何 Java 专用 helper（产物必须是普通 C#，两半区原则） |
| D10 | **锁内 await / unsafe / fixed** | **基线优先 + 防御镜像**（同 C04 D4/D6 方法论）：M0 用 Roslyn net10/latest 探针实测合法边界表并回填本文。已知：`lock` 体内 await = **CS1996**（基线必拒），Analyzer 防御性 CS2J1805；ref struct 实例跨 await（CS4007 族）/ ref local 跨 await（CS9217 族）→ CS2J1806，策略同 C04（不跨点保留局部，跨点报错）；unsafe/fixed 内 await 的合法性以探针为准，不臆造编号。用户源码基线已报错时 pass 不抢诊断（§4.1 基线快照） | 锁内 await 在 C# 本就非法，无需设计「Monitor 跨挂起」表达；重量版若未来启动才需要等价的锁释放形态 |
| D11 | **重量版否决后的再评估触发条件** | 满足任一即重启形态评审（不自动开工）：① M0/sdk 计数显示 D3(d) 捕获型 async lambda 占比高到轻量版实际可消面 < 50%，且 C45 closure-lift 排期不可得；② Java 半区决定转向 continuation 模型（`thenCompose` 链），且转换器能从 await 形态直接生成链的方案被证伪；③ **C38 落地**：async iterator（`yield` 于 `IAsyncEnumerable`）无法回避状态机，届时状态机综合在 C38 子域先行、经同一 `AsyncNormalize` 目录实施；④ EF Core 画像（catalog §5.1）证明阻塞 join 在目标库上造成线程池饿死/死锁等真实故障而非理论瑕疵；⑤ 出现对 ExecutionContext/调度等价的硬性兼容需求 | 第三种潜在形态（CPS/支撑库 `Then` 链直接化，产物仍为 C# 但引 Cs2jAsync 支撑库）仅在 ② 触发时一并评估，本文不展开 |
| D12 | **与 closure-lift（C45）的接口** | **自带最小提升能力，但不发明 display class 语义**：v1 只做零捕获/this 捕获（无需共享存储）；捕获型一律 CS2J1802  defer。逻辑契约与 C04 §11 对齐冻结：捕获布局 `ISymbol → 字段名`、前缀 `_cs2jThis/_cs2jCap_/_cs2jLoc_`、closure 类名 `Cs2jAsyncClosure_<n>`、提升方法名 `Cs2jAsync_<宿主方法>_<n>`（均 ASCII、避 C#/Java 关键字、禁用 Roslyn `<>` IL 名）。C45 落地后，提升方法改为落在 display class 上，接受一次快照漂移 | 不产生程序集依赖；命名/活性口径（跨 await 点即提升）与 C04/C45 一致，未来由统一 Naming 服务收口 |
| D13 | **C38 姊妹关系 / pass 所有权** | **同一 pass id `async-normalize` 承载，不新建 pass**：本 pass 翻转时 descriptor catalogItems 收窄为 `"C18"`，Description 注明「C38 async streams reserved as a future extension of this pass」；C38 落地时在同一 descriptor 扩宽回 `"C18, C38"`，代码在 `AsyncNormalize/AsyncStream/*` 子目录加入（所有权表 §4.7），选项面加 `EnableAsyncStreams`（M1 不暴露 CLI）。await 站点分类器（`AsyncShapeClassifier`）作为 C38 将复用的内部服务，其 API 在 M2 冻结 | 遵循任务锚点定义；避免两个 pass 两套异步语义（catalog 727 行） |
| D14 | **默认开关 / 模式枚举** | `AsyncNormalizeOptions.Mode = Lightweight`（枚举含保留值 `Heavyweight`，选之即抛 `NotSupportedException`——M1 不预埋半成品路径）；翻转 Implemented 后**默认关**，长期 opt-in（门槛 §8）；Strict 默认 true，接 `PassChainConfiguration.Strict` | 与 record-readonly/array-method/iterator-lowering 的 opt-in 先例一致 |
| D15 | **表达式树 / dynamic / 自定义 awaiter 边界** | ① 表达式树内 async lambda 基线即编译错误，无需特判（共享「表达式树节点内不改写」判定纪律）；② `await dynamic`（dynamic 表达式上的 await）v1 CS2J1801 显式报错（SemanticModel 无法确定 awaiter 契约）；③ 自定义 task-like（自定义 `GetAwaiter()`/`INotifyCompletion`，含 `ValueTask` 高级用法以外的任意 awaitable）v1 CS2J1801 显式报错——轻量版保留 await 后转换器只认 CompletableFuture 面，放行必产不可编译 Java；④ `Task.Yield()` 站点 M0 计数，映射缺口（无 yield 等价）登记 Java 半区清单 | catalog §4 风险 12 第一原则：无法等价时显式报错，禁止注释/占位/透传 |

### 2.1 轻量版形态示意

零捕获 async lambda（M1 主力形态）：

```csharp
// 输入
public async Task RunAsync()
{
    await Task.Run(async () => await Task.FromResult(Compute()));
}

// 输出（同类型内新增命名方法；调用点显式委托构造）
public async Task RunAsync()
{
    await Task.Run(new Func<Task>(Cs2jAsync_RunAsync_0));
}

private static async Task Cs2jAsync_RunAsync_0()
{
    await Task.FromResult(Compute());
}
```

void 返回委托（事件式 async lambda，D3(c)）：

```csharp
// 输入
clock.Tick += async (s, e) => await RefreshAsync(e.Count);

// 输出（wrapper 丢弃 Task；等价 async void 的不观察语义，但可见、可审计、带 annotation）
clock.Tick += new Action<object, TickArgs>(Cs2jAsync_Refresh_0_w);

private void Cs2jAsync_Refresh_0_w(object s, TickArgs e)
{
    Cs2jAsync_Refresh_0(s, e); // fire-and-forget，与 C# async void lambda 同语义
}

private async Task Cs2jAsync_Refresh_0(object s, TickArgs e)
{
    await RefreshAsync(e.Count);
}
```

ConfigureAwait 归一（D5）：

```csharp
// 输入
var v = await store.GetAsync(k, ct).ConfigureAwait(false);
// 输出
var v = await store.GetAsync(k, ct);
```

## 3. 注册表与链位契约

### 3.1 现状（stub，已核实）

[PassStubCatalog.cs:229-L238](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L229-L238)：id `async-normalize`、目录 `.cs2j-async-normalize-src`、`PassStage.StateMachine`、`PassStatus.Stub`、`defaultEnabled: false`、`runAfterIds: ["closure-lift"]`、`needsSemanticModel=true`、无支撑库、catalogItems=`C18, C38 (C44 subtopic)`。`closure-lift` 自身同为 stub（[L152-L161](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L152-L161)，RunAfter=`ref-out-in`，catalogItems=`C32, C45`）。禁用时不进执行计划；显式启用在触碰文件系统前报 CS2J9001（[CliPreprocessChain.cs:131-L136](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L131-L136)，行为由 [PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs) 锁定；当前断言 stub 总数 = **23**，[L24](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L24)）。

### 3.2 Stub→Implemented 翻转清单（在 worktree 执行，本文只写不做）

| 动作 | 文件 | 内容 |
|---|---|---|
| 新建 | `src/CSharpToJava.Core/AsyncNormalize/*` | 见 §4 组件清单 |
| 新建 | `src/CSharpToJava.CLI/Preprocessing/Adapters/AsyncNormalizePassAdapter.cs` | 见 §5 |
| 改 | [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) | 从 `Stubs` 删除 async-normalize 条目；**保留 `Ids.AsyncNormalize` 常量**（PassCatalog/排序断言/工厂复用，同 C04 做法） |
| 改 | [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) | `Implemented` 表新增 descriptor：Status=Implemented、**defaultEnabled=false**、verifyBuild/Tests=true、目录名不变、**catalogItems 收窄为 `"C18"`**、Description 注明 C38 reserved；声明位置放在 iterator-lowering 条目（[L196-L205](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L196-L205)）之后 |
| 改 | [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs) | `BuildLegacyToggles` 加 `[PassStubCatalog.Ids.AsyncNormalize] = false`（[L77 iterator 先例](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L77)）；`CreateAdapter` switch 加 `Ids.AsyncNormalize => new AsyncNormalizePassAdapter(new AsyncNormalizeOptions(), …, config.Strict)`（[L213-L220](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L213-L220) 同款） |
| 改 | [PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs) | `Assert.Equal(23, …)` → **22**；CS2J9001 用例若引用 async-normalize，改用仍为 stub 的 id（建议 `closure-lift`） |
| 新建 | `tests/CSharpToJava.Tests/Preprocessing/AsyncNormalizeOrderTests.cs` | 排序回归（§3.4） |
| 不改 | Program.cs / 指纹 / 保留目录 / 过滤 | 通用链按 descriptor 驱动：执行目录经 `ExecutedIntermediateDirectoryNames` 自动回填（[CliPreprocessChain.cs:101-L108](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L101-L108)），指纹令牌按注册表全集 `pass:<id>=on/off` 生成；本 pass 无支撑库、不重指指纹源（`RepointsFingerprintToPassInput=false`） |
| 不新增 | 旧 `--no-async-normalize` 长开关 | M1 只走通用 `--enable-pass/--disable-pass`（与 generic-deepen/iterator-lowering 一致），不污染 ConvertProjectOptions |

### 3.3 stub 期拓扑方案：RunAfter 的 `closure-lift` 自己也是 stub 怎么办

这是本链位最需要写死的细节。四个要点：

1. **stub 条目上的 RunAfter 不动**：`closure-lift` 是真实未来依赖（D12），stub 节点参与 [PassRegistry](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs#L95-L133) 全量拓扑（Kahn 稳定排序，RunAfter 即文档即顺序契约；指向未知 id 启动期即报错）。禁用 stub 不执行、不产目录、零成本。
2. **翻转时不照抄 RunAfter=closure-lift，沿用 C04 已验证先例**：注册表由 `Implemented.Concat(Stubs)` 构成（[PassCatalog.cs:242-L243](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L242-L243)），声明序兜底使 stub 在全序中整体排在全部 Implemented 之后；`closure-lift` 的全序位置实际落在 eliminate-goto **之后**（它要等 java-keywords 的 stub 依赖链排空才被 Kahn 放出）。若 Implemented descriptor 照抄 RunAfter=closure-lift，async-normalize 会排到 goto 之后——轻量版不发射 goto，不致命但违背 StateMachine 阶段意图，且给未来重量版埋雷。**C04 翻转时已把 iterator-lowering 的 RunAfter 从 `closure-lift` 改为最近已启用前驱 `make-readonly`**（[PassCatalog.cs:202](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L202) 实证，较其 spec §3.2 的原定方案做了这一调整）。本 pass 照办：Implemented descriptor `runAfterIds = ["make-readonly"]`，并在注释中保留「closure-lift 落地后补边」的未来契约。
3. **轻量版不给 eliminate-goto 加边**：本 pass 产物无 goto/label（方法外提 + 调用点替换 + 表达式剥离），不存在 C04 D2 那样的 goto 衔接需求。**仅当 D11 触发、形态②实施时**，才同步做两件事：async-normalize 补 goto 前边、eliminate-goto 的 runAfterIds 加 async-normalize（[StateMachineBuilder.cs:1668-L1690](file:///d:/code/cs2j/src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs#L1668-L1690) 已有 async 返回类型解包的辅助逻辑可借形）。
4. **C45 closure-lift 未来翻转时的合流**：closure-lift 的 Implemented descriptor 应声明在 iterator-lowering 之前、锚 `ref-out-in`/`make-readonly` 之间；落地时给 iterator-lowering 与 async-normalize **同时补 RunAfter=closure-lift 边**，三方接受一次排序快照漂移并同步更新排序测试。可选增强（非阻塞，C04 spec §3.3 已提议）：PassPlanResolver 增加「透传锚点」汇总，在 `--list-passes` 提示「async-normalize 实际紧随 iterator-lowering；closure-lift 尚未实现」。

### 3.4 顺序契约（测试锁死，不靠人工记忆）

排序回归断言：

- `index(make-readonly) < index(async-normalize)`；
- `index(async-normalize) < index(eliminate-goto)`（声明序：async-normalize 位于 iterator-lowering 之后，二者无数据依赖——async 方法内出现 yield 基线即非法，C38 async iterator 另算）；
- 默认计划**不含** async-normalize；`--enable-pass async-normalize` 后计划**含**且 stub 总数 22；
- 与 iterator-lowering 同时启用时两者相对序为 iterator → async（声明序保证，不另加边）。

## 4. Core 组件设计

目录 `src/CSharpToJava.Core/AsyncNormalize/`（internal 实现 + 一个公开入口，组织对齐 [IteratorLowering/](file:///d:/code/cs2j/src/CSharpToJava.Core/IteratorLowering/IteratorLoweringPreprocessor.cs)）：

```
AsyncNormalizeOptions.cs        # Mode(Lightweight|Heavyweight保留) / Strict / 各规则开关
AsyncNormalizeStatistics.cs     # 通用 5 字段 + pass 专有计数（§4.6）
AsyncNormalizeDiagnostic.cs     # Severity/Code/Message/FilePath + record with + ToMsBuildString
AsyncShapeClassifier.cs         # await 站点/lambda/local-fn/ConfigureAwait/async-void/awaitable 分类（C38 复用服务）
AsyncNormalizeAnalyzer.cs       # 整树分析：发现 + 基线快照 + 合法性闸门 + 提升计划 + skip 集
Lambda/LambdaLiftPlan.cs        # 一个 async lambda 的提升计划（目标委托签名/型别/捕获布局/命名）
Lambda/AsyncLambdaLifter.cs     # 按计划外提方法、（D3(c)）合成 wrapper、调用点改写
Await/AwaitShapeRewriter.cs     # ConfigureAwait(bool) 剥离；其余 await 原样保留
AsyncNormalizeNaming.cs         # Cs2jAsync_/Cs2jAsyncClosure_/_cs2j* 确定性命名与防撞
AsyncNormalizePreprocessor.cs   # 入口：dirty check → analyze → rewrite → format → residual 闸门
```

重量版预留（**M1 不创建文件**，仅本设计占位，见 §4.8）：`AsyncStateMachine/AsyncCfgBuilder.cs`、`AsyncStateMachinePlan.cs`、`AsyncStateMachineRewriter.cs`。C38 预留（C38 任务才创建）：`AsyncStream/AwaitForEachRewriter.cs`、`AwaitUsingRewriter.cs`、`AsyncEnumerableTypePlan.cs`。

### 4.1 Analyzer

- 入口走标准脊柱（[ProjectPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L117-L269) 已建好整树 compilation，parse 选项 latest、`allowUnsafe: true`）。
- 发现面（语法 + SemanticModel 双判）：① `ParenthesizedLambdaExpressionSyntax`/`SimpleLambdaExpressionSyntax`/`AnonymousMethodExpressionSyntax` 带 `AsyncKeyword`（匿名方法理论上已被 E05 消成 lambda，残余按 CS2J1807 处理）；② 残余 `LocalFunctionStatementSyntax` 带 async（D4）；③ `MethodDeclarationSyntax` 带 async 且返回 void（D6）；④ 全部 `AwaitExpressionSyntax`（计数 + 供 C38 未来分类）；⑤ `ConfigureAwait` 调用（区分 bool 实参 / ConfigureAwaitOptions / 非 bool 形态）；⑥ awaitable 类型判定（D15）；⑦ await 站点合法性（lock/unsafe/fixed，D10）。
- **基线优先原则**（借 C04 §4.1）：整树先取编译诊断快照；Roslyn 已拒的（CS1996/CS4007/CS9217 族、表达式树内 async lambda 等）不二次报错；CS2J18xx 只覆盖「基线没拦住但本 pass 无法归一」的防御层。
- 每个 lambda/局部函数产出一个 `LambdaLiftPlan` 或一条 skip 诊断；skip 集在任何重写前确定（静态、幂等）；严格模式下有 Error skip 即整体失败（成员原样保留，树仍可编译）。
- 委托目标签名（参数类型表/返回 `void`/`Task`/`Task<T>`）经 `SemanticModel.GetTypeInfo(lambda).ConvertedType`（`INamedTypeSymbol` 的 `InvokeMethod`）取得，wrapper 与显式委托构造都依赖它，禁止语法猜测。

### 4.2 LambdaLifter（D3 规则的执行体）

- 提升方法归属：零捕获 → 声明类型的 `private static`；仅 this 捕获 → `private` 实例方法；显式接口实现/泛型方法/泛型声明类型的处理借 C04 §4.4 泛型条款（状态/提升类携带方法类型参数，外层类型参数前置）。
- 调用点改写：lambda 节点 → `new <委托类型名>(<提升方法名或 wrapper 名>)`；委托类型名由符号渲染（`Func\`1`→`Func<T>`、`Action\`2`→`Action<T1,T2>`、用户自定义委托用其名）。
- wrapper（D3(c)）与提升方法成对生成，均挂 `SyntaxAnnotation("Cs2jAsyncNormalize")`，residual 闸门凭 annotation 白名单识别。
- **不做**局部/形参捕获的 display class（D3(d) 直接 skip 诊断）；不发明变量共享。
- 嵌套 async lambda：先按语法序外提内层再外提外层，内层对在外层提升方法内成为其私有姊妹方法（同归属类型，序号确定性分配）；M2 覆盖。
- 格式化与校验：产物经 AdhocWorkspace `Formatter.Format`（iterator/record-readonly 同款）。

### 4.3 AwaitRewriter（轻量版「归一」的实际幅度）

- 唯一强制改写：`InvocationExpressionSyntax` 且符号为 `Task.ConfigureAwait(bool)`/`ValueTask<T>.ConfigureAwait(bool)`（含扩展方法形态）→ 替换为 receiver 表达式（D5）；实参非常量 bool 但类型为 bool 的合法局部形态同样剥离（语义不依赖取值——Java 侧无上下文可捕获），计 Info。
- **await 关键字与其余 await 表达式一律不动**（这是轻量版与重量版的分水岭）。
- 幂等：重跑无 ConfigureAwait 即 clean；annotation 只挂合成节点，不二次处理。

### 4.4 捕获/命名契约（与 C04/C45 对齐，逻辑契约不建共享程序集）

- `LambdaLiftPlan.CaptureLayout`：被捕获 `ISymbol → 字段名` 只读表（本期实际只会出现 this；表结构先冻结）。
- 命名（[AsyncNormalizeNaming.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/AsyncNormalize/AsyncNormalizeNaming.cs) 借 [LocalFunctionLiftNaming](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftNaming.cs) 防撞手法、不共享代码）：提升方法 `Cs2jAsync_<宿主成员名>_<同类型内序号>`；未来 closure 类 `Cs2jAsyncClosure_<n>`；wrapper 后缀 `_w`；字段 `_cs2jThis/_cs2jCap_<名>/_cs2jLoc_<名>`；临时量 `_cs2jTmp_<n>`。序号在**类型作用域**按源 span 排序分配，跨文件/跨次运行稳定；碰撞追加 `_2,_3…`；ASCII、避 C# 与 Java 保留字；不用 Roslyn `<>` IL 名。
- 活性口径与 C04 一致：跨 await 点存活即视为提升候选（本期无字段发射，仅文档化）。

### 4.5 Options

```csharp
public enum AsyncNormalizeMode { Lightweight, Heavyweight /* 保留值，选择即抛 NotSupportedException */ }

public sealed record AsyncNormalizeOptions
{
    public bool Strict { get; init; } = true;
    public AsyncNormalizeMode Mode { get; init; } = AsyncNormalizeMode.Lightweight;
    public bool LiftAsyncLambdas { get; init; } = true;
    public bool StripConfigureAwait { get; init; } = true;
    public bool RejectAsyncVoid { get; init; } = true;
    // C38 预留：internal bool EnableAsyncStreams（M1 不暴露，不存在于公有构造面）
}
```

### 4.6 Statistics / Diagnostic

- 通用 5 字段（`FilesScanned/FilesTransformed/FilesSkippedClean/FilesDirtyNoRewrite/ResidualCount`）+ 专有：`AsyncMethodsSeen`、`AwaitSites`（触碰计数，轻量版不清零）、`AsyncLambdasLifted`（分 ZeroCapture/ThisOnly/VoidWrapper 桶）、`AsyncLocalFunctionsLifted`、`WrappersGenerated`、`ConfigureAwaitStripped`、`AsyncVoidRejected`、`CapturedAsyncLambdasDeferred`、`UnsupportedAwaiters`、`SkippedUnsupported`（按原因分桶）。
- 诊断码（码位只追加）：

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J1801 | Error/Warning | 不支持的 awaitable：自定义 task-like（自定义 GetAwaiter）/ `await dynamic` |
| CS2J1802 | Error/Warning | async lambda 捕获局部/形参，closure-lift 落地前无法等价提升（D3(d)） |
| CS2J1803 | Error/Warning | 用户 async void 方法/局部函数（D6） |
| CS2J1804 | Error/Warning | 残余未被 E02 提升且本 pass 无法提升的 async 局部函数（D4） |
| CS2J1805 | Error/Warning | 非法 await 站点防御镜像（lock 内 await＝CS1996；unsafe/fixed 编号 M0 回填） |
| CS2J1806 | Error/Warning | ref struct/ref local 跨 await 边界（CS4007/CS9217 族） |
| CS2J1807 | Error/Warning | 残余 async 匿名方法 / 不支持的 async lambda 目标位置 |
| CS2J1808 | Error/Warning | `ConfigureAwait(ConfigureAwaitOptions)` / `ConfiguredTaskAwaitable` 强类型形态（D5） |
| CS2J1810 | Error | residual 闸门：输出仍有 async lambda / ConfigureAwait / 未豁免 async void |
| CS2J1820 | Error/Warning | restore/build/test 失败（适配器钩子记录） |
| CS2J1830 | Info | 归一明细（剥离的 ConfigureAwait、提升站点，verbose 可见） |

非严格模式下 1801-1808 为 Warning（原样保留、计 skip 桶）；严格（默认）为 Error，pass 失败。

### 4.7 为 C38 预留的规则接口与文件所有权表

| 文件 / 面 | C18 所有权（本锚点） | C38 未来所有权 |
|---|---|---|
| `AsyncShapeClassifier.cs`（await 站点/awaitable/异步关键字分类） | **拥有**；M2 冻结 internal API：`ClassifyAwaitSite`、`IsTaskLike(ITypeSymbol)`、await foreach/using 关键字探测钩子先以内部虚方法/partial 形态留口（不预埋实现） | 消费，不改签名；新增分类维度走新增方法（非破坏） |
| `AsyncNormalizeOptions/Statistics/Diagnostic.cs` | 拥有；Options 为 C38 预留 internal 位但不暴露 CLI；诊断码段 18xx 内预留 C38 子段（1840-1849） | 子段内追加；不改既有码义 |
| `AsyncStream/*`（await foreach/await using/IAsyncEnumerable 类型计划） | **不创建** | C38 任务新建并全部拥有 |
| descriptor catalogItems | `"C18"` | 扩宽 `"C18, C38 (C44 subtopic)"`，改 PassCatalog 一行 |
| 注册表三热文件（PassStubCatalog/PassCatalog/PassChainFactory） | 翻转时改（§3.2） | 翻宽时再改；冲突解法 §11 |

### 4.8 重量版设计雏形（仅备查；M1 不实施）

若 D11 触发，仿 C04 状态机综合与 Roslyn `async` lowering，方法体改写目标：

- 嵌套 `private sealed class Cs2jAsyncStateMachine_<M>_<n> : IAsyncStateMachine`（+ 泛型携带），字段：`int _cs2jState`、`AsyncTaskMethodBuilder<T> _cs2jBuilder`（非泛型 Task 用非泛型 builder）、全部 awaiter 字段、this/形参/跨 await 局部（活性同 C04 §4.3）；
- `MoveNext()`：`try { switch(_cs2State){…} } catch(Exception ex){ _cs2jBuilder.SetException(ex); return; }`；每个 await 点翻译为 `_cs2jAwaiter_N = expr.GetAwaiter(); if(!_cs2jAwaiter_N.IsCompleted){ _cs2jState=N; _cs2jAwaiter_N.OnCompleted(MoveNext); _cs2jBuilder.AwaitUnsafeOnCompleted(ref _cs2jAwaiter_N, ref this); return; …resume… }`；完成路径 `_cs2jBuilder.SetResult(value)`；
- 原方法体变 `var sm = new …{…}; _cs2jBuilder.Start(ref sm); return _cs2jBuilder.Task;`；
- try/finally 跨挂起的清理时序 = C04 §4.6 同一区域机制（await in catch/finally 合法、lock 非法已由基线拒）；产出 goto 交 eliminate-goto（故需 §3.3 第 3 条的补边，StateMachineBuilder 的 async 返回解套辅助可借形）。

**阻断性前提（也是本期否决它的原因）**：① Java 半区必须新增 `AsyncTaskMethodBuilder/TaskAwaiter/YieldAwaitable/IAsyncStateMachine` 映射或全套 compat（当前零存在）；② 需证明 ExecutionContext/`Task.Yield`/自定义 builder（IValueTaskSource）/`ValueTask` 单次 await 约束在目标 Java 形态下可表达；③ 成本与风险等同重写编译器 lowering（catalog §4 风险 1）。M0 的 P-Heavy 原型只要求直线单 await 方法跑通 .NET 半区行为等价并产出「compat 缺口清单」，用于量化而非承诺实施。

## 5. CLI 适配器与集成

- 新建 `Adapters/AsyncNormalizePassAdapter.cs : ProjectPassAdapter`，**走默认脊柱**（不 override `ExecuteAsync`，对齐 [IteratorLoweringPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/IteratorLoweringPassAdapter.cs)）：
  - `Descriptor` 取 PassCatalog 实现项；`RewriteFileAsync` 调 `AsyncNormalizePreprocessor.Preprocess(source, SemanticModel, options)`，Changed=false 返回 null 走字节级原样复制（BOM 由 runner 保）；
  - `DeleteBuildOutputsAfterRestore = true`；VerifyBuild/VerifyTests 透传 chain config（descriptor 默认均 true，可用 `--no-verify-build/tests` 关）；
  - `AfterFilesAsync` 做 **residual 闸门**：跨文件汇总后扫描产物中残余「async 修饰 lambda / 未豁免 async void / ConfigureAwait 调用」（白名单=已发 skip 诊断节点 + `Cs2jAsyncNormalize` annotation 的合成 wrapper），违规在严格模式报 CS2J1810（仿 iterator 适配器 [L75-L89](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/IteratorLoweringPassAdapter.cs#L75-L89)）；
  - `OnRestoreFailed/OnBuildFailed/OnTestsFailed/HasErrors/BuildChainSummaryLine/BuildChainDiagnosticLines` 同构实现；摘要行：`async-normalize: N lambda(s) lifted, K wrapper(s), C ConfigureAwait stripped, A await-site(s) kept, S skipped, R residual`（明确「await kept」，避免误读为已消异步）。
- 启用方式：通用 `--enable-pass async-normalize` / `--disable-pass async-normalize`；独立 `preprocess --pass async-normalize -s .. -d ..`（不新增专属 verb、不加旧长开关）。
- 支撑库：**None**（轻量版只用 BCL Task；重量版未来才可能需要 builder 支撑库，descriptor 届时再改）。
- 指纹/保留目录：零手工接线（同 C04 §5：目录名进 `ExecutedIntermediateDirectoryNames`、preserve/过滤/`pass:async-normalize=on/off` 令牌自动生效）。
- `--list-passes`：翻转后展示 Implemented/default-off/catalogItems=C18；C38 意图在 Description 行可见。

## 6. 诊断策略（不可归一形态显式报错）

- 原则：**每个 async 构造只有两个归宿——被归一/提升，或被显式诊断**。不存在「注释掉/透传/偷偷降级」第三种；特别地，本 pass **不禁止 await 残留**（轻量版 await 是预期产物），residual 面只针对 async lambda、ConfigureAwait、未豁免 async void 三类。
- 三层防线：① Analyzer 静态分类与 skip 集（重写前定型）；② Rewriter 后 residual 闸门（CS2J1810）；③ 中间树 `dotnet build`（语法/类型疏漏在 restore+build 硬失败，CS2J1820，产物不流入 Java 转换）。
- 基线已有编译错误时不与编译器抢诊断（D10），日志仅标注「pass 跳过：目标树基线不编译」。
- 诊断带文件、行、列、宿主成员完全限定名与处置建议（如 CS2J1803 建议「改 async Task + 调用点 `_ =` 丢弃」；CS2J1802 建议「等 closure-lift 或手工提取为带参方法」）；MSBuild 格式与既有适配器一致。
- 对「Java 半区已知不完整但 C# 侧合法」的站点（`Task.Delay`、非泛型 ValueTask、CT 高级 API、自定义 awaiter 之外的普通 await）：**C# 半区不报错**，计入 M3 报告的跨半区缺口清单（两半区职责分界，不把 Java 侧欠账变成 C# pass 的 Error）。

## 7. 测试计划

### 7.1 Core 单测（建议 `tests/CSharpToJava.ProjectTests/AsyncNormalizeUnitTests.cs`，内联源码→compilation→Preprocess→文本断言 + 输出可编译断言，仿 JavaKeyword/Iterator 单测）

1. dirty/clean：无任何 async 面的文件 Changed=false；仅含普通 await 的 async 方法文件**零改写**（await 保留，轻量版边界）。
2. async lambda 零捕获：`Task.Run(async () => …)`（Func<Task>/Func<Task<T>>/Action 三委托型）、表达式体与块体、泛型方法/泛型类型宿主、显式委托 `new Func<…>` 调用点形状、重载消歧（Action vs Func 同参）。
3. async lambda this-only：提为实例方法；static 宿主中 this-only 不可达用例（语义防护）。
4. async lambda 捕获局部/形参/foreach 变量：CS2J1802（严格 Error、非严格 Warning、原样保留、无部分改写）。
5. void 返回委托（事件式 async lambda）：wrapper + Task 方法成对、annotation 白名单、residual 不误伤；嵌套 lambda（async 套 async、async 套同步）。
6. async 局部函数：E02 已提升形态零改写；手工构造残余 async local function（模拟 E02 skip）→ 提升或 CS2J1804。
7. ConfigureAwait：`false`/`true`/非常量 bool 变量实参 → 剥离为 receiver，await 保留；链式 `.ConfigureAwait(false).ConfigureAwait(false)`；语句位置；`ConfigureAwait(ConfigureAwaitOptions)` → CS2J1808；`ConfiguredTaskAwaitable` 强类型局部 → CS2J1808。
8. async void：方法与局部函数 → CS2J1803；合成 wrapper 不被误报；严格/非严格级别翻转。
9. try/catch/finally：await 于 try/catch/finally 的方法体零结构改写且仍可编译；lambda 跨 try 区域外提后体搬运正确。
10. 非法站点防御：lock 内 await（CS2J1805）；ref struct 跨 await（CS2J1806，用合成用例）；`await dynamic`/自定义 awaiter（CS2J1801）；残余 `async delegate {}`（CS2J1807）。
11. Task.Run/CT：`Task.Run(async () => …, ct)` 提升后 ct 实参不丢；提升方法签名/实参顺序确定性。
12. 确定性：同输入两次输出字节一致；成员顺序打乱后命名稳定；与用户同名方法防撞（`_2`）；输出无 `<` IL 风格名。
13. 基线快照：含 CS1996/CS4007 等既有编译错误的树不重复发诊断（仅观察桶）。
14. 每个正例额外断言：输出零编译错误；零 async lambda、零 ConfigureAwait（除非白名单）；**await 计数与输入一致**（轻量版指纹）。

### 7.2 专项 fixture sln

新建 `tests/SampleAsyncSolution/`（Lib + App + xUnit TestProject + `.sln`，Content Include 进 ProjectTests，TFM net10.0，仿 SampleJavaKeywordSolution/SampleRecordSolution 布局）：

- Lib：零捕获/this-only async lambda、事件式 async lambda（wrapper）、ConfigureAwait 混用、`Task.Run`/`FromResult`/`WhenAll`/CT 透传、await 于 catch/finally、async 局部函数（经 E02）、泛型异步方法。
- TestProject **.NET 半区**：任务结果值、异常透传、取消令牌可见性、wrapper 不观察异常的行为锁定（记录 C# 真值）。
- 项目级断言：pass Success、统计计数、residual=0；中间树 `dotnet build -c Release` 与 `dotnet test -c Release` 退出码 0。
- **Java 半区**（catalog §5.1 两半区原则）：中间树走主转换，跑同一套行为测试，明确分级——
  - **M1 要求全绿**：已完成 Task 的结果值、ConfigureAwait 剥离后可编译、提升方法签名映射；
  - **M3 记录不强求**：未完成 Task 的并发/调度（join 阻塞模型差异）、`catch` 异常类型匹配（CompletionException 包装）、`Task.Delay`/非泛型 ValueTask/CT 高级 API——输出差异清单，作为转换器/compat 侧后续任务的输入。

### 7.3 闸门与回归

- 中间树 **Release build + test 全绿**（descriptor verify 默认开）；全仓 slnx `-c Release` build/test 全绿。
- residual 闸门测试：「无法提升 + 严格模式」断言 pass 失败且无部分改写；wrapper annotation 白名单反例。
- 链顺序：§3.4 排序回归（含 23→22 注册表计数、CS2J9001 改用 closure-lift）。
- 一致性/CLI：`ProjectPreprocessorsConsistentTests` 按需追加；CLI 端到端增 `--enable-pass async-normalize`（默认计划不含、显式启用执行、`.cs2j-async-normalize-src` 保留目录出现、再跑 convert-project 命中缓存、`--list-passes` 状态正确）。
- 存量测试复核（M0 出基线，M1 后处置）：[AsyncAwaitTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ConversionTests/Categories/AsyncAwaitTests.cs)、[ConfigureAwaitConversionTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ConfigureAwaitConversionTests.cs) 按真实红绿重写/保留断言；[GetResultConversionTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/GetResultConversionTests.cs) 保持负例语义；两个空占位类（StreamAsyncTokenTests/TaskRunGenericMappingTests）在本 pass 补 Task.Run async lambda 用例（StreamAsync* 的异步流用例归 C38）。

## 8. 默认开启验收门槛（建议长期 opt-in）

仅当全部满足才把 `defaultEnabled` 翻 true：

1. **M0 探针闭环且未推翻 D1**（§9）；M1-M3 全部出口达成，SampleAsyncSolution 两半区结论成文。
2. 全仓 Release build/test 全绿；启用范围 residual=0；所有 skip 站点有显式诊断且分类清楚。
3. csharp-sdk 基线（json/regex/uri/xml-tiny 四子集）以 `--enable-pass async-normalize` 跑：中间树 build/test **不退化**；给出 async lambda 四型（D3）实际归一率、ConfigureAwait 剥离数、async void/自定义 awaiter 命中率。
4. **Java 半区阻断清单清零或显式签收**：至少包括——返回包装死路径（§1.2 第 3 条）的恢复或等效修复、`ConfigureAwait` camelCase 漏网（由本 pass C# 侧消除，验收以转译后无 `configureAwait` 为准）、`Task.Delay` 形状错误、CompletionException 解包策略定论。未签收项必须有「目标画像不触发」的数据证据，而非默认忽略。
5. opt-in 观察期至少跨一次主版本回归窗口（沿用 record-readonly/array-method/iterator-lowering 先例）。

建议：即便过门槛也**保留长期 opt-in 能力**（与链上其它状态机/高结构改写 pass 一致），默认开启与否取决于 EF Core 画像（catalog §5.1）中异步并发的真实强度；没有并发等价承诺前，默认开只换来「可编译面扩大」，需与 Java 半区方向决策同步。

## 9. 里程碑（★M0 形态原型探针先行，过闸才准开 M1）

| 里程碑 | 内容 | 出口 |
|---|---|---|
| **★M0 形态原型探针（不写生产代码）** | 三件交付：① **计数**——在 csharp-sdk 快照上跑分类脚本：async lambda 总量及四型分布（零捕获/this-only/void 委托/捕获局部）、async 局部函数残余、async void、ConfigureAwait bool/Options、await in catch/finally/lock、`await dynamic`、自定义 awaiter、非泛型 ValueTask、Task.Yield、CT 用法；② **双原型**——P-Light：手改 3-5 个代表文件走通「lambda 提升 + ConfigureAwait 剥离」，中间树 build 绿、主转换可编译；P-Heavy：1 个直线单 await 方法手写 Roslyn 形态状态机，.NET 半区行为等价，列出 Java/compat 缺口清单与体量估算；③ **基线实测**——HEAD 跑 AsyncAwaitTests/ConfigureAwaitConversionTests 等现有测试簇定红绿，回填 §1.2 第 3/5 条与 D10 合法边界表（net10/latest Roslyn 探针编号） | 探针报告评审，形成 D1/D11 正式记录：轻量版过闸开 M1；若数据推翻轻量版（D11 条件），回到形态评审 |
| **M1 最小可编译（轻量版）** | Options/Statistics/Diagnostic/Classifier/Analyzer/AwaitRewriter/AsyncLambdaLifter（D3(a)(c) 零捕获+this+wrapper）/Naming/入口 + 适配器 + Stub→Implemented 翻转（默认关，catalogItems=C18）+ residual 闸门 + 排序/CLI 测试 + 单测 1-4、7、11-14 | SampleAsyncSolution .NET 半区 Release build/test 绿；Java 半区 M1 分级项绿；转译产物无 async lambda/ConfigureAwait |
| **M2 嵌套与残余形态** | 嵌套 async lambda、泛型宿主/泛型方法、显式接口实现中的 async、D4 残余 async 局部函数补提升、`AsyncShapeClassifier` API 冻结（C38 接口冻结点）；评估 closure-lift 若先落地则接入 display class（D3(d) 解锁） | 单测 5-6、8-9 全绿；与 C45 的合流演练（或 CS2J1802 defer 率有数据） |
| **M3 异常/取消/Java 半区对照** | fixture Java 半区全量对照，产出差异清单与转换器/compat 侧任务（join 解包、Delay、ValueTask、CT）；CS2J1801/1805/1806/1808 反例齐；非严格模式定级验证 | §8 第 4 条清单成文并签收/挂账 |
| **M4 画像与默认化评审** | sdk 四子集 + EF Core 画像（catalog §5.1 前置行动）跑频率/skip 率；D11 重量版触发条件复核；C38 接口移交；默认开/继续 opt-in 决策记录 | §8 五条门槛结论成文 |

## 10. 风险

1. **（最高）Java 半区是伪异步且现状有回退**：join 阻塞模型无并发等价；返回包装置位点在 `9f09f426` 后已死（§1.2 第 3 条）；`ConfigureAwait` 经 camelCase 漏网、`Task.Delay→delayedExecutor` 形状错误；多个文本断言测试可能在 HEAD 已红。缓解：M0 先实测红绿与可编译性，不用「测试存在」当作「半区就绪」；轻量版只承诺可编译/结果值，承诺范围写进文档与验收；Java 侧修复另立任务，不夹带。
2. **形态选型被数据推翻**：M0 可能发现捕获型 async lambda 占主导，轻量版可消面小。缓解：M0 门控先于 M1；D11 预置重量版/CPS 再评估条件；M1 之前不动注册表状态（stub 零成本）。
3. **（catalog §4 风险 1）重量版等价性成本**：builder/ExecutionContext/SynchronizationContext/异常封送/try-finally 跨挂起，等同重写编译器 lowering。缓解：本期否决，仅留 §4.8 雏形与 M0 P-Heavy 量化。
4. **closure-lift 依赖悬空**：D3(d) 捕获型 defer 可能长期占比高，导致「fail-fast 变显式报错但不能转」。缓解：CS2J1802 诊断带手工修复法（提取带参方法）；M2 接入 C45 或按画像决定是否做范围内最小 display class（须连同外层访问改写，不做按值拷贝）。
5. **E02 交互**：E02 对 async 局部函数禁 ref 捕获快路但保留提升；残余 skip 门、`AllowRefInAsyncCallers` 选项（[LocalFunctionLiftOptions.cs:7](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftOptions.cs#L7)）与本 pass 的分工需在 M2 对齐，避免双重 skip/双重诊断。
6. **异常与锁语义错觉**：轻量版不改 try/catch/finally，团队可能误把「编译通过」当「语义等价」；lock 内 await 等基线错误在不做 build 的直连转换路径上可能漏网（直连入口无编译基线快照）。缓解：Analyzer 不依赖基线也自走一遍 D10 防御判定；fixture 两半区分级结论公开。
7. **C38 耦合**：若 C38 在本 pass M1 之前急需（EF Core 异步流画像高），状态机选型可能被迫提前（D11 ③）。缓解：AsyncShapeClassifier M2 冻结、AsyncStream 目录所有权预留，C38 可在同一 pass 平行扩文件。
8. **（catalog §4 风险 11）链耗时**：verify 默认开 + 1297 await 站点规模。缓解：verify 可关；指纹随注册表自动接入；M0 起度量增量耗时。
9. **链位回归**：Kahn 声明序兜底对 Implemented/Stubs 拼接顺序敏感（§3.3）；未来 C45 翻转会移动本 pass 全序位置。缓解：排序测试锁双边断言；补边时同步改测试。
10. **自定义 awaiter / 新 BCL 形态误判**：SemanticModel 对扩展方法 GetAwaiter、`ConfigureAwaitOptions` 枚举的判定需精确，误拒影响过宽、误放产不可编译 Java。缓解：D15 宁拒不错 + Info 桶回归盘点。
11. **LinqRewriter/合成面**：其 Phase 2 合成的是 yield 局部函数（C04 风险 7），需 M0 顺带确认是否合成 **async** 局部函数；若合成，发生在全部预处理之后、本 pass 够不到，按 C04 同款「先复现再定责」处置。

## 11. 并行协调

catalog §3.3（[L905](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L905)、[L911](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L911)）把 E02/E05/C32/C45/C04/C18 并为「闭包/状态机提升族」、C18+C38 并为「异步族」。协调约定：

1. **C45 closure-lift（锚点依赖）**：本 pass RunAfter 的真实对象；v1 无程序集依赖（D12）；逻辑契约（CaptureLayout、`_cs2j*`/`Cs2j*` 命名、跨 await 活性口径）先行冻结。推荐合流顺序：C45 落地 → 本 pass M2 接入 display class（解锁捕获型）；若 C45 晚于 M4，凭 M0 数据决定是否做范围内最小提升。C45 翻转时按 §3.3-4 同时给 iterator/async 补边并接受快照漂移。
2. **C04 iterator-lowering（状态机族共享）**：不碰 `IteratorLowering/*` 任何文件；共享仅限方法论（基线快照、skip 先于改写、区域机制、命名防撞）与未来可能的 CfgBuilder 抽取（抽取为联合后续 PR，本期不做）。C04 已落地的注册表翻转先例（RunAfter 透传到 make-readonly、goto 补边）是本 pass §3.3 的直接依据。两 pass 无数据依赖（async 方法体内 yield 基线非法；async iterator 归 C38）。
3. **C38 姊妹（同一 pass）**：本 pass 是锚点，形态选型定稿后 C38 依赖同一选型扩展；文件所有权 §4.7 已定；C38 不新建 pass/id，翻转 catalogItems 一行扩宽；共享 await 分类服务 API 在 M2 冻结。C38 若先于 M4 需要重量状态机（async iterator），走 D11 ③，在 `AsyncStream/` 内局部启用，不回改 C18 轻量规则。
4. **E02 local-function-lift**：链位天然在前（conditional 之后，本 pass 在 StateMachine 段），async 局部函数由其先提；本 pass 只补残余（D4）。M2 对齐 async 相关 gate（gate #8、`AllowRefInAsyncCallers`）与诊断归属，避免两 pass 对同一函数双报错。
5. **热文件冲突解法**：与 C45/C04/C38 的预期冲突仅三个注册表文件（PassStubCatalog 删条、PassCatalog 加条/边、PassChainFactory 加 case）外加 `PassStubCatalogTests` 计数——**纯文本冲突、无语义冲突**。规则固定：后合者保留各方 descriptor 与各自边，stub 计数按合并后实际数改（本 pass 翻转后 23→22；若 C45/C38 同窗口翻转，计数以注册表测试为准逐次调整）；Program.cs 不受影响（registry 驱动），不存在 Program.cs 级冲突。

## 12. Worktree 分配（只写不执行）

- 分支：`feature/preproc-c18-async-normalize`
- 目录：`.worktrees/preproc-c18-async-normalize`
- base：`953293b9`（已只读核实：`953293b908e9143437c45f287fb4277e890c398e` — "docs: update doc base commit"，当前 HEAD）
- 创建命令（**本设计任务不执行**；由实施环节在批准后运行）：

```powershell
git worktree add .worktrees/preproc-c18-async-normalize -b feature/preproc-c18-async-normalize 953293b9
```

- worktree 内实施顺序约束：M0 探针产物（报告）先评审 → 再动注册表翻转；M1 起所有 build/test 仅在该 worktree 内进行；主干工作区保持只读不动。
