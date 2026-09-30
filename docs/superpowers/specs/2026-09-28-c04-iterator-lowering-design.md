# C04 Yield 迭代器降级预处理器设计

**日期**: 2026-09-28
**范围**: 新增 C#→C# 预处理器 `iterator-lowering`（Core `IteratorLowering\` + CLI 适配器），把含 `yield return` / `yield break` 的迭代器成员降级为显式状态机类；不涉及 C#→Java 转换代码改造
**状态**: **已实现并合入 dev-3.x 主干**（2026-09-29 b784b47f 落地，2026-09-30 核实；M1 自包含范围，pass 默认关；M2-M4 与 §8 默认开启五门槛挂起）
**落地记录（2026-09-30 核实）**: 实现直接提交于 dev-3.x（**未使用 §12 规划的 worktree/分支**）：Core `IteratorLowering/` 9 文件（Preprocessor/Analyzer/CfgBuilder/StateMachinePlan/IteratorStateMachineRewriter/Naming/Options/Statistics/Diagnostic）+ CLI `IteratorLoweringPassAdapter`（AfterFilesAsync 残余门禁）+ PassCatalog 注册（[L209-L221](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L209-L221)，catalogItems `C04`）+ PassChainFactory 接线 + stub 条目删除（PassStubCatalogTests 计数 23→22）+ PassChainOrderTests 顺序断言；测试 `tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs`
**注册表**: Implemented 描述符 [PassCatalog.cs L209-L221](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L209-L221)（`iterator-lowering`，Stage=StateMachine，默认关，RunAfter=`make-readonly`——closure-lift 仍为 stub，按 §3.3 透传策略锚定；eliminate-goto 的 RunAfter 已含本 pass；catalogItems `C04`）；`PassStubCatalog.Ids.IteratorLowering` 常量按 §3.2 计划保留复用。设计期 stub 事实见 §3.1 留档

> 命名口径提示：catalog 正文曾出现 `IteratorLowering` / `iterator-lifting` 等写法。**一律以代码为准：pass id = `iterator-lowering`**；类型/命名空间用 `IteratorLowering`。

## 1. 背景与目标

### 1.1 频率证据

`prompts/preprocessor-catalog.md` 第 296-308 行 C04 条目（四次补全注记）：csharp-sdk 训练集实测 **2887 处** yield 站点，是训练集中**频率最高的结构性缺口**（lambda 4966、局部函数 3486 已有 E05/E02 通道承接；yield 无任何生产通道）。方向性输入（catalog §5.5）建议按频率列 P0，清单暂保留 **P1**——收益明确，但状态机综合复杂，try/finally 原型与捕获字段依赖未收敛。

### 1.2 当前 fail-fast 面（已逐处复核）

1. **方法体 yield 无条件硬失败**：[StatementTransformer.cs:50-51](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L50-L51) 分发到 [StatementTransformer.Declarations.cs:165-171](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs#L165-L171)，固定抛 `NotSupportedException("... must be lowered by a preprocessor (iterator lowering) ...")`。方法/局部函数（经 E02 提升后仍是方法）内的 yield **100% 转换失败**。
2. **实测红测**：[YieldReturnRefArgTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/YieldReturnRefArgTests.cs) 唯一用例 `YieldReturnWithRefArg_InIfElse_ShouldDeclareHolderBeforeUse` 在 `-c Release` 下**当前失败**（2026-09-28 复现：yield fail-fast 触发，`result.Success=false`）。该测试记录的是 AGL GeometryGraphWriter 存量痛点。
3. [IteratorBridgeTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/IteratorBridgeTests.cs) 是空占位类（无任何 `[Fact]`），仅说明 Java 侧迭代器桥曾是痛点承接面。
4. **转换器内一条自相矛盾的半失效路径**：[PropertyTransformer.cs:49-55](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L49-L55) 对含 yield 的属性 getter 设置 `IsInYieldMethod` 并包出 `CSharpList<T> _yieldResult = new ...; {body}; return _yieldResult;` 的**即时求值（eager）**形态；但语句分发对 yield 仍无条件抛异常，两条路径互斥、没有闭环，且 eager 累积在语义上也不等价于惰性迭代（无限序列、早退、Dispose 时序全失）。
5. **实验资产只可借形**：[LowerYield.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerYield.cs)（218 行）在实验 IR 上演示了 `_state/_current + switch + 嵌套 *_Iterator* 类型` 的形态，但：① 跑在 Java2 实验 IR 上，生产不可达（catalog §4 风险 9，仅 PipelineComparisonTests 引用）；② 无 try/finally、无 Dispose 时序、无捕获分析、无泛型、产出 `next/hasNext` 而非 C# 形态。本设计**只借状态编号与嵌套类型形态，不复用一行代码**。
6. **下游 LINQ 重写会合成 yield**：[LinqRewriter.Rules.cs:41-55](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.Rules.cs#L41-L55) 在主转换 Phase 2 为 27 个根方法（Where/Select/Cast/OfType/…，见 [LinqRewriter.Rules.cs:2375-2386](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/LinqRewriter.Rules.cs#L2375-L2386)）合成 `yield return` 局部函数。该合成发生在**全部预处理器之后**，C04 够不到——交互面见 §10 风险与 §13 开放问题。

### 1.3 目标

把所有**用户书写**的迭代器成员：

- 方法（返回 `IEnumerable<T>` / `IEnumerator<T>` / 非泛型 `IEnumerable` / `IEnumerator`）；
- 属性 / 索引器的 get 访问器；
- 迭代器局部函数（E02 `local-function-lift` 已在链前段将其提升为私有方法，yield 体原样保留）；

降级为**等价于 C# 编译器自身 lowering 形态**的、可编译的显式 C#：嵌套私有密封类实现 `IEnumerable<T>` + `IEnumerator<T>` + `IDisposable`，具备 `_state/_current/MoveNext/Current/Dispose/GetEnumerator`，保持惰性求值、`yield break` 早退、try/finally/using/lock 在提前 Dispose 时的清理时序。

非目标：不降级 async/await、`IAsyncEnumerable<T>`（属 C18/C38 `async-normalize`）；不重写 LINQ 语义；不产出也不引入支撑库（标准 BCL 接口即目标，见 §5.4）。

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据 / 备注 |
|---|---|---|---|
| D1 | 状态机形态 | **编译器风格单类**：一个嵌套 `private sealed class` 同时实现 `IEnumerable<T>`、`IEnumerator<T>`、`IDisposable`；`int _cs2jState` + `T _cs2jCurrent` + 线程 id + 克隆构造，模拟 Roslyn `<M>d__N` | 否决双类（enumerable/enumerator 分离）方案：类与样板翻倍、捕获参数要在两类间二次拷贝；单类与 C# 编译器语义同构，"多次枚举不共享状态"由"非首枚举走克隆构造"保证 |
| D2 | 挂起/恢复机制 | MoveNext 入口 `switch(_cs2jState)` 分发到 resume 标签；每个 `yield return e;` 翻译为 `_cs2jCurrent = e; _cs2jState = n+1; return true; _cs2jLabel_n: ;`；恢复路径用 `goto _cs2jLabel_n` | 产出的 goto/label **交给链后既有的 `eliminate-goto` 消除**（catalog §3.2 顺序：C04 在 E14 之前）；[StateMachineBuilder.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs#L862-L868) 已有 goto→`while(true)switch` 机械且含 `IsIteratorBody` 特判，形态匹配。备选无 goto 的 state-threaded CFG（自做 goto 消除）仅在原型证明 goto 通道吃不下时启用（见 §9 M1 出口准则） |
| D3 | `yield break` | `_cs2jState = -1;` → 执行当前保护区域 finally 栈 → `return false;`；正常走完体末同样 `_cs2jState = -1; return false;` | 与 Roslyn 一致：break 是"成功结束"，不是异常 |
| D4 | try/catch/finally 时序 | **以 Roslyn 2026 编译器实测合法边界为准**（探针实测，见 §4.6）：yield 在 try-finally（无 catch）/ using / lock 内合法；try-with-catch（CS1626）、catch（CS1631）、finally（CS1625）内 yield 非法。合法形态按"保护区域 + 状态区间"翻译；非法形态输入基线即不编译，Analyzer 再以 CS2J0402 显式兜底 | 提前 `Dispose()` 挂在保护区域内时必须执行对应 finally；区域外/已结束时空操作。这是本 pass 最高风险点（catalog §4 风险 2），原型必须先证 |
| D5 | `using` | `using (x) BLOCK` / `using var x;` 先在语法模型上归一为 try/finally（`x?.Dispose()`，含 pattern-based Dispose），再走 D4 同一条区域机制；资源对象跨挂起点存活时提升为字段并登记到所属区域 finally | `await using` 不在范围（C18/C38）；ref struct 的 using 跨 yield 见 D6 |
| D6 | ref struct / ref local / unsafe 组合 | 实测：ref struct 实例跨 yield 边界 **CS4007**、ref local 跨边界 **CS9217**、unsafe 块内 yield **CS9238**，均为基线编译错误。Analyzer 策略：不跨挂起点者保留为 MoveNext 内局部；跨边界者（理论上基线已拒）显式报 CS2J0403 并跳过该成员，禁止生成不可编译代码；ref/in/out 迭代器形参（**CS1623**，in 同样非法）显式报 CS2J0402 | 与 C01 holder、C06 Span 释放的耦合按 catalog 风险 2 处理：本 pass 不发明 ref 跨挂起表达，等 C06/C01 形态成熟后再评估 |
| D7 | 嵌套迭代器 | ① 迭代器成员调用其它迭代器成员：普通方法调用，不动；② 迭代器局部函数：E02 已提升为方法，按普通迭代器方法处理；③ 残余未提升的迭代器局部函数（E02 skip）：CS2J0404 显式报错（不支持方法体内嵌状态机）；④ 一个类型多个迭代器：确定性序号（§4.5） | lambda / 匿名方法内 yield 为 **CS1621**，基线必拒，无需 display class 处理——显著缩小与 closure-lift 的耦合面 |
| D8 | 与 closure-lift 的接口边界 | **自带最小闭包提升，不等 closure-lift 落地**：状态机类自身就是唯一 holder——参数/this/跨挂起局部全部成为状态机字段；不引用 closure-lift 任何类型。同时**预留接口形状**（文档化，不提前建共享程序集类型）：Analyzer 产出的捕获布局 `CaptureLayout`（符号→字段名）、命名约定 `_cs2jCap_/_cs2jLoc_` 即未来与 C32/C45/C18 共享的契约（见 §11） | 迭代器体内**含 lambda/局部函数且引用被提升变量**的形态（状态机需充当 lambda 的 display class）v1 以 CS2J0405 显式拒绝；此类输入在 sdk 画像中比例待 EF Core 画像复核 |
| D9 | 落地顺序 | C04 先行、自包含交付；closure-lift 落地后再把命名/捕获分析抽取为共享基建（可能统一生成名，接受一次快照漂移）；async-normalize 最后复用 | 频率最高、2887 处；等 closure-lift 无时间表；D8 边界保证未来抽取不改变外部契约 |
| D10 | 链位临时锚点 | descriptor 的 RunAfter **保持 `closure-lift` 不变**（stub 是排序节点，不执行但参与拓扑）；翻转为 Implemented 时**同步给 `eliminate-goto` 增加 `RunAfter: iterator-lowering` 边**并加排序回归断言（§3.3） | 现注册表拓扑按"声明顺序兜底"排序（[PassRegistry.cs:95-133](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs#L95-L133)），不加边则 iterator 会排到 goto 之后，D2 产出的 goto 将直接撞上转换器 fail-fast |
| D11 | 分析/执行形态 | **整树 compilation + 逐文件 Rewrite 的标准语义 pass 脊柱**（同 EnumPassAdapter），不引入 record-readonly 的多轮回放；成员级原子性（不部分降级单个成员）+ 静态 skip 集保证可编译性，交付闸门靠 residual 自检 + restore/build/test | 状态机嵌套类与原成员同文件，无跨文件改写、无支撑库、无 plan 回滚需求 |
| D12 | 默认开关 | 翻转 Implemented 后仍**默认关**（opt-in）；原型 + 专项 fixture + sdk 基线全绿后再翻默认开（门槛见 §8） | catalog §4 风险 10：每个通道落地即定义默认开启门槛 |

## 3. 注册表与链位契约

### 3.1 现状（2026-09-29 已翻转 Implemented；下文为设计期 stub 事实留档）

> **2026-09-30 核实**：b784b47f 已按 §3.2 清单完成翻转——stub 条目删除、`Ids.IteratorLowering` 常量保留、PassChainFactory 增加 Adapter case、测试计数（23→22）与顺序锁定就位；与计划的唯一偏差是 Implemented 描述符 RunAfter 锚定为 `make-readonly` 而非 `closure-lift`（后者仍是 stub，拓扑透传，见 §3.3 策略 3），catalogItems 补登 `C04`。以下段落保留为设计期事实。

[PassStubCatalog.cs:151-160](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L151-L160)：

- id `iterator-lowering`，DisplayName "Iterator lowering"，目录 `.cs2j-iterator-lowering-src`；
- `PassStage.StateMachine`，`PassStatus.Stub`，`defaultEnabled: false`；
- `runAfterIds: ["closure-lift"]`；`needsSemanticModel` 默认 true；无支撑库；
- `closure-lift` 自身（同文件 141-150 行）RunAfter=`ref-out-in`，覆盖 C32/C45，同为 stub。

禁用时不进执行计划（[PassPlanResolver.cs:67-90](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassPlanResolver.cs#L67-L90) 按 `registry.Ordered` 过滤）；显式 `--enable-pass iterator-lowering` 时 [CliPreprocessChain.cs:131-136](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L131-L136) 在触碰文件系统前报 CS2J9001 失败（该行为有测试 [PassStubCatalogTests.cs:71-104](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L71-L104) 锁定）。

### 3.2 Stub→Implemented 翻转文件清单

| 动作 | 文件 | 内容 |
|---|---|---|
| 新建 | `src/CSharpToJava.Core/IteratorLowering/*` | 见 §4 组件清单 |
| 新建 | `src/CSharpToJava.CLI/Preprocessing/Adapters/IteratorLoweringPassAdapter.cs` | 见 §5 |
| 改 | [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) | 从 `Stubs` 删除 iterator-lowering 条目；**保留 `Ids.IteratorLowering` 常量**（PassCatalog 与排序边复用） |
| 改 | [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) | `Implemented` 表新增 descriptor（Status=Implemented、defaultEnabled=false（M5 门槛后再翻 true）、`verifyBuildByDefault: true`、`verifyTestsByDefault: true`、runAfterIds=`closure-lift`、目录名不变）；并给 `eliminate-goto` 的 runAfterIds 追加 `iterator-lowering` |
| 改 | [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs) | `CreateAdapter` switch 增加 `PassStubCatalog.Ids.IteratorLowering => new IteratorLoweringPassAdapter(...)`（无 legacy toggle、无支撑库） |
| 改 | [PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs) | `Assert.Equal(23, ...)` → 22；CS2J9001 用例（90、102 行）改用仍为 stub 的 id（建议 `closure-lift`），不再用 iterator-lowering |
| 新建 | 排序回归测试（建议放 `tests/CSharpToJava.Tests/Preprocessing/`） | 断言全序与启用子集两处均有 `index(iterator-lowering) < index(eliminate-goto)`；默认计划含/不含随默认开关断言 |
| 不改 | Program.cs / 指纹 / 保留目录 | 通用链已按 descriptor 驱动：执行目录集合经 `ExecutedIntermediateDirectoryNames` 自动回填（[CliPreprocessChain.cs:101-108](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L101-L108)、Program.cs:216），指纹令牌按注册表全集 `pass:<id>=on/off` 生成；本 pass 无支撑库、不重指指纹源（`RepointsFingerprintToPassInput=false`） |

### 3.3 RunAfter 锚点策略（closure-lift 未实现期）

1. **不**把 RunAfter 改成 null 或已实现锚点：`closure-lift` 是真实的未来依赖（D8），stub 节点参与 [PassRegistry](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs) 全量拓扑，写在那里即文档即顺序契约；注册表启动校验已保证"指向不存在 id"报错（RunAfter=null/空列表语义为链首，本 pass 不适用）。
2. **必须补的边**：`eliminate-goto` RunAfter 增加 `iterator-lowering`（D10）。同理未来 `async-normalize` 翻转时也要补这条边（其 stub 同样锚在 closure-lift 之后、goto 之前）。
3. **建议（可选增强，非阻塞）**：在 `PassPlanResolver`/诊断面增加"透传锚点"汇总：某 Implemented pass 的 RunAfter 链只经过未启用 stub 时，解析出最近的已启用 Implemented 前驱用于 `--list-passes` 展示与启动期提示（"iterator-lowering 将紧随 make-readonly 执行；closure-lift 尚未实现"）。执行语义不变（Enabled 本就是全序过滤），仅消除"RunAfter 一个永不执行的 pass"的认知歧义。
4. 顺序正确性以测试锁死，不靠人工记忆（§3.2 排序回归）。

## 4. Core 组件设计

目录 `src/CSharpToJava.Core/IteratorLowering/`（internal 实现 + 一个公开入口，仿 EnumPreprocessor/JavaKeywords 的组织）：

```
IteratorLoweringOptions.cs      # Strict（默认 true）
IteratorLoweringStatistics.cs   # 通用 5 字段 + pass 专有计数（§4.7）
IteratorLoweringDiagnostic.cs   # Severity/Code/Message/FilePath/Line + ToMsBuildString
IteratorLoweringAnalyzer.cs     # 迭代器成员发现 + 合法性闸门 + 捕获/活性分析
ControlFlow/CfgBuilder.cs       # 结构化 CFG、挂起点编号、保护区域表
StateMachinePlan.cs             # 一个迭代器成员的完整降级计划（字段布局/区域/状态数/生成名）
IteratorLoweringNaming.cs       # 确定性命名：类型名、字段名、标签名、防撞
IteratorStateMachineRewriter.cs # 按 plan 重写成员体 + 合成嵌套类 SyntaxFactory
IteratorLoweringPreprocessor.cs # 对外入口：Preprocess(source, SemanticModel, options)→FileResult
```

### 4.1 Analyzer

- 入口：每个 CompilationUnit 用标准 compilation 提供的 SemanticModel（标准脊柱 [ProjectPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L117-L269) 已建好整树 compilation，parse 选项 `LanguageVersion.Latest`、`allowUnsafe: true`，与 record-readonly 同款）。
- 迭代器成员判定（语法 + 语义双条件）：方法符号返回类型是 `IEnumerable`/`IEnumerable<T>`/`IEnumerator`/`IEnumerator<T>` 且体含 `YieldStatementSyntax`；属性/索引器 get 访问器同判；partial/显式接口实现以 `IMethodSymbol` 归一。
- **基线优先原则**：整树先取一次编译诊断快照。Roslyn 已拒绝的非法组合（§4.6 表）属于用户源码既有错误，本 pass 不为其负责、不二次报错；只对"基线没拦住但本 pass 无法降级"的形态发 CS2J04xx（防御层，正常不可达）。
- 每个成员产出一个 `StateMachinePlan` 或一条 skip 诊断；skip 集在任何重写前确定（静态、幂等），严格模式下有 skip 即整体失败（成员原样保留，树仍可编译）。

### 4.2 控制流图与状态编号

- **段（segment）划分**：以每个 `yield return` 为切分点，沿结构化语法（block/if/while/do/for/foreach/switch/try/using/lock）把方法体切成顺序执行段；不做 goto 风格的任意基本块 flatten（用户 goto 已由链后 goto pass 负责；迭代器体内出现用户 goto 是合法 C#，v1 以 CS2J0406 拒绝，列入 M4 后评估——挂起点与 goto 交叉的活性分析复杂）。
- **状态编号**：`-2`=enumerable 未启动（构造态）；`0`=首段；每个 `yield return` 按语法序分配 resume 状态 `1..N`；`-1`=已结束。编号仅依赖语法序与 plan 内确定性遍历，重跑字节稳定。
- **循环中的挂起**：resume 标签落在循环体内原处，依赖 goto 跳回原嵌套层级；循环归纳变量、foreach 的迭代变量与 foreach 自身 enumerator（`IDisposable`）凡生命周期跨挂起点一律提升字段，foreach enumerator 同时登记到合成 finally（区域机制的一部分，M1 即需——这是最高频形态）。
- **保护区域表**：对每个词法上包含挂起点的 try-finally / using / lock（lock 实测合法，等价 Monitor.Enter/Exit-finally）记录：区域 id、进入状态集合、resume 状态区间、finally 语句块（引用全部改走提升字段）、资源字段。嵌套区域按栈包含；MoveNext 正常路径与 Dispose 路径共用同一份 finally 形态。
- 借形对象：CFG 切分参考 [StateMachineBuilder.SplitToBlocks](file:///d:/code/cs2j/src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs#L19-L128) 的遍历/Flatten 手法；实验 [LowerYield.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerYield.cs) 仅参考编号思路。**两者都不直接复用**。

### 4.3 字段布局（CaptureLayout，未来共享契约）

`StateMachinePlan.Fields`（确定性顺序：this → 形参 → 提升局部 → foreach enumerator → 状态基建）：

| 字段 | 类型 | 来源 |
|---|---|---|
| `_cs2jState` | `int` | 状态 |
| `_cs2jCurrent` | 元素类型 | Current 后备 |
| `_cs2jInitialThreadId` | `int` | 枚举线程守卫 |
| `_cs2jThis` | 声明类型 | 实例成员的 `this`（值类型成员见 4.6 备注） |
| `_cs2jCap_<名>` | 形参类型 | 全部形参（含未使用者？——只提升体内被引用者，减噪） |
| `_cs2jLoc_<名>` | 局部类型 | 跨挂起点存活的局部（见活性规则） |
| `_cs2jEnum_<n>` | foreach enumerator 类型 | 跨挂起的 foreach |

活性规则（语义模型辅助，避免过度提升）：局部在两个及以上段中被引用、或在某挂起点处 live-out 才提升；段内私有局部保持 MoveNext 局部。ref struct/ref local/unsafe 局部提升非法 → CS2J0403（基线 CS4007/CS9217/CS9238 的防御镜像）。

`CaptureLayout`（被捕获符号 `ISymbol → 字段名` 的只读表）是与 closure-lift/async-normalize 约定的**逻辑契约**：本期只在本命名空间内定义为内部类型；closure-lift 落地时以同一形状产出 display class 布局，三方在不互相引用程序集的前提下对齐字段命名与活性口径（§11）。

### 4.4 生成类型形态（示意）

```csharp
// 输入
public IEnumerable<string> GetTokens(int count)
{
    for (int i = 0; i < count; i++)
        if (i % 2 == 0)
            yield return i.ToString();
}

// 输出（成员体）
public IEnumerable<string> GetTokens(int count)
{
    return new Cs2jIterator_GetTokens_0(this, count, -2);
}

// 同文件、声明类型内合成的嵌套类（名称经 §4.5 防撞）
[CompilerGenerated]
private sealed class Cs2jIterator_GetTokens_0
    : IEnumerable<string>, IEnumerator<string>, IDisposable
{
    private int _cs2jState;
    private string _cs2jCurrent;
    private int _cs2jInitialThreadId;
    private readonly Writer _cs2jThis;
    private int _cs2jCap_count;
    private int _cs2jLoc_i;

    public Cs2jIterator_GetTokens_0(Writer @this, int count, int state) { /* 赋值；state 只取 -2 或 0 */ }

    public string Current => _cs2jCurrent;
    object System.Collections.IEnumerator.Current => _cs2jCurrent;

    public IEnumerator<string> GetEnumerator()
    {
        // Roslyn 同构：首枚举且同线程复用 this；否则克隆（拷贝 this 与全部参数字段）
        Cs2jIterator_GetTokens_0 e =
            _cs2jState == -2 && _cs2jInitialThreadId == Environment.CurrentManagedThreadId
                ? this : new Cs2jIterator_GetTokens_0(_cs2jThis, _cs2jCap_count, -2);
        e._cs2jState = 0;
        return e;
    }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public bool MoveNext()
    {
        switch (_cs2jState)
        {
            case 0: goto _cs2jResume_0;
            case 1: goto _cs2jResume_1;
        }
        _cs2jResume_0:
        try   // 有保护区域时；无则不包
        {
            _cs2jLoc_i = 0;
            while (_cs2jLoc_i < _cs2jCap_count)
            {
                if (_cs2jLoc_i % 2 == 0)
                {
                    _cs2jCurrent = _cs2jLoc_i.ToString();
                    _cs2jState = 1;
                    return true;
                    _cs2jResume_1: ;
                }
                _cs2jLoc_i++;
            }
        }
        finally { /* 区域 finally；无区域时无此 try */ }
        _cs2jState = -1;
        return false;
    }

    public void Dispose()
    {
        // 按当前 _cs2jState 所属区域执行对应 finally 栈；-1/-2 空操作；末尾置 -1
    }
    public void Reset() => throw new NotSupportedException();
}
```

要点：

- 非泛型返回类型只实现非泛型接口，`Current` 为 `object`；`IEnumerator<T>` 返回形态时原成员直接 `return new X(this, args, 0)`（无 GetEnumerator/线程守卫）。
- 泛型方法：状态机类携带方法的全部类型参数；声明类型本身为泛型时，外层类型参数按序前置于类参数表，构造点用封闭类型实参实例化（M1 必测，sdk 高频）。
- 属性/索引器 getter：合成 `<PropertyName>Getter` 等价的方法形态由状态机承担，getter 体变为 `return new Cs2jIterator_..._0(this, -2);`；使 §1.2 第 4 条 eager 路径在预处理后的树上成为死路径（转换器侧冗余清理列入后续 PR，同 java-keywords spec §10 范式，本特性不动转换器）。
- 所有合成节点挂统一 annotation（`SyntaxAnnotation("Cs2jIteratorLowering")`），便于 residual 统计与测试断言；输出经 `FormatAndValidate`（与 record-readonly/java-keywords 同款三候选格式化）。

### 4.5 确定性命名

独立 `IteratorLoweringNaming`（借 [LocalFunctionLiftNaming](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftNaming.cs) 的防撞手法，不共享代码）：

- 嵌套类：`Cs2jIterator_<成员名>_<同类型内序号>`；显式接口实现成员用去点拼接名；碰撞（用户已有同名嵌套类型）追加 `_2,_3…`，序号在**类型作用域**内按成员源位置（文件、span）排序分配，保证跨文件/跨次运行稳定。
- 字段：`_cs2jState/_cs2jCurrent/_cs2jInitialThreadId/_cs2jThis/_cs2jCap_<原标识符>/_cs2jLoc_<原标识符>/_cs2jEnum_<n>`；碰撞走 LocalFunctionLiftNaming 同款 `ResolveUniqueName`。
- 标签：`_cs2jResume_<n>`；局部临时量 `_cs2jTmp_<n>`。
- 全部名字只用 ASCII、避开 C# 保留字与 Java 保留字（即便最终还要过 C08，这里先不制造新冲突）；**不使用 Roslyn IL 风格的 `<M>d__N`**（`<` 不是合法 C# 源码标识符）。
- 命名表是 catalog §5.4 "确定性命名服务"缺口的第二个事实客户（E02 之后）；closure-lift 统一命名服务落地时本 pass 迁移为消费方，名字形态在 §11 接口约定中冻结。

### 4.6 合法/非法构造表（Roslyn 实测 2026-09-28，net10/latest 探针）

| 构造 | 合法性 / 诊断号 | 本 pass 处理 |
|---|---|---|
| `yield` 在 try-finally（无 catch） | 合法 | 保护区域机制（M2） |
| `yield` 在 `using`（块/声明） | 合法 | 归一 try-finally（M3） |
| `yield` 在 `lock` | 合法（Monitor enter/exit-finally） | 同 finally 区域；专项 fixture（M3） |
| foreach 内 `yield`（含嵌套循环） | 合法 | M1 必做：enumerator 提升 + finally 释放 |
| 嵌套 try-finally、yield break 于 try-finally | 合法 | 区域栈（M2） |
| 迭代器局部函数 | 合法 | 依赖 E02 提升；残余则 CS2J0404 |
| 属性/索引器 getter、struct 成员、`IEnumerator`/非泛型返回 | 合法 | M1/M4 覆盖 |
| try-with-catch 内 yield | **CS1626**（基线错） | 不生成；防御性 CS2J0402 |
| catch 内 yield | **CS1631** | 同上 |
| finally 内 yield | **CS1625** | 同上 |
| ref/in/out 形参 | **CS1623**（in 同罪） | CS2J0402 镜像 |
| lambda/匿名方法内 yield | **CS1621** | 基线必拒；display class 无需覆盖此面 |
| ref struct 实例跨 yield | **CS4007** | 不跨则保留局部；跨则 CS2J0403 |
| ref local 跨 yield | **CS9217** | 同上 |
| unsafe 块内 yield | **CS9238** | CS2J0403（unsafe 块不含 yield、指针局部不跨挂起时可原样保留，M4 评估） |
| 迭代器体内用户 goto/labeled | 合法 C# | v1 CS2J0406 显式拒绝，M4 后评估 |
| 体内 lambda/局部函数引用被提升变量 | 合法 C# | v1 CS2J0405 显式拒绝（需状态机兼作 display class） |
| 表达式树/动态边界 | — | 体内表达式原样搬运，不重写表达式语义，无交互 |

### 4.7 Options / Statistics / Diagnostic

- `IteratorLoweringOptions { bool Strict = true; }`（Strict 接链上共享 `PassChainConfiguration.Strict`，与 lift/??/enum/delegate 同源）。
- Statistics：通用 `FilesScanned/FilesTransformed/FilesSkippedClean/FilesDirtyNoRewrite/ResidualCount`（枚举适配器摘要行依赖通用口径）+ `IteratorMembersLowered`、`StateMachinesGenerated`、`YieldReturnSites`、`YieldBreakSites`、`HoistedParameters`、`HoistedLocals`、`FinallyRegions`、`SkippedUnsupported`（按原因分桶）。
- 诊断（码位只追加）：

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J0401 | Error | 不支持的迭代器返回形态（无法解析元素类型等） |
| CS2J0402 | Error/Warning | 命中编译器非法组合（CS1623/1625/1626/1631 族的防御镜像） |
| CS2J0403 | Error/Warning | ref struct/ref local/unsafe 跨 yield 边界（CS4007/CS9217/CS9238 族） |
| CS2J0404 | Error/Warning | 残余未提升迭代器局部函数（E02 skip 联动） |
| CS2J0405 | Error/Warning | 体内 lambda/局部函数捕获提升变量（v1 不支持） |
| CS2J0406 | Error/Warning | 迭代器体内用户 goto/label（v1 不支持） |
| CS2J0420 | Error | restore/build/test 失败（适配器钩子记录，仿 EnumPassAdapter） |
| CS2J0430 | Info | 成员已降级（含成员名、状态数、区域数，verbose 可见） |

非严格模式下 0402-0406 为 Warning（成员原样保留、不计 residual 违规但计入 skip 桶）；严格模式（默认）为 Error，pass 失败。**禁止注释/占位/原样透传 yield 进下游**（catalog §4 风险 12 第一原则）。

## 5. CLI 适配器与集成、verify 默认、指纹

- 新建 `Adapters/IteratorLoweringPassAdapter.cs : ProjectPassAdapter`，**走默认脊柱**（不 override `ExecuteAsync`，参照 [EnumPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/EnumPassAdapter.cs)）：
  - `Descriptor` 取注册表实现项；`RewriteFileAsync` 调 `IteratorLoweringPreprocessor.Preprocess`，Changed=false 返回 null 走字节级原样复制（BOM 由 runner 保持）；
  - `DeleteBuildOutputsAfterRestore = true`、VerifyBuild/VerifyTests 透传 chain config（descriptor 默认均 true，用户可用 `--no-verify-build/tests` 关，入口同现有开关）；
  - `AfterFilesAsync` 做 **residual 闸门**：统计产物中残余 `YieldStatementSyntax`（白名单=已发 skip 诊断的成员），非白名单残余在严格模式报 Error（仿 goto 适配器 `ContainsGotoOrLabel` 后置自检与 enum 的 residual 汇总）；
  - `OnRestoreFailed/OnBuildFailed/OnTestsFailed/HasErrors/BuildChainSummaryLine/BuildChainDiagnosticLines` 按现有适配器同构实现，摘要行如 `iterator-lowering: N member(s) lowered, K yield-return, R region(s), S skipped, X residual`。
- 启用方式：通用 `--enable-pass iterator-lowering` / `--disable-pass iterator-lowering` 与独立 `preprocess --pass iterator-lowering -s .. -d ..`（通用 verb 已存在，不新增专属 verb）。
- 支撑库：**None**（目标全是 BCL 接口；Java 侧 `CSharpEnumerator`/`CSharpGenericEnumerator` 是下游翻译的既得承接，不需要 C# 侧注入）。
- 类型映射已就绪（已核实）：`IEnumerable<T>`→`Iterable<T>`（[TypeMappingRegistry.cs:103](file:///d:/code/cs2j/src/CSharpToJava.Core/TypeMapping/TypeMappingRegistry.cs#L103)）、`IDisposable`→`AutoCloseable`（config/TypeMappings.json:254）、`GetEnumerator()`→`iterator()`（MethodTransformer.cs:181 等）、`MoveNext/Current/Dispose` 经命名规则分别落 `moveNext/getCurrent/close`；foreach 仅认数组/Iterable（StatementTransformer.Loops.cs:101-126），降级树保持标准 C# foreach 即可。
- 指纹/保留目录：零手工接线。中间目录名经 descriptor 进入 `ExecutedIntermediateDirectoryNames`，Program.cs:216 回填 `opts.EnabledPassIntermediateDirectoryNames`，preserve/过滤与 `pass:iterator-lowering=on/off` 令牌自动生效；catalog §4 风险 11 要求的"新增中间目录接入 fingerprint/preserve"由注册表机制天然满足（不重指指纹根，无 goto 式特殊处理）。

## 6. 诊断策略（不可降级构造显式报错）

- 原则：**任何 yield 都只有两个归宿——被降级，或被显式诊断**。不存在第三种"注释掉/透传/偷偷 eager 化"。
- 三层防线：
  1. Analyzer 静态分类（§4.6 表，skip 集先于改写确定）；
  2. Rewriter 后 residual 闸门（白名单外残余 yield 即 Error）；
  3. 中间树 `dotnet build`（语法/类型疏漏在 restore+build 阶段硬失败，CS2J0420，产物不会流入 Java 转换）。
- 诊断带文件、行、列、成员完全限定名与处置建议（"移除该组合后重试 / 等 closure-lift 落地 / 改写为先物化 List"）；MSBuild 格式与既有适配器一致，`--list-passes` 展示 Stub→Implemented 后状态翻转。
- 基线已有编译错误时：不与编译器抢诊断（D11 基线快照），仅在日志里标注"pass 跳过：目标树基线不编译"，避免噪音淹没真错误。

## 7. 测试计划

### 7.1 Core 单测（建议 `tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs`，内联源码→CSharpCompilation→Preprocess→文本断言 + 输出可编译断言，仿 JavaKeywordRewriterUnitTests）

1. 直线：单 yield、多 yield、无 yield 成员零改动（Changed=false）。
2. 循环：while/for/do/foreach 内 yield（foreach enumerator 提升与 finally 释放断言）；嵌套循环；循环归纳变量提升。
3. 分支：if/else、switch、条件表达式、`yield break` 早退（含 break 后语句为死代码）。
4. 参数/this：多参、实例成员 `_cs2jThis`、静态成员无 this、泛型方法、泛型声明类型嵌套、显式接口实现。
5. try/finally：yield 于 try、yield break 于 try、嵌套 finally；**语义断言**：枚举到一半 `Dispose()` 后 finally 恰好执行一次；正常枚举完 finally 执行一次；重复枚举互不共享状态（线程守卫/克隆构造）。
6. using：using 块、using 声明、多个资源、嵌套；Dispose 调用次数与时序断言。
7. lock：lock 内 yield 的 Exit 时序（M3 后）。
8. 嵌套迭代器：迭代器调迭代器、E02 提升后的迭代器局部函数。
9. 属性/索引器 getter；非泛型 `IEnumerable`/`IEnumerator` 与 `IEnumerator<T>` 返回形态。
10. 反例/诊断码：CS2J0402（Analyzer 直调喂入含 catch-yield 的语法模型，验证防御镜像与 skip 归因）、CS2J0403（跨边界 ref struct/ref local 合成用例）、CS2J0404/0405/0406；严格/非严格级别翻转；residual 闸门触发；基线已含 CS16xx/CS4xxx 错误的树不重复发诊断（仅计 skip 观察桶）。
11. 确定性：同一输入两次输出字节一致；成员声明顺序打乱后生成名稳定；与用户同名嵌套类型防撞。
12. 每个正例额外断言：输出零编译错误、无 `yield` 令牌、含 `_cs2jState/MoveNext/Dispose` 形态。

### 7.2 专项 fixture sln（风险 2 硬性要求）

新建 `tests/SampleIteratorSolution/`（Lib + App + xunit TestProject + `.sln`，Content Include 进 ProjectTests，TFM net10.0，仿 SampleJavaKeywordSolution/SampleRecordSolution 布局）：

- Lib：覆盖 §7.1 全部形态的真实库代码（参数校验、无限序列、提前 Take、using 文件/内存流、嵌套 finally、lock、泛型容器迭代器）。
- TestProject：在 **.NET 半区**锁行为（惰性：无限序列 `Take(n)` 不死；Dispose 时序；枚举次数；异常透传）。
- 项目级测试断言：pass Success、统计计数、residual=0；对中间树 `dotnet build -c Release` 与 `dotnet test -c Release` 退出码 0。
- **Java 半区**（catalog §5.1 两半区原则）：把降级后的中间树走主转换，跑转译后的同一套行为测试，验证 `Iterable/Iterator/AutoCloseable` 映射与 foreach 互操作；红测 YieldReturnRefArgTests 的场景（yield 表达式内含 ref 实参方法调用）纳入 fixture 并断言转译通过。

### 7.3 闸门与回归

- 中间树 **Release build + test 必须全绿**（descriptor verify 默认开）；全仓 `CSharpToJavaConverter.slnx` `-c Release` build/test 三工程全绿。
- residual 闸门测试：构造"无法降级 + 严格模式"用例，断言 pass 失败且无部分改写。
- 链顺序测试：§3.2 排序回归（iterator 先于 goto）。
- 一致性/CLI：`ProjectPreprocessorsConsistentTests` 必要追加；`PreprocessVerbCLITests` 增 `--enable-pass iterator-lowering` 端到端（默认计划不含、显式启用执行、保留目录出现、再跑 convert-project 命中缓存）。
- 启用后重跑现有 yield 相关测试：YieldReturnRefArgTests 转绿或按新形态重写断言；IteratorBridgeTests 补实用例或删除占位。

## 8. 默认开启验收门槛（风险 2：原型先行）

仅当全部满足才把 descriptor `defaultEnabled` 翻 true：

1. **原型出口（M1+M2）**：try/finally（含嵌套）、using、foreach 跨挂起、提前 Dispose 时序在专项 fixture 的 .NET 与 Java 两半区全绿；D2 的 goto 产物被 eliminate-goto 全量吃下（或已切换无 goto 发射方案）。
2. 全仓 Release build/test 三工程全绿；residual 在 fixture 与启用范围内为 0。
3. csharp-sdk 基线：以 `--enable-pass iterator-lowering` 跑训练集（json/regex/uri/xml-tiny 四子集），中间树 build/test **不退化**；统计 yield 站点实际降级率，未降级站点全部有显式诊断且分类清楚。
4. EF Core 画像（catalog §5.1 前置行动）拉取后重跑频率/形态分布：捕获 lambda 迭代器、泛型嵌套、lock 等形态的命中率与 skip 率可接受；若 CS2J0405（体内 lambda 捕获）占比高，则默认开启推迟到 closure-lift 或该能力补齐之后。
5. opt-in 观察期至少跨一次主版本回归窗口（沿用 record-readonly/array-method 的 opt-in 先例）。

## 9. 里程碑

| 里程碑 | 内容 | 出口 |
|---|---|---|
| **M1 最小可编译原型** | Analyzer（方法/ getter 判定 + 基线快照 + skip 集）、直线/if/循环/foreach/泛型的状态机发射、单类形态+线程守卫+克隆构造、字段提升、residual 闸门、适配器接线、Stub→Implemented 翻转（默认关） | SampleIteratorSolution .NET 半区 Release build/test 绿；goto 链衔接验证；Core 单测 1-4、9、11-12 |
| **M2 try/finally** | 保护区域表、Dispose 状态区间、嵌套 finally、yield break 时序、提前 Dispose 语义 | §7.1 第 5 组与 fixture 两半区全绿（风险 2 原型关口） |
| **M3 using / lock** | using 归一（含 pattern-based、using declaration）、资源字段与区域 finally、lock Monitor 形态专项 | §7.1 第 6-7 组全绿；CS2J0402 非法组合用例齐 |
| **M4 ref/unsafe/残余形态 + 默认化评估** | 不跨边界 ref struct/ref local/unsafe 保留策略、struct 迭代器 this 捕获语义、CS2J0404/0405/0406 形态盘点（决定哪些随 closure-lift 前置）、sdk 基线 + EF Core 画像、默认开门槛评审 | §8 五项结论成文；默认关→开或继续 opt-in 的决策记录 |

> **落地进展（2026-09-30 核实）**：b784b47f 交付 **M1 范围**——Analyzer（成员发现/合法性闸门/捕获活性分析）、CFG 与状态编号、直线/分支/循环形态状态机发射、单类线程守卫与克隆构造、字段提升、residual 闸门、适配器、stub→Implemented（默认关）、Core 单测（IteratorLoweringUnitTests）与链序测试（PassChainOrderTests）。**M2（try/finally 两半区原型）、M3（using/lock）、M4（§8 默认化五门槛）与 §7.2 SampleIteratorSolution 项目级 fixture 未做**，是默认翻转前的全部挂起项。

## 10. 风险

1. **（catalog §4 风险 2，最高）yield × try/finally × using × ref 的 Dispose 时序**：提前 Dispose、嵌套区域、挂起在区域边界附近时的 finally 选择，任一错误都是"少释放/双释放"且 .NET 单测未必全覆盖。缓解：M2 原型先行 + 专项 fixture 计数断言 + Java 半区复测；不进默认链直到 §8 门槛全过。
2. **（catalog §4 风险 11）验证成本**：本 pass 改写结构性强，verify build/test 默认开会拉长 convert-project 串行链。缓解：descriptor verify 可关；指纹令牌随注册表自动接入；M1 起在 sdk 上度量增量耗时，必要时纳入 §5.4 未来的"内存串联/落盘调试"两模式试点。
3. **（catalog §4 风险 9）实验 Lowering 不可复用**：LowerYield.cs 是 Java2 IR 资产，强行共用会把两套 IR 耦合；已确认只借形态。
4. **goto 衔接风险**：生成的 goto 跳入嵌套块/try 周边的合法标签布局受 C# 定到规则约束；eliminate-goto 的 StateMachineBuilder 虽有 `IsIteratorBody` 特判但从未在"无用户 yield、只有生成 goto 的 MoveNext"形态上被验证过。M1 出口若不达标，退路是直接发射 state-threaded 的 `while(true)switch`（无 goto），代价是 codegen 自研更多。
5. **链位回归风险**：拓扑声明顺序兜底可能让未来新增 stub/pass 改变相对顺序；以 §3.2 排序测试锁死，不靠约定。
6. **红测/死路径清理**：YieldReturnRefArgTests 当前红、PropertyTransformer eager `_yieldResult` 路径与 fail-fast 自相矛盾；降级上线后这些路径在预处理项目上成死代码，转换器侧清理要单列后续 PR（本特性不动转换器，直连单文件入口仍需 fail-fast 安全网）。
7. **LinqRewriter 合成 yield（§1.2 第 6 条）**：主转换 Phase 2 合成的 27 个根方法迭代器局部函数发生在本 pass 之后，C04 无法覆盖；现状该合成面如何与 fail-fast 共存需在 M1 前补一次专项复现（直连转换 `.Select(...)` 根调用），二选一：LinqRewriter 改为不合成 yield（直接累积/显式发射），或转换器对合成点保留受控 eager 路径。不解决则 Java 半区对 LINQ 迭代链的验收会持续泛红。
8. **泛型嵌套与值类型 this**：泛型声明类型中状态机类的类型参数前置于实例化点易错；struct 迭代器的 `this` 按引用/按值捕获语义需专项行为测试（Roslyn 合法，但字段化后必须复核变异可见性）。

## 11. 并行协调（与 closure-lift、async-normalize 的接口约定）

catalog §3.3（第 905 行）将 E02/E05/C32/C45/C04/C18 子项并为"闭包/状态机提升族"，共享捕获分析/确定性命名/holder，fixpoint 互相喂送（§5.4）。本 pass 的协调约定：

1. **本期不产生编译期依赖**：`IteratorLowering` 不引用 closure-lift/async-normalize 任何类型；状态机字段自承捕获存储（D8）。
2. **逻辑契约先行冻结**：① `CaptureLayout`（被捕获 `ISymbol → 字段名`，含活性判定口径：跨挂起点/跨 await 点即提升）；② 命名前缀表（`_cs2jState/_cs2jCurrent/_cs2jCap_/_cs2jLoc_/_cs2jEnum_`、嵌套类 `Cs2jIterator_*`；closure 类命名在其 spec 中取同族前缀如 `Cs2jDisplayClass_*`，由未来统一 Naming 服务收口）；③ 区域/清理模型（finally 区域表同时是 async Dispose 区域的子集）。
3. **fixpoint 喂送预案**：未来 closure-lift 把 lambda 提升为 display class 后，可能产生新的可降级面；async 状态机可能含迭代式枚举。编排器未来按 catalog §5.4 跑到"无变化"为止（带环检测与最大轮次）。本 pass 为此保证**幂等**：重跑只认 `YieldStatementSyntax`，合成节点带 annotation 不被二次处理。
4. **合并顺序建议**（三方各自 worktree，见 §12）：
   - **推荐**：C04 先合（自包含、频率最高、红测在即）→ closure-lift 次合（落地时抽取共享 Naming/CaptureLayout，把 iterator 字段/类名迁移视为一次有意的快照失效，同步更新快照测试）→ async-normalize 最后（直接消费两套共享件）。
   - 备选：若 closure-lift 先于 C04 M4 落地，C04 rebase 到共享契约，CS2J0405 形态（体内 lambda 捕获）优先收编；不因此推迟 C04 M1-M3。
   - 任一合并点都必须重跑三方交界 fixture（lambda 内 foreach-yield 合成面、async iterator 占位用例）。
5. 三个 worktree 的代码隔离以"不碰同一文件"为纪律：C04 触碰 PassStubCatalog（删一条）、PassCatalog（加一条+goto 边）、PassChainFactory（加一个 case）；closure-lift 翻转时触碰同样三个文件——**这是预期的文本冲突点，解决规则在合并顺序中固定（后合者保留两条 descriptor 与各自边），不存在语义冲突**。

## 12. Worktree 分配（规划，未采用）

- **实际落地（2026-09-30 核实）**：下列 worktree/分支**从未创建**——C04 由 b784b47f（2026-09-29）直接提交到 dev-3.x 主干，因此 2026-09-30 的 worktree 清理不涉及 C04。以下为原规划留档：
- （原规划）分支：`feature/preproc-c04-iterator-lowering`
- （原规划）目录：`.worktrees/preproc-c04-iterator-lowering`
- base：`f68a783c`（已核实存在：`f68a783cd74cbfc398204bcdabe000e4f881ad30` — "feat(preprocessing): register 23 merged-family stub passes"）
- 创建命令：

```powershell
git worktree add .worktrees/preproc-c04-iterator-lowering -b feature/preproc-c04-iterator-lowering f68a783c
```

## 13. 关键开放问题（评审输入）

1. **LinqRewriter 合成 yield 面（风险 7）归属**：在 M1 前必须先复现现状并定责——改 LinqRewriter 不合成 yield，还是保留转换器受控 eager 路径？该决策影响 Java 半区验收范围与转换器改动禁令的开口。
2. **goto 发射 vs 无 goto CFG**：M1 原型需要一个二选一结论点：eliminate-goto 能否吃下合成 MoveNext 的全部 goto 形态（跳入嵌套循环/区域周边）；吃不下时是否接受自发射 `while-switch` 状态线程化代码的额外复杂度。
3. **CS2J0405 形态（体内 lambda/局部函数捕获提升变量）在真实画像中的占比**：决定该能力是随 M4 自做（状态机兼作 display class）还是明确等 closure-lift；依赖 EF Core 画像数据。
4. **struct 迭代器 this 捕获语义**：Roslyn 允许且按引用捕获，字段化表达需要确认与现有 StructTransformer/值语义通道（C07/make-readonly）无相互破坏；可能需要额外形态（this holder 字段）。
5. **默认开启时的链耗时预算**：verify build/test 默认开叠加 2887 站点规模，sdk 全量增量耗时阈值未定（§8 第 3 条只定了不退化，未定预算绝对值）。
