# C32 Lambda 可变捕获前置化设计（closure-lift 第一阶段：CaptureHolder）

> 通道锚点：注册表 stub `closure-lift`（catalogItems `C32, C45`，Stage=StateMachine，RunAfter=`ref-out-in`）。
> 本任务翻转该 stub 的**第一阶段**：只把「被 lambda 捕获且可变」的局部/参数提升为显式 holder，catalogItems 收窄为 `C32`；C45（lambda → display class + 命名方法的全量 Roslyn lowering）在同一 pass 内以新阶段接续，不改 RunAfter。
> 本文证据核实时间 2026-09-29，工作树 dev-3.x HEAD=`5e3e796d`；第 12 章指定 base `953293b9`，两处注册表/共享谓词状态已用 `git show 953293b9:*` 核对一致。

## 1. 背景与目标

### 1.1 现状证据：转换器内嵌两套半隐式捕获状态

lambda 可变捕获今天没有 C# 层形态，全部在 C#→Java 转换器内以运行时状态代偿，且逻辑分散在「方法体预扫描 + lambda 内即时发射」两条同构路径上：

1. **方法体级预扫描（外部再赋值路径）**：[StatementTransformer.cs:68-89](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L68-L89) 在 `ScopeDepth==0` 调 `PreScanLambdaCaptures` 并把参数 holder 预置到方法体首；同构第二份在结构化 IR 路径 [StatementTransformer.cs:124-143](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L124-L143)——两份拷贝必须手工保持一致。
2. **预扫描实现**：[StatementTransformer.Utilities.cs:120-255](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Utilities.cs#L120-L255) 做整方法 lambda 收集、`ILocalSymbol/IParameterSymbol` 捕获归类、外部赋值/`++`/`--`/`ref`/`out` 实参判定，并把 foreach 迭代变量保守视为每轮重赋值。
3. **holder 发射点**：局部声明处 [StatementTransformer.Declarations.cs:69-77](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs#L69-L77)、for 循环变量（循环前）与 foreach 变量（循环体首每轮新建）[StatementTransformer.Loops.cs:57-66](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L57-L66)、[StatementTransformer.Loops.cs:142-150](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L142-L150)。
4. **挂起/激活状态机**：[MethodConversionState.cs:310-555](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/MethodConversionState.cs#L310-L555) 的 `LambdaCapturePreScanDone`、`PendingLambdaCaptureHolder`、按作用域帧组织的 active holder 表（push/pop 绑定在 [MethodConversionState.cs:229-250](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/MethodConversionState.cs#L229-L250)）。
5. **lambda 内改写路径（第二套同构逻辑）**：[LambdaTransformer.cs:66-78](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L66-L78) 的 `GetMutatedCaptures`（[:228-274](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L228-L274)）即时发射单元素数组；块体用**正则文本替换**把捕获名改成 `_name[0]`（[:169-174](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L169-L174)），绕过 IR；ref 实参写回在 [ArgumentTransformer.Ref.cs:139-140](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.Ref.cs#L139-L140) 特判穿透。
6. **泛型 hack**：holder 对泛型/引用类型发的是 `(T[]) new Object[] { x }` 强转数组（[StatementTransformer.cs:83-85](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L83-L85)），依赖 Java 擦除侥幸成立，是无类型安全的形态。
7. **IR 标识符路径不查 holder 帧**：[IdentifierExpressionTransformer.cs:45-54](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs#L45-L54) 直接输出名字，active holder 帧只有 ref 写回一个消费者——字符串路径与 IR 路径行为不对称，是这套隐式状态「最难调试」的实据。
8. **fail-fast 面**：async lambda [LambdaTransformer.cs:49-53](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L49-L53)、lambda ref/out 参数 [:96-100](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L96-L100)、表达式体展开 [:197-201](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L197-L201)、匿名方法 [:38-39](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L38-L39)。
9. **重复建设**：E02 在 [LocalFunctionLift/](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs) 已建成体系化捕获分析（`CollectCaptures`/`IsCapturedVariableWritten`/fixpoint 传播 [LocalFunctionLiftAnalyzer.cs:405-583](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs#L405-L583)）、确定性命名（[LocalFunctionLiftNaming.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftNaming.cs)）、跨文件命名表（[LocalFunctionLiftPreprocessor.cs:15](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftPreprocessor.cs#L15)）。
10. **回归资产**：[LambdaExternalReassignmentTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/LambdaExternalReassignmentTests.cs)（直连转换管线，断言 Java 文本里的 `int[] _x`/`[0]`）、[LinqRewriterVariableCaptureTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/LinqRewriterVariableCaptureTests.cs)。

catalog 四次补全已定调（[preprocessor-catalog.md:640-651](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L640-L651)、[:810-821](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L810-L821)、§3.3 [:905](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L905)、[:910](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L910)）：C45 是本通道超集，**C32 作为 C45 第一阶段落地，不建第二套捕获分析**。

### 1.2 目标

1. 在 C# 源码层把「被 lambda 捕获且存在写站点」的局部与参数提升为**显式 holder**（holder 声明 + 全部读写点改字段形式），产物是合法、可 `dotnet build -c Release` + `dotnet test -c Release` 验证的 C#，语义与原程序逐字节等价（同一变量的别名共享语义不变）。
2. 新建 `src/CSharpToJava.Core/ClosureLift/` 捕获分析与 holder 提升基建 + pass 脚手架，翻转 `closure-lift` stub（默认关）；捕获分类原语与 E02 收敛为单一实现（§4.2）。
3. 为 C45 预留阶段接口：C32 生成的 holder 类就是「单字段、零方法」的 display class，C45 只增字段/方法与委托点改写，不返工读写点形态，不改链位。
4. 转换器本期**一行不改**：直连单文件管线继续由旧 holder 机制兜底；默认开启且稳定后，转换器侧清理单列后续 PR（与 C04 风险 6 同模式）。

### 1.3 与 C45 的分阶段定义

| 维度 | C32（本任务） | C45（后续同 pass） |
|---|---|---|
| 触发面 | 仅「被捕获 + 可变」的局部/参数；foreach 迭代变量保守提升 | 全部 lambda/匿名方法：无捕获静态方法、仅 this、只读值捕获、可变捕获、嵌套/递归 |
| 生成物 | holder 类（1+ 字段、0 方法）+ holder 实例 + 站点改写 | display class 合并、lambda 体提升为命名方法、委托点 `new Action/Func<>(dc, dc.Method)` |
| this/只读捕获 | 不动 | 提升为字段 |
| 链位/目录/id | `closure-lift` / `.cs2j-closure-lift-src` / RunAfter=`ref-out-in` | **全部不变**，同 pass 加阶段 |
| catalogItems | 翻转为 `C32` | 再扩为 `C32, C45` |

### 1.4 非目标

- 不做 lambda → 命名方法提升、不合并只读捕获、不生成委托构造（C45）；不处理 async lambda 的状态机（C18），但 async lambda **体内**的可变变量 holder 照做（§2）。
- 不动表达式树内 lambda（共享 [ExpressionTreeFacts.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs)）；不做 ref 捕获 lowering（C01 职责，本 pass 只报错等待）。
- 不删除转换器旧 holder 机制；不引入支撑库、不注入新工程。

## 2. 关键设计决策表

| # | 议题 | 决策 | 依据/论证 |
|---|---|---|---|
| D1 | **holder 形态** | **推荐：命名 holder 类**（host 类型内 `private sealed class Cs2jDisplayClass_<member>_<n>`，字段 `_cs2jCap_<name>`，实例 `__cs2jClosure<n>`）。单元素数组仅保留为非默认 `HolderForm.SingleElementArray`（审计/对照用） | ① holder 类就是 C45 display class 的严格子集（先有字段后加方法），站点 `inst.field` 形态 C45 零返工；数组 `[0]` 是死路形态，C45 必须二次改写全部站点。② 多 lambda/多变体共享：Roslyn 按作用域把共捕获变量合并成一个 display 类实例，类形态天然支持分组；数组迫使每变量一个数组。③ 嵌套 lambda 捕获外层 holder 是「捕获一个不再赋值的只读引用」，类字段链直接表达。④ 消灭泛型 `(T[])new Object[]{}` 强转 hack（§1.1-6），类字段 `T _cs2jCap_x` 是类型安全 C#。⑤ 与 C04 `CaptureLayout`/`_cs2jCap_` 命名同族（[c04 设计 §4.3](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c04-iterator-lowering-design.md)）。代价：每作用域多一个嵌套类型，由命名表与 fixture 消化 |
| D2 | **可变判定（触发谓词）** | 符号 s 满足：① 是 `ILocalSymbol/IParameterSymbol`；② 被至少一个语句/表达式 lambda 的体引用且声明点在该 lambda 外（按符号同一性，不按名字）；③ 存在写站点：赋值左值（排除声明器初始化）、前缀/后缀 `++`/`--`、`ref`/`out` 实参；或 ④ 声明是 foreach 迭代变量（保守，见 D9）。不做完整 DFA，采取「宁可提升」的保守策略 | 与现有 `PreScanLambdaCaptures`/`GetMutatedCaptures` 的并集口径一致，避免漏判（漏判=改变闭包语义，最高风险）；过提升只多一次分配，M1 审计量化 |
| D3 | 只读值捕获 | **不动**。无写站点的捕获变量在 Java 侧即 effectively final，直连路径已可用 | catalog C32 范围仅限可变；动只读会产生无收益 churn，且提前侵占 C45 工作面 |
| D4 | 参数捕获 | 参数保留在签名上；holder 实例在方法体首句构造、字段以参数初值初始化；方法体内全部站点（含再赋值、lambda 内外）改写 | 对齐现转换器参数 holder 前置发射（[StatementTransformer.cs:76-88](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L76-L88)），但在 C# 层一次做对 |
| D5 | this 捕获 | 不动（无「可变性」概念；Java lambda 可直接捕获 this） | C45 字段化；C32 提升 this 无收益 |
| D6 | ref 捕获 | 被捕获符号是 `ref` 局部或 `RefKind != None` 的参数（C01 未归一形态）→ **CS2J3201 显式报错**，不猜测 | 字段化会切断与调用方槽位的别名；C01 默认在链（[PassCatalog.cs:120-131](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L120-L131)），其产物 `ByRef<T>` 形参是普通对象，不再触发本条；lambda 的 ref/out 形参仍由转换器 fail-fast，C32 不碰 lambda 签名 |
| D7 | 多 lambda 共享 | 同一符号全方法只有一个 holder（一个字段、一个实例）；同一声明作用域内多个被提升符号**共用一个 holder 类与实例**（一组） | Roslyn 同作用域单个 display class 实例；也对齐现测试「holder 只建一次」（LambdaExternalReassignmentTests `SingleHolder`） |
| D8 | 嵌套 lambda | 外层变量提升后，内层 lambda 捕获的是 holder 实例（只读局部），不再触发二次提升；分析对所有嵌套 lambda 一次算完，重写共用同一组站点表 | 单层 Analyze→Rewrite 即可收敛，无需迭代；生成名带 annotation 保证重跑幂等（§4.6） |
| D9 | for/foreach 循环捕获 | **for** 归纳变量：holder 实例在循环**之前**构造，循环内/捕获 lambda 全部改字段（单一存储，等价 C# 单一循环变量）。**foreach** 迭代变量：holder 在循环体**首句每轮新建**并以本轮值初始化，lambda 捕获本轮实例（C# 5+ 每轮独立变量语义） | 现转换器两处专门发射点（Loops.cs:57-66 与 :142-153）即此二分；旧版 C#（≤4）foreach 共享变量是历史坑，本 pass 以 Roslyn/Latest 编译语义为准并在 fixture 用「循环内创建委托、循环外调用」断言锁定每轮值 |
| D10 | lock/using/const 变量 | `lock(x)` 的 x 被提升时锁表达式同步改 `lock(holder._cs2jCap_x)`（别名不变）；using 局部在 C# 中只读、const 不可赋值——均无写站点，不提升；using 外再赋值本就非法（基线 CS 错误） | 基线优先：Roslyn 已拒绝的非法组合不二次负责（§6） |
| D11 | 表达式树 | `Expression<TDelegate>` 引号内 lambda 整体跳过（共享 [ExpressionTreeFacts.IsExpressionTreeLambda](file:///d:/code/cs2j/src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs#L12)），子树内标识符不被重写；若同一可变符号还被语句 lambda 捕获（混合），**CS2J3206 报错并跳过该符号**（只改外部站点会令树内引用悬空，改字段会改变表达式树形态） | catalog §4 风险 8（[:927](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L927)）；表达式树 lambda 不可能含赋值，纯引号引用不需要 holder |
| D12 | 静态 lambda / 无捕获 | 含 `static` 修饰或经分析无捕获的 lambda：不产生任何 holder（其体自然不在任何站点表内） | C45 时映射为静态命名方法；本期无作业 |
| D13 | async lambda | 体的可变变量照常提升（holder 改写与 await 无关，C# 语义保持）；不拒绝、不生成状态机 | C18 负责 async 语义；holder 先显式化正是 C18 的前置条件（catalog C45 行） |
| D14 | 匿名方法 / 局部函数 | 匿名方法应由 E05（`delegate` pass，默认开）先降为 lambda；残余 `AnonymousMethodExpressionSyntax` → CS2J3203。局部函数应由 E02（默认开）提升为私有方法；提升后方法体内的 lambda 按普通方法体处理；残余局部函数与其外层的跨边界捕获 → CS2J3204 | E02/E05 都在链上且先于本 pass（[PassCatalog.cs:45-89](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L45-L89)）；残余属异常防线 |
| D15 | 指针/ErrorType/dynamic | 指针类型变量被 lambda 捕获本就是基线编译错误（镜像 E02 `IsUnsupportedCaptureType`，[LocalFunctionLiftAnalyzer.cs:787-797](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs#L787-L797)）→ CS2J3205 防御诊断；符号解析失败/ErrorType → CS2J3202；dynamic 正常可分析，不特殊处理 | 无法等价表达时显式报错，禁止注释/占位（catalog 第一原则） |
| D16 | 字段/属性初始化器内 lambda | 无方法体宿主放置 holder 实例，v1 CS2J3204 跳过并报码 | Roslyn 自身为此生成 display class；形态留给 C45，M1 审计先量命中率 |

## 3. 注册表与链位契约

### 3.1 现状（stub，已在 base 与 HEAD 双重点核实）

[PassStubCatalog.cs:152-161](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L152-L161)：id `closure-lift`，DisplayName "Closure/display class lift"，目录 `.cs2j-closure-lift-src`，`PassStage.StateMachine`，`PassStatus.Stub`，`defaultEnabled: false`，`runAfterIds: ["ref-out-in"]`，`needsSemanticModel=true`（默认），catalogItems `C32, C45`。禁用时不进计划；显式启用报 CS2J9001（[PassStubCatalogTests.cs:59-106](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L59-L106)，现用例正以 `ClosureLift` 为靶子）。

两处链位事实（与 [c04 设计 §3.3](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c04-iterator-lowering-design.md) 的预期不同，按现状定案）：

- **C04 已翻转为 Implemented**（[PassCatalog.cs:196-205](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L196-L205)，默认关），其 RunAfter 锚的是 `make-readonly` 而非 `closure-lift`；`eliminate-goto` 已带 `iterator-lifting` 边（[:206-215](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L206-L215)）。
- stub 中 `async-normalize` RunAfter=`closure-lift`（[PassStubCatalog.cs:229-238](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L229-L238)）、`member-desugar` 的 RunAfter 含 `closure-lift`（[:192-201](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L192-L201)）。

### 3.2 Stub→Implemented 翻转清单

| 动作 | 文件 | 内容 |
|---|---|---|
| 新建 | `src/CSharpToJava.Core/ClosureLift/*` | 见 §4 |
| 新建 | `src/CSharpToJava.Core/Analysis/CaptureClassification.cs` | M2 从 E02 提取的共享捕获分类原语（§4.2） |
| 新建 | [Adapters/ClosureLiftPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters) | 仿 [LocalFunctionLiftPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/LocalFunctionLiftPassAdapter.cs)：整树语义、verify build/test、residual 闸门（§5） |
| 改 | [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) | 删除 closure-lift 条目；**保留 `Ids.ClosureLift` 常量**（PassCatalog/排序边/其他 stub 引用） |
| 改 | [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) | `Implemented` 表在 make-readonly 之后、iterator-lowering 之前插入 descriptor：Status=Implemented、`defaultEnabled:false`（M3 门槛后再议翻 true）、verifyBuild/Tests=true、runAfterIds=`["ref-out-in"]`、catalogItems=`"C32"`、无支撑库；**同时给 iterator-lowering 的 runAfterIds 追加 `PassStubCatalog.Ids.ClosureLift`**（拓扑锁死 closure→iterator） |
| 改 | [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs) | `CreateAdapter` switch 加 `PassStubCatalog.Ids.ClosureLift => new ClosureLiftPassAdapter(...)`；`BuildLegacyToggles` 加 `[ClosureLift] = false`（仿 iterator [:77](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L77)，无 legacy 开关，仅通用 `--enable-pass`） |
| 改 | [PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs) | `Assert.Equal(23, …)` → 22；`Assert.Contains(ClosureLift,…)` 删除；两处 CS2J9001/启用计划用例（:65、:92）改用仍为 stub 的 id（建议 `async-normalize`——它同样语义默认关且无副作用） |
| 新建 | `tests/.../Preprocessing/ClosureLiftOrderingTests.cs` | 全序断言：`index(ref-out-in) < index(closure-lift) < index(iterator-lowering) < index(eliminate-goto)`；默认计划不含 closure-lift；全启用计划含且相邻关系成立 |
| 不改 | Program.cs / 指纹 / 保留目录 | 通用链 descriptor 驱动，目录集合经执行结果自动回填；指纹令牌按注册表全集 `pass:<id>=on/off` 自动纳入；无支撑库、不重指指纹源 |

### 3. 链位语义（默认关期与开启后）

1. closure-lift 默认关：启用计划是全序过滤（[PassRegistry.cs:95-133](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs#L95-L133) 声明序兜底），现有链一字节不变。
2. 开启后目标全序：`… → ref-out-in → … → make-readonly → closure-lift → iterator-lowering → eliminate-goto → …`。closure-lift 与 make-readonly 的先后无数据依赖，但 descriptor **声明位置**放在 make-readonly 与 iterator 之间 + iterator 补边，得到唯一确定序，不靠人工约定。
3. C04 当前自承捕获存储（其 `_cs2jCap_` 字段）；closure-lift 默认关期两形态不相遇。未来两者同开时，「先 holder 显式化、后迭代器状态机」保证 C04 deferred 的 CS2J0405 形态（体内 lambda 捕获）有显式字段可收编。

### 3.4 ★为 C45 预留：阶段接口与演进约束

- pass 身份（id、目录、Stage、RunAfter=`ref-out-in`、无支撑库）**C45 落地时不变**；只把 catalogItems 从 `C32` 改回 `C32, C45`，默认开关独立决策。
- C32 的 plan/生成物按「display class 子集」建模：holder 类 = 仅有字段段的 display class；站点只允许出现「实例.字段」形态——C45 增加字段、把 lambda 体搬为同类命名方法、委托点改写时，既有站点零改动。
- Core 内预留阶段装配点（§4.8 `IClosureLiftStage`）：C32 只注册一个 `HolderLiftStage`；C45 追加 `DisplayClassConsolidationStage` / `NamedMethodLiftStage` / `DelegateSiteRewriteStage`，编排按阶段序执行，不另起 pass、不改注册表。
- C45 文档（同日）依赖本设计的：命名前缀表、holder 类形状、站点改写口径、共享 `CaptureClassification`、residual 「无隐式可变捕获」断言。

## 4. Core 组件设计

目录 `src/CSharpToJava.Core/ClosureLift/`（internal 实现 + 一个公开入口，仿 [LocalFunctionLiftPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftPreprocessor.cs) 与 IteratorLowering 的组织）：

```
ClosureLiftOptions.cs        # Strict=true, AuditOnly=false, HolderForm=HolderClass
ClosureLiftStatistics.cs     # 通用计数 + pass 专有计数（§4.7）
ClosureLiftDiagnostic.cs     # Severity/Code(CS2J32xx)/Message/FilePath/Line
ClosureLiftResult.cs         # OutputCode/Changed/Diagnostics/Statistics（逐文件）
Capture/CaptureClassification.cs   # 共享原语（落点先在 Analysis/，§4.2）
Capture/CaptureAnalysis.cs         # 每个成员体的捕获+写站点分析，产出方法级 Model
ClosureLiftStage.cs          # IClosureLiftStage（C45 预留，§4.8）
HolderPlan.cs                # HolderGroupPlan / CapturedSymbolPlan / SiteRef
ClosureLiftNaming.cs         # 类名/实例名/字段名/防撞（跨文件表）
HolderSyntaxGenerator.cs     # 嵌套 holder 类 + 实例声明/初始化的 SyntaxFactory
CaptureSiteRewriter.cs       # CSharpSyntaxRewriter：声明点/读写点/lock/循环点改写
HolderLiftStage.cs           # C32 唯一阶段：Analyze→Plan→Generate→Rewrite
ClosureLiftPreprocessor.cs   # 公开入口；项目模式跨文件共享命名表（仿 E02）
```

### 4.1 三阶段、整树、语义模型

- 标准脊柱（[ProjectPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs)）已建好整树 compilation，parse 选项 `LanguageVersion.Latest`、`allowUnsafe:true`；本 pass `needsSemanticModel=true`，逐文件拿 `SemanticModel`，但**分析与重写分两遍**：第一遍整 CompilationUnit 收集所有成员体的分析结果与命名占用，第二遍统一重写（保证同文件多方法的 holder 类名/实例名确定性）。
- 阶段内子步骤：**Analyze**（只产 model 与诊断，不改树；`AuditOnly=true` 时到此为止并输出报告）→ **Plan**（分组、命名、站点表、skip 集冻结）→ **Rewrite**（发类、发实例、改站点）。skip/报错集必须在任何重写前确定，失败时文件原样保留且仍可编译（与 E02/C04 同纪律）。
- 分析根：所有有体方法成员（方法/构造器/运算符/转换/析构/属性与索引器及事件访问器）；局部函数按 D14 处理（E02 稳态下不存在；残余则按独立根分析并对跨边界捕获报 CS2J3204）；字段/属性初始化器按 D16。
- 幂等：生成节点带 annotation（键 `cs2j_closure_lift`，记录 group id/字段名），Analyze 跳过带注解的生成类与 `__cs2jClosure*` 符号；重复执行输出字节稳定（为 catalog §5.4 fixpoint 编排做准备）。

### 4.2 CaptureAnalysis：复用 E02 形态，不复制第二套

边界规定（落实 catalog「不要建成两套捕获分析」）：

1. **本期 M2 做一次小提取**：把 E02 已验证的三件原语从 [LocalFunctionLiftAnalyzer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs) 抽到 `src/CSharpToJava.Core/Analysis/CaptureClassification.cs`（public static，无状态）：
   - `CollectCapturedSymbols(model, bodyRoot, scopeKinds)`：体中引用的外部 `ILocalSymbol/IParameterSymbol/IRangeVariableSymbol`（形状借 :405-465，含 lambda 参数排除、自身声明排除、NameColon 排除、ErrorType 过滤）；
   - `FindWriteSites(model, symbol, bodyRoot)`：赋值左值/前后缀 inc-dec/ref-out 实参枚举（形状借 :491-529；C32 扩展：排除声明器初始化、区分站点是否位于引号 lambda 内）；
   - `IsUnsupportedCaptureKind(symbol/typeDisplay)`：指针/`ref ` 前缀/匿名类型判定（借 :787-797）。
2. E02 改为**调用**提取后的原语，其 gate/plan/fixpoint 传播（`PropagateCaptures`）保持在 LocalFunctionLift 命名空间内不动——行为零变化，由 E02 现有测试守门；本次提取作为 M2 的一个独立提交，先绿再开发 C32。
3. ClosureLift 只拥有自己的**策略层**：D2 触发谓词、foreach 保守规则、引号 lambda 混合判定、holder 分组与站点表。捕获「怎么找、怎么判写」全树只有 `CaptureClassification` 一份实现。
4. 退路（若 M2 提取波及面超预期）：先在 ClosureLift 内用同形状实现并挂 `// TODO(C45): merge with Analysis/CaptureClassification` 注释，C45 必须合并；但默认路径是第 1 条，避免三份漂移（C09/C10 等设计已登记同一收敛义务）。
5. 与转换器旧代码的关系：[StatementTransformer.Utilities.cs:120-255](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Utilities.cs#L120-L255) 与 `GetMutatedCaptures` **不提取、不复用**（Java 类型映射、字符串发射耦合，属默认开启后删除对象）。

### 4.3 HolderPlan（数据契约）

```csharp
internal sealed class HolderGroupPlan
{
    public required TypeDeclarationSyntax HostType;          // 嵌套类插入目标
    public required MemberDeclarationSyntax HostMember;      // 成员体（命名锚）
    public required BlockSyntax ScopeRoot;                   // 实例声明所在作用域块
    public required string ClassName;                        // Cs2jDisplayClass_M_n
    public required string InstanceName;                     // __cs2jClosure<n>
    public List<CapturedSymbolPlan> Symbols = new();         // 同组被提升符号（有序）
    public bool PerIteration;                                // foreach 每轮实例化
    public ForEachStatementSyntax? PerIterationLoop;
}

internal sealed record CapturedSymbolPlan(
    ISymbol Symbol, string OriginalName, string FieldName,   // _cs2jCap_<name>
    string TypeDisplayFullyQualified,
    SyntaxNode DeclaringNode, TriggerReason Reason,
    List<SiteRef> Sites);                                    // 全部读/写/声明/lock 站点

internal enum TriggerReason { ExternalWrite, InsideLambdaWrite, RefOutArgument, ForeachIteration }
```

分组规则：键 = (宿主成员, 声明作用域块, 是否 foreach 每轮)。同组符号共享一个类类型与一个实例；foreach 迭代变量单独成组（`PerIteration=true`，类类型可与其他组复用命名但实例每轮新建）。for 归纳变量入宿主方法体组（循环前实例化）。符号顺序按 `DeclaringNode.SpanStart` 确定性排序；组顺序按成员体语法序——重跑字节稳定。

### 4.4 HolderSyntaxGenerator：生成形态（示意）

```csharp
// 输入
int Count(int initial) {
    int count = initial;
    Action inc = () => count++;
    inc();
    Console.WriteLine(count);
    return count;
}

// 输出（同文件、宿主类型内合成 private sealed 嵌套类）
private sealed class Cs2jDisplayClass_Count_0
{
    public int _cs2jCap_count;
}

int Count(int initial) {
    var __cs2jClosure0 = new Cs2jDisplayClass_Count_0 { _cs2jCap_count = initial };
    Action inc = () => __cs2jClosure0._cs2jCap_count++;
    inc();
    Console.WriteLine(__cs2jClosure0._cs2jCap_count);
    return __cs2jClosure0._cs2jCap_count;
}
```

要点：

- 嵌套类挂在 E02 同款「宿主类型本文件声明」节点上（partial 类型的另一分册由该分册各自插入同名风险由跨文件命名表规避，借 E02 `_crossFileNameTable` 形态 [LocalFunctionLiftAnalyzer.cs:952-988](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs#L952-L988)）。
- 类无方法、无属性、字段 public 可变——这正是 Roslyn display class 的字段段；sealed + private + 引用相等，不实现 Equals/GetHashCode。
- 局部声明器改写：声明从 `T x = init;` 变为「实例声明中带字段初始化器」或「实例声明 + 紧随的字段赋值」（无初始化/多声明器 `int a,b;` 用默认值，default 字面量在 C# 合法）；原声明语句移除。
- 参数（D4）：方法体首句 `var __cs2jClosure0 = new … { _cs2jCap_p = p };`。
- foreach（D9）：循环体首句 `var __cs2jClosureN = new … { _cs2jCap_item = item };`，循环体与 lambda 内引用走字段；for 归纳变量：循环前 `new …{ _cs2jCap_i = 0 }`，for 的 initializer/increment/condition 同步改字段。
- 数组形态（`HolderForm.SingleElementArray`，非默认）发 `T[] __cs2jArr_x = new[] { x };` + `[0]` 站点，仅供审计对照与回归比对，不进默认验收。

参数捕获形态（D4；签名不变，体首实例化，含初始别名快照语义）：

```csharp
// 输入
void Run(int seed, Action trigger) {
    seed++;
    Action a = () => seed += 2;
    a(); trigger();
    Console.WriteLine(seed);
}

// 输出（Run 签名不变；所有站点——包括 seed++——都只触及字段）
private sealed class Cs2jDisplayClass_Run_0 { public int _cs2jCap_seed; }

void Run(int seed, Action trigger) {
    var __cs2jClosure0 = new Cs2jDisplayClass_Run_0 { _cs2jCap_seed = seed };
    __cs2jClosure0._cs2jCap_seed++;
    Action a = () => __cs2jClosure0._cs2jCap_seed += 2;
    a(); trigger();
    Console.WriteLine(__cs2jClosure0._cs2jCap_seed);
}
```

for/foreach 对照形态（D9；两类循环的存储生命周期差异由实例化位置表达）：

```csharp
// for：循环前一个实例，所有轮次与所有委托共享同一字段（= C# 单一循环变量）
var __cs2jClosure0 = new Cs2jDisplayClass_Work_0 { _cs2jCap_i = 0 };
for (; __cs2jClosure0._cs2jCap_i < n; __cs2jClosure0._cs2jCap_i++)
    acts.Add(() => __cs2jClosure0._cs2jCap_i);

// foreach：每轮首句新建实例，本轮委托捕获本轮对象（= C# 5+ 每轮独立迭代变量）
foreach (var item in source)
{
    var __cs2jClosure1 = new Cs2jDisplayClass_Work_1 { _cs2jCap_item = item };
    acts.Add(() => __cs2jClosure1._cs2jCap_item);
}
```

### 4.5 SiteRewriter 改写口径

- 单个 `CSharpSyntaxRewriter`，输入 = 冻结的站点表（`SyntaxReference` 列表，按 `ISymbol` 归一，阴影同名不误伤——现转换器按名帧匹配正是易错点）。
- 改写：① 标识符读 → `instance.Field` 成员访问；② 赋值/`++`/`--` 左值 → 成员访问左值；③ `ref`/`out` 实参 → `ref instance.Field`/`out instance.Field`（字段可传 ref，别名语义保持；D6 命中则先报码不改）；④ 声明语句按 §4.4 替换；⑤ `lock(...)` 表达式同步；⑥ for/foreach 特殊点。
- **不进入**的子树：引号 lambda（D11）、生成类自身、带注解节点、特性/nameof（`nameof(x)` 是字符串字面量，必须保留原名——借 E02 `IsInsideNameOf` 形态 [LocalFunctionLiftAnalyzer.cs:750-762](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs#L750-L762)）、XML doc 注释。
- 改写后用 `Microsoft.CodeAnalysis.Formatting.Formatter` + `AdhocWorkspace` 格式化（与 E02 一致 [LocalFunctionLiftPreprocessor.cs:71-73](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftPreprocessor.cs#L71-L73)），再做 Roslyn 重新解析诊断快照比对（基线已有的错误不新增）。

**等价性论证（评审基线）**：holder 实例对每个被提升符号引入一个 fresh 存储位置，原符号的全部读/写/取别名站点被双射到该位置，故：① 任意时刻原变量与字段值一一对应（初始化点双射建立）；② lambda 内外对同一字段的读写与 C# 闭包共享同一变量的可观察行为一致（含「lambda 未被调用则字段不变」「lambda 内写对后续外读可见」）；③ `ref/out` 经 `ref instance.Field` 保持引用同一托管槽位，方法返回前的写回时序不变；④ foreach 每轮 fresh 实例精确对应 C# 每轮 fresh 迭代变量，for 单实例对应单一循环变量；⑤ 不引入拷贝、不引入新的可见标识（生成名撞名由命名表排除），异常行为（赋值中抛出）仅取决于右侧表达式，与原程序同。残余风险只可能来自站点枚举不完备，由 §7.3 闸门与风险 1 缓解覆盖。

### 4.6 确定性命名

- 类名 `Cs2jDisplayClass_<HostMember>_<n>`：`<n>` 在宿主类型内从 0 按成员语法序分配；成员名经 [LocalFunctionLiftNaming.GetBaseName](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftNaming.cs#L13-L39) 同款大小写/关键字处理（本 pass 用 Pascal 成员名）；跨文件同名类型共用计数表（E02 形态）。
- 实例名 `__cs2jClosure<n>`：成员体内从 0 按组序分配；撞名（用户已有同名局部）走 ResolveUniqueName 风格后缀。
- 字段 `_cs2jCap_<originalName>`（与 E02 捕获参数、C04 捕获字段同族前缀）；冲突加 `_2`。
- 输出经 `Formatter` 规范化；任何命名决策只依赖语法序与符号表，禁止随机/哈希。

### 4.7 Options / Statistics / Diagnostic

```csharp
public enum HolderForm { HolderClass, SingleElementArray }
public sealed record ClosureLiftOptions(
    bool Strict = true,          // skip/残余在 strict 下为 Error
    bool AuditOnly = false,      // M1：只报告不改写
    HolderForm HolderForm = HolderForm.HolderClass,
    bool Verbose = false);
```

Statistics：`FilesScanned/FilesTransformed/FilesSkippedClean`、`HolderClassesGenerated`、`HolderInstancesGenerated`、`CapturesLifted`（按 TriggerReason 分桶）、`SitesRewritten`、`ForeachPerIterationGroups`、`SkippedByReason`、`ResidualMutableCaptures`。

诊断码段 **CS2J32xx**：

| 码 | Severity | 触发 |
|---|---|---|
| CS2J3201 | Error（strict）/Warning | 被 lambda 可变捕获的 ref 局部或 by-ref 参数（C01 形态未到位），见 D6 |
| CS2J3202 | Error | 符号不可解析/ErrorType，无法保证站点全集（含基线外的不可分析站点） |
| CS2J3203 | Error | 残余匿名方法（E05 产物缺失），见 D14 |
| CS2J3204 | Error/Warning | 残余局部函数跨边界捕获 / 字段属性初始化器内 lambda（D14、D16） |
| CS2J3205 | Error（防御，正常不可达） | 指针等不合法捕获类型（镜像基线 CS 错误） |
| CS2J3206 | Error | 可变捕获符号同时被表达式树 lambda 引用（D11 混合形态） |
| CS2J3207 | Info | AuditOnly 报告行（成员/符号/原因/站点数/foreach 标记） |

### 4.8 给 C45 的阶段接口与文件所有权

```csharp
internal interface IClosureLiftStage
{
    string Id { get; }
    ClosureStageResult Apply(ClosureStageInput input); // input 携带整树/model/命名表/累计诊断
}
// C32：注册 [HolderLiftStage]。C45：[HolderLiftStage, DisplayClassConsolidationStage,
//      NamedMethodLiftStage, DelegateSiteRewriteStage]，由 Preprocessor 按序执行。
```

`HolderGroupPlan` 即 C45 `DisplayClassPlan` 的子集记录（类引用 + 字段集 + 零方法 + 零委托点）；C45 以 `record add` 扩字段，不改 C32 已写字段语义。文件所有权：

| 文件/目录 | C32 | C45 | E02 |
|---|---|---|---|
| `ClosureLift/**`（本 §4 全部文件） | 新增/拥有 | 同目录扩展（新增 `DisplayClass/` 子目录与阶段），不重写 C32 规则 | 不动 |
| `Analysis/CaptureClassification.cs` | M2 从 E02 提取 | 按需加查询，保持单一实现 | 提取后改为消费方 |
| `LocalFunctionLift/**` | M2 仅改调用点（行为不变） | 不动 | 拥有 |
| 注册表三文件 + 测试 | 翻转一次（§3.2） | catalogItems/描述微调，无第二次拓扑变更 | 不动 |
| 转换器 `StatementTransformer*` / `LambdaTransformer` / `MethodConversionState` | 不动 | 默认开启稳定后另立清理 PR 删除 | 不动 |

## 5. CLI 适配器与集成

- 新建 `ClosureLiftPassAdapter : ProjectPassAdapter`，整体仿 [LocalFunctionLiftPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/LocalFunctionLiftPassAdapter.cs)：`RewriteFileAsync` 调 `ClosureLiftPreprocessor.Preprocess(source, model, options)`，合并统计/诊断（补 FilePath）；`VerifyBuild/VerifyTests` 透传（descriptor 默认 true）；`DeleteBuildOutputsAfterRestore=true`；`BuildChainSummaryLine` 输出 `closure-lift: N transformed, M clean, K holders, C captures lifted, R residual`。
- `AfterFilesAsync` 跑 **residual 闸门**（§7.3）：重新 parse 中间树全部 .cs，以 `AuditOnly` 口径再分析，残余可变捕获 >0 时 strict=Error；仿 E02 `CountResidualLocalFunctions`（[LocalFunctionLiftPassAdapter.cs:74-91](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/LocalFunctionLiftPassAdapter.cs#L74-L91)）。
- 无支撑库：descriptor `supportLibrary=None`，不过滤工程、不改 csproj、不注入资源。
- 通用 verb 可达：`preprocess --pass closure-lift` 与 `--enable-pass closure-lift` 经现有 `PassPlanResolver`/`CliPreprocessChain` 自动生效，不新增命令行选项；`--list-passes` 显示 Implemented/默认关/StateMachine/目录。
- 指纹/保留目录：随注册表全集令牌自动纳入（[c04 §3.2「不改」行](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c04-iterator-lowering-design.md)同款），无额外工作。

## 6. 诊断策略（无法等价表达即显式报码）

1. 基线优先：整树先取编译诊断快照；Roslyn 已拒绝的形态（匿名方法在 E05 之后不应存在、指针捕获、using 变量赋值等）属用户源码错误，不重复报、不兜底。
2. 本 pass 只对「基线没拦住、但本 pass 不能安全改写」的形态发 CS2J32xx（§4.7）；strict 下任何 Error 令本次 pass 失败、文件原样保留（树仍可编译），verify build 不会被坏树污染。
3. 禁止注释/占位/原样透传（catalog §4 第一原则，[:931](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L931)）：不得发 `/* TODO capture */`，不得只改一半站点（站点表不全 = CS2J3202，不改）。
4. 每条诊断带文件/行号/成员名/符号名/TriggerReason，AuditOnly 报告同时输出「若改写将生成的类名/字段名」，供 M1 评审命中率。
5. 转换器侧既有 fail-fast（async lambda、lambda ref 参数等）保持不变，是默认关期与直连管线的安全网。

## 7. 测试计划

### 7.1 Core 单测（`tests/CSharpToJava.ProjectTests/ClosureLiftUnitTests.cs`，仿 [IteratorLoweringUnitTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs)：内联源码→`CSharpCompilation`→Preprocess→文本断言 + 输出树可编译断言）

1. Options/Statistics/Diagnostic 默认值与字段；命名工具（类名/实例名/字段名/撞名后缀/跨文件表）。
2. 只读捕获：`int x=5; Action a=()=>Use(x);` 输出零变化（Changed=false）。
3. 外部再赋值：holder 类 + 实例 + 外部与 lambda 内全部站点为字段形式；无裸名残留。
4. lambda 内赋值、外部读（`()=>count++` 后 `Console.WriteLine(count)`）：同组单实例单字段。
5. 内赋 + 外赋并存：holder 仅一个（对齐现 `SingleHolder` 断言语义）。
6. 多 lambda 共享同一变量（一读一写两个委托）：字段共享、实例共享。
7. 同作用域两个被提升变量：共用一个 holder 类/实例、两字段顺序确定；跨两次运行字节一致。
8. 参数捕获（含参数在 lambda 后被再赋值）：体首实例、签名不变。
9. 嵌套 lambda：外层 lambda 内声明的可变局部被内层 lambda 捕获；外层捕获的方法局部只一层 holder；内层不再生成第二套。
10. for 归纳变量被 lambda 捕获且循环外调用委托：holder 在循环前、单一存储、结果反映终值。
11. **foreach 每轮语义**：循环体创建 `Action` 存入列表，循环后依次调用，断言得到每轮独立值（与 for 组共享形态形成对照）；holder 实例位于循环体首句。
12. 静态 lambda、无捕获 lambda：零生成物。
13. async lambda 内可变捕获：holder 正常、await 语义树不被破坏（输出可编译）。
14. 表达式树：纯引号 lambda 内引用可变符号不报错时不改；引号 + 语句 lambda 混合引用同一符号 → CS2J3206 且文件原样。
15. ref/out 参数与 ref 局部被捕获 → CS2J3201；`M(ref x)`（x 是被提升普通局部）站点改 `ref __cs2jClosure0._cs2jCap_x` 且可编译。
16. 残余匿名方法直输 → CS2J3203；残余局部函数跨边界 → CS2J3204；指针捕获 → CS2J3205；坏符号 → CS2J3202。
17. **与 E02 交互**：输入为「已提升私有方法 `..._cs2jCap_count` 形参被该方法内 lambda 捕获并再赋值」（E02 产物形态），C32 在该私有方法内对该参数正常提升。
18. **与 E05 交互**：标准链产物（匿名方法已消失）上 Changed 统计正确。
19. lock 对象被捕获再赋值：`lock(__cs2jClosure0._cs2jCap_locker)` 等价；using 变量/const 不提升。
20. this 捕获：不动（输出与输入一致）。
21. 多声明器 `int a=1,b=2;` 仅 a 被捕获时只提升 a，b 保持普通局部。
22. 幂等：连续 Preprocess 两次，第二次 Changed=false 且文本字节相同；带注解节点不被二次分析。
23. AuditOnly：Changed=false、统计/CS2J3207 报告含成员/原因/站点数。
24. `HolderForm.SingleElementArray`：数组形态与类形态在同一 fixture 上行为断言一致（只跑 Core 文本/编译，不做默认验收）。
25. nameof/特性/XML 注释中的同名标识符不被改写。

### 7.2 项目级 fixture（`tests/CSharpToJava.ProjectTests/SampleClosureLiftSolution/`）

- Lib：外部再赋值计数器、多委托共享、参数捕获、嵌套委托、for/foreach 对照（每轮值断言）、lock、泛型方法 `<T>` 捕获（验证消灭 Object[] 强转）、与 E02 提升方法共存、静态/无捕获对照。
- Tests：xUnit `RuntimeBehaviorTests` 断言**可观察行为**（计数结果、列表内容、调用时序），不断言生成文本；`dotnet build -c Release` + `dotnet test -c Release` 全绿（.NET 半区）。
- 多线程不引入新同步：加一组「两线程经委托递增共享计数器」用例做行为对照（holder 字段与原局部同为无同步共享存储，语义不增不减）。

### 7.3 residual 闸门与回归

- 适配器 `AfterFilesAsync`：中间树重解析后，任何非引号、非生成注解子树内存在「被捕获 + 有写站点」符号 = residual >0 → strict 失败。Core 单测直接断言 `ResidualMutableCaptures==0`。
- 静态断言「无隐式捕获残留」的方式：residual 分析器只认语法+符号（不依赖转换产物），对 fixture 每个文件断言 0；另加一条「输出中不存在对原符号名的裸引用（nameof/引号树除外）」的站点表完备性断言。
- 转换器回归：[LambdaExternalReassignmentTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/LambdaExternalReassignmentTests.cs) 与 [LinqRewriterVariableCaptureTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/LinqRewriterVariableCaptureTests.cs) 直连管线、不经预处理链，必须原样全绿（本期不改转换器）。
- E16 LINQ 过程式化（转换器内 LinqRewrite）：holder 字段访问是普通成员表达式，加一条「被过程式化的 Where/All lambda 外提升变量」端到端用例，确认提取方法对字段访问透明。

### 7.4 CLI/链测试

- 默认计划不含 closure-lift；`--enable-pass closure-lift` 时计划含且位于 make-readonly 与 iterator-lowering 之间（§3.2 排序测试）；stub CS2J9001 用例换靶子后全绿；`--list-passes` 内容快照；独立 `preprocess --pass closure-lift` 跑 SampleClosureLiftSolution build/test 绿。

## 8. 默认开启验收门槛

沿用 foundation plan 横切规则（[:116](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L116)）与 C04 §8 形态，翻转 `defaultEnabled:true` 必须同时满足：

1. 三个测试工程 `dotnet test -c Release` 全绿（含 7.1/7.2/7.4 全部新增）；fixture .NET 半区 build/test 绿。
2. 转换器既有用例零改动全绿（直连路径行为不变）。
3. csharp-sdk（D:\csharp-sdk）画像：AuditOnly 报告成文——命中文件数/符号数/原因分桶/foreach 占比/CS2J3201-3206 命中数；开启后基线 build/test 不退化、无新增 CS2J32xx Error；holder 分配增量在可接受范围（审计给数据，门槛评审定阈值）。
4. residual 闸门在 sdk 中间树为 0（除显式登记的 skip 诊断且全部 Warning 级有主）。
5. 与 C04 联合开关（closure-lift + iterator-lowering 同开）在含「lambda 内 foreach-yield」交界 fixture 上绿；与 E02/E05 默认链任意组合幂等。
6. 默认关至少稳定一个里程碑窗口，再评审翻默认开；不重蹈「长期 opt-in 多套事实标准」（catalog 风险 10，[:929](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L929)）。

## 9. 里程碑

| 里程碑 | 内容 | 出口 |
|---|---|---|
| **M1 分析 + 审计（只报不改）** | `CaptureClassification` 提取（E02 改消费方、其测试先绿）；CaptureAnalysis + Options/Statistics/Diagnostic + AuditOnly 报告；适配器可挂接但不翻转 | Core 审计单测；sdk 跑 AuditOnly 产出命中率报告；树零改动 |
| **M2 holder 提升 + stub 翻转（默认关）** | HolderPlan/Naming/Generator/SiteRewriter/HolderLiftStage；class 形态全规则（D1-D16）；residual 闸门；适配器；§3.2 翻转清单；数组形态作为对照选项 | §7 全部用例绿；fixture Release build/test 绿；排序/CLI 测试绿；默认计划零变化 |
| **M3 默认关稳定 + 默认化评审** | sdk 画像复核、CS2J32xx 清零或全部有主、与 C04/E02/E05 联合验证、性能分配数据 | §8 六项结论成文；决定翻默认开或继续 opt-in |
| **（C45 接续，不在本 pass 承诺）** | 同 pass 注册 display class 合并/命名方法/委托点阶段；catalogItems 扩回 `C32, C45`；转换器旧 holder 机制删除 PR | 另立同日设计，依赖本文件 §3.4/§4.8 接口 |

## 10. 风险

1. **（最高）捕获分析误判改变闭包语义**：漏写站点 = 字段与原变量分裂，单测难穷尽。缓解：D2 保守并集口径与现双路径对齐；站点表按 `ISymbol`+`SyntaxReference` 完备枚举；rewrite 后「无裸名残留」断言；M1 只审计；Release 行为 fixture（外赋/内赋/共享/嵌套/foreach）逐类锁定；失败即不改（CS2J3202）。
2. **foreach 版本语义差异**：每轮实例化位置错放（循环外）会退回 C# 4 共享变量坑。缓解：D9 二分 + 7.1-11 委托外调断言；统一 `LanguageVersion.Latest` 编译。
3. **E02 fixpoint 环**（catalog §5.4、C45 行）：提升局部函数产生新 lambda、lambda 提升未来产生新方法形态。缓解：C32 单遍即收敛（嵌套共享 holder，D8）；annotation 幂等；残余局部函数显式报码不猜测；未来编排器带环检测跑到无变化，本 pass 保证可重入。
4. **注册表三文件热冲突**：C18/member-desugar/C45 未来都碰同一翻转点。缓解：§3.2 清单与排序测试先行；后合者保留两条 descriptor 与各自边（与 [c04 §11-5](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c04-iterator-lowering-design.md) 同一解决规则）。
5. **表达式树混合形态**：CS2J3206 在 sdk 的命中率未知；若大量命中需评估「只提升非引号站点 + 树内保留对原局部的编译级捕获」的可行折中（本期不做，先审计量化）。
6. **泛型/嵌套宿主类型命名**：泛型类型、同名成员、partial 分册的 holder 类名冲突。缓解：沿用 E02 跨文件命名表 + 成员名/类型键防撞；fixture 含泛型宿主与 partial。
7. **过提升分配开销**：写站点全在 lambda 创建之前时严格无需提升。保守策略会多分配；AuditOnly 量化，必要时 M3 加「末次写先于首个捕获点则不提升」的精化（必须数据支持，本期不做）。
8. **链耗时**：verify build/test 默认开 + 整树语义两遍分析。缓解：descriptor verify 可关；指纹自动接入；M3 度量 sdk 增量耗时。
9. **转换器清理脱节**：默认关期新旧两套事实并存（C# 显式 holder vs 转换器 holder）。缓解：本期明确一行不改转换器；清理是默认开启后的独立 PR，直连管线 fail-fast 与旧测试保留到那时。
10. **属性/索引器访问器与初始化器形态**（D16）：v1 报码面可能在 GUI/配置代码命中；M1 审计数据决定是否提前收编，不临时扩面。

## 11. 并行协调

1. **C01（前置，已在链默认开）**：RunAfter=`ref-out-in` 已满足；C01 产物 `ByRef<T>` 形参是普通对象，捕获其形参不再触发 CS2J3201（字段/属性访问不构成对参数的写）。C01 worktree（`.worktrees/preproc-c01-refout-finish`）若后合改变 ByRef 形态，需复跑 7.1-15；holder 命名不与 C01 Java 侧 holder 族（ByRef/RefStructByRef）耦合——本 pass 只产 C#。
2. **E02（资产复用不复制）**：M2 提取 `Analysis/CaptureClassification.cs` 是硬纪律；E02 gate/命名表/skip 原因不外借；E02 测试是提取的零行为回归门。E02 提升后的私有方法是 C32 的正常分析根（7.1-17）。
3. **E05（前置）**：匿名方法→lambda 已默认在链；C32 只消费 lambda，CS2J3203 是防线不是常态。
4. **C04 iterator-lowering（下游，已落地）**：现状锚 `make-readonly`；本 pass 翻转时**补边** `iterator-lowering.RunAfter += closure-lift`（§3.2），锁死「捕获先显式、状态机后保存」；默认关期 C04 自承 `_cs2jCap_` 字段，两形态不相遇；同开时共跑交界 fixture。
5. **C18 async-normalize（下游 stub）**：其 RunAfter 已含 closure-lift（[PassStubCatalog.cs:236](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L236)），翻转时无需改拓扑；async lambda 内 holder（D13）是其前置输入。
6. **C45（同 pass 超集）**：严格按 §3.4/§4.8 接口接阶段；同日设计引用本文件命名/形状/诊断段，不另起 id、不另建目录。
7. **C26/C25（表达式树/dynamic 共享判定）**：[ExpressionTreeFacts.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs) 已在主树（`Analysis/`，C02 提取，base `953293b9` 已存在），C32 直接引用；C25 设计的 `DynamicSiteFacts` 落地后本 pass 不重复 dynamic 判定（dynamic 站点本就可经 SemanticModel 分析，D15）。
8. **member-desugar（stub）**：RunAfter 已含 closure-lift；访问器内 lambda 的 holder 宿主在成员去糖前后都是类型成员，无文本冲突预期，仅注册表热文件协同（见风险 4）。
9. **热文件清单**（冲突只允许在这些点）：[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)（删一条）、[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)（加一条 + iterator 边）、[PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)（case + toggle 两行）、[PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs)（计数与靶子）；新增 Core/适配器/测试文件均为本 pass 独占，与所有在途 worktree 不重叠。

## 12. Worktree 分配（只写不执行；本设计文档在主工作区编写）

- 分支：`feature/preproc-c32-capture-lift`
- 目录：`.worktrees/preproc-c32-capture-lift`（`.worktrees/` 已被 .gitignore 忽略，与现存 6 个 worktree 同级）
- base：`953293b9`（已核实存在："docs: update doc base commit"；该 base 下 closure-lift 仍为 stub（23 条）、C04 已 Implemented 且锚 make-readonly、ExpressionTreeFacts 已在 `Analysis/`，与本文证据一致）
- 创建命令（PowerShell，开工时执行者手动运行；本任务未执行任何 git 写操作）：

```powershell
git worktree add .worktrees/preproc-c32-capture-lift -b feature/preproc-c32-capture-lift 953293b9
```

- 开工顺序提示：M1 第一步先做 `CaptureClassification` 提取并让 E02 测试全绿，再开 C32 规则；每独立提交跑相关测试，禁止在未验证提取零行为变化前叠加 holder 重写。

---

## 附：关键证据索引

- catalog：[preprocessor-catalog.md:640-651](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L640-L651)（C32）、[:810-821](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L810-L821)（C45）、[:898-915](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L898-L915)（§3.3 族表）、[:918-939](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L918-L939)（风险 8 等）
- 注册表/链：[PassStubCatalog.cs:152-161](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L152-L161)、[PassCatalog.cs:196-215](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L196-L215)、[PassChainFactory.cs:60-80](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L60-L80)、[PassRegistry.cs:95-133](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs#L95-L133)
- 转换器现状：[StatementTransformer.cs:68-89](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L68-L89)、[:124-143](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L124-L143)、[StatementTransformer.Utilities.cs:120-255](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Utilities.cs#L120-L255)、[StatementTransformer.Declarations.cs:66-77](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs#L66-L77)、[StatementTransformer.Loops.cs:57-66](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L57-L66)、[:142-153](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs#L142-L153)、[MethodConversionState.cs:310-555](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/MethodConversionState.cs#L310-L555)、[LambdaTransformer.cs:38-100](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L38-L100)、[:160-201](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L160-L201)、[:228-274](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L228-L274)、[IdentifierExpressionTransformer.cs:45-54](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs#L45-L54)、[ArgumentTransformer.Ref.cs:139-140](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.Ref.cs#L139-L140)
- E02 资产：[LocalFunctionLiftAnalyzer.cs:405-529](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs#L405-L529)、[:531-583](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs#L531-L583)、[:952-988](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs#L952-L988)、[LocalFunctionLiftNaming.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftNaming.cs)、[LocalFunctionLiftPassAdapter.cs:74-136](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/LocalFunctionLiftPassAdapter.cs#L74-L136)
- 共享/姊妹：[ExpressionTreeFacts.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs)、[2026-09-28-c04-iterator-lowering-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c04-iterator-lowering-design.md)、[2026-09-28-c01-ref-out-in-completion-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c01-ref-out-in-completion-design.md)、[preprocessor-foundation_plan.md:77-117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L77-L117)
