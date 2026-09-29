# C44 using 显式化设计（using 声明/语句 → try/finally）——分治决策与最小工作

**条目**：C44 `UsingDesugar`（catalog P3，★观察项；Layer 3，实验管线 `Lowering/LowerUsing.cs` 仅形态证据）
**日期**：2026-09-29　**状态**：分治设计稿，待评审
**本期结论（先读这段）**：

1. **同步 using 现路径语义正确，不前置、不动生产代码**；本任务不注册独立 pass、不翻独立 stub（注册表现状：[PassStubCatalog.cs:238](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L238) 的 async-normalize catalogItems 已标注 `"C18, C38 (C44 subtopic)"`，C44 无独立条目——保持不变）。
2. **三个真实缺口各归其主**：ref struct disposable → C06（`unsafe-ffm`）；`await using` → C38（`async-normalize/AsyncStreams/`）；yield 内 using → C04（`iterator-lowering`）。本文档给出三个接缝的精确落点与一处需要回填的归属矛盾（§3.2、§11）。
3. **CsCore 全量收敛期**若决策「同步 using 也前置」，本文档第 2/3/4 章已预留可直接实施的完整规则设计（新 pass `using-desugar`，RunAfter `eliminate-goto`）。
4. **worktree 条件使用**：仅做三缺口子题时**不建**本 worktree；本 worktree 仅在「CsCore 同步前置立项」或「跨三 pass 抽共享 UsingFinally 模板」时创建（§12）。

---

## 1. 背景与目标

### 1.1 同步形态现状正确——证据表（2026-09-29 逐行复核）

| 形态 | 现状处理 | 证据 | 判定 |
|---|---|---|---|
| using 语句（含嵌套堆叠） | 直映 Java try-with-resources；沿 `Statement as UsingStatementSyntax` 下钻，把堆叠资源收集为**单一 resource 列表**（LIFO 由 t.w.r 多资源逆序关闭保证），再取最内层 using 的真实语句体 | [StatementTransformer.SwitchAndResource.cs:198-244](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L198-L244)（`TransformUsingStatement`:202；下钻 :205-219；找最内层 :222-224；发射 :243） | 正确（一处已知语义偏差见 §10 风险 4，不阻断） |
| using 语句·表达式资源 `using (expr)` | 标识符直用；其他表达式合成 `_cs2jResource` 临时，类型取语义模型，失败回落 `AutoCloseable` | [SwitchAndResource.cs:272-284](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L272-L284) | 正确 |
| using 声明 `using var x = …;` | 语句级预扫描：块内遇到 using 声明即收集**连续** using 声明的全部资源，把剩余语句整体包成一个 try-with-resources，模拟「作用域延伸到块尾」 | [StatementTransformer.cs:167-190](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L167-L190)（预扫描入口 :167-170）、[:241-249](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L241-L249)（IR 变体）、[BuildUsingDeclarationResources 在 SwitchAndResource.cs:246-253](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L246-L253)、连续收集 [StatementTransformer.cs:457-475](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L457-L475)、递归包体 [:477-495](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L477-L495) | 正确（结构化块内；goto 面见 §10） |
| using 声明的控制流交互 | 无条件跳转分析把「含 using 声明的块」排除出裸跳转判定（跳转须经 t.w.r 出口） | [StatementTransformer.cs:341-344](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L341-L344)（`ContainsUsingDeclaration`:447-455） | 正确 |
| 漏网防护 | using 关键字的局部声明若走到普通声明变换直接 fail-fast（设计上不可达） | [StatementTransformer.Declarations.cs:21-25](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs#L21-L25) | 防线在位 |
| 实验形态证据 | IR 级 `IrCSharpUsingStatement → IrTryCatchStatement + close()`；仅 Java2 对比测试可达，**生产不可达、不可复用代码**（catalog §4 风险 9） | [Lowering/LowerUsing.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerUsing.cs)（全 46 行；核心 :28-38） | 仅借形 |

grep 复核（`src/CSharpToJava.Core/Transformers`，`UsingStatementSyntax|UsingKeyword|IDisposable`）：using 全部处理集中在 Statement 族上述 7 处命中，无第二套分叉实现。

**★C38 调研新发现（本设计复核确认）**：using 声明判定 [StatementTransformer.cs:444-445](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L444-L445) 的 `IsUsingDeclaration` **只检查 `UsingKeyword`、不检查 `AwaitKeyword`**；`TransformUsingStatement` 同样不读 `AwaitKeyword`。因此 `await using var x = …;` 今天被静默按同步 using 降为 try-with-resources（`DisposeAsync()` 丢失为 `close()`）。该缺口归属 C38，证据已引入 [C38 设计 §1.1](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md)，本条目不重复实现。

### 1.2 三个真实缺口（同步形态之外的全部缺口）

| # | 缺口 | 不可在现路径表达的原因 | 归属 | 归属文档锚点 |
|---|---|---|---|---|
| G1 | **ref struct disposables**（`Span` 族、自定义带 `Dispose()` 的 ref struct 的 using） | ref struct 不能装箱、不能实现接口（CS8377），进不了 try-with-resources 的 `AutoCloseable` 类型面；Java 侧根本无栈-only 类型 | C06 `unsafe-ffm` | catalog L889、§3.3（L1085 值语义族 / L1091 unsafe 家族双重列名）；C06 D9/R7 |
| G2 | **`await using` 语句与声明、`IAsyncDisposable`、pattern-based `DisposeAsync`** | 释放须 `await`，同步 `close()` 静默替换是语义错误 | C38（async-normalize） | [C38 设计 D12](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md)（AS-4/AS-5） |
| G3 | **yield 迭代器内 using 的 Dispose 时序** | 迭代器编译为状态机后，using 必须随保护区域表进入 `MoveNext/Dispose` 两条路径；提前 `Dispose()` 枚举器须正确执行 finally 栈 | C04 `iterator-lowering` | [C04 设计 D5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c04-iterator-lowering-design.md)、M3 |

catalog L1033 已定调：「**C44 using 显式化仅 ref struct/yield 组合子题随 C06/C04/C38 顺带处理，同步形态维持转换器内联**」。

### 1.3 本条目的分治定位与「不独立成链」结论

C44 是 catalog 中少有的**分治型观察条目**：其价值不在新增通道，而在 ① 记录同步形态「为何不前置也是对的」的证据；② 把三个子题的接缝、归属目录、诊断码段一次写清，防止三个 pass 各自重复实现 using 归一；③ 为 CsCore 收敛期预留一份拿来即用的同步前置规则设计。

**不独立成链**的三条理由：

1. 同步形态没有错误输出（P3 观察项标准：前置仅收敛输入面，不修正确性）；
2. 三个缺口的语义载荷分别属于三个已立项 pass 的核心机制（FFI 拒绝/白名单、异步状态、迭代器区域表），独立第四套 using 实现必然与三者争抢同一语法面；
3. 注册表无独立 stub 是**有意状态**，不是遗漏——async-normalize 的 catalogItems 字符串已显式承载 C44 子题标注。

### 1.4 目标（本期）

- 交付三份可执行的接缝说明（子题规则名、落点目录、统计桶、诊断码段、交叉 fixture 义务），其中 G1 需要回填一处归属矛盾（C06 现 spec 把 ref struct using 推出门外，与 catalog 矛盾，见 §3.2）。
- 预留设计：CsCore 期 `using-desugar` pass 的完整规则集、链位、组件划分（§2.4、§3.3、§4.2），触发条件成立时直接进入 plan 阶段。
- 明确 residual 口径：**本期同步 using 允许保留在中间树**（现状正确）；只有 G1/G2/G3 分别在其归属 pass 的 residual 闸门中为零。

---

## 2. 关键设计决策表

> 标注【预留】的决策为 CsCore 同步前置期生效；标注【本期】的决策约束三个归属 pass。

### 2.1 同步 using 语句目标形态（【预留】完整 C# 规则）

基本展开（引用类型资源，null 检查是 C# using 语义的硬性部分——`using(null) { }` 合法且不抛，资源为 null 时**不调用** Dispose）：

```csharp
// 输入
using (var r = Make()) { Body(); }
// 输出
{
    var r = Make();
    try { Body(); }
    finally { if (r != null) r.Dispose(); }
}
```

| 决策 ID | 主题 | 规则 |
|---|---|---|
| D1 | 引用类型资源 | finally 内 `if (r != null) r.Dispose()`；表达式资源 `using (e)` 先提局部 `var __cs2jUs = e;`（只求值一次）再守 null |
| D2 | 非空值类型资源（readonly struct 等实现 IDisposable 的 struct） | **不装箱、不加 null 守卫**：直接约束调用 `r.Dispose();`（Roslyn 降为 constrained `callvirt`，无 box）。这是前置到 C# 层相对 Java t.w.r. 的一个语义收益点 |
| D3 | `Nullable<T>` 资源（T 为 IDisposable struct） | `if (r.HasValue) r.Value.Dispose();`（constrained）；精确 IL 是否经 box 以 Roslyn 探针为准（§13 开放问题 2），fixture 锁行为不锁 IL 文本 |
| D4 | 堆叠 using | **嵌套** try/finally、LIFO：外层资源先获取后释放。必须嵌套而非单 finally 多语句——后一资源获取表达式抛异常时，前一资源仍须释放（嵌套形态天然保证；单 finally 不保证） |
| D5 | 同声明多资源 `using (T a = .., b = ..)` | 按声明序展开为 D4 嵌套；释放逆序 |
| D6 | 隐式只读 | using 变量（语句与声明）隐式 readonly：重写器不得产生对资源变量的再赋值；资源变量不得作为 `ref/out` 实参（编译器基线已拒，规则不补偿） |

### 2.2 using 声明的作用域边界（【预留】）

```csharp
// 输入：using 声明作用域 = 所在块末尾；连续声明按逆序释放
{
    using var a = Make();
    using var b = Make();
    Body();
}   // b.Dispose(); a.Dispose();
// 输出：块尾包 try/finally，结构同 D4
```

| 决策 ID | 主题 | 规则 |
|---|---|---|
| D7 | 作用域终点 | using 局部生存期延伸到**所在作用域末尾**，同块多个 using 局部按声明逆序释放（C# 8 提案已核） |
| D8 | 跨退出边 | `return`/`break`/`continue`/异常离开作用域均须经过 finally——C# try/finally 原生覆盖，重写器只需把声明点之后到块尾的语句包进 try，**不手工线程化跳转** |
| D9 | **goto 顺序** | 【预留 pass 排在 E14 eliminate-goto **之后**】：用户 goto/goto case 已被 E14（及 C03）消除，重写器看不到 goto 出入边，无需复刻编译器的 goto-threading。若输入残留 goto（理论不可达），residual 闸门报错而不是部分改写 |
| D10 | switch case 边界 | using 声明**不得直接位于 case 标签**下，必须在 case 内的块中（编译器基线 CS 规则）；作用域容器取该块，不取整个 switch section |
| D11 | 声明符约束 | 每个 using 声明符必须有初始值设定项；不得是 `out var` 一部分（基线已拒）；不满足者按基线优先原则不归本 pass |
| D12 | 块中位置 | 只把「声明点之后」的语句纳入 try 体；声明点之前的语句（含前序 using 预发资源）留在 try 外，保证获取顺序与异常面正确 |

### 2.3 Pattern-based Dispose（duck-typed，C# 8 已核）

官方已核（[C# 8 pattern-based using 提案](https://learn.microsoft.com/dotnet/csharp/language-reference/proposals/csharp-8.0/using)、[using 语句文档](https://learn.microsoft.com/dotnet/csharp/language-reference/statements/using)）：类型具有**可访问的、无参数的、`void` 返回类型的实例 `Dispose` 方法**即可参与 using 语句/声明，无需实现 `IDisposable`；ref struct 只能走此模式（不能实现接口）。

| 决策 ID | 主题 | 规则 |
|---|---|---|
| D13 | 绑定顺序 | SemanticModel 分类：① 资源类型存在到 `IDisposable` 的隐式转换 → 走接口槽（显式接口实现时经 `(IDisposable)r` 调用）；② 否则查可访问的参数无参 `void Dispose()` 模式方法 → 直接调用 `r.Dispose()`；③ 两者皆无 → 编译错误（CS1674 族），属基线条目，不是本 pass 诊断 |
| D14 | 优先级探针义务 | Roslyn 对「同时实现接口且自带可访问 Dispose」类型的实际选择口径，实施时以 `Compilation.TryGetDisposeMethod` 探针 + 双实现 fixture 锁定（规则按探针结果写死，禁止凭文本猜测）；foreach 的 pattern 优先惯例不可直接类推 |
| D15 | ref struct 模式 | 仅模式路径；该形态**不在【预留】同步 pass 处理**（G1，归 C06），同步 pass 的分类器遇到 IsRefLikeType 资源一律跳过并交 C06 判定 |
| D16 | await 模式 | `DisposeAsync()` 返回可 await 类型的模式面归 C38（CS2J1842），同步规则零接触 |

### 2.4 三个缺口子题的规则形态（【本期】接缝契约）

| 缺口 | 规则形态要点 | 归宿 |
|---|---|---|
| G1 ref struct using | Java 类型面无法表达栈-only 释放。v1 规则 = **显式诊断拒绝**（白名单外），不产部分改写；BCL 白名单（`Span<T>` 等实际无托管资源的 ref struct）是否归一为空操作/特殊映射，随 C06 M4 白名单选型评估，不提前承诺 | C06 新增规则 R9（建议名 `RefStructUsingRewriter`），诊断码段建议 UF05x（新增 UF055，§6） |
| G2 await using | 语句与声明两形态同归：try/finally + `await r.DisposeAsync()`，引用类型带 null 守卫；在途主异常优先（ok 标志：正常完成才让释放异常冒泡，异常在途时吞释放异常——C# 8 规则）；LIFO 嵌套同 D4 | C38 `AsyncStreams/AwaitUsingRewriter.cs`（AS-4/AS-5），CS2J1841/1842/1849 |
| G3 yield 内 using | 先在语法模型上把 using（语句/声明，含 pattern-based Dispose）归一为 try/finally，再纳入迭代器保护区域表；跨挂起点存活的资源提升为字段并登记区域 finally；MoveNext 正常路径与枚举器 Dispose 路径共用同一份 finally | C04 `IteratorStateMachineRewriter`（D5，M3）；ref struct 跨 yield 由 CS2J0403 拒；await using 不在 C04 |

### 2.5 异常语义（同步 vs 异步，两族不可互相抄）

- **同步 using**：finally 内 `Dispose()` 抛异常时正常冒泡；若 try 体已有在途异常，**在途异常被丢弃**、Dispose 异常成为最终异常（finally 语义，无 suppressed 机制）。
- **await using（C# 8）**：在途异常优先，释放异常被吞（C38 设计的 ok 标志形态）。
- **现路径的已知偏差**：Java try-with-resources 把 close 异常记为 `addSuppressed`（在途异常保留、释放异常挂附）——与 C# 同步语义不同。该偏差今天即存在，记录在 §10 风险 4，本期不修；【预留】pass 产出的 C# try/finally 经主转换器后走普通 finally 通道，反而自然消除该偏差。

---

## 3. 注册表与链位契约

### 3.1 本期动作：零注册表改动

- **不新增 stub、不新增 pass id、不新增目录名**。[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) 无 C44 条目，保持；async-normalize 条目（:232-238）catalogItems = `"C18, C38 (C44 subtopic)"` 是 C44 子题的唯一注册表面，C18 翻转/C38 扩宽时按 [C38 §3.1](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c38-async-stream-design.md) 既有计划修改，本文档不产生新动作。
- 不新增 CLI 选项、不新增适配器；`--list-passes` 不出现 C44。

### 3.2 子题规则落点表（实施时代码落在哪里）

| 子题 | pass / id（状态） | 规则目录与文件 | 规则/诊断 | 归属文档 |
|---|---|---|---|---|
| G1 ref struct using | `unsafe-ffm`（stub，Stage=ABI） | `src/CSharpToJava.Core/UnsafeFfm/RefStructUsingRewriter.cs`（建议新增 R9，与 R7 `RefStructDeclRewriter.cs` 同目录并列） | R9 + UF055；复用 UnsafeFfmAnalyzer 的 ref struct 声明清单与 SemanticModel 绑定 | [C06 §4](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c06-unsafe-ffm-design.md)（需按本文档回填一行，见下） |
| G2 await using | `async-normalize`（stub，StateMachine；C18 翻转后承载） | `src/CSharpToJava.Core/AsyncNormalize/AsyncStreams/AwaitUsingRewriter.cs` | AS-4（语句）/AS-5（声明）；CS2J1841/1842/1849；统计 `AwaitUsingExpanded`、`AsyncDisposeGuardsEmitted` | C38 §2.2/§4.3，**无需任何回填**（D12 已写明不另立规则） |
| G3 yield 内 using | `iterator-lowering`（stub，StateMachine；Core 目录已存在骨架） | `src/CSharpToJava.Core/IteratorLowering/IteratorStateMachineRewriter.cs` + `ControlFlow/CfgBuilder.cs`（保护区域表） | D5 using 归一（M3）；区域/资源字段登记；CS2J0403 拒 ref struct 跨 yield | C04 D5/M3，**无需回填**（已显式包含 pattern-based 与 using 声明） |

**★需评审拍板的归属矛盾（G1）**：catalog 两处（L889、§3.3 L1091 unsafe 家族/L1085 值语义族）明确把「C44 的 ref struct disposables 子项」划入 C06；但 [C06 设计 D9（:101）](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c06-unsafe-ffm-design.md#L101)、R7（:163）、§11（:306）三处写明「`using ref struct` 子题归 C44，本 pass 不触碰」。两边不可能同时为真。本设计的建议（与 catalog 权威结论一致）：

1. C06 spec 在 M4 段补一条 **R9 `RefStructUsingRewriter`**：扫描 using 语句/声明资源类型为 IsRefLikeType 的站点，白名单外发 **UF055**（Error，「ref struct disposable cannot be represented in the Java type surface」），白名单内按 C06 统一选型处理；
2. R7 文案保持「只判声明、不碰 using 体」，using 站点由 R9 拥有，两规则同 pass 内顺序执行、互不重叠；
3. 该回填是 C06 M4 实施时的一行文档改动 + 一个规则文件，不需要新开 pass——这正是「C44 不独立成链」的具体含义。

### 3.3 【预留】CsCore 同步前置的链位推荐

触发条件成立（§8.2）时，**推荐新建独立 pass** 而非挂入 expression/control 族：

| 项 | 推荐值 | 理由 |
|---|---|---|
| pass id | `using-desugar` | 与三归属 pass 的 using 规则按「资源种类/上下文」切分，边界可判定（见下「职责切分」） |
| Stage | ControlFlow | 块级结构改写；需要 SemanticModel（D13 绑定、D15 分类），**不得并入 C28/C43 的 Layer 0 纯语法合批**（§11.4） |
| RunAfter | **`eliminate-goto`**（先例：`platform-isolation`/`concurrency-normalize`/`dynamic-desugar` 三个 stub 均 RunAfter EliminateGoto，[PassStubCatalog.cs:266,278,288](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L266)） | D9：goto 已先行消除；且拓扑上自然晚于 unsafe-ffm/closure-lift/iterator-lowering/async-normalize（它们都在 goto 之前），保证到达本 pass 的 using 只剩「非异步、非 ref struct、非迭代器体」的同步普通形态 |
| catalogItems | `"C44"` | 翻转时注册表三热文件（PassStubCatalog 加条 → 翻转日删条、PassCatalog 加 descriptor、PassChainFactory 加 case）按 foundation plan 标准工序 |
| 默认开关 | 翻转即默认关（opt-in），达 §8.2 门槛后再议默认开 | 观察项转实现项的统一纪律（catalog §4 风险 10） |

**拒绝的备选**：挂入 foreach-source（C16/C40）或 pattern-desugar（C03）。前者只拥有迭代源插入点，using 声明是任意块内的作用域改写；后者无 SemanticModel 块重组定位。混挂会让 residual 闸门与统计口径无法独立。

**职责切分（拓扑序保证无歧义）**：`async-normalize` 先吃所有带 AwaitKeyword 的 using；`unsafe-ffm` 先吃 ref struct 资源（R9）；`iterator-lowering` 先吃迭代器方法体内的全部 using（D5）；`using-desugar` 只处理剩余同步 using 语句/声明（含 pattern-based 普通类/struct 资源）。四者对同一节点不存在竞争窗口。

---

## 4. Core 组件设计

### 4.1 本期实际交付：三份接缝说明（本文件）+ 可选的小规则补丁

本期不新增 Core 目录、不改生产代码。实施期的物理改动全部发生在三个归属 pass 内（落点见表 §3.2）。本文件即「接缝契约」，C06 spec 的 R9 回填是唯一需要的文档联动。

### 4.2 【预留】CsCore 期方案（本期不实施）

目录 `src/CSharpToJava.Core/UsingDesugar/`，按 foundation plan 交付包模板组织：

```
UsingDesugarOptions.cs        # Strict（默认 true）
UsingDesugarStatistics.cs     # 通用 5 字段 + §5.2 专有桶
UsingDesugarDiagnostics.cs    # CS2J44xx（§6，码段预留，实施时冻结）
UsingDesugarResult.cs
UsingDesugarPreprocessor.cs   # 门面：RewriteFile(code, SemanticModel, options)，单次 parse
UsingScopeAnalyzer.cs         # ① using 站点发现 ② 资源分类（引用/struct/Nullable/pattern/ref-struct/异步）
                              # ③ 作用域容器与退出边校验（D7-D12）④ skip 集在重写前固定
ResourceCollector.cs          # 堆叠/连续声明收集、LIFO 嵌套计划、表达式资源临时名、只求值一次
DisposeBindingResolver.cs     # D13/D14：IDisposable 转换 vs pattern Dispose，结果缓存于站点 plan
TryFinallyRewriter.cs         # 按 plan 做块重组：局部提升 + 嵌套 try/finally + Dispose 语句合成
UsingDesugarResidualGate.cs   # 输出树 UsingStatementSyntax/using 声明零残留（白名单=已发诊断节点）
```

设计约束（沿用各 pass 通行纪律）：

1. **语义模型驱动**：资源分类与 Dispose 绑定全部走 `ITypeSymbol`/`IMethodSymbol`，禁止文本/类型名匹配；单文件模式缺跨文件信息时按既有 090 模式 skip 并诊断。
2. **基线优先**：输入树先取编译诊断快照；CS1674（using 了不可释放类型）等基线错误不二次报。
3. **拒绝优先 + 幂等**：skip/诊断节点原样保留；合成节点带 annotation，重跑 Changed=false。
4. **形态借形不借码**：[LowerUsing.cs:28-38](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerUsing.cs#L28-L38) 的 try/finally 形态可参考；IR 不同、生产不可达（catalog §4 风险 9），禁止引用其类型。
5. **不碰三归属面**：分析器分类到 IsRefLikeType / AwaitKeyword / 迭代器方法体三类节点时只记 skip 原因，不重写；residual 闸门不计这三类（它们由上游闸门各自收口）。

### 4.3 文件归属总表

| 工作项 | 何时做 | 落点 | 本 worktree？ |
|---|---|---|---|
| G2 await using（AS-4/AS-5） | C38 M2 | `AsyncNormalize/AsyncStreams/`（C38 worktree） | 否 |
| G3 迭代器内 using | C04 M3 | `IteratorLowering/`（C04 worktree） | 否 |
| G1 R9 + UF055 + C06 spec 回填 | C06 M4 | `UnsafeFfm/RefStructUsingRewriter.cs`（C06 worktree） | 否 |
| 共享 UsingFinally 模板抽提（可选，§11.3） | 第二个消费方落地时 | 见 §11.3 建议 | **是（启用条件之一）** |
| 同步 using 前置全套 | CsCore 收敛期 | `UsingDesugar/` + 注册表三热文件 | **是（启用条件之一）** |

---

## 5. CLI 集成

### 5.1 本期

无独立适配器、无 verb、无 `--enable-*` 选项；不进任何执行计划。

### 5.2 统计计数如何挂在归属 pass 上

| pass | 追加/已有统计桶 | 口径 |
|---|---|---|
| C38（已有设计） | `AwaitUsingExpanded`（分语句/声明/堆叠三子桶）、`AsyncDisposeGuardsEmitted` | C38 §5 已列，按其摘要行输出，C44 不另加 |
| C04 | 已有 `FinallyRegions`；建议子桶 `UsingRegionsNormalized`（using 归一而来的区域数）、`UsingResourcesHoisted`（提升为字段的资源数） | 并入 IteratorLoweringStatistics 通用 5 字段之外的专有桶，摘要行不单列 C44 名义 |
| C06（R9） | 建议新增 `RefStructUsingSites`（命中站点）、`RefStructUsingRejected`（UF055）、`RefStructUsingWhitelisted` | 并入 UnsafeFfmStatistics「按规则命中数/拒绝数」既有口径 |
| 【预留】using-desugar | `UsingStatementsExpanded`、`UsingDeclarationsExpanded`、`StackedResourceGroups`、`PatternDisposeCalls`、`NullGuardsEmitted`、`BoxlessStructDispose`、`SkippedAsyncOrRefStructOrIterator`、`ResidualUsingSites` | 翻转时按 IPassStatistics 通用契约实现 |

原则：**对用户可见的 pass 身份只有三个归属 pass**；C44 从不出现在 CLI 摘要中（观察项不制造第四套计数面）。

---

## 6. 诊断策略

| 情形 | 性质 | 由谁发码 | 码 |
|---|---|---|---|
| 资源非 IDisposable 且无 pattern `Dispose()` | 用户源码编译错误（CS1674 族） | Roslyn 基线，**不到任何 pass** | — |
| ref struct using 无法表达（白名单外） | 显式拒绝，禁止流入 try-with-resources 静默错译 | **C06 R9 发** | 建议 **UF055**（Error；码段随 C06 §6 表追加，最终编号实施时冻结） |
| pattern-based `DisposeAsync`/ref struct 异步释放 v1 不开放 | 显式报错 | C38 | CS2J1842（已定） |
| await using 残留（C38 启用、未被规则吃下） | residual | C38 | CS2J1849（已定） |
| ref struct/ref local 跨 yield 边界 | 状态机不可提升 | C04 | CS2J0403（已定） |
| 【预留】同步 using 残留/绑定失败 | residual / 内部缺口 | using-desugar | 建议 CS2J4401（residual）、CS2J4402（绑定不可解析，防御性），实施时冻结 |

统一红线（catalog §4 风险 12）：任何一类 using 站点都不得被 TODO 注释、占位或原样透传「静默处理」；无法等价表达时显式诊断。当前同步内联路径是**有语义保障的主动转换**，不属于静默透传。

---

## 7. 测试计划

### 7.1 同步形态（【预留】pass 的 Core 单测；当前作为主转换器既有行为回归锁）

建议测试类 `tests/CSharpToJava.ProjectTests/UsingDesugarUnitTests.cs`（内联源码 → CSharpCompilation → Preprocess → 文本/形态断言 + 输出可编译断言，仿 JavaKeywordRewriterUnitTests）；在前置 pass 落地前，同组用例以**主转换器回归测试**形态锁定现状：

1. using 声明跨 `return`：声明后块尾前 return，Dispose 先于返回（.NET 半区探针计数恰好一次）。
2. using 声明跨异常：try 体抛异常时 Dispose 仍执行且异常原样冒泡。
3. 堆叠 LIFO：三层嵌套 using，断言释放顺序与「第二层获取表达式抛异常时第一层仍释放」。
4. null 资源：`using (IDisposable)null` 不抛；引用类型资源为 null 不调 Dispose（探针计数 0）。
5. 值类型资源：readonly struct : IDisposable，断言输出无 box 形态（中间树层面为直接 `r.Dispose()`）、无 null 守卫。
6. Nullable<T> 资源：HasValue 两分支探针。
7. pattern-based Dispose：不实现 IDisposable 的 class/struct（各一）带可访问无参 void Dispose；**双实现探针类型**（既实现接口又自带 Dispose）锁定 D14 实际选择。
8. using 声明作用域：同块多声明逆序；声明点之前语句不在 try 体内；case 块内 using 的作用域终点。
9. 表达式资源 `using (Make())`：只求值一次（探针计数）。
10. 隐式只读：资源变量再赋值/作 ref 实参为基线编译错误（断言 pass 不补偿）。

### 7.2 三缺口组合（交叉 fixture 义务，落在归属 pass）

| 组合 | fixture 归属 | 引用方式 |
|---|---|---|
| ref struct using（自定义 ref struct Dispose、Span 族白名单两例） | C06 fixture sln（unsafe 家族专项） | C06 §7；R9 用例：UF055 断言 + 白名单行为断言；本 pass 不另建 |
| await using 语句/声明/堆叠/pattern | C38 fixture | 引用 C38 §7 第 5/7/8 组与 .NET 半区 await using fixture，不复制 |
| yield 内 using（using 块/声明/多资源/嵌套；枚举到一半 Dispose() 的时序） | C04 专项 fixture sln | 引用 C04 §7.1 第 6 组、§7.2（提前 Take、using 文件/内存流、两半区计数断言） |
| 跨族交界（固定/建议各 1 例，仅登记在各自 pass 的联合 fixture 清单） | C06×C04：fixed/Span 局部与 yield+using；C38×C04：async iterator 占位用例（M4） | C06 §11 已要求交叉 fixture；C04 §11 第 5 条已要求三方交界 fixture |

### 7.3 闸门与回归

- 验证命令（实施期执行，本设计阶段**不执行**）：`dotnet build -c Release`（CSharpToJavaConverter.slnx）+ 三工程 `dotnet test -c Release`（CSharpToJava.Tests / ProjectTests / ConversionTests）。
- **residual 口径（本条目特有）**：同步 using 语句/using 声明在中间树中**允许保留**（现状正确，主转换器负责吸收）；启用范围内的 residual 为零要求只针对三个缺口——G1 随 unsafe-ffm residual 闸门（UF010 族扩展到 using 站点白名单口径）、G2 随 CS2J1849、G3 随 C04「输出无 yield 令牌」闸门的区域覆盖断言。
- CLI：三归属 pass 各自的 `--list-passes`/摘要行/退出码测试覆盖新增统计桶；不存在 C44 名义的 CLI 测试。
- 【预留】pass 落地时追加：启用后输出树零 UsingStatementSyntax/using 声明（skip 白名单外）；`EnableUsingDesugar=false` 时零改写、零快照漂移。

---

## 8. 默认开启验收门槛

### 8.1 本期（观察项 + 子题随属）

- C44 本身无开关、无门槛可谈。
- G1/G2/G3 分别随 C06/C38/C04 各自的默认开启门槛（三份设计文档 §8 已定：build/test 全绿 + csharp-sdk 基线不退化 + 各自原型/两半区关口）。本条目不新增、不稀释这些门槛。

### 8.2 【预留】M2 同步前置的启动条件与门槛

**启动条件（任一）**：

1. 统一引擎/CsCore 收敛立项（catalog §5.2/§5.4），主转换器输入面需要移除版本/结构糖分支——using 内联路径（§1.1 六个站点）列入收口清单；
2. 三归属 pass 全部翻转 Implemented，using 四类形态（同步/异步/ref struct/迭代器内）在 C# 层都有明确承接，前置不再产生孤儿站点；
3. §10 风险 4（异常屏蔽偏差）被要求修复——同步前置是最自然的修复路径。

**默认开启门槛**：① §7.1 全组 + 三方交叉 fixture 在 .NET 与 Java 两半区全绿；② 三工程 Release build/test 全绿；③ csharp-sdk 基线全量转换不退化（产物行为差异清单仅含已接受的异常语义修正项）；④ 转换器内联 using 路径删除/降级为防御性 fail-fast 的同步 PR 与本 pass 同批（避免两套事实标准，catalog §4 风险 10）。

---

## 9. 里程碑

| 里程碑 | 内容 | 分支/交付 |
|---|---|---|
| **M1（本期，协调/文档）** | 本设计稿；C38 AS-4/AS-5、C04 D5（M3）、C06 R9（M4）按各自计划交付三个子题；C06 spec 回填 R9/UF055 一行；交叉 fixture 义务登记。**本分支不写代码**，仅作为协调/文档分支存在 | 仅本文档；代码在 C06/C38/C04 各自 worktree |
| 检查点（随三 pass 合并） | 每个归属 pass 合入时复核：using 站点未被重复实现、统计桶口径一致、residual 白名单包含「非本 pass 资源种类」的 skip | 各 pass 评审清单加一条「C44 接缝核对」 |
| **M2（CsCore 期，条件触发）** | §8.2 条件成立 → 注册 `using-desugar` stub → 实现 UsingDesugar 全套 → opt-in 翻转 → 门槛达成后评估默认开；同步删除转换器内联 using 路径 | 使用本 worktree（§12） |
| M2.1（可选，可早于 M2） | 跨三 pass 的共享 UsingFinally 模板抽提（§11.3） | 使用本 worktree 的唯一另一情形 |

---

## 10. 风险

1. **using 声明作用域误判（最高，针对 M2）**：块尾边界、case 块边界（D10）、嵌套块中声明点之前/之后语句切分（D12）出错会造成「早释放/晚释放/漏释放」。缓解：作用域容器判定走语法祖先结构 + 作用域结束探针 fixture（每类容器一个计数用例）；**强依赖 D9 顺序——E14 必须先行**，若拓扑被改到 goto 之前需重做 goto-threading 设计（回退方案：直接报 skip 不重写）。
2. **switch 穿透与 goto 残留**：C# 虽禁止空 case 隐式穿透，但 goto case/label 合法；D9 依赖 C03（pattern switch 归一）+ E14 已消除这些边。若上游 skip 产生残留 goto（如 C04 CS2J0406 拒绝节点保留原文），M2 pass 对含 goto 的块整体 skip + 诊断，不做局部改写。
3. **null 语义与值类型装箱**：漏掉 null 守卫会在 `using(null)` 上抛 NRE（C# 合法不抛）；给 struct 资源误加守卫/强转会引入装箱与调用口径差异（D1-D3）。缓解：分类全部基于 ITypeSymbol（IsReferenceType / IsValueType / NullableAnnotation），三分类各有探针 fixture。
4. **try/finally 异常屏蔽（现存偏差登记）**：C# 同步 using 在 try 体与 Dispose 同时抛异常时保留 **Dispose 异常**（在途异常丢失）；当前 Java t.w.r. 产物用 addSuppressed **保留在途异常**——行为不同。今天不修；M2 前置后天然收敛为 C# 语义，届时需在 sdk 基线上评估有无代码依赖 suppressed 形态（fixture：双异常探针，明确期望）。
5. **Pattern 绑定口径（D14）**：双实现类型、显式接口实现、可访问性跨程序集边界三种情况若凭猜测实现会调错槽位。缓解：实施第一步先写 Roslyn 探针锁定 `TryGetDisposeMethod` 行为，再写规则。
6. **重复建设（本条目的主要防范对象）**：若 C06/C38/C04 各自实现一份 using→try/finally，会产生三套命名、三套异常语义、三套统计。缓解：§3.2 落点表 + §11.3 共享模板 + M1 检查点评审动作。
7. **归属矛盾久拖**：C06 spec 现状把 G1 推给 C44（§3.2）。若不在 C06 M4 前回填，ref struct using 将成为「三不管」站点并静默流入 t.w.r.。缓解：本设计 §3.2 给出明确建议与码位，评审一次拍板。

---

## 11. 并行协调

### 11.1 三个归属 pass 的热文件与规则目录

| pass | 规则目录（using 相关） | 注册表热文件触碰点 | worktree |
|---|---|---|---|
| C06 unsafe-ffm | `UnsafeFfm/RefStructUsingRewriter.cs`（R9，新增；R7 同目录不动） | PassStubCatalog 删一条 / PassCatalog 加一条 / PassChainFactory 加一 case（翻转日） | `.worktrees/preproc-c06-unsafe-ffm`（base `f68a783c`） |
| C38 async-normalize | `AsyncNormalize/AsyncStreams/AwaitUsingRewriter.cs` | 无独立热文件（C18 翻转承载；C38 仅扩 catalogItems 一行） | `.worktrees/preproc-c38-async-stream`（base `953293b9`，且须先合 C18） |
| C04 iterator-lowering | `IteratorLowering/IteratorStateMachineRewriter.cs`、`ControlFlow/CfgBuilder.cs` | 同三热文件（翻转日） | `.worktrees/preproc-c04-iterator-lowering`（base `f68a783c`） |

三 pass 的注册表文本冲突是预期冲突点，按 C04 §11 第 5 条既有规则解决（后合者保留全部 descriptor 与各自边），无语义冲突。

### 11.2 E14 goto 先行

M2 链位（RunAfter eliminate-goto）是硬顺序不是偏好：using 声明的退出边处理因此退化为纯结构化 try/finally 合成。协调义务：M2 立项时复核 E14 在拓扑表中仍位于所有 StateMachine/ControlFlow pass 之后；C04 发射的 goto 由 E14 的 StateMachineBuilder 吃下（C04 风险 4 已列），using-desugar 不承担 goto 区域展开。

### 11.3 C40 foreach Dispose 同构与共享 UsingFinally 模板

[C40 设计 FA-4](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c40-foreach-source-design.md) 的枚举器骨架（`EnumeratorLoweringBuilder.cs` 在 `DisposeMethod != null` 时生成 try/finally + Dispose，区分 pattern/接口/显式接口/值类型不装箱）与 using 归一的 finally 合成**同构**；C38 的 await using、C04 的区域 finally 也含同一族构造。建议：

1. **第一消费方先在自有目录内实现**（按当前各 spec 计划，不提前抽象）；
2. **第二个消费方落地时抽提**共享件，建议落点 `src/CSharpToJava.Core/Shared/DisposeFinallyFactory.cs`（新建 `Shared/` 目录；只放 SyntaxFactory 级无状态助手：单资源 Dispose 语句合成、null 守卫、LIFO 嵌套 try、ok 标志异步版），调用方：C40 EnumeratorLoweringBuilder、C38 AwaitUsingRewriter、C04 IteratorStateMachineRewriter（仅复用「单资源 Dispose 语句」子函数，区域表/状态区间不共享）、M2 的 TryFinallyRewriter；
3. 抽提属于跨 pass 重构，是启用本 worktree 的两个条件之一（§12）；若抽提发生时本 worktree 不存在，落在当时第二个落地的 pass worktree 中亦可，本设计不强制分支归属，但三份 spec 须同步引用。

### 11.4 不与 Layer 0 混

C28（指令/trivia）/C43（词法字面量）是「纯语法、幂等、不需语义模型、一次 parse 合批」族（catalog §3.3 L1080）。using 归一是**语义驱动的块结构改写**（Dispose 绑定、资源分类、作用域分析），M2 pass 永远不进入 Layer 0 合批试点，即使统一引擎落地也应保持独立 Analyze 阶段。

### 11.5 与其余相关条目的边界

- C07 struct 值语义：值类型资源的防御性 clone 与本 pass 无交叉（using 变量只读，D6）；若 M2 与 C07 同批，fixture 互跑即可。
- C10/C42：无 attribute/平台语义面交叉。
- C36 版本防线：using 声明（C# 8）本身 parse 无门槛（Roslyn 包已支持），不进版本差集审计。

---

## 12. Worktree 分配（只写不执行）

- 分支：`feature/preproc-c44-using-desugar`
- 目录：`.worktrees/preproc-c44-using-desugar`
- base：`953293b9`（与 C38 同一 doc base；已由 C38 设计只读核实存在 `953293b908e9143437c45f287fb4277e890c398e`）
- 创建命令（**本设计阶段不执行**；`.worktrees/` 已被 .gitignore 忽略）：

```powershell
git worktree add .worktrees/preproc-c44-using-desugar -b feature/preproc-c44-using-desugar 953293b9
```

**使用条件（重要，避免空分支）**：

1. **若实施时仅做三个缺口子题（M1 的默认情形）：不创建本 worktree。** G1 代码在 `.worktrees/preproc-c06-unsafe-ffm`、G2 在 `.worktrees/preproc-c38-async-stream`、G3 在 `.worktrees/preproc-c04-iterator-lowering` 内完成；本设计文档在主干随文档流程合入，主干工作区在这些任务中保持只读协调角色（评审接缝、核对统计桶与 residual 口径）。
2. **仅以下两种情形创建本 worktree**：
   - §8.2 启动条件成立，实施 M2（注册并实现 `using-desugar`，含注册表三热文件改动与转换器内联路径收口）；
   - 执行 §11.3 的跨 pass 共享 `DisposeFinallyFactory` 抽提（改动同时触及 C40/C38/C04 中两方以上目录，不宜挂在任一方分支）。
3. worktree 内禁止 git 写操作的约束随各任务通用纪律；不创建标签、不 push；不实施 dotnet build/test 以外的验证捷径。

---

## 13. 关键开放问题（评审输入）

1. **G1 归属矛盾拍板**：是否同意 §3.2 建议——C06 M4 新增 R9（`RefStructUsingRewriter` + UF055）拥有 ref struct using 站点，回填 C06 D9/R7/§11 三处「归 C44」措辞？白名单（Span 族）v1 是随 R9 一起给空操作归一，还是 UF055 一律拒绝、白名单延后？
2. **Roslyn 绑定细节探针**：D14 的接口/pattern 双实现优先级、D3 的 `Nullable<T>` 资源是否产生 box，需在实施首步以 `TryGetDisposeMethod` 探针 + IL 查看锁定；本设计规则按「接口优先、Nullable 经 HasValue+Value」草拟，存在按探针修订的可能。
3. **共享模板抽提时机与第一个/第二个消费方次序**：C40、C38 M2、C04 M3 谁先落地决定 §11.3 抽提发生在哪条分支；`Shared/` 新目录是否认可，还是放入既有某目录（如 GotoEliminator 旁）。
4. **M2 触发条件的客观判据**：§8.2 三个启动条件目前是定性描述；CsCore/统一引擎立项时需要把「转换器输入面收敛」转成可验收清单（含 using 六个内联站点的删除顺序）。
5. **异常屏蔽偏差的处置时点**：风险 4 的 t.w.r. addSuppressed 与 C# finally 语义差异，是否接受「现状保留至 M2 一次性收敛」，还是在 M1 阶段就应在主转换器层评估修正（修正会影响现有 Java 产物快照）。
