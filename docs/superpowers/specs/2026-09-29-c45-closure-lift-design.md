# C45 Lambda 闭包提升设计（closure-lift 第二阶段：全量 display class lowering）

> 通道锚点：注册表 stub `closure-lift`（catalogItems `C32, C45`，Stage=StateMachine，RunAfter=`ref-out-in`）。
> C32（第一阶段 CaptureHolder）设计已定稿（[2026-09-29-c32-capture-lift-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c32-capture-lift-design.md)，提交 `50ec565b`），其实现按 [§12](#12-worktree-分配只写不执行本设计文档在主工作区编写) worktree 分工独立推进；**本任务不注册、不翻转 pass**，只在 C32 合入后把 holder 阶段扩展为全量 Roslyn lowering：lambda/匿名方法 → display class 字段 + 命名方法 + 显式委托构造。
> 本文证据核实时间 2026-09-29，主工作区分支 `dev-3.x`，HEAD=`55d9d109`；base 提交原定 `953293b9`，**2026-10-03 更新为 `46bdeeca`（`dev-3.x-26100311` HEAD）**——C32（`41049d41`）已合入该分支，C45 直接建立在含 C32 全绿 ClosureLift 骨架的基线上（[§12](#12-worktree-分配只写不执行本设计文档在主工作区编写)）。主工作区现状（2026-10-03 复核）：closure-lift 已随 C32 翻转为 Implemented（默认关），`ClosureLift/` 目录与 `Analysis/CaptureClassification.cs` 已落树，C32 全量测试绿——本文一切「C32 已有」均指该已合入实现，C45 在其上做加性演进。

## 1. 背景与目标

### 1.1 频率与族位

lambda/匿名方法是 csharp-sdk 训练集**第一高频语法糖**：4966 处（[preprocessor-catalog.md:1259](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1259)，高于局部函数 3486、yield 2887）。catalog 把 C45 定为 Layer 4「重量级 pass，生成新类型」（[:895-908](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L895-L908)），与 E02（局部函数提升）、C04（迭代器状态机）、C18（async 状态机）同属「闭包/状态机提升族」（[:1083](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1083)），共享捕获分析/确定性命名/holder 三件基建。E05 已完成匿名方法 → lambda 的前半步（[DelegateSyntaxRewriter.cs:244-334](file:///d:/code/cs2j/src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs#L244-L334)），C32 已登记可变捕获 holder 子集并预留阶段接口（C32 设计 §3.4/§4.8）。

### 1.2 现状证据：转换器直映 lambda 的可用面与边角清单

今天 lambda 在 Java 半区直映 Java lambda（[LambdaTransformer.cs:12-18](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L12-L18) 类注释自述边界），可用面（参数/体转换、shadow rename）之外的缺口全部以 fail-fast 或隐式运行时状态代偿：

1. **async lambda 直接拒绝**：[LambdaTransformer.cs:49-53](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L49-L53)。
2. **lambda ref/out 形参 fail-fast**：[LambdaTransformer.cs:96-100](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L96-L100)（`:200` 表达式体写回同族拒绝）。
3. **匿名方法 fail-fast**：[LambdaTransformer.cs:38-39](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L38-L39)（E05 默认在链，残余属防线）。
4. **方法体级预扫描 + holder 前置发射两份同构逻辑**：[StatementTransformer.cs:68-89](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L68-L89) 与结构化 IR 路径 [:124-143](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L124-L143)；预扫描实现在 `StatementTransformer.Utilities.cs:120-255`。
5. **挂起/激活隐式状态机**：[MethodConversionState.cs:310-555](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/MethodConversionState.cs#L310-L555) 的 `LambdaCapturePreScanDone`、`PendingLambdaCaptureHolder`（[:409](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/MethodConversionState.cs#L409)）、按作用域帧组织的 active holder 表；lambda 内即时发射单元素数组与**正则文本替换** `_name[0]`（[LambdaTransformer.cs:169-174](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L169-L174)、[:228-274](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L228-L274)）。
6. **泛型擦除 hack**：`(T[]) new Object[] { x }` 强转（[StatementTransformer.cs:83-85](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L83-L85)）。
7. **未覆盖语义面**：递归 lambda、多 lambda 共享捕获的合并规则、嵌套作用域 display class 链、静态 lambda 缓存语义、discard 形参、lambda 形参 attribute、捕获后被 E16 LINQ 过程式化消费——均无 C# 层显式形态。

### 1.3 目标：与 Roslyn 同构的 display class lowering

在 C# 源码层按 Roslyn 编译器自身的 closure lowering 完成五件事（catalog [:900](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L900)）：

1. **生成 display class**：含捕获的 lambda → 宿主类型内 `private sealed class Cs2jDisplayClass_<member>_<n>`；**无捕获/静态 lambda 不建类**，lambda 体提为宿主类型上的 `private static` 命名方法。
2. **捕获提升为字段**：被捕获局部/参数（可变与只读）、`this` 全部成为 display class 字段（`_cs2jCap_<name>` / `_cs2jThis`，与 C32/C04/C18 同族前缀）；ref 捕获显式判负（§2 D6）。
3. **lambda 体提升为命名方法**：签名 = 目标委托的参数表（显式类型）+ 返回类型；有捕获为实例方法、无捕获为静态方法。
4. **委托点显式化**：lambda 节点整体替换为 `new <DelegateType>(instance.Method)` / `new <DelegateType>(HostType.StaticMethod)`（§2 D8）。
5. **子项收口**：discard `_` 形参 → 真实命名形参；lambda 形参 attribute 随参数迁移（C10 交界）；静态 lambda 无类；递归 lambda 的 display class 自引用字段；同作用域多 lambda 合并、嵌套作用域嵌套 display class 链。

产物是合法、可 `dotnet build -c Release` + `dotnet test -c Release` 验证的 C#；语义与原程序在第 1.5 节硬不变式列举的可观察面上逐点等价。

### 1.4 与 C32 的阶段定义（同 pass、同 id、同目录）

| 维度 | C32（第一阶段，已设计） | C45（本设计，第二阶段） |
|---|---|---|
| 触发面 | 仅「被捕获 + 可变」局部/参数；foreach 迭代变量保守提升 | 全部语句 lambda：无捕获静态、仅 this、只读值捕获、可变捕获、嵌套、递归、多播点 |
| 生成物 | holder 类（字段段、0 方法）+ holder 实例 + 读写点改写 | display class 合并/嵌套、加 this/只读字段、lambda 体命名方法、委托点 `new Delegate(...)` |
| lambda 节点 | 保留（只改捕获变量站点） | 消除（residual 闸门：语句 lambda=0，表达式树除外） |
| 目录/id/Stage/RunAfter | `.cs2j-closure-lift-src` / `closure-lift` / StateMachine / `ref-out-in` | **全部不变**；catalogItems `C32` → `C32, C45` 单行 |
| 注册表动作 | stub → Implemented（默认关），给 iterator-lowering 补边 | **无第二次拓扑变更**；async-normalize 翻转时复用既有 stub 边 |

### 1.5 硬不变式（评审与测试的等价性基线）

1. **语义等价**：提升后程序对同一输入产生同一可观察行为——返回值、异常类型与时序、委托调用结果、`ref/out` 写回、循环捕获（for 单一变量 vs foreach C# 5+ 每轮独立变量）逐点保持；不引入拷贝语义（只读捕获除外，其等价性由「零写站点 ⇒ 值恒等」论证，§2 D5/D14）。
2. **委托标识与相等**：每次求值含捕获 lambda 表达式都**新分配 display 实例 + 新委托**（C# 中两个这样的委托恒不等）；实例化点必须位于 lambda 表达式的执行点（不得提循环），多播调用列表（方法+接收者）与原程序一致。静态无缓存是有意偏差，仅限 `ReferenceEquals`/锁对象等非语义面（§2 D11）。
3. **捕获可见性**：display 字段对 lambda 体、对嵌套 display class（经父引用字段）、对 C04/C18 后续状态机的可见性与原词法作用域一致；字段在 display 实例上的读写即原变量的唯一存储（可变捕获双射，C32 §4.5 已论证）。
4. **非目标**：不做 yield/async 状态机体（C04/C18）；不做表达式树内 lambda；不做 ref 捕获 lowering（C01）；不引入支撑库；M5 之前转换器一行不改。

## 2. 关键设计决策表

| # | 议题 | 决策 | 依据/论证 |
|---|---|---|---|
| D1 | **分阶段落地顺序** | M1 无捕获静态方法提升（只提方法不建类）→ M2 仅 this / 只读值捕获 display class（含 C32 holder 正式演进合并）→ M3 嵌套作用域/递归 → M4 fixpoint 收敛与默认开门槛 → M5 转换器旧 holder 状态退役 | catalog [:904](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L904) 明定同一顺序；每阶段独立可验证、默认关，前阶段是后阶段严格子集 |
| D2 | **holder → display class 零返工论证（★）** | C32 holder 类即「仅有字段段」的 display class：类名 `Cs2jDisplayClass_<member>_<n>`、实例 `__cs2jClosure<n>`、字段 `_cs2jCap_<name>`、站点 `instance.Field` 全部冻结（C32 §4.3-4.6）。C45 对同一类只做**加性**演进：加字段（this/只读）、加方法（lambda 体）、加嵌套关系；既有可变站点零改动；`HolderGroupPlan` 以 `record add` 扩为 `DisplayClassPlan`；阶段经 C32 §4.8 预留的 `IClosureLiftStage` 追加，不改编排骨架 | C32 §3.4 把「display class 子集」写为硬约束；返工只能来自改名或改站点形态，二者均被 C32 冻结。实施时先以 C32 全绿测试为底，演进提交必须保持 C32 用例字节断言不失效 |
| D3 | **display class 合并规则** | 键 = （宿主成员, 声明作用域块, foreach 每轮标记），沿 C32 分组：同一方法体作用域内全部含捕获 lambda **共用一个类与一个实例**（即使捕获集不相交——与 Roslyn 同作用域单 closure 一致，且让嵌套 lambda 共享父实例的边最少）；同层并列作用域（if/else、连续块）各自成类；组/字段/方法序只依赖语法序与符号表，重跑字节稳定 | Roslyn 按方法体作用域合并；C32 D7 已定同组单实例，现测试 `SingleHolder` 锁定该语义 |
| D4 | **嵌套作用域 → 嵌套类链** | 内层作用域 lambda 捕获外层作用域变量时：内层 display class 声明为外层类的 C# 嵌套类，持父引用字段 `_cs2jParent`（类型=外层 display class）；内层方法访问外层捕获走 `_cs2jParent._cs2jCap_x`，访问内层自有捕获走本类字段；内层实例在内层作用域入口构造并以当前外层实例初始化。链式嵌套同理（`_cs2jParent._cs2jParent…`） | Roslyn 嵌套 closure 的源码同构；父引用是「对不再赋值的只读引用」，C32 D8 已预留该形态。嵌套类链在 C# 天然可访问私有成员 |
| D5 | **只读值捕获进字段** | 被 lambda 引用（按 `ISymbol` 同一性）但无写站点的局部/参数也成为 display class 字段：lambda **体内**站点改字段读；**体外**站点保留原局部/参数不改（与可变捕获不同——可变必须全站改字段以维持单存储双射）。字段在 display 实例化点以当时值初始化 | 零写站点 ⇒ 字段与原变量在任何时刻值相等，体内读字段/体外读原变量不可观察差异；比 Roslyn（原变量彻底消失）少 churn，且与 C32「只读不动」的 M1 前行为衔接。GC 生命周期论证见 D14 |
| D6 | **ref 捕获与 ref lambda 形参** | ① 被捕获符号是 `ref` 局部或 `RefKind != None` 参数（C01 未归一形态）→ **CS2J4501 Error**，不猜测（沿 C32 D6）；② lambda 自身形参加 `ref/out/in/ref readonly/scoped`（含 C# 14 省略类型简单形参修饰符，catalog [:908](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L908)）→ v1 **CS2J4501 Error**：Action/Func 无 ref 形参重载，仅自定义委托可表达，需连同 C01 `ByRef<T>` 位置模型另立子题；③ `M(ref x)`（x 为已提升普通变量）站点改 `ref __cs2jClosure0._cs2jCap_x`，字段可传 ref，别名语义保持（C32 §4.5-③） | 字段化会切断与调用方槽位别名；C01 默认在链，其产物 `ByRef<T>` 形参是普通对象不再触发①。②是 C45 新面，显式门优于转换器 `:99` 的末端 fail-fast |
| D7 | **this 捕获** | 实例成员中 lambda 引用实例成员/`this`：display class 加 `private readonly THost _cs2jThis;`（名随 C04 §4.3/C18 D12 契约），构造时赋值；提升方法内 `this.M()` → `_cs2jThis.M()`。仅捕获 this（无局部捕获）也建类（方法需接收者）；静态成员/静态 lambda 不生成该字段 | Roslyn display class 必有 this 字段；嵌套类访问宿主私有成员在 C# 合法。`_cs2jThis` 只读不参与可变性判定 |
| D8 | **委托点目标形态（★E05 后核实）** | 一律**显式委托构造**，禁止裸方法组：有捕获 `new <DelType>(__cs2jClosure<n>.Lambda_<k>)`；无捕获 `new <DelType>(Cs2jLambda_<member>_<k>)`（静态方法，宿主类型上）。`<DelType>` 由 `SemanticModel.GetTypeInfo(lambda).ConvertedType`（`INamedTypeSymbol.DelegateInvokeMethod`）符号渲染：`Func\`1`→`Func<T>`、`Action\`2`→`Action<T1,T2>`、**自定义委托类型用其源名**（E05 不改委托类型声明，Java 半区 [DelegateTransformer.cs:11-15](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/DelegateTransformer.cs#L11-L15) 把 delegate 声明转 `@FunctionalInterface`，`new MyDel(inst.M)` 是合法 C# 输入） | E05 只把 `+/-`/`+=/-=` 改 `Cs2jPrelude.CsPrelude.Combine/Remove`（[DelegateSyntaxRewriter.cs:115-140](file:///d:/code/cs2j/src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs#L115-L140)），lambda 子树原样留在实参里，C45 局部替换实参即可衔接多播。显式 `new` 规避 Action/Func 重载歧义（C18 D3(a) 同结论） |
| D9 | **命名方法确定性命名** | display class 上的实例方法 `Lambda_<k>`（k 在该类内按 lambda 的 `SpanStart` 序分配，不同类间无需互避）；无捕获静态方法落宿主类型：`Cs2jLambda_<HostMember>_<k>`，防撞复用 [LocalFunctionLiftNaming.GetBaseName](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftNaming.cs#L13-L39)/`ResolveUniqueName` 与跨文件命名表（[LocalFunctionLiftAnalyzer.cs:952-988](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs#L952-L988) 形态）。全部 ASCII、避 C#/Java 保留字、**不用 Roslyn IL 名 `<>b__0`**（`<` 非法源码标识符），与 C04/C18 的 `Cs2jIterator_*`/`Cs2jAsync_*` 成一族 | 确定性命名是 catalog §5.4 命名服务的既有缺口，E02/C04 已各有事实客户；类内短名因类作用域隔离而安全 |
| D10 | **静态 lambda（C# 9 `static`）** | 带 `static` 修饰或经分析零捕获的 lambda：不建类、不发实例，体提为宿主类型 `private static` 方法，站点 `new <DelType>(Host.Cs2jLambda_..)`；静态成员中的 lambda 同理。`static` 修饰由编译器保证无 this/局部捕获，分析仍独立核验（防御不一致树） | Roslyn 对零捕获 lambda 发宿主静态方法；消灭无意义分配 |
| D11 | **静态 lambda 缓存不实现** | 不发射 Roslyn 的 `[CompilerGenerated] cached` 单例委托字段：每次站点求值照常 `new`。文档化偏差面：仅 `ReferenceEquals`/锁身份/`GetType()` 等非委托相等语义可观察（C# 对 lambda 缓存本属编译器实现细节，语言不保证），`Delegate.Equals`/`==` 比较调用列表（同静态方法+null 目标）不受影响 | 缓存字段会引入宿主类型初始化时序与跨语言目标复杂度；等价面不变，M4 画像若显示热点分配再议 |
| D12 | **discard 形参与签名合成** | ① 一个或多个 `_` discard 形参（合法 C# 9 lambda）→ 合成唯一名 `_cs2jArg_<index>`；② 一切省略类型的 lambda 形参（含 E05 为无参匿名方法合成的无类型 `p0,p1`，[DelegateSyntaxRewriter.cs:316-322](file:///d:/code/cs2j/src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs#L316-L322)）→ 按委托 InvokeMethod 形参补**显式类型**；③ 命名方法返回类型取 InvokeMethod 返回类型（`void/Task/Task<T>/值类型` 原样）；④ lambda 形参 attribute 随参数**迁移到命名方法形参**（C# 10 起合法；基线编译拒绝的特例会先报基线诊断），与 C10 的 attribute 归一在不同层不冲突（C10 处理声明端/Caller*，本 pass 只搬运语法） | 命名方法要求完整签名；显式类型同时让 Java 半区免去 lambda 参数推断。`ConvertedType` 不可解析/非委托 → CS2J4509，禁止语法猜测 |
| D13 | **递归 lambda（★自引用字段）** | 直接自递归：lambda 体引用其赋值目标变量（如 `Func<int,int> fact = n => … fact(n-1) …`）：该委托局部成为 display class 字段（沿用 `_cs2jCap_<name>`），原声明改写为「实例化 display → 字段赋 `new <DelType>(__c.Lambda_0)`」两步，体内递归引用解析为同字段；方法首次调用前字段已完成赋值（赋值表达式先构造委托、委托体内读字段发生在更晚的调用点——C# 同名规则）。**互递归**（两个委托变量互相捕获）v1 保守：同组两字段都可生成，但需数据流确认初始化前不可达；无法证明时 CS2J4508 整成员跳过（禁止半提升） | Roslyn 以 closure 字段承载自引用；M3 用阶乘/斐波那契/互递归三 fixture 锁定。字段先于任何委托调用赋值是等价性关键论证点 |
| D14 | **实例化点与初始化顺序** | ① 含可变捕获组：沿用 C32 点位（变量声明处/方法体首参数/for 循环前/foreach 循环体首每轮）；② 纯只读/this 新建组：实例声明插在**该作用域块内、第一个捕获该组的 lambda 站点之前最近的语句边界**，确保执行到站点时实例与字段已就绪；只读字段在实例化点以当前值初始化（变量均已过声明点且经明确赋值分析）；③ 分支中声明、分支外使用的形态在 C# 基线本就受明确赋值约束，不特殊处理；④ 循环体内作用域组随每次进入块自然每轮分配，保持每轮独立 | Roslyn 在首次使用前分配；循环内不提外是 D3 委托标识不变式的实现保证。foreach 每轮组沿用 C32 D9 二分 |
| D15 | **变量生命周期延长（GC 语义）** | 只读捕获复制进字段后，display 实例存活期间持续引用原值；与 C# 闭包延长变量生命周期的行为同（原实现下编译器 closure 同样持有），无新增可观察差异（终结器调度时序非语义保证）；不新增 `IDisposable`/释放语义 | D5 体外保留原变量 ⇒ 体外生命周期不变；仅 lambda 侧延长，恰为 C# 语义 |
| D16 | **多播 += / 事件** | 变量/字段式委托 `d += () => …` 在 E05 之后已是 `d = CsPrelude.Combine(d, () => …)` 形态；C45 只替换实参子树为 `new …`，Combine 语义零改动。**事件** `+=`（`IEventSymbol`）E05 显式跳过（[DelegateSyntaxRewriter.cs:174-176](file:///d:/code/cs2j/src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs#L174-L176)）：C45 直接改写事件访问参数 lambda 为 `new <EventHandlerType>(…)`，事件 add 访问器接收新委托，语义同构 | 事件 += 右侧本就是「求值为委托」的表达式，显式 new 是其值 |
| D17 | **async lambda 边界（★结论）** | **C45 先建类、先提方法，`async` 修饰与 `await` 体原样搬运不解释**：async lambda 与同步 lambda 走同一签名/字段/委托点规则，返回类型取 `Task/Task<T>/ValueTask<…>`（InvokeMethod 原样）；async 语义归一（ConfigureAwait、async void wrapper、CS2J1802 捕获型解锁）归 C18，其 RunAfter=`closure-lift` 保证它看到的已是命名方法/display 字段。C18 v1 自带的零捕获/this 最小提升（C18 D3(a)(b)(c)）在 C45 同开时变为重复作业：residual 闸门以「无 async lambda」属 C18、「无语句 lambda」属 C45 各计各的，同树上幂等共存，接受一次快照漂移（C18 D12 已预告） | catalog [:903](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L903) 要求捕获先成字段、状态机后保存；C18 设计 §11-1 已冻结此合流顺序。本 pass 不依赖 C18 即可产出可编译 C#（async 方法本就合法） |
| D18 | **迭代器/表达式树/初始化器宿主** | ① lambda 体内 `yield` 基线 CS1621 拒，不设计；② `Expression<TDelegate>` 引号内 lambda **整体跳过**（共享 [ExpressionTreeFacts.IsExpressionTreeLambda](file:///d:/code/cs2j/src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs#L12)），子树零改写；同一只读符号被引号 lambda 与语句 lambda 混合引用时**不报混合错误**（语句侧字段持拷贝、引号侧编译期捕获原局部，零写 ⇒ 等价）；可变符号混合沿用 C32 CS2J3206；③ 字段/属性初始化器内 lambda v1 沿 C32 D16 报 CS2J4504（Roslyn 在 ctor 建 closure，宿主形态留 M 后审计） | catalog 风险 8（[:1106](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1106)）与 C26 三路分流；只读混合可放行是 D5 体外不改的直接推论 |
| D19 | **泛型宿主/泛型方法** | display class 携带类型形参：宿主类型形参按序前置、宿主方法形参追加（借 C04 §4.4 同名规则）；类上实例方法用类形参表；构造点以封闭类型实参实例化。消灭 `(T[])new Object[]{}` hack，字段 `T _cs2jCap_x` 类型安全（消灭 C32 §1.1-6 之债） | sdk 高频形态；M2 必测泛型方法捕获 |
| D20 | **与 E16 LINQ 过程式化共处** | C45 提升全部语句 lambda（含 `Where/Select` 实参）；现 E16 是转换器内 LinqRewrite（非预处理 pass），输入将从内联 lambda 变为 `new Func<…>(inst.Method)`。M2 必须双端验证：fixture 覆盖「被过程式化的 Where/All 实参 lambda 带外部捕获」；若 LinqRewrite 不能内联委托构造形态，退路两选（评审定）：(a) 选项 `LiftQueryArgumentLambdas` 暂跳 Enumerable 扩展方法实参 lambda（residual 白名单，违反零残留，仅过渡）；(b) 推进 C26 linq-channel 把过程式化前移到本 pass 之前（catalog 链位 linq-channel 在 extension-methods 后、StateMachine 前，方向正确）。**默认目标是不留白名单** | 任务点名的双端风险；转换器 [Lambda.cs:34-48](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/Lambda.cs#L34-L48) 只包装 `AnonymousFunctionExpressionSyntax`，委托构造实参是否被识别必须以 fixture 实证，不臆测 |
| D21 | **分析根/宿主覆盖面** | 与 C32 §4.1 同：方法、构造器、运算符、转换运算符、析构、属性/索引器/事件访问器的有体成员皆为分析根；命名锚取成员名（显式接口实现成员去点拼接，借 C04 §4.5）。析构体内 lambda 的可见名分析现散见 [LambdaTransformer.cs:306](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L306)，提升后该特判成为死路径（M5 随旧 holder 一同清理）。静态构造器/静态成员中只允许静态提升（无 `_cs2jThis`）。表达式体成员（`=> …`）的体即分析根，其内 lambda 与块体同规则 | Roslyn 对各类成员一视同仁生成 closure；M2 fixture 至少覆盖 ctor 与 getter 两个宿主。访问器 lambda 还与 member-desugar 有远期交集，但其 RunAfter 已含 closure-lift，顺序无冲突 |
| D22 | **lock/using/const 与提升方法体** | 沿 C32 D10：被提升的 `lock(x)` 同步为 `lock(__c._cs2jCap_x)`；using 局部/const 无写站点不提升（lambda 体只读引用时按 D5 进只读字段）；提升后的命名方法内出现 `lock/using/await/yield` 语句时**语句结构原样搬运**——await 合法（D17）、yield 若出现说明基线已报 CS1621，不二次处理。lambda 体内 `unsafe` 上下文随宿主 unsafe 编译选项（整树 `allowUnsafe:true`，C32 §4.1） | 命名方法是普通 C# 方法，基线编译器重新承担全部合法性检查；本 pass 只保证搬运不丢语句 |
| D23 | **委托返回类型推断的边角** | InvokeMethod 返回 `void` → 命名方法 void（表达式体 lambda `x => Console.Write(x)` 据此判 void，禁止从体推断）；返回 `ref T` 的自定义委托 → CS2J4501；`System.Runtime.CompilerServices` 的 task-like 自定义委托极少，按普通委托渲染返回类型（async 性由 async 关键字承载，D17）；可空引用类型标注（`string?`）按符号 `NullableAnnotation` 渲染显式类型形参，保持基线可空上下文 | 签名一律以符号为准而非语法猜测，是 D12/CS2J4509 的完整化规则 |

## 3. 链位与族契约

### 3.1 本阶段动作：无拓扑变更，单行 catalogItems

- C32 翻转已把 closure-lift 移入 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) `Implemented`（默认关，位于 make-readonly 与 iterator-lowering 之间，给 iterator-lowering 补 RunAfter 边——C32 §3.2 清单）。C45 **不翻转、不挪位、不改 RunAfter、不增删 stub**。
- 唯一注册表改动：descriptor 的 catalogItems 文本 `"C32"` → `"C32, C45"`；`Description` 补「full display-class lowering: named methods + delegate sites」。测试侧 `--list-passes` 快照随文本更新。
- C45 代码在 C32 已建的 stage 装配点追加（[§4.1](#41-阶段装配与三阶段整树数据流)）；适配器、目录 `.cs2j-closure-lift-src`、verify 策略、CS2J9001 防线全部沿用，不开新 pass、不建新目录。
- 当前主工作区实证（供 rebase 对照）：closure-lift 仍是 stub（[PassStubCatalog.cs:152-161](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L152-L161)）；iterator-lowering 已 Implemented 且现锚 `make-readonly`（[PassCatalog.cs:196-205](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L196-L205)，C04 翻转时相对其 spec 做了锚点调整，C18 §3.3 有同款记录）；async-normalize 仍为 stub、RunAfter=`closure-lift`（[PassStubCatalog.cs:229-238](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L229-L238)）。

### 3.2 ★fixpoint 环：与 E02 的多趟终止性设计

catalog §5.4 要求编排器对「lift 局部函数产生 lambda、状态机产生新 lambda」跑到无变化（[:1180](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1180)），但 foundation plan 明确当前手写链**不做** fixpoint 编排器（`preprocessor-foundation_plan.md:161` 范围排除项）。本 pass 的设计同时满足「单趟穷尽」与「未来可入环」：

1. **单趟穷尽性论证（终止性）**：本 pass 四个 stage 中只有删除/替换 lambda 节点的作业，**没有任何 stage 产生 lambda 节点**（产物是字段、命名方法、嵌套类、对象创建表达式）；成员体内任意嵌套深度的 lambda 由整树分析一次性枚举（`DescendantNodes` 穿透 lambda 边界），内层 lambda 落同类/内层类的方法。故每趟 lambda 节点数严格下降，一趟即穷尽。
2. **E02 侧**：local-function-lift 在前序链位先跑（默认开），其产物（类级私有方法）是本 pass 的普通分析根；E02 提升方法体内若有 lambda，在本 pass 同一趟内被处理，不需要 E02 再跑一趟。E02 的捕获传播 fixpoint（[LocalFunctionLiftAnalyzer.cs:531-583](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs#L531-L583)）是其**单 pass 内**不动点，与本 pass 正交。
3. **本 pass 内部 worklist 防御**：Rewrite 后对「合成方法体」再扫描，仅允许 0 新 lambda；实现为带上限（8，常量 `MaxInternalLiftPasses`）的内部循环，超限 CS2J4507（正常不可达——产物语法不含 lambda，纯防御树损坏/annotation 丢失）。
4. **跨 pass 环（登记给未来编排器，本期不实现）**：C04/C18 合成面理论上可产生新 lambda（C04 §11-3、C18 D12 都已承诺幂等）；未来统一编排器按 E02 → closure-lift → C04/C18 跑到「无变化」，带环检测与最大轮次。本 pass 保证：① 幂等（合成节点带 `cs2j_closure_lift` annotation，重跑 Changed=false）；② residual 计数可作为环的收敛信号。
5. 残余局部函数（E02 skip 漏网）按 C32 D14 报 CS2J3204/CS2J4504，不靠再跑 E02 兜底。

### 3.3 C04/C18 默认开启对 C45 的阶段门（结论）

- **C45 不是 C04/C18 默认开的硬前置**。C04 自承 `CaptureLayout`/`_cs2jCap_` 字段（[c04 §4.3](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c04-iterator-lowering-design.md)），C32 holder 已把最高风险的可变捕获变成显式字段，状态机「保存什么」的核心输入在 C32 后即已显式；C18 轻量版 v1 只做零捕获/this 型（C18 D3(a)(b)(c)），本就不依赖 display class。
- **C45 是两个特定残余桶的解锁条件**：C04 的 CS2J0405（迭代器体内 lambda 捕获提升变量——C45 后体内无 lambda，仅余 display 实例这个普通局部）与 C18 的 CS2J1802（捕获局部/形参的 async lambda——D17 后 C18 直接消费 display 字段）。
- 建议：C04/C18 可按各自 §8 门槛默认开启并保留两个 deferral 诊断；其默认化评审材料必须分别注明「CS2J0405/CS2J1802 关闭以 C45 默认为前置」。closure-lift 翻转（含 C45）时按 C18 §3.3-4 给 iterator-lowering 与 async-normalize **同时确认** RunAfter=closure-lift 边（iterator 的边 C32 已补；async 的边在其翻转时自然生效），三方同步排序快照。

## 4. Core 组件设计

目录 `src/CSharpToJava.Core/ClosureLift/`（C32 已建骨架；C45 在同目录加子目录与阶段，internal 实现 + 一个公开入口）：

```text
# C32 已有（本 pass 演进，不搬家）
ClosureLiftOptions.cs / ClosureLiftStatistics.cs / ClosureLiftDiagnostic.cs / ClosureLiftResult.cs
ClosureLiftStage.cs            # IClosureLiftStage（C32 §4.8 预留）
HolderPlan.cs                  # → 扩展为 DisplayClassPlan（record add，D2）
ClosureLiftNaming.cs
HolderSyntaxGenerator.cs       # → DisplayClassSyntaxGenerator 复用/薄封装
CaptureSiteRewriter.cs
HolderLiftStage.cs
ClosureLiftPreprocessor.cs     # 公开入口 + 跨文件命名表（仿 LocalFunctionLiftPreprocessor）

# C45 新增
Capture/LambdaSiteIndex.cs          # 整树 lambda 站点枚举（排除引号 lambda/注解节点）
DisplayClass/DisplayClassPlanner.cs # 合并(D3)/嵌套链(D4)/this/只读字段并入/泛型形参(D19)
DisplayClass/DisplayClassMergeValidator.cs # 分组冲突/初始化序/每轮标记校验
Lift/LambdaSignatureResolver.cs     # ConvertedType→DelegateInvokeMethod→显式形参/返回类型(D12)
Lift/LambdaMethodLifter.cs          # 体迁移：this 改写、字段站点、控制流原样搬运
Lift/StaticLambdaHandler.cs         # D10 宿主静态方法路径
Lift/RecursiveLambdaHandler.cs      # D13 自引用字段/互递归分析
Lift/ParameterNormalizer.cs         # discard 改名+显式类型+attribute 迁移(D12)
Rewrite/DelegateSiteRewriter.cs     # D8 站点 new 形态；多播/事件子树衔接(D16)
Stages/DisplayClassConsolidationStage.cs
Stages/NamedMethodLiftStage.cs
Stages/DelegateSiteRewriteStage.cs
```

`src/CSharpToJava.Core/Analysis/CaptureClassification.cs`（C32 M2 从 E02 提取）在本阶段只**加查询不加第二实现**：`EnumerateLambdaSites(model, root)`、`GetLambdaConvertedDelegate(model, lambda)`、`IsThisCaptured(bodyRoot, model)`；写站点/捕获枚举仍只有 E02 提取的 `CollectCapturedSymbols`/`FindWriteSites` 一份。

### 4.1 阶段装配与三阶段整树数据流

C32 的 `ClosureLiftPreprocessor` 注册一个阶段；C45 把阶段表改为：

```csharp
// 顺序即 lowering 顺序；每阶段输入/输出都是不可变 plan + 当前语法树
_stages = new IClosureLiftStage[]
{
    new HolderLiftStage(),                    // C32：可变 holder（M2 时其产物被下一阶段就地收编）
    new DisplayClassConsolidationStage(),    // D3/D4/D5/D7：合并、嵌套链、this/只读字段
    new LambdaMethodLiftStage(),             // D9/D10/D12/D13：签名合成 + 体迁移 + 静态/递归
    new DelegateSiteRewriteStage(),          // D8/D16：站点显式委托构造
};
```

三阶段整树数据流（沿 C32 §4.1「分析与重写分两遍」纪律，扩为 plan 级多文件原子性）：

```text
Compilation（整树，LanguageVersion.Latest, allowUnsafe:true）
  │
  ├─ Analyze（整 CompilationUnit，不改树）
  │    LambdaSiteIndex 枚举全部成员体根（方法/ctor/运算符/转换/析构/访问器；
  │    E02 提升方法同权）→ 每 lambda：ConvertedType/InvokeMethod、捕获符号集
  │    （含 this/可变写站点）、引号标记、async 标记、递归自引用、宿主与作用域块
  │    ├─ skip/报错集在本遍冻结（CS2J45xx；AuditOnly 到此为止）
  │    └─ 跨文件命名表在本遍共享（类型 FQN 键，E02 形态）
  │
  ├─ Plan（确定性，仅依赖语法序+符号表）
  │    HolderLift 计划（C32）→ DisplayClassPlanner 合并/嵌套收编
  │    → 每成员产出：类集（字段序/方法序/父引用）、静态方法集、
  │      站点表（SyntaxReference 按 ISymbol 归一）、实例化点表
  │
  └─ Rewrite（四 stage 顺序；整文件一次落盘）
       发嵌套类（[CompilerGenerated] private sealed，挂本文件宿主声明）
       → 发实例/字段初始化（D14）→ 体迁移为命名方法 → 站点 new 形态
       → Formatter 规范化 → 重解析诊断快照比对（不新增基线外错误）
```

任何文件/成员在 Rewrite 前命中 Error 诊断：该文件整体原样保留（C32 plan 级回滚纪律延续，C04/E11 同款），verify build 不被坏树污染。

### 4.1.1 演进后数据契约（C32 `HolderGroupPlan` 的加性超集）

```csharp
// C32 已冻结的字段全部保留（语义不变），C45 只新增字段与记录
internal sealed class DisplayClassPlan
{
    // —— C32 既有（HolderGroupPlan 改名，字段一一对应）——
    public required TypeDeclarationSyntax HostType;
    public required MemberDeclarationSyntax HostMember;
    public required BlockSyntax ScopeRoot;
    public required string ClassName;                 // Cs2jDisplayClass_<member>_<n>
    public required string InstanceName;              // __cs2jClosure<n>
    public List<CapturedSymbolPlan> Symbols = new();
    public bool PerIteration;
    public ForEachStatementSyntax? PerIterationLoop;

    // —— C45 新增 ——
    public bool CapturesThis;                         // → _cs2jThis 字段（D7）
    public List<TypeParameterSyntax> ClassTypeParameters = new(); // 宿主类型+方法形参（D19）
    public DisplayClassPlan? Parent;                 // 嵌套作用域父类（D4，null=顶层）
    public string? ParentFieldName;                  // 内层类上的 _cs2jParent
    public List<LambdaMethodPlan> Methods = new();    // Lambda_<k> 实例方法
    public List<StaticLambdaPlan> HostStaticMethods = new(); // 零捕获：宿主静态方法（D10）
    public List<DelegateSitePlan> Sites = new();      // lambda 节点 → 委托构造（D8）
}

internal sealed record LambdaMethodPlan(
    SyntaxReference LambdaNode,          // 原 lambda（站点表锚点）
    string MethodName,                   // Lambda_<k>
    bool IsAsync,                        // D17：async 修饰原样保留
    IMethodSymbol DelegateInvoke,        // 签名来源（D12/D23）
    List<LiftedParameterPlan> Parameters,
    List<SyntaxReference> CaptureReads,  // 体内只读捕获站点
    List<SyntaxReference> ThisSites,     // 隐式/显式 this 站点
    SyntaxReference? RecursiveSelf);     // D13：自引用字段（null=非递归）

internal sealed record DelegateSitePlan(
    SyntaxReference LambdaNode,
    string DelegateTypeDisplay,          // 符号渲染：Action<T>/Func<...>/自定义委托名
    string ReceiverExpression,           // __cs2jClosure<n> 或宿主类型名
    string MethodName, bool IsStatic);
```

约束：① C32 消费者（HolderSyntaxGenerator/CaptureSiteRewriter）只读前段字段，后段为 default 时产物与 C32 逐字节相同（M2 回归红线）；② 所有 plan 在任何 Rewrite 前冻结，skip 集不可在重写中扩张；③ plan 对象只持 `SyntaxReference` 不持重写后节点，保证跨阶段树替换后引用可重新解析。

### 4.2 生成形态示意（M2 综合形态）

```csharp
// 输入
int Aggregate(int seed, int extra, bool flag)
{
    int total = seed;                       // 只读捕获
    var inc = new Action<int>(x => total += x);   // 可变捕获（C32 字段）
    Func<int, int> add = x => total + x + extra + (flag ? Seed() : 0); // 只读+this
    inc(10);
    return add(2);
}

// 输出（同名类上 C32 字段段被收编，方法为 C45 新增）
[CompilerGenerated]
private sealed class Cs2jDisplayClass_Aggregate_0
{
    public int _cs2jCap_total;              // C32 已建：唯一可变存储
    public int _cs2jCap_extra;              // C45 新增：只读值捕获
    public bool _cs2jCap_flag;
    private readonly Worker _cs2jThis;      // C45 新增：this（D7）

    public Cs2jDisplayClass_Aggregate_0(Worker @this) { _cs2jThis = @this; }

    internal void Lambda_0(int x) { _cs2jCap_total += x; }
    internal int Lambda_1(int x)
        => _cs2jCap_total + x + _cs2jCap_extra + (_cs2jCap_flag ? _cs2jThis.Seed() : 0);
}

int Aggregate(int seed, int extra, bool flag)
{
    var __cs2jClosure0 = new Cs2jDisplayClass_Aggregate_0(this)
    {
        _cs2jCap_total = seed,              // 可变：声明点即字段（C32）
        _cs2jCap_extra = extra,             // 只读：初始化快照（D5）
        _cs2jCap_flag = flag,
    };
    var inc = new Action<int>(__cs2jClosure0.Lambda_0);
    var add = new Func<int, int>(__cs2jClosure0.Lambda_1);
    inc(10);
    return add(2);
}
```

无捕获静态（M1）与递归（M3）：

```csharp
// M1：零类、零字段、零分配语义变化（显式 new，不做缓存 D11）
Func<int, int> sq = x => x * x;
// → Func<int,int> sq = new Func<int,int>(Cs2jLambda_Aggregate_0);
private static int Cs2jLambda_Aggregate_0(int x) => x * x;

// M3：自引用字段承载递归
Func<int, int> fact = null;
fact = n => n <= 1 ? 1 : n * fact(n - 1);
// → __cs2jClosure1._cs2jCap_fact = new Func<int,int>(__cs2jClosure1.Lambda_0);
//   Lambda_0: n => n <= 1 ? 1 : n * _cs2jCap_fact(n - 1)
```

嵌套作用域（M3，D4：内层类持父引用，内层实例每轮/每进入块构造）：

```csharp
// 输入
void Subscribe(int outer)
{
    int kept = outer + 1;                       // 外层作用域被内外两层捕获
    foreach (var item in Items)
    {
        int local = item.Id;                    // 仅内层捕获
        item.Changed += () => Use(kept, local);
    }
}

// 输出（嵌套类 + 父引用；foreach 每轮内层实例，外层单实例）
[CompilerGenerated]
private sealed class Cs2jDisplayClass_Subscribe_0
{
    public int _cs2jCap_kept;
    internal void Lambda_0(int _cs2jArg_0_unused) { } // 外层若也有方法同理

    [CompilerGenerated]
    private sealed class Cs2jDisplayClass_Subscribe_1
    {
        private readonly Cs2jDisplayClass_Subscribe_0 _cs2jParent;
        public int _cs2jCap_local;
        internal Cs2jDisplayClass_Subscribe_1(Cs2jDisplayClass_Subscribe_0 parent)
            => _cs2jParent = parent;
        internal void Lambda_0(object sender, Args e)
            => Use(_cs2jParent._cs2jCap_kept, _cs2jCap_local);
    }
}

void Subscribe(int outer)
{
    var __cs2jClosure0 = new Cs2jDisplayClass_Subscribe_0 { _cs2jCap_kept = outer + 1 };
    foreach (var item in Items)
    {
        var __cs2jClosure1 =
            new Cs2jDisplayClass_Subscribe_0.Cs2jDisplayClass_Subscribe_1(__cs2jClosure0)
            { _cs2jCap_local = item.Id };
        item.Changed += new EventHandler<Args>(__cs2jClosure1.Lambda_0);
    }
}
```

要点：内层类作为 C# 嵌套类可见父类私有成员，但字段访问仍统一走 `_cs2jParent`（不依赖隐式外层引用，Java 半区嵌套类语义不同，显式字段两端都可表达）；事件订阅点类型取事件的 `EventHandler<T>`（D16/D23）。

### 4.3 站点改写口径（CaptureSiteRewriter/DelegateSiteRewriter）

- 可变捕获全部站点（读/写/`++--`/`ref-out` 实参/`lock`/for 条件与步进）沿用 C32 §4.5 六类改写，**一字节不动**。
- 新增：① lambda 体内对只读捕获的标识符读 → 字段读；② `this.`/隐式 this（实例成员访问、base 访问）→ `_cs2jThis.`（base 访问需经 `_cs2jThis` 上的可调用形态，M2 fixture 覆盖；不可达虚调用结构报 CS2J4509）；③ 递归自引用标识符 → 字段；④ 整个 lambda 节点 → 委托构造表达式；⑤ discard/无类型形参在体迁移时同步换名（D12）。
- **不进入**子树：引号 lambda（D18）、带 `cs2j_closure_lift` annotation 的合成节点、`nameof(x)`（借 E02 `IsInsideNameOf`）、特性参数与 XML doc 注释。
- 委托点识别只认原树 lambda 节点；E05 产物（Combine/Remove 调用、事件参数）中的 lambda 由同一 rewriter 自底向上访问自然命中，不需要为多播写专门分支。

### 4.4 文件所有权表（与 C32 并列、与族内其他 pass 不冲突）

| 文件/目录 | C32 | C45 增量 | 族内第三方 |
|---|---|---|---|
| `ClosureLift/**` | 全部骨架与 Holder 阶段 | 同目录新增 `DisplayClass/Lift/Rewrite/Stages/Capture` 子目录文件；`HolderPlan*`/`ClosureLift*` 以加字段/加阶段演进 | E02 不动 |
| `Analysis/CaptureClassification.cs` | M2 提取并拥有 | 加查询，保持单一实现 | E02 消费方 |
| `Adapters/ClosureLiftPassAdapter.cs` | 新建 | 同文件扩统计映射/摘要/residual 口径（§5） | — |
| 注册表三文件 + stub 测试 | 翻转一次 | catalogItems/Description 单行文本；无第二次计数/拓扑变更 | async-normalize 翻转时碰同表，后合者保留两条边 |
| `StatementTransformer*` / `LambdaTransformer` / `MethodConversionState` | 不动 | M5 前不动；M5 退役（§9） | C04/C18 不碰转换器 |
| 测试 | ClosureLift 全簇 + 排序/CLI | 同目录加 `ClosureLiftDisplayClass*Tests` 与 fixture 扩展 | — |

## 5. CLI 适配器与集成

- 适配器 [ClosureLiftPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters)（C32 已建，仿 [LocalFunctionLiftPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/LocalFunctionLiftPassAdapter.cs)）本阶段只扩四处：
  1. **统计映射**：新增 `DisplayClassesGenerated`（含 C32 `HolderClassesGenerated` 的口径改名，老计数保留为同值别名供 C32 测试）、`LambdaMethodsLifted`、`StaticLambdaMethods`、`ThisOnlyLambdas`、`RecursiveLambdas`、`NestedDisplayClasses`、`DiscardParametersNormalized`、`DelegateSitesRewritten`、`ExpressionTreeLambdasSkipped`、`ResidualStatementLambdas`。
  2. **摘要行**：`closure-lift: N transformed, M clean, K display classes (S nested), L lambda methods (A static, R recursive), D delegate sites, E quoted skipped, G residual`。
  3. **verify**：descriptor `VerifyBuild/VerifyTests=true` 沿用；生成类与原文件同编译单元，verify build 直接覆盖「生成类可编译」；plan 级回滚（多文件任一失败全树不写）延续 C32。
  4. **residual 闸门**（`AfterFilesAsync`，重解析中间树）：非引号、非注解子树内 `SimpleLambdaExpressionSyntax`/`ParenthesizedLambdaExpressionSyntax`/`AnonymousMethodExpressionSyntax` 计数 = 0；残余匿名方法同时计 CS2J4503。闸门只认语法+`ExpressionTreeFacts`，不依赖转换产物。
- **生成类放置**：**private 嵌套类放原类型内部、原文件内**（推荐且唯一默认）。理由：① 与 Roslyn 同构、与 C04 `Cs2jIterator_*` 放置一致；② 天然访问私有成员与泛型形参；③ 可见性最简（private + 同编译单元），不碰跨文件生成基建（E06 tuple 是唯一跨文件例外，本 pass 不复制该复杂度）；④ partial 类型由跨文件命名表防撞（E02 形态），每个分册各挂各的嵌套声明不成立时（同类型多分册命名冲突）由命名表保证类名全局唯一、仅一个分册承载类体——承载分册选 span 最小分册，M2 fixture 覆盖。
- **无支撑库**：产物只用 BCL 委托类型与用户源码既有类型；descriptor `supportLibrary=None` 不变；不过滤工程、不改 csproj、不注入资源。
- 通用 verb/指纹/保留目录全部随 C32 descriptor 自动生效，C45 无新增 CLI 面。

## 6. 诊断策略

1. 基线优先：整树编译诊断快照先行；Roslyn 已拒形态（指针捕获、锁内 await、表达式树内 async lambda、CS1621 等）不抢诊断。
2. 本 pass 只对「基线没拦住、但不能安全/完整 lowering」的形态发码；strict（默认）下任一 Error 令本次改写失败、成员/文件原样保留且树仍可编译。**禁止半提升、禁止注释/占位/透传**（catalog 第一原则）：签名合成失败就整 lambda 不搬，不留「提了一半的方法」。
3. 码段：C32 的 CS2J32xx 继续有效（holder 阶段条件不变）；C45 新阶段使用 CS2J45xx：

| 码 | Severity | 触发 |
|---|---|---|
| CS2J4501 | Error（strict）/Warning | ref 捕获未归一位，或 lambda 形参带 ref/out/in/ref readonly/scoped（D6，C01 子题） |
| CS2J4502 | Error | 符号不可解析/ErrorType，无法保证捕获与站点全集（沿 C32 CS2J3202 同族） |
| CS2J4503 | Error | 残余匿名方法节点（E05 产物缺失防线；residual 闸门同步计数） |
| CS2J4504 | Error/Warning | 不支持的 lambda 宿主（字段/属性初始化器 v1）、残余局部函数跨边界捕获 |
| CS2J4505 | Error（防御，不可达） | 指针等不合法捕获类型（镜像基线 CS 错误） |
| CS2J4507 | Error（防御，不可达） | 内部 lifting worklist 超 `MaxInternalLiftPasses`（§3.2-3） |
| CS2J4508 | Error/Warning | 互递归委托初始化前可达性无法证明（D13）；递归形态超 v1 分析能力 |
| CS2J4509 | Error | ConvertedType 非委托/不可解析，无法合成命名方法签名（含 base 访问不可达结构） |
| CS2J4510 | Error（strict） | residual 闸门：重解析后仍有语句 lambda/匿名方法（表达式树除外） |
| CS2J3206 | Error | 可变捕获符号被表达式树 lambda 混合引用（C32 D11 沿用；只读混合放行） |

4. 每条诊断带文件/行/列/宿主成员完全限定名/lambda 签名摘要/处置建议（如 CS2J4501 建议「先启用 ref-out-in 或手工提取为 ByRef 形参方法」）；AuditOnly 报告附「将生成的类名/方法名/字段名」。
5. async 体不判负、不警告（D17）；迭代器体内 lambda 按基线 CS1621 处理，不发本 pass 码。

## 7. 测试计划

### 7.1 Core 单测（`tests/CSharpToJava.ProjectTests/ClosureLiftDisplayClassUnitTests.cs`，仿 [IteratorLoweringUnitTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/IteratorLoweringUnitTests.cs)：内联源码 → `CSharpCompilation` → Preprocess → 文本断言 + 输出树可编译断言）

1. **M1 无捕获静态**：表达式体/块体、零参数/多参数，输出为宿主 `private static` 方法 + `new Func/Action(...)`；不生成任何嵌套类与实例；文本跨两次运行字节一致。
2. **M1 静态 lambda（`static` 关键字）**：与零捕获分析路径同产物；`static` lambda 误带捕获的畸形树防御（基线报错优先）。
3. **M2 仅 this**：display class 仅 `_cs2jThis`；实例方法调用经字段；静态成员中的 this 不生成字段。
4. **M2 只读值捕获**：字段在实例化点初始化；体内站点字段化、体外站点保留原局部（D5）；无写站点不变量断言。
5. **M2 可变回归（C32 全量）**：C32 全部用例在阶段扩展后字节断言不失效（D2 零返工验收）。
6. **多 lambda 共享同类同实例**：同作用域两个 lambda 捕获集不相交也共用一类一实例（D3），方法名 `Lambda_0/Lambda_1` 顺序确定。
7. **嵌套作用域嵌套 display**：内层类持 `_cs2jParent`；内层方法经父引用读写外层字段；三层链；并列作用域各自成类。
8. **嵌套 lambda**（lambda 内 lambda）：内外 lambda 一次分析全部提升，无第二趟残留。
9. **M3 递归**：阶乘（自引用字段赋值时序）、递归 Action（`Action f = () => f();`）、互递归可证形态；不可证形态 CS2J4508 且整成员原样。
10. **discard 形参**：单 `_`、双 `_`、`_` 与具名混用；合成 `_cs2jArg_<i>` 且体内引用正确；无类型形参全部补显式类型（含 E05 合成的 `p0,p1` 输入）。
11. **自定义委托 vs Action/Func**：`new MyDel(__c.Lambda_0)` 用源类型名；泛型自定义委托渲染封闭实参；Action/Func 0-4 元数渲染表。
12. **多播与事件**：`CsPrelude.Combine/Remove` 实参内 lambda 被替换、组合语义文本保持；事件 `+= new HandlerType(...)`；`-=` 同形。
13. **for/foreach 捕获（.NET 行为对照）**：for 归纳变量单实例（循环外建）、foreach 每轮首句新建；委托循环外调用断言终值/每轮值（沿用 C32 7.1-10/11 并加命名方法维度）。
14. **参数捕获（可变+只读）**：体首实例化；签名不变；只读参数体外站点保留。
15. **async lambda**：async 修饰保留、返回类型 Task/Task<T> 原样、await 体零语句结构改写、输出可编译；不产生任何 C18 码（边界 D17）。
16. **表达式树零改动**：纯引号 lambda 子树字节不变、不建类；只读混合引用不报 3206 且两侧可编译；可变混合 CS2J3206。
17. **fixpoint/幂等**：连续 Preprocess 两次第二次 Changed=false；合成方法体再扫描 0 新 lambda；`MaxInternalLiftPasses` 注入故障树 → CS2J4507。
18. **委托标识相等（★）**：循环内每轮求值 lambda → 断言各轮委托不等且各持独立计数；循环外两站点各求值一次 → 引用不等但（静态方法场景）`Equals`/`==` 比较调用列表相等；`+=` 后多播调用顺序正确。
19. **泛型宿主/泛型方法**：类形参前置+方法形参追加；构造点封闭实参；消灭 `Object[]` 强转（输出无 `(T[])` 文本）。
20. **E16 共处（双端）**：`Where/All/Select` 实参 lambda 带外部可变/只读捕获的端到端用例，记录 LinqRewrite 对 `new Func` 实参的实际行为（D20 退路决策依据；本单测至少锁定 .NET 半区行为与中间树形态）。
21. **ref 门**：ref 局部被捕获/by-ref 参数 → CS2J4501；lambda ref 形参 → CS2J4501；`M(ref field)` 站点可编译。
22. **nameof/特性/XML 注释/形参 attribute 迁移**：标识符不误伤；lambda 形参 attribute 落到命名方法形参且可编译。
23. **残余/码族**：残余匿名方法 CS2J4503、初始化器宿主 CS2J4504、坏符号 CS2J4502、ConvertedType 缺失 CS2J4509。
24. **AuditOnly**：Changed=false，报告含类名/方法名/字段名/实例化点/静态-实例分桶。
25. **多声明器/lock/using/const**：沿 C32 用例 19/21/25 回归，追加 lock 对象经 `_cs2jCap_` 的命名方法内形态。

### 7.2 项目级 fixture（`tests/SampleClosureLiftSolution/`，C32 已规划，C45 扩 Lib/Tests）

- Lib 增补：静态 lambda 工厂、this-only 计数器、只读闭包（配置快照）、嵌套作用域事件订阅、阶乘递归、互递归状态机、discard 订阅、自定义委托总线、循环内委托标识实验台、泛型缓存、async lambda 透传（不 await 断言语义，只断可编译/任务结果）、表达式树对照。
- Tests：xUnit `RuntimeBehaviorTests` 只断**可观察行为**（计数、列表内容、调用时序、每轮值、委托不等），不断言生成文本；`dotnet build -c Release` + `dotnet test -c Release` 全绿（.NET 半区预言机，catalog §5.1）。

### 7.3 residual 闸门与回归

- Core 直接断言 `ResidualStatementLambdas==0`（引号/注解白名单外）；适配器 `AfterFilesAsync` 在 sdk 规模中间树复跑同一闸门。
- 转换器回归：[LambdaExternalReassignmentTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/LambdaExternalReassignmentTests.cs)、[LinqRewriterVariableCaptureTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/LinqRewriterVariableCaptureTests.cs) 直连管线不经预处理链，M5 前必须原样全绿。
- E02 交互：E02 提升的私有方法内 lambda 正常提升；E02 捕获形参（`_cs2j_cap_*`）在本 pass 中按普通参数处理，不与其前缀冲突。

### 7.4 CLI/链测试

- 默认计划行为与 C32 翻转后一致（C45 无拓扑变更）；`--enable-pass closure-lift` 产物中 residual=0；`--list-passes` catalogItems 文本快照 `C32, C45`；独立 `preprocess --pass closure-lift` 跑 fixture build/test 绿；与 iterator-lowering/async-normalize 同开的排序断言（C32/C18 排序测试加双边）。

### 7.5 .NET 行为对照与 Roslyn 同构抽查

- 全部行为 fixture 以「同一份 C# 源 → 原程序 vs 提升后程序」双跑对照（不依赖手算）：闭包共享写可见性、foreach C# 5+ 每轮值、for 单一变量终值、嵌套链读写透传、递归阶乘、多播调用顺序与返回值（void 多播取最后一个）、事件订阅/退订。
- 委托相等矩阵（D8/D11）：① 循环内两次求值含捕获 lambda → `ReferenceEquals` false 且 `==` false（调用列表接收者不同）；② 两处零捕获静态 lambda → `==`/`Equals` true（同方法、null 目标），`ReferenceEquals` 不做断言（缓存非本 pass 语义）；③ 同一委托实例多播 `a+a` 与 `(a+a)` 比较（与 catalog C47 委托相等规则一致，仅抽查不越界实现）。
- Roslyn 同构抽查（审计性质，非等价性必需）：选 8-10 个代表片段用 SharpLab/ILSpy 查看编译器 closure 形状，人工核对类数/嵌套关系/字段集一致；允许的有意偏差仅三项——显式源码名（非 `<>c__DisplayClass`）、静态不缓存（D11）、只读捕获体外站点保留（D5），偏差清单写入 fixture README。
- Release 配置跑测（`dotnet test -c Release`），避免 Debug 下调试器/求值器对闭包时序的偶然掩盖。

## 8. 默认开启验收门槛

重量级 pass 沿用「每阶段默认关 → 阶段门槛 → 终审默认开」节奏（foundation plan `:116`、C32 §8、C04 §8）：

1. **阶段门槛（M1-M3 各自）**：当阶段 7.1/7.2 用例全绿；C32 全部既有用例零改动绿；fixture Release build/test 绿；不允许 CS2J45xx Error 无主残留。
2. **终审默认开（M4）同时满足**：
   - 三个测试工程 `dotnet test -c Release` 全绿；转换器直连既有测试零改动全绿；
   - csharp-sdk（D:\csharp-sdk，4966 站点）AuditOnly 画像成文：lambda 站点分桶（零捕获/仅 this/只读/可变/嵌套/递归/async/引号/初始化器/未解析委托型）、生成 display class 数与平均方法数、CS2J45xx 命中数、委托点形态分布；
   - 开启后 sdk 基线 build/test 不退化、无新增 Error；residual 语句 lambda 在 sdk 中间树 = 0（引号 lambda 单列计数）；
   - 编译时长/生成代码膨胀量化（新增嵌套类型数、IL 体积、build 增量分钟数），门槛评审定阈值；
   - 与 E02/E05/C01/C32 默认链任意组合幂等；与 C04（lambda 内 foreach-yield 合成面）、C18（捕获型 async lambda，CS2J1802 桶归零）联合 fixture 绿；
   - **D20 E16 双端结论成文**：LinqRewrite 对显式委托构造实参的行为已验证，退路选项不进默认路径；
   - 默认关至少稳定一个里程碑窗口再翻 true（catalog 风险 10，[:1108](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1108)）。
3. **最终门槛（M5）**：转换器旧 holder 路径（§1.1-4/5/6 + MethodConversionState 挂起状态）退役且全量测试绿，作为「闭包单一事实在 C# 层」的验收闭环；未完成 M5 不影响 C45 默认开（两套事实以 C32 §10-9 同款纪律共存）。

## 9. 里程碑

| 里程碑 | 内容 | 出口 |
|---|---|---|
| **M1 无捕获静态提升** | LambdaSiteIndex（零捕获判定）、LambdaSignatureResolver、StaticLambdaHandler、命名方法/委托点生成；CS2J4509/4503；阶段表追加 `NamedMethodLiftStage`（仅静态路径）+ `DelegateSiteRewriteStage` | 7.1-1/2/11（静态部分）/17 绿；fixture 静态段 Release build/test 绿；无类生成断言 |
| **M2 this/值捕获 display class（含 C32 holder 演进）** | DisplayClassPlanner（合并/this/只读/泛型）、HolderPlan→DisplayClassPlan 收编、实例化点（D14）、体迁移字段站点、多播/事件衔接、E16 双端探针 | D2 零返工：C32 用例字节不失效；7.1-3-7/11-15/19-22/25 绿；sdk 分桶审计初稿 |
| **M3 嵌套/递归/discard/attribute** | 嵌套类链（D4）、RecursiveLambdaHandler（D13）、ParameterNormalizer（D12）、初始化器宿主 CS2J4504 防线 | 7.1-7-10/16/23 绿；嵌套/互递归 fixture 行为锁定 |
| **M4 fixpoint 幂等与默认化评审** | 内部 worklist 上限/CS2J4507、幂等加固、residual 闸门全量、sdk 画像与膨胀度量、C04/C18 联合验证、E16 退路定案 | §8-2 六条结论成文；翻默认开或继续 opt-in 的评审记录 |
| **M5 转换器 holder 状态退役** | 删 MethodConversionState 的 Pending/Active holder 与作用域帧（[:310-555](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/MethodConversionState.cs#L310-L555)）、StatementTransformer 两份预扫描（:68-89/:124-143）、LambdaTransformer 数组发射与正则替换（:66-78/:169-174/:228-274）、泛型 Object[] hack；LambdaTransformer 仅保留 shadow rename 与「理论不可达」防线 | 转换器直连测试改写为经预处理链形态后全绿；单一捕获事实；增量性能不退化 |

## 10. 风险

1. **（最高）display 实例化时机改变语义**：提出循环/分支会同时破坏委托标识（每轮新实例）与值快照时机。缓解：D14 点位规则 + C32 for/foreach 二分回归 + 7.1-13/18 行为断言；实例化语句必须在块内执行点，plan 校验器拒绝跨块提升。
2. **只读捕获体外不改的等价边界**：D5 依赖「零写 ⇒ 值恒等」；若写站点枚举漏判（把可变当只读），字段与原变量分裂。缓解：写站点仍以 C32 保守并集口径为准（C32 D2），可变/只读分类复用同一 `FindWriteSites`，不允许第二口径；7.1-4/5 用反射改字段类对抗用例锁定。
3. **fixpoint 误判不终止**：未来跨 pass 环若某 stage 意外产生 lambda（C04/C18 合成 bug），内部上限报 CS2J4507 而非死循环；单趟穷尽性有「产物语法不含 lambda」的结构证明（§3.2-1），code review 对此条做专项检查。
4. **生成代码膨胀与 sdk 编译时长**：4966 站点最坏产生数千嵌套类。缓解：D3 同作用域合并（不相交捕获也共享）、静态零类路径消化大头；M4 给类型数/IL/分钟三项数据再定默认开；必要时评估「同成员同类」更激进取舍（评审项，不预做）。
5. **Java 半区对 `new Func(display::method)` 形态的转换质量（双端）**：委托类型→函数式接口（[DelegateTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/DelegateTransformer.cs)）与对象创建/方法组的衔接此前主要吃内联 lambda；显式委托构造 + 嵌套类接收者可能暴露重载解析/私有嵌套类可见性问题。缓解：M2 起每个 fixture 同步跑 Java 半区编译（SampleX 双端范式）；问题归入转换器独立缺陷，不回灌 C# 侧形态（C# 形态是 Roslyn 同构目标，不为 Java 半区扭曲）。
6. **E16 过程式化脱节（D20）**：LinqRewrite 只认 `AnonymousFunctionExpressionSyntax`（[Lambda.cs:39-47](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/Lambda.cs#L39-L47)），提升后实参变为委托构造。缓解：M2 探针先行量化；退路 (a) 过渡白名单有 residual 代价、退路 (b) 依赖 C26 排期；默认开以双端结论为硬门。
7. **与 E02 互相触发/捕获前缀碰撞**：E02 形参 `_cs2j_cap_` 与本 pass 字段 `_cs2jCap_` 同族不同形；提升方法内再次提升时命名表必须跨阶段共享同一 usedNames 视图。缓解：命名表以类型 FQN 为键贯穿四 stage；fixture 含 E02→C45 链式形态与撞名构造。
8. **递归 lambda 赋值时序**：字段在委托构造后才赋值，体内若在「赋值表达式求值期间」同步进入递归会读 null（C# 同样在委托首次调用前变量未赋值即报错/为 null——行为对齐但论证易漏）。缓解：D13 只处理「调用点晚于赋值点」可证形态；互递归不可证即 CS2J4508，不侥幸。
9. **调试体验**：大量合成类/方法使堆栈与单步偏离源码。缓解：类/方法名携带宿主成员与序号；`[CompilerGenerated]` 与既有 E02/C04 产物一致；不发射 IL 名；调试器「Just My Code」行为与 Roslyn 产物同。
10. **注册表热文件预期冲突**：async-normalize/其他族翻转同碰 PassCatalog。缓解：本 pass 不做拓扑变更（仅文本行），冲突面降到最小；后合者保留各自 descriptor 与边（C04 §11-5 既定规则）。
11. **属性/索引器/初始化器宿主面（CS2J4504）**：GUI/配置代码命中率未知；AuditOnly 分桶量化，不临时扩面（与 C32 风险 10 同纪律）。

## 11. 并行协调

1. **C32（硬前置，同 pass 同 worktree 序列）**：实现基线必须先合入 C32 分支（[§12](#12-worktree-分配只写不执行本设计文档在主工作区编写)）；D2 加性演进是合并审查第一检查项；C32 的 CS2J32xx、命名表、residual 基础不改语义。
2. **E02（fixpoint 对端 + 资产方）**：`CaptureClassification` 单一实现纪律不变；本 pass 加查询不改 E02 gate；E02 提升方法是普通分析根；未来环检测在编排器落地前，双方各自保证幂等（§3.2）。
3. **E05（前置，默认开）**：只消费 lambda 与 `CsPrelude.Combine/Remove` 形态；E05 合成的无类型形参由 D12 补类型；残余匿名方法 CS2J4503。
4. **C01（ref 门）**：CS2J4501 是门不是兜底；C01 产物 `ByRef<T>` 形参是普通对象，正常进字段；lambda ref 形参子题若立项，在本 pass 加 stage 而不是改门。
5. **C04/C18（下游消费方，阶段门 §3.3）**：C45 非其默认开硬前置，但解锁 CS2J0405/CS2J1802 两桶；命名契约（`Cs2jDisplayClass_*`/`_cs2jThis`/`_cs2jCap_`/`Cs2jLambda_*`）与 C04 §11、C18 D12 对齐冻结，未来由统一命名服务收口；C45 翻转时同步确认两条 RunAfter 边与排序快照；async lambda 边界 D17：C45 不解释 await、C18 不重建类。
6. **C26（表达式树/三路分流）**：共享 `ExpressionTreeFacts` 单一谓词；linq-channel 前移是 D20 退路 (b) 的承接者；Expression<> 在 sdk 仅 2 处（[:1262](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1262)），只读混合放行风险低但需 fixture。
7. **C10（attribute 交界）**：lambda 形参 attribute 由本 pass 搬运到命名方法形参；C10 的 Caller*/声明端归一在其 pass 处理同形参时看到的是普通方法参数；两侧测试各加一条交界用例。
8. **转换器热文件退役顺序（M5）**：先删 MethodConversionState 状态 API 与 StatementTransformer 预扫描，再删 LambdaTransformer holder 发射，最后删 `IdentifierExpressionTransformer` 不对称路径相关消费者；每步直连测试改写后独立提交；M5 不得早于 M4 默认开后一个稳定窗口。
9. **C21 匿名类型/C09 等**：display class 是嵌套命名类，不经匿名类型提升；与 thread-static 无共享面。

## 12. Worktree 分配（只写不执行；本设计文档在主工作区编写）

- 分支：`feature/preproc-c45-closure-lift`
- 目录：`.worktrees/preproc-c45-closure-lift`（`.worktrees/` 已被 .gitignore 忽略，与 C32 及其余在途 worktree 同级）
- base：`46bdeeca`（**2026-10-03 更新**：`dev-3.x-26100311` HEAD，原定 `953293b9`；C32 分支 `feature/preproc-c32-capture-lift`（`41049d41`）已完整合入该分支——C45 的第一个提交即建立在 C32 全绿的 ClosureLift 骨架上，禁止在本分支重复实现 holder 阶段，无需再执行合入 C32 步骤）
- 创建命令（PowerShell，开工时执行者手动运行；本任务未执行任何 git 写操作）：

```powershell
git worktree add .worktrees/preproc-c45-closure-lift -b feature/preproc-c45-closure-lift 46bdeeca
```

- 开工顺序提示：跑 C32 既有测试取得绿基线 → M1 静态路径（不动 holder 代码）→ M2 做 HolderPlan→DisplayClassPlan 加性演进（D2 零返工为该里程碑审查红线）→ M3/M4；catalogItems 单行文本放到 M4 随默认化评审一起改，M1-M3 期间保持 `C32`。

---

## 附：关键证据索引

- catalog：[preprocessor-catalog.md:895-908](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L895-L908)（C45）、[:708-720](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L708-L720)（C32）、[:1083](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1083)（§3.3 族表）、[:1106](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1106)（风险 8）、[:1157-1184](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1157-L1184)（§5.4 契约/fixpoint）、[:1255-1262](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1255-L1262)（Layer 4/4966）
- foundation plan：[preprocessor-foundation_plan.md:77-117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L77-L117)（交付模板/门槛）、`:161`（fixpoint 编排明确排除）
- 注册表/链：[PassStubCatalog.cs:152-161](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L152-L161)（closure-lift stub）、[:229-238](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L229-L238)（async-normalize）、[PassCatalog.cs:196-215](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L196-L215)（iterator-lowering/goto 边）、[PassChainFactory.cs:60-80](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L60-L80)（toggle 先例）
- C32 契约：[c32 设计 §3.4/§4.8](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c32-capture-lift-design.md)（阶段接口预留）、§4.3-4.6（holder 形状/命名冻结）、§12（worktree/base）
- E02 资产：[LocalFunctionLiftAnalyzer.cs:405-465](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs#L405-L465)（CollectCaptures）、[:491-529](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs#L491-L529)（写站点）、[:531-583](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs#L531-L583)（传播不动点）、[:952-988](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftAnalyzer.cs#L952-L988)（跨文件命名表）、[LocalFunctionLiftNaming.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/LocalFunctionLift/LocalFunctionLiftNaming.cs)、[LocalFunctionLiftPassAdapter.cs:74-136](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/LocalFunctionLiftPassAdapter.cs#L74-L136)（residual 先例）
- E05 现状：[DelegateSyntaxRewriter.cs:115-140](file:///d:/code/cs2j/src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs#L115-L140)（Combine/Remove）、[:174-176](file:///d:/code/cs2j/src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs#L174-L176)（事件跳过）、[:244-334](file:///d:/code/cs2j/src/CSharpToJava.Core/DelegatePreprocessor/DelegateSyntaxRewriter.cs#L244-L334)（匿名方法→lambda）
- 转换器现状（M5 对象）：[LambdaTransformer.cs:38-100](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L38-L100)、[:160-201](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L160-L201)、[:228-274](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LambdaTransformer.cs#L228-L274)、[StatementTransformer.cs:68-89](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L68-L89)、[:124-143](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.cs#L124-L143)、[MethodConversionState.cs:310-555](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/MethodConversionState.cs#L310-L555)
- 共享/姊妹：[ExpressionTreeFacts.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs)、[DelegateTransformer.cs:11-15](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/DelegateTransformer.cs#L11-L15)、[Lambda.cs:34-48](file:///d:/code/cs2j/src/CSharpToJava.Core/LinqRewrite/Lambda.cs#L34-L48)、[c04 §4.3/§11](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c04-iterator-lowering-design.md)、[c18 D3/D12/§3.3/§11](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c18-async-normalize-design.md)

---

## 附：开放问题裁决（2026-09-29 拍板）

> **拍板目标**：支持全部 C# 语法/语义、便利下游 C#→Java 转换。统一口径：① 每种合法 C# 构造必须有语义等价的可编译 Java 终态，v1 可显式诊断门控但必须登记终态去向，禁止长期静默丢弃；② 中间树始终是普通合法 C#，Java 侧差异集中到 compat/映射层收口；③ 语义精确优先于分配/性能，优化变体须有画像证据；④ 预处理默认开前转换器旧臂由独立 PR 退役、不留双轨窗口，CheckPass 安全网永久保留；⑤ 合成类型名 `Cs2j<Role>_<Member>_<n>`、合成成员/临时 `_cs2j…`、让位改名 `<Name>_cs2j<Purpose>`（单下划线后缀）；⑥ 跨 pass 顺序依赖一律显式 RunAfter 固化；⑦ Implemented+默认关观察期不超过两个里程碑窗口；⑧ worktree 创建命令维持 base `953293b9`，创建后第一步 rebase 到当时 dev-3.x HEAD（本裁决时为 `ef70e5a2`），stub 计数红灯由首个锚点翻转 PR 按真值收口。

**裁决（散见决策表/里程碑门的悬置项，按主题收口）：**

1. **D2 加性演进为 M2 红线**：**HolderPlan → DisplayClassPlan 必须纯加性演进、C32 全部用例字节断言零失效**，此为 M2 合并审查第一红线（不允许借收编重写 holder 形状）；C32 冻结的 `Cs2jDisplayClass_<member>_<n>`/`_cs2jCap_` 命名只增不改。
2. **RunAfter 双边确认时点**：closure-lift 翻转（或 C45 阶段扩宽）时，**与 iterator-lowering、async-normalize 两条 RunAfter 边在同一 PR 同时确认**并补排序快照双边断言（"捕获先显式、状态机后保存"；async 边界 D17 两不跨）；不依赖 Kahn 声明序兜底。
3. **默认开数据门**：M4 是否翻默认开，**以三项硬数据定案——新增嵌套类型数、IL 体积增量、sdk 编译时长增量**（AuditOnly 4966 站点分桶 + 三项测量齐全才评审），阈值在门槛评审定；数据不达标即继续 opt-in，不用定性判断放行。
4. **CaptureClassification 单一事实**：沿用 C32 裁决——C45 只查不改 E02 提取的唯一 `Analysis/CaptureClassification`，写站点仍走同一 `FindWriteSites` 保守并集口径，禁止第二口径。
5. **M5 退役节奏**：转换器旧 holder 路径退役（MethodConversionState 状态帧 → StatementTransformer 预扫描 → LambdaTransformer holder 发射，三步独立提交）**不得早于 M4 默认开后一个稳定窗口**；M5 未完成不阻塞 C45 默认开，两套事实按 C32 §10-9 纪律共存。
6. **E16 退路（D20）**：M2 探针先行量化 LinqRewrite 对显式委托构造实参的行为；退路 (a)/(b) 均不进默认路径，默认开以"双端结论成文"为硬门；linq-channel 前移（退路 b）的承接归 C26，本 pass 不自带。
