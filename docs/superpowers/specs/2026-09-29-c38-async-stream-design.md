# C38 异步流与异步释放归一（async-normalize 姊妹扩展）设计

**日期**: 2026-09-29
**范围**: 在既有注册表 stub `async-normalize`（catalogItems `C18, C38 (C44 subtopic)`）内，于 C18 锚点合入后扩展 **C38 子域**：① `await foreach` → 显式 `IAsyncEnumerator<T>` 驱动（`GetAsyncEnumerator` + `while (await MoveNextAsync())` + `finally DisposeAsync`）中间形态；② `await using`（语句与声明两形态）→ `try/finally` + `await DisposeAsync()`；③ `IAsyncEnumerable<T>` / `IAsyncEnumerator<T>` / `IAsyncDisposable` / `ConfiguredCancelableAsyncEnumerable` 类型使用点归一（compat 桥映射或显式报错）；④ 异步迭代器方法（`async IAsyncEnumerable` 体内 `yield return`）v1 显式报错、M4 与 C04 联合状态机
**状态**: 设计稿，待评审；**本任务不注册新 pass、不翻转任何 stub、不改动生产代码**；实施在 C18 合入后于同一 pass 的 `AsyncNormalize/AsyncStreams/` 子目录落地；长期 opt-in
**注册表**: stub 条目已存在 [PassStubCatalog.cs:229-L238](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L229-L238)（`Ids.AsyncNormalize = "async-normalize"`，Stage=StateMachine，RunAfter=`closure-lift`，默认关，目录 `.cs2j-async-normalize-src`，catalogItems=`C18, C38 (C44 subtopic)`）；C38 **不新建 pass/id**，实施时仅把 C18 翻转时收窄为 `"C18"` 的 descriptor 一行扩宽回 `"C18, C38"`（C18 D13、[C18 设计 §4.7](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md)）

> 命名口径：pass id 与目录归属以 C18 锚点为准 = `async-normalize` / Core 命名空间 `AsyncNormalize`；C38 代码全部落在 `AsyncNormalize/AsyncStreams/` 子目录。诊断码使用 C18 §4.7 为 C38 预留的 **CS2J1840-1849** 子段，选项位使用 C18 §4.5 预留的 internal `EnableAsyncStreams`。

## 1. 背景与目标

### 1.1 两处静默语义丢失（已逐处复核，证据以工作区源码为准）

1. **`await foreach` 被按同步 foreach 直译为 Java enhanced-for**：[StatementTransformer.Loops.cs:103-154](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L103-L154) `TransformForEachStatement` 全函数不读 `stmt.AwaitKeyword`，无条件产出 `for (T x : expr)`；注释 L99-101 自称「Iteration sources must be Java arrays or Iterables after preprocessing」，但异步枚举器既非数组也非 `Iterable`，命中后产物语义是「同步遍历一个不存在的 iterator」。全 Core 对 `AwaitKeyword` 的引用仅两处——[TupleToStructPreprocessor.cs:1344](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleToStructPreprocessor.cs#L1344) 与 [ForeachDeconstructionRewriter.cs:89](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L89)——且都是**重建节点时原样透传**，不做任何异步判定。
2. **`await using` 语句被降为 try-with-resources**：[StatementTransformer.SwitchAndResource.cs:202-L244](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L202-L244) `TransformUsingStatement` 同样不读 `AwaitKeyword`，堆叠 using 被收集进单个 resource 列表后直映 `try (...)`，异步释放被静默换成同步 `AutoCloseable.close()`。
3. **catalog 未单列的第三处（本次复核新增）**：`await using var x = …;` **声明形态**走另一条路径——[StatementTransformer.cs:444-494](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L444-L494) `IsUsingDeclaration` / `CollectConsecutiveUsingDeclarationResources` 只判定 `UsingKeyword`，连续 using 声明被包成 `try (...) { … }`，同样不读 `LocalDeclarationStatementSyntax.AwaitKeyword`。C38 规则必须同时覆盖语句/声明两个语法面，不能只改 SwitchAndResource。
4. **测试现状锁定了「原样流入」**：[ForeachDeconstructionTests.cs:159-181](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ForeachDeconstructionTests.cs#L159-L181) `Lowers_AwaitForeach_PreservesAwait` 显式断言 E07 产物**仍含 `await foreach`**——即异步 foreach 带着关键字穿过全部既有 E 通道直达主转换器。[StreamAsyncTokenTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/StreamAsyncTokenTests.cs) 是空占位类（仅一个 `Convert` helper，零 `[Fact]`）。
5. 以上正是 catalog §4 风险 12 点名的「静默错误输出」面：不抛异常、不发诊断，产出不可编译或语义错误的 Java。

### 1.2 类型映射 / compat 双空白（含对 catalog 叙述的一处修正）

- [config/TypeMappings.json](file:///d:/code/cs2j/config/TypeMappings.json) 全文 grep `IAsyncEnumerable|IAsyncEnumerator|IAsyncDisposable|ConfiguredCancelableAsyncEnumerable` **零命中**；既有最近邻映射只有 `Task/Task<T>/ValueTask<T> → CompletableFuture`（[L142-L161](file:///d:/code/cs2j/config/TypeMappings.json#L142-L161)），非泛型 `ValueTask` 无映射、落 14 行空壳 [ValueTask.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ValueTask.java)（opaque marker，无 await/continuation 面）。
- java compat 全目录 grep `IAsyncEnumerable|IAsyncEnumerator|AsyncIterator|AsyncEnumerable` **零命中**：异步序列桥完全不存在。[csharp-collection-compat-design.md:12](file:///d:/code/cs2j/docs/superpowers/specs/2026-07-02-csharp-collection-compat-design.md#L12) 已明确把 async interfaces（IAsyncEnumerable/IAsyncEnumerator）列为 **Out**。
- **修正 catalog L725 / C18 §1.2-10「compat 无对应类」的笼统叙述**：compat 实际存在孤本 [IAsyncDisposable.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/IAsyncDisposable.java)（12 行，注释自署 generated），但：① 不在 TypeMappings.json，全 src/tests **零引用**（grep 仅 catalog/docs 命中），是无发射点的死桥；② 签名 `CompletableFuture<ValueTask> disposeAsync()` 与 `ValueTask → CompletableFuture` 的既有映射自相矛盾（双重包装、`join()` 得到的是 marker 而非完成信号）。C38 不能直接复用，须把「重塑该接口」列入 Java 半区依赖清单（§5.2）。
- 方法面同样空白：`GetAsyncEnumerator / MoveNextAsync / get_Current / DisposeAsync / WithCancellation / ConfigureAwait`（枚举器形态）在方法映射与转换器中均无特判。
- **连锁产物推演**（解释「双空白」如何与 §1.1 叠加成坏码）：`await foreach (var x in src)` 经 Loops.cs 变成 `for (T x : src)` 后，`src` 的类型 `IAsyncEnumerable<T>` 无映射（按未映射类型路径输出 C# 原名，Java 侧无此符号），且 Java 增强 for 要求 `Iterable`——类型面与语句面**双重不可编译**；`await using` 则语句面降成 try-with-resources 后，资源被要求为 `AutoCloseable`，而 `IAsyncDisposable` 即使按孤本映射也不含 `close()`——同样双重不匹配。这与 C18 面「join 通道已存在但语义弱」不同：C38 面在今天**没有任何一条可编译的下游路径**，因此「先显式报错」本身就是独立价值（§8 末条）。
- **频率未知**：catalog C38 条目标 P2 但训练集计数表（catalog §5.5）只有 `await foreach / IAsyncEnumerable / IAsyncDisposable` 的「—」占位（[catalog:1075](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1075)），无数字；EF Core 画像（catalog §5.1）是预期高命中面但尚未扫描。M1 必须先交计数（§9），桥的投入规模以数据为准。

### 1.3 目标与非目标

**目标**：

1. 消灭 §1.1 三个静默面：`await foreach` / `await using` 语句 / `await using` 声明要么被显式归一，要么拿到显式诊断（CS2J184x），禁止再按同步形态处理。
2. 产出符合 C18 轻量形态纪律的中间树：**循环糖消除、await 保留**（await 仍交 C18 既定通道与 Java join 模型承接），产物是普通可编译 C#。
3. 给类型使用点确定归宿：compat 桥就绪则映射，未就绪则显式报错；桥的最小方法面一次性冻结（§2 D5、§5.2）。
4. 异步迭代器方法 v1 显式报错并指明出路，**回应 C18 开放问题 3 / D11③ 的触发条件**（§2 D7、§11.1）。
5. residual 闸门：C38 出口产物零裸 `await foreach` / `await using`（白名单=已发诊断节点）。

**非目标**：

- 不改写 await/Task 的运行时语义，不承诺异步流的真并发/背压等价（join 阻塞模型下逐元素串行，与 C18 同边界，§10 风险 1）。
- 不在本 pass 提交任何 Java compat/TypeMappings 改动——它们是 Java 半区依赖项，只列清单与验收门（§5.2、§8）。
- 不覆盖同步 `using`（转换器已内联，C44 维持观察项）、同步/duck/ref foreach（C16/C40）、同步 yield（C04）。
- 不改造主转换器；Loops.cs / SwitchAndResource.cs / StatementTransformer.cs 三个热文件**只记录、不改写**（§11.6）。
- 不处理生产者侧 `[EnumeratorCancellation]` 语义（只在异步迭代器方法体内有意义，随 D7 v1 报错一并 defer）。

### 1.4 与族内条目的分工

| 条目 | 管什么 | 与 C38 的边界 |
|---|---|---|
| **C18**（锚点） | async **方法体**：async lambda 提升、ConfigureAwait 归一、async void/awaitable 诊断 | 同一 pass；C38 复用 `AsyncShapeClassifier` / Naming / 选项与统计骨架 / CS2J18xx 码段；轻/重形态选型以 C18 D1 为准 |
| **C40/C16**（foreach-source） | 同步 duck-typed 枚举器适配、`ref`/`ref readonly` foreach、string/残余解构 | 同「foreach 插入点」不同规则目录：同步/ref 源归 `ForeachSource/Rules/`，**异步源归 `AsyncNormalize/AsyncStreams/`**；C16 §11 已承诺对 AwaitKeyword 节点零接触（FS-4），其 residual 闸门只计数 CS2J5442 不替 C38 收口 |
| **C44**（using 显式化，观察项） | 同步 using 声明/语句 → try/finally；ref struct disposables | catalog L805 已把 `await using` 子题划归 C38；**C44 不重复实现**，C38 落地后 C44 条目无需改动 |
| **C04**（iterator-lowering） | 同步 `IEnumerable`/`IEnumerator` yield 状态机 | C04 §1 非目标明确不降级 `IAsyncEnumerable`；异步迭代器 = C04 区域机制 + C18 await 挂起的**联合状态机**，v1 双方都报错，M4 联合（§2 D7） |
| E07（foreach-deconstruction，已实现） | KVP/解构 foreach | 重建 foreach 时原样保留 `AwaitKeyword`（[Rewriter:89](file:///d:/code/cs2j/src/CSharpToJava.Core/ForeachDeconstruction/ForeachDeconstructionRewriter.cs#L89)），是 C38 的输入约定而非冲突 |
| C45（closure-lift，stub） | lambda/display class 全量提升 | await foreach/using 体内若有捕获型 async lambda，沿用 C18 D3(d) 先 CS2J1802 defer，C38 不自带 display class |

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据 / 备注 |
|---|---|---|---|
| D1 ★ | **await foreach 目标形态** | **推荐 C# 层显式枚举器驱动的中间形态（循环糖消除、await 保留），否决「保留 await foreach 关键字交 Java 半区」** | 三维度论证：① **转换器可承接性**——主转换器只会发数组/Iterable 的 enhanced-for（[Loops.cs:122-126](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L122-L126)），`IAsyncEnumerable` 不可能实现 `Iterable`（`MoveNextAsync()` 返回 `ValueTask<bool>` 而非 `bool`），保留关键字等于强迫 Java 半区同时新学一种循环结构 + 全套异步序列 compat；显式 `while (await e.MoveNextAsync())` 复用 C18 已验证的 await→join 通道与既有 while/try-finally 语句发射能力，Java 半区只需补**类型与方法**。② **与 C18 形态纪律一致**——轻量版边界是「await 保留、语法糖归一」，await foreach 与 ConfigureAwait 同属糖层。③ **可验证性**——显式形态可在 .NET 半区直接写行为测试逐元素比对；保留关键字则 .NET/Java 两侧都没有对应形态可对照。**与重量版的关系**：本形态不需要 C# 状态机；async yield 生产者才需要（D7），消费侧 foreach 不需要 |
| D2 | **await foreach 展开形状** | 直连 `IAsyncEnumerable<T>` 源（AS-1）展开为：`var __cs2jAsE = src.GetAsyncEnumerator(__cs2jTok); try { while (await __cs2jAsE.MoveNextAsync()) { var x = __cs2jAsE.Current; BODY } } finally { …释放… }`；枚举器临时名走 `AsyncNormalizeNaming`（`__cs2jAsE_<n>`，作用域=宿主方法，防撞规则同 C18 §4.4） | `await foreach (V x in e)` 的元素变量 V 可为解构/元组形态——该形态在 E07/E06 之后已被规范为普通声明（[ForeachDeconstructionTests.cs:162-181](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ForeachDeconstructionTests.cs#L162-L181) 即先解构再保留 await）；C38 在链路上位于 E07 之后，看到的 foreach 变量以普通 `ForEachStatement` 为主，残余 `ForEachVariableStatement` 带 await 走 CS2J1840 防御报错 |
| D3 | **取消令牌与 ConfigureAwait 链（AS-2）** | ① 裸源：`GetAsyncEnumerator(CancellationToken.None)`（显式发射默认实参，消除 C# 编译器默认参数魔法，Java 半区无默认参数）；② `source.WithCancellation(ct)`（SemanticModel 绑定到 `AsyncEnumerable.WithCancellation` 扩展）→ 把 `ct` 透传进 `GetAsyncEnumerator(ct)`，WithCancellation 节点消除；③ `.ConfigureAwait(bool)`（`ConfiguredConfiguredAsyncEnumerable` 形态）→ 同 C18 D5 剥离为 receiver 并计 CS2J1830 Info（Java 无 SynchronizationContext，论证同 Task 侧）；④ 链式 `WithCancellation(ct).ConfigureAwait(false)` 按序消解；⑤ v1 拒绝：非常量/非 CT 实参的 WithCancellation（CS2J1846）、`ConfiguredCancelableAsyncEnumerable<T>` 强类型局部/参数/字段（CS2J1843，剥离会改类型）、重复 WithCancellation | 不发明 C# 侧取消桥；`[EnumeratorCancellation]` 生产者语义随 D7 defer。compat CT 仅有 `getNone/isCancellationRequested`（C18 §1.2-7），`GetAsyncEnumerator(CT)` 形参在 Java 半区如何被消费是 compat 实现细节，C# pass 只保证 token 数据流不断（同 C18 D8） |
| D4 ★ | **await using 目标形态（AS-4/AS-5）** | **`try/finally` + `await r.DisposeAsync()`，不映 try-with-resources**。语句形态 `await using (r) BODY` 与声明形态 `await using var r = …;`（作用域=外层块剩余部分，作用域分析借 C04 D5/C44 已验证口径）统一改写：`var r = EXPR; try { BODY } finally { await r.DisposeAsync(); }`。引用类型资源发射**空条件守卫**：C# 语义允许 `await using (null)`（直接 using 抛 NRE，await using 对 null 宽容——编译器对类类型资源发射 null 检查），故 finally 内发 `if (r != null) await r.DisposeAsync();`；非可空 struct 显式实现不发守卫（SemanticModel 判定） | 堆叠/嵌套规则：多个 await using 按 LIFO 嵌套展开（内层先释放）；await using 与同步 using 混叠时各自展开为自己的 try/finally（同步侧走 `Dispose()`/C44 既有形态），嵌套顺序保持源码顺序；`return`/`break`/异常穿越时 finally 自然执行，与 await foreach 的释放特例不同（D6） |
| D5 ★ | **类型映射策略与 compat 桥最小方法面** | **分阶段推荐：await using 先行（M2，只需 IAsyncDisposable 一个面），await foreach 后行（M3，需异步序列全套桥，硬前提不满足则相关使用点显式报错）**。★Java 半区需新增/重塑的最小面（v1 冻结，C# pass 不提交）：① `AsyncDisposable`（重塑现有孤本 [IAsyncDisposable.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/IAsyncDisposable.java)）：`CompletableFuture<Void> disposeAsync()`（废弃现有 `CompletableFuture<ValueTask>` 双重包装签名）；② `AsyncEnumerable<T>`：`AsyncEnumerator<T> getAsyncEnumerator(CancellationToken ct)`；③ `AsyncEnumerator<T>`：`CompletableFuture<Boolean> moveNextAsync()`、`T getCurrent()`、`CompletableFuture<Void> disposeAsync()`；④ TypeMappings.json 三条类型项 + 方法命名项（`GetAsyncEnumerator→getAsyncEnumerator`、`MoveNextAsync→moveNextAsync`、`Current→getCurrent`、`DisposeAsync→disposeAsync`）。**不新增** `ConfiguredCancelableAsyncEnumerable` 映射（D3 在 C# 层消解）。选项 `EnableAwaitUsing`（M2）、`EnableAwaitForeach`（M3）独立门控；关闭或桥未就绪时，对应语句/类型使用点 CS2J1841 显式报错 | 备选「v1 全部显式报错、等 compat」被否为默认：await using 只欠一个接口重塑，成本极低且语义独立，先交付；async enumerable 桥涉及每元素 future 分配（§10 风险 3），成本/性能须 M1 计数 + M3 原型量化后再定是否进默认门，但**任何阶段都不允许静默按同步处理**。桥随 java compat 工程交付（类似 C04 复用既有 `CSharpEnumerator`），**不是** C# pass 注入的 Cs2j 支撑工程 |
| D6 | **异常传播与 finally 时序（AS-3）** | 两场景分开锁定：① **await using**——遵循普通 try/finally 语义：体正常则传播 DisposeAsync 异常；体抛异常后 DisposeAsync 再抛则后者屏蔽前者（与同步 using 同族，直接展开即可）。② **await foreach（C# 8 特例）**——按 C# async streams 规则，**循环体/MoveNextAsync 已有在途异常时，枚举器 DisposeAsync 的异常不得屏蔽主异常**（与同步 foreach 的 finally 屏蔽语义刻意不同；典型场景：取消导致 MoveNext 抛 OCE、DisposeAsync 又抛 OCE）。因此 AS-1 默认发射**保真守卫形态**（非朴素 finally）：成功标志位 + finally 内分支——正常完成路径 `await e.DisposeAsync()`（其异常正常传播）；在途异常路径仍 await DisposeAsync 但吞掉其异常（catch 后丢弃，主异常由原 try 传播）。`FaithfulAsyncDisposeGuard` 选项可关（关=朴素 finally，仅用于差异对照，不进默认） | 这是显式中间形态相对「保留关键字」的额外义务：C# 编译器对 await foreach 的故障处理不是普通 finally；朴素展开在「双异常」用例上与 .NET 真值不一致。M3 fixture 必须有双异常时序用例在 .NET 半区锁定真值（§7.2）。`break`/`continue`/`return` 穿越按正常完成路径释放（break 属正常离开循环） |
| D7 ★ | **异步迭代器方法（async yield）** | **v1 显式报错 CS2J1844**：SemanticModel 识别「`async` 修饰 + 返回 `IAsyncEnumerable<T>`/`IAsyncEnumerator<T>` + 体内含 `yield return`/`yield break`」的方法/局部函数，成员原样保留、严格模式 pass 失败。**M4+ 联合状态机**：形态 = C04 同步状态机（状态字段/Current/Dispose 区域）与 C18 §4.8 async builder 雏形（awaiter 字段/挂起恢复/SetResult）的**直积**——生成同时实现 `IAsyncEnumerable<T>`+`IAsyncEnumerator<T>` 的类，`MoveNextAsync()` 返回 `ValueTask<bool>` 且其内部既有 yield  resume 又有 await 挂起点，`DisposeAsync` 驱动 finally 区域；在 `AsyncNormalize/AsyncStreams/AsyncIterator/` 子目录实施，不回改 C18 普通 async 规则与 C04 同步规则 | **对 C18 D11③ 的正式回应**：async iterator 确实是重量形态的真实驱动力，但它只在「异步迭代器方法」这一子域强制状态机，**不推翻 C18 D1 对普通 async 方法的轻量版选型**；触发后的重量能力按 C18 D11 原话「经同一 AsyncNormalize 目录、在 C38 子域先行」实施。M4 前置 = C04 try/finally 区域机制稳定（C04 M2-M3）+ M1 计数证明该形态在画像中有真实命中；否则长期显式报错也是可接受终态 |
| D8 | **Dispose / IAsyncDisposable 双实现优先级（AS-7）** | 资源类型同时实现 `IDisposable` 与 `IAsyncDisposable` 时：`await using` / `await foreach` 的释放**一律选 `DisposeAsync()`**（C# 编译器按 await 关键字绑定异步释放，不做 Sync-over-Async）；同步 `using`/`foreach` 仍选 `Dispose()`。绑定以 SemanticModel 的接口实现判定为准，禁止按方法名猜测；仅具 pattern-based `DisposeAsync`（鸭子类型，含 ref struct 自定义异步释放）而无接口的 v1 CS2J1842 | 常见于同时包装同步句柄与异步客户端的库类型；误选 Dispose() 会静默退化为同步阻塞释放。ref struct 的 await using 与 C06/C44 ref struct disposables 同族，v1 不吞 |
| D9 | **await foreach 源类型判定（AS-1 入口）** | 只接受 SemanticModel 判定为 `IAsyncEnumerable<T>`（含 `ConfiguredCancelableAsyncEnumerable<T>` 经 D3 先消解）的源；duck-typed（手写 `GetAsyncEnumerator` 但不实现接口）v1 CS2J1840；数组/string/`IEnumerable` 源上误写 await 是基线编译错误（CS1895 族），Analyzer 不抢诊断（基线快照纪律同 C18 §4.1） | 不做语法猜测；`CancellationToken` 给 GetAsyncEnumerator 的默认实参按 D3 显式化 |
| D10 | **选项面** | 在 `AsyncNormalizeOptions` 上以 C18 预留的 internal 位展开：`EnableAsyncStreams`（总闸，M1 不暴露 CLI）、`EnableAwaitUsing=true/false`、`EnableAwaitForeach`、`FaithfulAsyncDisposeGuard=true`、`RejectAsyncIterator=true`（v1 恒 true 的 kill-switch 形态）、Strict 复用 C18；所有 C38 规则只在总闸开启时注册，**C18 单测/快照在总闸关闭时零漂移** | 实施期是否暴露独立 `--enable-pass` 面：不新增 pass，随 `--enable-pass async-normalize` 一起进；子开关先走 Options 代码面，CLI 暴露需求在 M3 评审 |
| D11 | **幂等 / 合成节点标注** | 所有合成节点挂 `SyntaxAnnotation("Cs2jAsyncNormalize")`（复用 C18 既有 annotation，不新增第二种）；重跑时已展开节点不被二次处理；residual 扫描只认语法糖（`AwaitKeyword` 挂在 foreach/using 上的节点），展开产物内的 `await DisposeAsync/MoveNextAsync` 是预期 await（C18 residual 面只管 async lambda/ConfigureAwait/async void，双方闸门互不计数对方的合法产物） | 两个闸门的白名单/标注口径必须在 M2 联调，避免互相把对方产物判 residual |
| D12 | **C44 await using 子题归属** | catalog 注册表 catalogItems 字符串即 `C18, C38 (C44 subtopic)`——**C44 的 await using 子题就是本条 AS-4/AS-5，不另立规则、不在 C44 重复实现**；C38 落地时无需修改 C44 任何文档结论（其 §现状「await using 归 C38」的指向即本设计） | 同步 using 维持转换器内联现状（C44 P3 观察项），CsCore 全量收敛时再议 |

### 2.1 形态示意

await foreach（AS-1 + AS-3 保真守卫 + D3 取消/ConfigureAwait 链）：

```csharp
// 输入
await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
{
    Process(item);
}

// 输出（await 保留，循环糖消除；默认实参与异常守卫显式化）
var __cs2jAsE_0 = source.GetAsyncEnumerator(ct);
bool __cs2jAsOk_0 = false;
try
{
    while (await __cs2jAsE_0.MoveNextAsync())
    {
        var item = __cs2jAsE_0.Current;
        Process(item);
    }
    __cs2jAsOk_0 = true;
}
finally
{
    if (__cs2jAsOk_0)
    {
        await __cs2jAsE_0.DisposeAsync();
    }
    else
    {
        try { await __cs2jAsE_0.DisposeAsync(); }
        catch { /* 保真：在途主异常优先，释放异常丢弃，C# 8 规则 */ }
    }
}
```

await using（语句与声明两形态同归；AS-4）：

```csharp
// 输入
async Task M(IAsyncDisposable a)
{
    await using var r = Create();
    await using (a)
    {
        await WorkAsync(r);
    }
}

// 输出（LIFO 嵌套；引用类型带 null 守卫；声明作用域外包到块末）
async Task M(IAsyncDisposable a)
{
    var r = Create();
    try
    {
        try
        {
            await WorkAsync(r);
        }
        finally
        {
            if (a != null) await a.DisposeAsync();
        }
    }
    finally
    {
        if (r != null) await r.DisposeAsync();
    }
}
```

### 2.2 v1 归宿矩阵（每种站点在各阶段的确定行为，禁止出现「静默按同步处理」格）

| 站点 | M2（await using 阶段） | M3（桥交付后） | 桥选项关闭 / 长期形态 |
|---|---|---|---|
| `await using`（接口 IAsyncDisposable 资源） | 展开 try/finally | 同 M2 | CS2J1841（显式报错） |
| `await using`（pattern-based/ref struct） | CS2J1842 | 同（评估后再开放） | CS2J1842 |
| `await foreach`（IAsyncEnumerable 直连/WithCancellation/bool ConfigureAwait） | CS2J1841 | 展开 AS-1/AS-2/AS-3 | CS2J1841 |
| `await foreach`（鸭子型源 / 残余解构变量形态） | CS2J1840 | CS2J1840（v1 不开放） | CS2J1840 |
| `ConfiguredCancelableAsyncEnumerable<T>` 强类型使用点 | CS2J1843 | CS2J1843（C# 层只消解调用链，不接强类型） | CS2J1843 |
| `IAsync*` 参数/返回/字段/局部（非语句位） | IAsyncDisposable 放行；其余 CS2J1841 | 全部放行映射 | CS2J1841 |
| async iterator 方法（async yield） | CS2J1844 | CS2J1844 | CS2J1844（M4 才可能翻转） |

> 「放行」指 C# 树零改写、由 Java 半区 TypeMappings 承接；「报错」均为严格 Error/非严格 Warning + 原样保留，绝不产部分改写。

## 3. 链位与族契约

### 3.1 不翻转、不新 pass、依赖 C18

- C38 **不拥有 pass id**：实施前提是 C18 已按 [C18 §3.2](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md) 完成 Stub→Implemented 翻转。届时注册表三热文件的动作仅为：[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) async-normalize descriptor 的 catalogItems 一行 `"C18"` → `"C18, C38 (C44 subtopic)"`（Description 同步）；**PassStubCatalog.cs 不再动**（C18 翻转时该 stub 条目已删除，常量保留）；PassChainFactory 不新增 case（同一适配器内按 Options 注册 C38 规则）。
- 本设计任务（2026-09-29）**不产生任何注册表改动**；stub 继续默认关、显式启用报 CS2J9001 的现状不变。
- 代码物理位置：`src/CSharpToJava.Core/AsyncNormalize/AsyncStreams/`（C18 §4 文件清单已预留 `AsyncStream/AwaitForEachRewriter.cs`、`AwaitUsingRewriter.cs`、`AsyncEnumerableTypePlan.cs` 三个名字；本设计把子目录复数化为 `AsyncStreams/` 并补全组件，落地时以 C18 锚点代码为准，若 C18 已建单数目录则沿用，不制造二义）。

### 3.2 执行顺序（在 async-normalize 内部）

C38 规则在 pass 内的建议注册序：**AS-6 异步迭代器识别（先诊断，生产者方法整体 skip）→ AS-2 取消/ConfigureAwait 链消解 → AS-1/AS-3 await foreach 展开 → AS-4/AS-5 await using 展开 → AS-7/AS-8 类型使用点扫描 → residual 闸门**。理由：先把 WithCancellation/ConfigureAwait 从 foreach 表达式上剥干净，枚举器展开才不需要识别配置包装类型；先生产者 skip（async iterator 内的 await foreach 属 M4 联合域，v1 不进部分改写）。

### 3.3 与 C40「同插入点」的目录划分

foreach 全规则集按源类型三分，分属两个 pass、不共享代码：

| 源类别 | 判定 | 归属 | 现状通道 |
|---|---|---|---|
| 同步源（数组/集合/string/解构/duck `GetEnumerator`/ref foreach） | 实现 `IEnumerable` 或模式匹配同步枚举 | `foreach-source`（ControlFlow 段，RunAfter=foreach-deconstruction，[PassStubCatalog.cs:102-L111](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L102-L111)），C16/C40 | E04/E07/C16/C40 |
| **异步源（await foreach）** | `AwaitKeyword` 在场且源为 `IAsyncEnumerable` | **`async-normalize` / AsyncStreams（StateMachine 段），C38** | 现状无人（本设计） |
| 交叉形态（`await foreach (var (k,v) in …)`） | await + 解构 | E06/E07 先规范化解构、**原样保留 await**，C38 再展开 | ForeachDeconstructionTests:162 锁定 |

顺序由链段位天然保证（foreach-source 在 ControlFlow、async-normalize 在 StateMachine）；**不加 pass 间 RunAfter 边**。契约只一条：foreach-source 任何规则不得消费/擦除 AwaitKeyword（C16 FS-4 已承诺，CS2J5442 只计数）；C38 不处理非 await 的 foreach。

## 4. Core 组件设计

目录 `src/CSharpToJava.Core/AsyncNormalize/AsyncStreams/`（全部 internal，入口由 C18 `AsyncNormalizePreprocessor` 在 `EnableAsyncStreams` 时装配）：

```
AsyncStreams/
  AsyncStreamOptions.cs          # D10 子开关（挂在 AsyncNormalizeOptions 上的 internal 节）
  AsyncStreamAnalyzer.cs         # 发现面 + 基线快照 + skip 集 + foreach/using/类型使用点计划
  AsyncStreamSite.cs             # 一个重写站点的不可变计划（种类/源符号/资源符号/token/区域）
  AsyncForeachRewriter.cs        # AS-1/AS-2/AS-3：链消解 + 枚举器展开 + 保真守卫
  AwaitUsingRewriter.cs          # AS-4/AS-5：语句/声明两形态 → try/finally + DisposeAsync
  AsyncTypeResolver.cs           # AS-7/AS-8：IAsync* 类型使用点解析、双实现优先级、无桥报错
  AsyncIteratorDetector.cs       # AS-6：async yield 方法识别（v1 诊断，M4 换联合状态机）
  AsyncStreamNaming.cs           # __cs2jAsE_/__cs2jAsOk_/__cs2jAsRes_ 确定性命名（委托 AsyncNormalizeNaming 防撞）
```

### 4.1 Analyzer

- 与 C18 共享 C18 入口建好的 `Compilation`/SemanticModel（parse latest、allowUnsafe），不二次建编译。
- 发现面（语法粗筛 + 符号双判）：① `CommonForEachStatementSyntax` 带 AwaitKeyword；② `UsingStatementSyntax` 与 `LocalDeclarationStatementSyntax`（using 声明）带 AwaitKeyword；③ 类型使用点——字段/属性/参数/返回/局部/base 接口列表中的 `IAsyncEnumerable<T>`/`IAsyncEnumerator<T>`/`IAsyncDisposable`/`ConfiguredCancelableAsyncEnumerable<T>`；④ `WithCancellation`/枚举器 `ConfigureAwait` 调用；⑤ async iterator 方法（D7）；⑥ 鸭子型 `GetAsyncEnumerator`/`DisposeAsync` 模式（v1 诊断面）。
- skip 集在任何重写前冻结（同 C04/C18 纪律）：async iterator 方法整体进入 skip（CS2J1844），其体内站点不做部分改写；严格模式任一 Error 即 pass 失败、全部成员原样保留（树仍可编译）。
- 基线优先：`await foreach` 写在非 async 方法（CS4033 族）、对 `IEnumerable` 源写 await（CS1895 族）等编译器已拒形态不二次诊断。
- await using 声明的作用域分析复用 C04/C44 文档口径：作用域终点=外层块末尾（含穿越 `return`/异常路径），同一作用域多个连续 await using 声明合并为一组 LIFO 嵌套计划。

### 4.2 AsyncForeachRewriter（AS-1/AS-2/AS-3）

- 输入一个 `AsyncStreamSite`（含源表达式、元素符号类型、绑定到的取消 token、是否含 ConfigureAwait）。
- 发射顺序见 §2.1；要点：① `GetAsyncEnumerator` 实参恒显式（无 token 时 `CancellationToken.None`，需 `System.Threading` using——由 C# 编译基线兜底，不手工插 using）；② 元素变量声明挪到 while 体内首句（保持 `var` 或显式类型以源符号渲染）；③ 循环体内 `break`/`continue` 保持原节点（finally 语义自动正确）；④ 保真守卫的成功标志位在「while 正常结束」处置位，MoveNextAsync/Current/body 任一抛异常则走在途异常分支（D6）；⑤ 枚举器/标志位临时量在宿主方法作用域命名并防撞（不干扰外层同名用户变量）。
- 经 AdhocWorkspace `Formatter.Format` 规范化（iterator/record 同款）。

### 4.3 AwaitUsingRewriter（AS-4/AS-5）

- 语句形态：直接把 `await using (EXPR) BODY` / `await using (T r = EXPR) BODY` 换 try/finally；无声明变量的纯表达式形态引入 `__cs2jAsRes_n` 临时。
- 声明形态：在块级把 `await using var r = EXPR;` 从语句列表抽出，以其后继语句序列（至块末）为 BODY 包 try/finally；连续多个声明按源码序反向嵌套。
- 守卫：资源类型为引用类型或其可空标注可为 null → `if (r != null)`；struct/非可空值类型 → 直接调用。
- 双实现类型由 AsyncTypeResolver 预先标注，释放调用一律绑定 `IAsyncDisposable.DisposeAsync`（D8）。
- 与同步 using 混叠：各自独立成 try/finally，不做资源列表合并（同步侧的 try-with-resources 合并不适用于 await 形态）。
- 边界细则：① **纯表达式形态** `await using (expr) { … }`（无变量）引入 `__cs2jAsRes_n` 持有资源；② **多变量声明** `await using (var a = …, b = …) BODY` 按声明序展开为嵌套两层（C# 不要求同类型，禁止合并）；③ **声明形态作用域内提前 return/throw**：BODY 包含全部后继语句，return 表达式若引用资源不影响 finally 顺序；④ **空体/空块**仍发射完整 try/finally（资源获取副作用与释放必须保留）；⑤ **资源表达式含方法调用/await**（`await using var r = await FactoryAsync()`）：表达式整体只在 try 外求值一次，Factory 的 await 留在原位、不被移动进 try；⑥ **显式类型 vs var**：渲染以 SemanticModel 资源符号为准，null 守卫取决于类型（值类型无守卫、引用类型有守卫、nullable 注解为提示而非依据——以符号 IsReferenceType 判定）；⑦ **对象初值在 using 前可能为 null 的赋值模式**（`IDisposable? r = null; …; await using (r!) …`）v1 按引用类型发守卫，不做流分析精化。
- 区域嵌套与 C04 的口径对齐：await using 不跨 yield（async yield 整体属 AS-6 skip 域），故 M2/M3 不需要 C04 的跨挂起 finally 区域提升；await using 与普通 await 共存时 finally 内 `await DisposeAsync()` 是合法 C#（await in finally），由基线编译保证，不做额外改写。

### 4.4 AsyncTypeResolver（AS-7/AS-8）

- 全部 `IAsync*` 类型使用点经符号判定归三类：**可映射**（桥选项开启且目标类型在 §5.2 清单内，透传给 C18 之后的主转换，不改写语法）、**显式拒绝**（桥未就绪/选项关闭 → CS2J1841，定位到具体使用点）、**v1 永不支持**（`ConfiguredCancelableAsyncEnumerable<T>` 强类型 CS2J1843、鸭子型异步释放 CS2J1842）。
- 双实现查询：对每个 await using/foreach 资源符号查 `IAsyncDisposable` 实现关系；同时实现 `IDisposable` 不改变选择（D8），只在 Info（CS2J1830 桶）记录一次，供画像统计。
- 不改任何类型名（不做 C# 层换类型）；映射是 Java 半区 TypeMappings 的职责，本组件只做「放行/报错」与绑定信息供给。

### 4.5 对 C18 设施的复用清单

| C18 设施 | C38 复用方式 |
|---|---|
| `AsyncShapeClassifier`（C18 M2 冻结 internal API） | 调 `IsTaskLike`/await 站点分类；新增 `IsAsyncEnumerable(ITypeSymbol)`、`IsAsyncDisposable`、`ClassifyAsyncStreamSite` 走**新增方法**（C18 §4.7 允许的非破坏扩展），不改既有签名 |
| `AsyncNormalizeNaming` | 临时量命名与防撞；C38 前缀 `__cs2jAs*` 由本族追加，不与 `__cs2jTmp` 冲突 |
| `AsyncNormalizeDiagnostic` / CS2J18xx 码段 | 复用记录类型与 MSBuild 输出；码位只用预留子段 1840-1849 + 共享 1820/1830 |
| 基线快照 / skip 先行 / annotation / residual 白名单 | 整套方法论照搬；annotation 复用 `Cs2jAsyncNormalize` 单一种类 |
| `AsyncNormalizeStatistics` | 追加 C38 桶（§5.1），不另建统计类 |

### 4.6 异常/finally 顺序汇总（fixture 必须逐格锁定）

| 场景 | 正常完成 | 体/MoveNext 抛异常 | 释放也抛异常 |
|---|---|---|---|
| await using | 传播释放异常 | finally 释放；释放异常**屏蔽**主异常（普通 finally 语义） | 同左（与同步 using 一致） |
| await foreach | 传播 DisposeAsync 异常 | 主异常传播；DisposeAsync 仍执行，其异常**丢弃不屏蔽**（D6 守卫） | — |
| await foreach 内 `break`/`return` | 正常释放路径（标志位置位前离开循环的路径需在实现时以 .NET 探针复核：break 属正常离开，释放异常应传播） | — | — |
| 嵌套 await using | LIFO | LIFO；内层 finally 异常不阻止外层释放 | 各层普通 finally 语义 |

> break/return 路径的标志位置位细节列入 M3 探针项（以 net10/latest Roslyn 实测真值回填，不臆造）。

## 5. CLI 集成与 Java 半区依赖

### 5.1 适配器与统计

- **不新增适配器文件**：C38 全部经 C18 的 `AsyncNormalizePassAdapter` 承载。C18 `AfterFilesAsync` residual 闸门扩展两类扫描（白名单仍=已发 skip 诊断节点 + `Cs2jAsyncNormalize` annotation 合成节点）：
  - 残余带 AwaitKeyword 的 `CommonForEachStatementSyntax` / `UsingStatementSyntax` / using 声明 → CS2J1849（严格 Error）；
  - 未被诊断覆盖的 `IAsyncEnumerable/IAsyncEnumerator/IAsyncDisposable/ConfiguredCancelableAsyncEnumerable` 类型使用点（仅在对应桥选项开启时才要求归零；选项关闭时这些站点应已全部拿到 CS2J1841，闸门校验「无诊断无映射」的漏网）。
- 统计在 C18 通用 5 字段与既有专有桶上追加：`AwaitForeachExpanded`、`AsyncEnumeratorsEmitted`、`WithCancellationResolved`、`EnumerableConfigureAwaitStripped`、`AwaitUsingExpanded`（分语句/声明/堆叠桶）、`AsyncDisposeGuardsEmitted`、`AsyncTypeSitesMapped/Rejected`、`AsyncIteratorsRejected`、`DuckTypedAsyncDeferred`。
- 摘要行在 C18 行尾追加 C38 段：`…, F await-foreach, U await-using, T async-type sites, I async-iterator rejected`；明确展开产物中 await 计数**不下降**（轻量形态指纹，避免误读为异步已消除）。
- verify/指纹/保留目录/`--enable-pass async-normalize`/`preprocess --pass` 全部沿用 C18 既有接线，零新增手工接线；支撑库声明保持 **None**（桥在 Java compat 工程，不经 C# pass 注入）。

### 5.2 Java 半区依赖清单（C# pass 之外的交付物，默认门硬前提）

1. 新增 compat（建议包 `io.github.ningpp.compat`，命名与 collection compat 同族）：`AsyncEnumerable<T>`、`AsyncEnumerator<T>`（方法面 D5）。
2. **重塑** [IAsyncDisposable.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/IAsyncDisposable.java)：`CompletableFuture<Void> disposeAsync()`；现有 `CompletableFuture<ValueTask>` 签名废弃（零引用，无兼容负担）。
3. [config/TypeMappings.json](file:///d:/code/cs2j/config/TypeMappings.json) 增加类型项：`System.Collections.Generic.IAsyncEnumerable\`1 → AsyncEnumerable<T>`、`IAsyncEnumerator\`1 → AsyncEnumerator<T>`、`System.IAsyncDisposable → AsyncDisposable`（接口命名与重塑后文件名对齐，二选一在 Java 半区评审定稿）。
4. 方法命名/映射：`GetAsyncEnumerator`、`MoveNextAsync`、`Current`（getter）、`DisposeAsync` 四条；`WithCancellation` **不映射**（C# 层消解）。
5. 转换器侧配套（独立任务，不在 C38 C# pass 内）：① `moveNextAsync().join()` 的 `Boolean` 拆箱（while 条件）——核实/扩展 C18 所述 join 装箱表；② 确认展开产物的 `CompletableFuture<Void>` await 语句位形态可编译（await `DisposeAsync()` 不取值）。
6. 取消语义仍挂账：compat `CancellationToken` 仅有 `getNone/isCancellationRequested`，token 传入 `getAsyncEnumerator` 后能否被消费是独立 compat 债务（与 C18 D8 同账）。

清单 1-4 是 M3 的**硬前提**（§8）；清单 5 可与 M3 并行但必须在 Java 半区验收前关闭；清单 6 允许挂账，fixture 记录差异。

桥的 Java 形状草图（仅冻结**方法面契约**，实现归 Java 半区；命名可按 collection compat 惯例加 `CSharp` 前缀）：

```java
public interface AsyncDisposable {
    CompletableFuture<Void> disposeAsync();          // 重塑孤本 IAsyncDisposable.java
}

public interface AsyncEnumerator<T> extends AsyncDisposable {
    CompletableFuture<Boolean> moveNextAsync();      // join 后 Boolean 拆箱进 while 条件
    T getCurrent();
    @Override CompletableFuture<Void> disposeAsync();
}

public interface AsyncEnumerable<T> {
    AsyncEnumerator<T> getAsyncEnumerator(CancellationToken ct);  // 无 WithCancellation 映射
}
```

约束：① 不引入 Rx/Reactive Streams/`Flow.Publisher` 依赖（C# async streams 是拉模型，推模型适配是另一个量级的语义重写）；② 不试图在 Java 侧实现 `IValueTaskSource` 式零分配（v1 接受 future 分配，风险 3 量化）；③ 桥接口的 Java 泛型签名必须能承受转换器对 `MoveNextAsync` 结果的既有 `join()` 通道（`CompletableFuture<Boolean>` 而非 boolean，拆箱责任在 C18/转换器侧既有装箱表，§5.2 清单 5）；④ 桥实现是否需要同步提供「从 `Iterable`/`Stream` 适配」的工厂由 fixture 需要决定（fixture 的真异步源用 Java 侧 Channel 等价物手写，不强制 v1 提供）。

## 6. 诊断策略

原则同 catalog §4 风险 12 / C18 §6：**每个异步流构造只有两个归宿——被显式归一，或被显式诊断**；禁止注释/占位/按同步形态透传。码位只追加，使用 C18 §4.7 预留子段：

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J1840 | Error/Warning | `await foreach` 源非 `IAsyncEnumerable`（鸭子型异步枚举器 v1 不支持）或残余解构形态无法绑定元素类型 |
| CS2J1841 | Error/Warning | `IAsyncEnumerable/IAsyncEnumerator/IAsyncDisposable` 类型使用点命中但对应桥选项关闭/Java 半区桥未就绪（消灭「无映射静默落盘」） |
| CS2J1842 | Error/Warning | pattern-based（鸭子型）`DisposeAsync` / ref struct 异步释放，v1 不支持（与 C06/C44 同边界） |
| CS2J1843 | Error/Warning | `ConfiguredCancelableAsyncEnumerable<T>` 强类型局部/参数/字段；枚举器上非 bool ConfigureAwait 形态 |
| CS2J1844 | Error/Warning | **异步迭代器方法**（async + IAsyncEnumerable/IEnumerator 返回 + yield），v1 不降级（D7，附 M4 出路说明） |
| CS2J1845 | Error/Warning | 防御镜像：await foreach/await using 出现在基线非法上下文（基线 CS4033/CS1996 族之外的漏网） |
| CS2J1846 | Error/Warning | `WithCancellation` 实参非可绑定 CancellationToken、重复 WithCancellation、无法静态消解的取消链 |
| CS2J1849 | Error | residual 闸门：输出仍含裸 await foreach/await using 或「无诊断无映射」异步类型使用点 |
| CS2J1820 | Error | restore/build/test 失败（沿用 C18 适配器钩子） |
| CS2J1830 | Info | 归一明细：剥离的枚举器 ConfigureAwait、双实现选择 DisposeAsync、守卫发射站点（verbose） |

非严格模式 1840-1846 降 Warning（原样保留、计 skip 桶）；严格（默认）Error、pass 失败、无部分改写。诊断带文件/行/列/宿主成员完全限定名与处置建议（1841 建议「启用对应桥选项或等 compat 交付」；1844 建议「先物化为 `List<T>` 同步返回，或等 async-iterator 联合状态机（M4）」）。三层防线（Analyzer skip → residual CS2J1849 → 中间树 build CS2J1820）与 C04/C18 同构。

## 7. 测试计划

### 7.1 Core 单测（建议 `tests/CSharpToJava.ProjectTests/AsyncStreamUnitTests.cs`，内联源码→compilation→Preprocess→文本断言 + 输出零编译错误，仿 Iterator/AsyncNormalize 单测）

1. **await foreach 基本**：`IAsyncEnumerable<int>` 参数直连；断言展开四件套（GetAsyncEnumerator/while MoveNextAsync/Current/finally DisposeAsync）、await 计数守恒、输出零编译错误。
2. **取消**：`WithCancellation(ct)` → token 进 GetAsyncEnumerator；裸源 → `CancellationToken.None` 显式实参；`ct` 数据流不丢。
3. **ConfigureAwait**：`.ConfigureAwait(false)`/`(true)`/非常量 bool → 剥离保留 await（CS2J1830）；链式 WithCancellation+ConfigureAwait 顺序消解；强类型 `ConfiguredCancelableAsyncEnumerable<T>` 局部 → CS2J1843。
4. **嵌套**：await foreach 套 await foreach、await foreach 套普通 foreach、内层 `break/continue/return` 的释放时序文本断言。
5. **await using 单与堆叠**：语句形态、声明形态（作用域到块末）、连续声明 LIFO、await/sync using 混叠各自成 try/finally、纯表达式资源临时量、引用类型 null 守卫、struct 资源无守卫。
6. **异常 + DisposeAsync 时序（D6）**：文本断言保真守卫四分支；`FaithfulAsyncDisposeGuard=false` 时朴素 finally；await using 双异常走普通 finally 语义（不发守卫）。
7. **IAsyncDisposable 参数/返回/字段/局部**：桥开 → 放行零改写；桥关 → CS2J1841 精确定位；双实现类型 await using 选 DisposeAsync（Info 记录）、同步 using 仍选 Dispose。
8. **与 lock/using 组合**：`lock` 内 await foreach/await using 属基线 CS1996（不抢诊断）；try/catch/finally 内 await using 区域嵌套正确；await foreach 体内 await（C18 面）共存不互改。
9. **async iterator**：`async IAsyncEnumerable<T> M() { yield return … }` 方法/局部函数 → CS2J1844、整体原样保留、体内不发生部分改写；严格/非严格级别翻转。
10. **防御面**：鸭子型 GetAsyncEnumerator 源（CS2J1840）、鸭子型 DisposeAsync/ref struct await using（CS2J1842）、非 CT 实参 WithCancellation（CS2J1846）、对 `IEnumerable` 源误写 await（基线错，不重复诊断）。
11. **选项隔离**：`EnableAsyncStreams=false`（C18 默认）时所有 C38 用例零改写、零 C18 快照漂移；仅 `EnableAwaitUsing=true` 时 await foreach 站点仍报错/保留。
12. **确定性/幂等**：同输入两次输出字节一致；重跑 pass 无二次展开（annotation）；临时量与用户同名防撞；无 `<>` IL 名。
13. **await using 边界细则**（§4.3 七项逐条）：纯表达式资源、多变量声明、空体、`await FactoryAsync()` 资源表达式不求值两次、struct 资源无守卫、可空引用类型资源有守卫、与 try/catch/finally 嵌套。
14. **await foreach 元素形态**：显式类型元素（非 var）、元素为 struct/泛型参数类型、源本身是属性/方法调用链（只求值一次——断言 GetAsyncEnumerator 接收者不被重复发射）、源表达式带副作用临时量提升。
15. **直接枚举器源**：源静态类型为 `IAsyncEnumerator<T>`（无 GetAsyncEnumerator 可绑）属基线编译错误——断言 Analyzer 不抢诊断、不产出 CS2J1840 以外的噪音。
16. **混合异步面文件**：同一文件内 async lambda（C18 提升）+ ConfigureAwait(Task)（C18 剥离）+ await foreach（C38 展开）+ await using（C38 展开）共存，断言两边统计桶各归各、await 总数守恒、residual 双闸门互不误伤。
17. **非严格模式**：全部 1840-1846 站点降 Warning、Changed=false/原样保留、文件仍可编译、skip 桶按原因分类计数。

### 7.2 专项 fixture sln（扩展 C18 的 `tests/SampleAsyncSolution/`，新增 AsyncStreams 片段；不新建第二 sln）

- Lib：异步流**消费侧**（await foreach：直连/WithCancellation/ConfigureAwait/嵌套/break）、await using（语句/声明/堆叠/双实现类型）、`IAsyncEnumerable<T>` 参数与返回值（消费签名面，不含 yield 生产者）。
- **.NET 半区行为对照（真异步真值）**：用 `Channel<T>`/`Task.Run` 造真实多元素异步源，断言逐元素顺序、取消可见性、**双异常时序真值**（体抛 OCE + DisposeAsync 抛异常时收到的是哪个/是否聚合）、await using null 不抛、释放顺序 LIFO；这是「真异步并发」的 C# 预言机，Java 半区以此对照。
- **Java 半区分级**（随 compat 桥交付状态）：M2 要求 await using 全绿（DisposeAsync 映射可编译/可运行）；M3 要求 await foreach 全绿但**并发等价不强求**——逐元素顺序/结果值/释放时序全绿，「生产者-消费者流水线重叠」差异列入差异清单（§10 风险 1）；取消可观察性随 CT compat 进度挂账。
- 项目级断言：pass Success、C38 统计桶计数、residual=0；中间树 `dotnet build -c Release` + `dotnet test -c Release` 退出码 0。

### 7.3 闸门与回归

- residual 闸门测试：桥关 + 严格 → CS2J1841/1849 失败且无部分改写；annotation 白名单反例（展开产物内的 `await DisposeAsync` 不被判残留）。
- **双半区闸门**：.NET 半区 fixture 真值测试 + 转译后 Java 同行为测试并存，差异清单逐项签收/挂账。
- **residual 静态闸门**：对启用范围内的中间树做文本扫描——零裸 `await foreach`/`await using`（或节点已带 skip 诊断记录）。
- 存量测试：[ForeachDeconstructionTests.cs:159-181](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ForeachDeconstructionTests.cs#L159-L181) 仍断言 E07 出口保留 await（C38 在其后，职责不同，不断言 E07 展开）；[StreamAsyncTokenTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/StreamAsyncTokenTests.cs) 空占位类补入 C38 消费侧用例（与 C18 §7.3 对该文件的分工一致：异步流用例归 C38）。
- CLI/链序：C38 不新增排序断言（同 pass）；`--enable-pass async-normalize` 端到端在 `EnableAsyncStreams` 开启方式定稿后补一例（保留目录、指纹缓存、`--list-passes` catalogItems 文案含 C38）。
- 全仓 slnx `-c Release` build/test 全绿；`ProjectPreprocessorsConsistentTests` 按需追加。

## 8. 默认开启验收门槛

`EnableAsyncStreams` 随 async-normalize **长期 opt-in**（与 C18 §8 同节奏，不单独抢默认开）。满足全部条件才评审默认：

1. **compat 桥齐备（硬前提）**：§5.2 清单 1-4 合并且 Java 半区有对应行为测试；IAsyncDisposable 孤本重塑完成、TypeMappings 四条映射落盘；清单 5 的转换器配套关闭。
2. M1 计数成文：csharp-sdk 四子集 + EF Core 画像（catalog §5.1）中 await foreach / await using / IAsync* 类型使用点 / async iterator / WithCancellation 的实际命中率，证明桥的投入有收益面。
3. 启用范围内 residual=0；全部 skip 站点有显式诊断且分类清楚；M2/M3 出口达成，fixture 两半区结论成文。
4. 全仓 Release build/test 全绿；sdk 基线以 `--enable-pass async-normalize`（含 AsyncStreams）跑不退化。
5. §10 风险 1（时序等价）与风险 2（异常聚合）的差异清单被 Java 半区显式签收或确认目标画像不触发；风险 3（每元素 future 分配）有基准数据且无热点退化证据。
6. opt-in 观察期与 C18 一致（至少跨一次主版本回归窗口）。

在 compat 桥齐备前，**默认行为的唯一改进是「显式报错取代静默降级」**——该改进不依赖桥，可随 M2 独立成立。

## 9. 里程碑

| 里程碑 | 内容 | 出口 |
|---|---|---|
| **M1 审计盘点（不写生产代码，可与 C18 M0 合并执行）** | sdk/EF Core 画像计数：await foreach、await using（语句/声明分桶）、IAsync* 类型使用点、async iterator 方法、WithCancellation/ConfigureAwait、双实现类型；net10/latest Roslyn 探针回填 §4.6 break/return 守卫真值表与 await-using null 检查规则；核对 IAsyncDisposable 孤本无引用结论 | 计数报告评审；M2/M3 优先级由数据确认（await using 先行的建议若被命中分布推翻，回到 §2 D5 评审） |
| **M2 await using** | `AsyncStreams/` 建目录（C18 合入后）；AsyncStreamAnalyzer 骨架 + AwaitUsingRewriter（AS-4/AS-5）+ AsyncTypeResolver 的 IAsyncDisposable 面 + CS2J1841/1842/1845/1849 + 选项闸 + 统计桶；**桥未就绪时默认全部显式报错**；单测 5/7/8/11/12 | .NET 半区 await using fixture 绿；Java 半区在孤本重塑后绿（依赖项可并行） |
| **M3 await foreach + compat 桥** | AS-1/AS-2/AS-3 全规则、WithCancellation/ConfigureAwait 消解、保真守卫、AsyncEnumerable/Enumerator 桥与映射（Java 半区）、CS2J1840/1843/1846；双半区行为对照（真异步源、双异常时序）；StreamAsyncTokenTests 补例 | §8 第 1/3/4 条达成；并发差异清单签收/挂账 |
| **M4 async yield 与 C04 联合（可推后/可不做）** | 异步迭代器联合状态机（D7）落地于 `AsyncStreams/AsyncIterator/`，CS2J1844 从拒绝改放行（保留选项）；前置=C04 区域机制稳定 + M1 命中数据；否则维持显式报错终态 | 异步迭代器 fixture .NET 半区行为等价；若画像零命中则书面关闭 M4 |

## 10. 风险

1. **（最高）异步时序等价性**：显式 while + join 把异步流逐元素**串行阻塞化**——结果值/顺序/释放语义可等价，但 C# 异步流的非阻塞调度、生产者-消费者流水线重叠（`Channel`/`yield` 内 `Task.Run`）、线程池让出全部丢失，与 C18 风险 1 同族且对流式场景更敏感（大序列长循环中 join 阻塞可能放大线程池占用）。缓解：承诺范围写死（可编译/顺序/值/释放，不承诺并发）；M3 .NET 真异步 fixture 给真值、Java 半区差异逐项签收；是否值得为流追求 continuation 化属于 C18 D11② 的全局决策，不在 C38 局部解决。
2. **DisposeAsync 异常聚合语义**：await foreach 的「释放异常不屏蔽在途主异常」是 C# 8 刻意区别于同步 foreach 的规则（D6），朴素 try/finally 展开会静默改变双异常下的可观察行为；await using 又恰是普通 finally 语义，两规则相邻极易写反。缓解：§4.6 时序表 + 双异常 fixture 在 .NET 半区锁真值；守卫形状文本断言；M1 探针复核 break/return 路径。
3. **compat 桥性能与成本**：`moveNextAsync()` 每元素一次 `CompletableFuture` 分配 + join，热循环上相对 C# `ValueTask<bool>`/`IValueTaskSource` 池化有数量级差距；桥接口设计若仿照 BCL 全量面（cancellation token 传递、ConfigureAsyncEnumeratorOptions 等）会膨胀。缓解：D5 冻结**最小方法面**（3 接口 6 方法）；M3 加微基准；高级枚举选项一律 C# 层消解或显式报错，不进桥。
4. **取消传播假象**：token 透传进 `getAsyncEnumerator` 只保证数据流不断；compat CT 无 `Register/ThrowIfCancellationRequested`，生产者侧（async iterator，M4）还有 `[EnumeratorCancellation]` 标记参数语义——消费侧「能传 token」不等于「取消可观察」。缓解：fixture 把「token 到达」与「取消生效」分级断言；挂账随 C18 D8/CancellationToken compat 工作。
5. **孤本重塑的认知风险**：[IAsyncDisposable.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/IAsyncDisposable.java) 虽零引用，但「compat 已有 IAsyncDisposable」的表面事实可能让实施者误判桥已就绪而跳过映射设计。缓解：本设计 §1.2 已记录其死桥性质与错误签名；M2 把「重塑 + 映射 + 引用证据」列为一个交付项。
6. **规则/闸门互相误伤**：C18 residual（async lambda/ConfigureAwait/async void）与 C38 residual（await foreach/using/类型点）作用在同一输出树上；C18 的 ConfigureAwait 剥离与 C38 的枚举器 ConfigureAwait 是两个绑定面（Task vs IAsyncEnumerable）。缓解：共享 annotation 白名单、各计各桶；M2 联调「展开产物内 await 不下降」的双向断言（D11）。
7. **async iterator 被迫提前**：EF Core 画像若大量使用 `[EnumeratorCancellation]` 异步流生产者，M4 联合状态机可能成为硬需求，牵动 C18 D11③ 与 C04 排期。缓解：v1 显式报错保证无静默坏产物（可审计、可手工物化 List 规避）；M1 用命中数据决定是否提前投资；联合状态机严格限制在 AsyncStreams/AsyncIterator 子域，不回改轻量规则。
8. **链耗时**：verify build/test 默认开叠加全树 SemanticModel 站点扫描。缓解：复用 C18 已建 compilation 零增量 parse；verify 可关；指纹随注册表自动生效。
9. **源表达式求值次数与临时量提升**：C# 规范要求 foreach/using 的源/资源表达式只求值一次（含异常路径）。展开为 `GetAsyncEnumerator(...)` 时若源是带副作用的调用链或含 ref/out 后读（转换器在 foreach/using 表达式上对此已有 NSE 抛点，[Loops.cs:134-139](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L134-L139)、[SwitchAndResource.cs:232-L237](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L232-L237)），重写必须保持「一次求值」且不得改变后读顺序。缓解：Analyzer 对含后读的源 v1 CS2J1840/1845 defer；单测 14 文本断言接收者只出现一次。
10. **扩展方法 WithCancellation/ConfigureAwait 绑定歧义**：两者都是 `System.Threading.Tasks` 上的扩展方法，用户可自定义同名扩展；SemanticModel 绑定到非 BCL 符号时剥离会改错。缓解：D3 只接受绑定到 BCL 符号的调用，其余 CS2J1846；与 C18 D15「宁拒不错」同原则。

## 11. 并行协调

1. **C18（锚点，强依赖）**：C38 不先于 C18 合入；依赖其 M2 冻结的 `AsyncShapeClassifier` API（只做非破坏新增）、Options internal 预留位、1840-1849 码段、`AsyncStream/*` 文件所有权（C18 §4.7）与 descriptor 扩宽约定（C18 D13）。**对 C18 D11③ / 开放问题 3 的回应**：async iterator 的重量需求被确认存在，但作用域收敛在「异步迭代器方法」子域（D7/M4），普通 async 方法维持轻量版选型；C18 M0 探针若同时统计 async 流站点（本设计 M1 可并入），数据一次采集两份共用。
2. **C40（foreach-source，同插入点）**：按 §3.3 三分表各管各源；契约硬条款——foreach-source 不读不删 AwaitKeyword（C16 FS-4 已写死，CS2J5442 仅计数），C38 不碰非 await foreach；E07 产物保留 await 是双方共同输入假设，若 E07 未来改写该行为须同步改两家。
3. **C04（同步迭代器）**：无编译期依赖、无共享文件；共享仅限方法论（基线快照、skip 先行、区域/finally、命名防撞）；异步迭代器 M4 是双方**唯一交汇点**，联合设计在 M4 启动时单开文档（输入：C04 §4 状态机 + C18 §4.8 雏形 + 本设计 D7），M4 之前 C04 对 async yield 继续不报错也不触碰（其非目标已声明），由 C38 CS2J1844 单点报错，不双重诊断。
4. **C44（using 显式化）**：await using 子题即本设计 AS-4/AS-5（catalogItems 字符串的 `(C44 subtopic)` 即此），C44 不实现、不重复诊断；同步 using 与 ref struct using 仍归转换器内联/C06，边界不变。
5. **C16（string foreach）**：零交集——string 源上写 await foreach 是基线错误；C16/C38 不共享规则代码。
6. **热文件纪律**：[StatementTransformer.Loops.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs)、[StatementTransformer.SwitchAndResource.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs)、[StatementTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs) 是**转换器侧**文件，C38 只做只读证据记录，不改写；未来 C38 稳定后是否在三个静默点加「未预处理显式报错」是转换器侧独立收口任务（同 C15/C16 模式），不夹带在本 pass。
7. **注册表三热文件冲突解法**：C38 实施期 C18 已翻转，故无 stub 删除、无新增 case，唯一编辑是 PassCatalog descriptor 一行 catalogItems/Description（与 C40 扩宽 C16 catalogItems 同款的纯文本冲突）；后合者保留各方文本；stub 计数断言（C18 翻转后 23→22）不受 C38 影响（不删 stub）。
8. **E02 local-function-lift / C45 closure-lift**：异步迭代器**局部函数**先经 E02 提升（链位在前），AsyncIteratorDetector 对残余按 C18 D4 同款纪律处理（能提则提、不能则 CS2J1844 归属到宿主）；await foreach/using 体内捕获型 async lambda 一律沿用 C18 D3(d) 的 CS2J1802 defer，C38 不自带 display class、不与 C45 建编译期依赖。
9. **文档/计划同步**：实施时按 foundation plan 交付包模板补 `docs/superpowers/plans/2026-09-29-c38-async-stream.md`；catalog C38 条目状态列在合入后由「缺失」更新为「部分/已落地（分阶段）」，并修正 L725 关于 compat 的措辞（孤本 IAsyncDisposable 的存在与 §1.2 结论）。

## 12. Worktree 分配（只写不执行）

- 分支：`feature/preproc-c38-async-stream`
- 目录：`.worktrees/preproc-c38-async-stream`
- base：`953293b9`（已只读核实存在：`953293b908e9143437c45f287fb4277e890c398e` — "docs: update doc base commit"；注：当前工作区 HEAD 已前行至 `5e3e796d`，**实施前须先把 C18 分支（`feature/preproc-c18-async-normalize`）合入 base**，因为 C38 依赖其翻转后的 AsyncNormalize 骨架与冻结接口）
- 创建命令（**本设计任务不执行**；由实施环节在 C18 合入后于仓库根 PowerShell 运行）：

```powershell
git worktree add .worktrees/preproc-c38-async-stream -b feature/preproc-c38-async-stream 953293b9
```

- worktree 内实施顺序：M1 计数/探针（可搭 C18 M0 顺风车）→ 等 C18 翻转合入 → M2 `AsyncStreams/` 建目录（只增文件 + PassCatalog 一行扩宽）→ M3 Java 半区桥在**同一 worktree 还是独立 worktree** 由实施评审定（桥属 java compat 工程，建议同一分支但独立提交，便于双半区同测）；主干工作区保持只读；不创建 git 标签、不 push。
