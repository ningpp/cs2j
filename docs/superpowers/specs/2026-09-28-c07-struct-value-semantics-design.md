# C07 Struct 值语义补全（可变 struct 防御性 clone）预处理器设计

**日期**: 2026-09-28（2026-09-29 核订：对照 dev-3.x@2d25700e 复核全部代码证据；实测类数据保留原始测量口径，依赖代码已变更处的复核注记见正文）
**范围**: 新增注册表 pass `struct-value-semantics`（Core `StructValueSemantics\` + CLI `Adapters\StructValueSemanticsPassAdapter.cs`），把残余【可变 struct】的复制/变异/装箱边界在 C#→C# 中间树上显式化；不改 C#→Java 转换器代码、不引支撑库
**状态**: 已核订（2026-09-29 对照 dev-3.x@2d25700e 逐条核验全部代码证据并修正；C02 generic-deepen、C03 pattern-desugar、C04 iterator-lowering、C11–C14 member-desugar 已落地进 PassCatalog Implemented 表且均默认关，member-desugar 下游锚点从 stub 表迁至 PassCatalog、链位现状、W 系列行号与 `CloneStructValueIfNeeded` 调用点计数按现状更新）
**catalog 锚点**: `prompts/preprocessor-catalog.md` §2.B C07（L363，P1，条目状态仍为「缺失」）；§1.2 值语义类；§3.3 合并族 = E11 + E13 + C07 + C01 ref struct 子项 + C44 ref struct using 子题
**注册表现状**: stub 已登记于 [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L120-L129)（id=`struct-value-semantics`，Stage=Declarative，RunAfter=`make-readonly`，默认关）
**base 提交**: 30d13211 `feat(preprocessing): C31 Index/Range 落地 + 已落地 pass 默认开启收口`（2026-09-30 自 f68a783c 前移至 dev-3.x 最新提交）

---

## 1. 背景与目标

### 1.1 Java 侧语义错误证据

C# 的 struct 是值类型：变量初始化、赋值、传值参、返回、装箱都产生**独立副本**；转换器把 struct 一对一映射为普通 Java class（引用语义），值语义差异只能靠在 Java 产物上零散插入 `clone()` 代偿。[StructTransformer.cs:14-19](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/StructTransformer.cs#L14-L19) 明确注释「不生成 clone/equals/hashCode/默认构造/value-class 等值语义补丁；struct 的值语义差异由 C#→C# 预处理器在需要时消除」。

当前 Java 侧代偿资产 [StructCloneHelper.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/StructCloneHelper.cs)（457 行）只覆盖了少数边界，且每处都留有已知缺口：

| # | C# 形态（值语义） | 生产转换器现状 | 错误 Java 形态 / 语义丢失点 |
|---|---|---|---|
| W1 | `S b = a; b.Mutate();`（局部初始化复制） | 局部声明转换**完全不插 clone**（[StatementTransformer.Declarations.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs) 无任何 clone 逻辑；全仓 `CloneStructValueIfNeeded` 仅 2 个调用点：[StatementTransformer.ExpressionAndReturn.cs:233](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L233)、[PropertyTransformer.cs:42](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L42)；2026-09-29 核订） | `S b = a;` 直译为同引用赋值，`b` 的变异污染 `a` |
| W2 | `a = b;`（结构体赋值，含字段/属性槽） | [AssignmentTransformer.cs:76](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs#L76) 默认分支裸发 `lhs = rhs` | 两变量共享同一对象，后续变异互相可见 |
| W3 | `a = b = c;`（链式赋值，三个独立副本） | 无处理；实验管线 [LowerStruct.cs:139-166](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerStruct.cs#L139-L166) 有 temp + 逐目标 clone 展开，但 Lowering 仅被 Java2 对比测试引用（catalog §4 风险 9：**不是实现资产**） | 三个目标互相别名；任一变异其余皆可见 |
| W4 | `M(arr[i]);`（数组元素作值参，C# 传副本） | [ArgumentTransformer.cs:227](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.cs#L227) 对 `ElementAccessExpressionSyntax` **显式跳过** clone（跳过点本身无注释；「for backward compatibility」的自认注释现位于 [StructCloneHelper.cs:65-67](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/StructCloneHelper.cs#L65-L67) 的 `IsCloneableTemporary` 注） | 被调方对形参的变异直接改写成数组元素 |
| W5 | `object o = s; o` 装箱 / `(S)o` 拆箱 / 接口转换 `IFoo f = s;` | struct 即 Java class，装箱/接口转换是恒等引用；无副本产生 | 箱与原变量共享状态：经接口分派的变异（C# 只作用于装箱副本）回写原变量；拆箱后修改也回写原对象 |
| W6 | `return s;`（返回值副本） | [StatementTransformer.ExpressionAndReturn.cs:229-234](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L229-L234) 插 clone，但有 `SuppressReturnClone` 与 `Clone()` 自递归两个特判口 | 依赖特判正确；调用方与被返回字段仍可能在未走该路径处别名 |
| W7 | 属性 getter 返回 struct、auto-property setter | [PropertyTransformer.cs:36-63](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L36-L63) 对 auto-property 发 `field.clone()`/`value.clone()`；自定义 getter 走 W6 | 自定义 setter、字段槽赋值无统一保证；调用点是否再 clone 靠 [StructCloneHelper.IsPropertyAccess](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/StructCloneHelper.cs#L76-L90) 名字猜测 |
| W8 | `readonly S _f; _f.Mutate();`、`in S p; p.Mutate();`、foreach 变量上调变异方法 | C# 编译器对接收者做**隐藏防御性拷贝**；转换器无对应物 | Java 在原只读存储/调用方对象上直接变异 |
| W9 | `arr[i].X = v`（数组元素是存储槽，合法直改） | 基本成立（数组元素 Java 侧也是槽），但与 W4 的读取侧不对称，元素整体替换（W2 类）无 clone | 元素槽语义依赖周边路径恰好正确，无统一模型 |

代偿的维护证据：回归测试 [StructCloneReturnThisTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/StructCloneReturnThisTests.cs)、[JaggedArraySelectCloneDebugTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/JaggedArraySelectCloneDebugTests.cs)、[JaggedArrayOutParamLinqTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/JaggedArrayOutParamLinqTests.cs) 均为补丁驱动；[MemberwiseCloneRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Java/Rewriters/MemberwiseCloneRewriter.cs) 还要为 `clone()` 的 checked exception 包 try/catch。

### 1.2 目标

在 make-readonly（E13）/ record-readonly（E11）把**能只读化的 struct 全部只读化之后**，对链上残余的**可变 struct**，在 C# 源码层面把值语义边界显式化：

1. **防御性 clone 插入**：局部初始化、赋值（含链式）、值参、返回、属性/字段取值消费、数组元素存取等点插入显式 `Clone()`，使中间 C# 的每一处副本在文本上可见——这些 clone 在 C# 语义下是**冗余但恒等**的（多一次值拷贝），故 .NET 侧源项目测试天然验证其等价性；在 Java 侧则补上引用语义所缺的隔离。
2. **Clone 成员保证**：为每个被本 pass 覆盖、且缺少可复用克隆成员的可变 struct 合成拷贝构造 + `Clone()`（逐字段、对嵌套可变 struct 字段递归 clone），保证中间树必然编译且 Java 侧 `clone()` 语义正确（§4.4）。
3. **装箱/接口可达性**：复用并细化 [BoxingReachabilityAnalyzer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/BoxingReachabilityAnalyzer.cs)，把装箱（object/dynamic）、接口转换、拆箱使用点纳入 clone 插入集；该分析在 E13 是「跳过只读化」信号，在 C07 是「必须插入 clone」信号。
4. **不可保持值语义的构造显式报错**：`this` 按引用逃逸、ref 返回自身状态、unsafe/指针别名、表达式树内等无法靠 clone 保持语义的构造，发 SV040/SV042 错误并使 pass 失败，禁止注释、占位或原样透传流入 Java（catalog §4 风险 12 第一原则）。

非目标：不处理 ref struct 本体（C01 ref struct 子项 + C06 边界）、不做指针/stackalloc（C06）、不重述 E13 的方法迁移（L5）与 WithXxx 体系、不改任何转换器代码。

### 1.3 与 E11 / E13 的分工

| 维度 | E13 make-readonly（已实现、默认开） | E11 record-readonly（已实现、opt-in） | C07 struct-value-semantics（本设计） |
|---|---|---|---|
| 输入意图 | 把可变 struct **变为** readonly struct（L1–L5 五级转换 + shim/接口桥） | record struct/class → init/readonly | **保持 struct 可变**，只显式化复制边界 |
| 对装箱可达 struct | 跳过只读化（[StructAnalyzer.cs:301-318](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs#L301-L318)） | 同系规则 | **接收**这批残余类型，插入 clone |
| 变异方法 | L5 迁移（void→返回 struct、MethodMigrator、跨文件 CallSiteUpdater） | record 版方法迁移 + 接口 ripple | 不迁移方法签名；处理「未迁移的变异方法在副本上的调用」与 this 逃逸判定 |
| 写访问 | 字段→get-only 属性 + WithXxx（AssignmentRewriter） | set→init + With 表达式 | 不动成员可写性；在写/读的**值流边界**插 clone |
| 失败/跳过 | NotConvertible/Skip 仍为可变 struct（[StructAnalyzer.cs:329-484](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs#L329-L484)） | RR020 原子跳过 | 输入正是这些残余：readonly 化失败者、ref/partial/opt-out 跳过者、装箱可达者 |
| 资产关系 | StructAnalyzer / BoxingReachabilityAnalyzer / CallGraphBuilder / MethodMigrator 的产出方 | MutationSiteClassifier/RecordPlan 范式 | **同程序集 internal 直接复用**（§3.4），不复制分析器 |

顺序契约：E11 在链上远早于 E13（[PassCatalog.cs:152-181](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L152-L181)）；C07 紧随 E13。readonly 化成功的类型不需要 clone 插入（其成员已不可变，别名无观测差异），C07 的残余集合在语法上可判定：**声明处无 `readonly` 修饰符的源内 struct**（含 E13 跳过与失败类型，扣除 ref struct 等范围外类型）。

### 1.4 硬不变式

1. 中间源码树在本 pass 后永远可编译（整树重编译相对基线零新增错误），且源项目测试在 .NET 侧全绿（clone 在 C# 下恒等）。
2. 每个 struct 的处理是原子 plan：声明 shim 与其全部使用点改写同生共灭；plan 回退 = 该 struct 触碰的所有文件恢复原状。
3. 无法用 clone 保持值语义的构造**显式错误**，不静默透传。
4. 幂等：对 C07 产物再跑一次 C07，零 diff（已存在 `Clone()` 调用与 shim 必须被识别）。

---

## 2. 关键设计决策表

| 决策点 | 结论 | 备注 / 被否决方案 |
|---|---|---|
| 残余集合判定 | 整树编译后，取**源内声明、非 `ref struct`、声明处无 `readonly` 修饰符**的 struct（含 generic、嵌套；record struct 残余同理）；partial struct 在 E13 被整体跳过，C07 按符号合并（`DeclaringSyntaxReferences`）建一个 plan | 不依赖 E13 落盘 sidecar（§3.4） |
| 已有 `Clone()` 的 struct | 存在零参、返回类型恰为本类型、可访问的实例 `Clone()` → 复用（SV010），仅做使用点插入；签名不符（返回 object、带参等）→ plan 判 Infeasible（SV050），不即兴包装 | 用户 `Clone()` 已与转换器 [StructCloneReturnThisTests.cs:29-33](file:///d:/code/cs2j/tests/CSharpToJava.Tests/StructCloneReturnThisTests.cs#L29-L33) 契约定形 |
| shim 形态 | 合成 **private 拷贝构造 + public Clone()**：`public S Clone() => new S(this);`，拷贝构造内逐字段赋值；**字段静态类型为「残余可变 struct 且有 Clone 成员」时递归 `o.f.Clone()`**，其余字段（基元/enum/引用类型/数组/已 readonly 化 struct）直接赋值（§4.4） | 否决 `S c = this; return c;`：转换器对局部初始化不插 clone（W1），且会在 clone() 内自递归；否决 `(S)MemberwiseClone()`：Java 浅拷贝与嵌套值字段语义不符 |
| readonly 字段可赋值性 | 拷贝构造是唯一能对实例 readonly 字段赋值的位置，故 shim 必须走拷贝构造，不能「new() 后逐字段 set」 | readonly 实例字段在可变 struct 中合法 |
| 变异点分类 | VS-1 存储写入 / VS-2 值读取消费 / VS-3 防御性接收者 / VS-4 数组元素 / VS-5 装箱与接口（§4.3 全表） | 分类全部经 SemanticModel 绑定，禁止纯文本规则 |
| clone 插入点 | 见 §4.3：local init、= / 链式 =、return、值参（被调方可能变异时）、装箱/接口转换、拆箱后消费、数组元素存/取、struct 类型字段/属性槽写入；新鲜值（new/default/二元一元结果/invocation/字面量）不插 | 新鲜值谓词从 [StructCloneHelper.IsCloneableTemporary](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/StructCloneHelper.cs#L52-L68) 移植为 Core 内 `ValueCopyFacts`，**取消其数组元素特判**（修 W4） |
| 值参是否插 clone | 移植 [IsValueParamEffectivelyReadOnly](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/StructCloneHelper.cs#L194-L205) 的跨过程只读分析（含 ref 转发链、环检测、无体保守不跳过）；形参可能变异才插；`in` 参按 VS-3 处理 | 无 body 的外部方法保守为「可能变异」（现行实现同口径） |
| VS-3 防御性接收者 | 对 `in` 参、readonly 字段/属性、foreach 迭代变量、非左值表达式（方法返回值、属性取值、数组元素）上调变异实例方法时，提升临时副本：`var __cs2jVc0 = receiver.Clone(); __cs2jVc0.Mutate();` | 对应 C# 编译器隐藏拷贝；变异方法集合复用 StructAnalyzer 的 IsMutating + 传递闭包 |
| this 逃逸 | 命中任一即 SV040（Error）：实例成员把 `this` 以 ref/out 传给外部方法；`ref`/`ref readonly` 返回 `this` 或返回指向本类型可变实例字段的引用；`this` 被逃逸出方法的 lambda/匿名方法/迭代器/async 状态机捕获；`this = expr` 不由 C07 处理（E13 已判 NotConvertible，C07 遇到亦报错 SV041） | ref 返回**只读且指向不可变状态**不报错；ref 返回成员的**读取点**按别名处理（不插 clone） |
| ref struct 子项边界 | `ref struct` 声明、`Span<T>`/`ref` 字段类型、`ref struct` 局部/参数/返回站点**整体不纳入**（plan 集合直接排除，SV011 Info）；ref struct 与普通可变 struct 的交互点（如 `Span<S>` 元素变异）报 SV042 交 C01/C06 | 与 C01 设计 Q2/Q5、C06 对齐，不提前实现 |
| unsafe/指针别名 | `unsafe` 块、`&`/`->`/`fixed`/指针寻址覆盖的存储与站点不插 clone（插了反而破坏身份同一）；含 fixed 缓冲区字段的 struct 排除；残余无法判定者 SV042 | 交 C06；禁止产出半对的 clone |
| 表达式树 / dynamic | `Expression<TDelegate>` lambda 内的站点不改写（插 `Clone()` 改变树节点形态）；纯 dynamic 绑定站点不改写；两者均 Info 登记（SV013/SV014），不算错误——站点本身不涉及副本显式化的可证义务 | 与 JK-8、RR 同族共享「表达式树内跳过」判定，后续抽到公共工具（catalog §4 风险 8） |
| 回滚粒度 | 每 struct 一个 plan；TouchedFiles = 声明文件 + 全部插入点文件；整树重编译新增错误按文件+最近类型/成员归属唯一 plan → 原子回退（SV031）并重放迭代；无法归属 → 本次运行全量回退（SV030），复刻 [RecordReadOnlyPassAdapter.cs:80-208](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/RecordReadOnlyPassAdapter.cs#L80-L208) | 上限 1024 轮，同 RR |
| 分析共享 | v1 无 sidecar：C07 在 E13 输出树上重建编译，同程序集直接调用 internal 的 StructAnalyzer/BoxingReachabilityAnalyzer/CallGraphBuilder；残余集合由语法+符号自洽算出（§3.4） | 否决跨 pass 序列化 plan（破坏「中间目录即纯源码」模型与指纹机制） |
| 支撑库 | **无**（`PassSupportLibrary.None`）：shim 注入用户源文件，不新增工程 | descriptor 无需支撑库声明/过滤 |
| 默认开关 | 落地即 Implemented 但**默认关**；满足 §8 门槛后翻默认开 | 对齐 catalog §4 风险 10「opt-in 长期默认化」 |

---

## 3. 注册表与链位契约

### 3.1 现状（stub）

[PassStubCatalog.cs:120-129](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L120-L129)（核订重定位）已登记：

| 字段 | 值 |
|---|---|
| Id | `struct-value-semantics`（常量 `PassStubCatalog.Ids.StructValueSemantics`） |
| DisplayName | `Struct value semantics` |
| IntermediateDirectoryName | `.cs2j-struct-value-semantics-src` |
| Stage | `PassStage.Declarative` |
| Status | `Stub`（显式启用 → CS2J9001，见 [PreprocessDiagnosticCodes.cs:12](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PreprocessDiagnosticCodes.cs#L12)） |
| DefaultEnabled | `false` |
| RunAfterIds | `{ "make-readonly" }` |
| CatalogItems | `C07` |
| Description | `Make struct copy/mutation/readonly boundaries explicit.` |

下游锚点已存在（**2026-09-29 核订重定位与状态更正**）：

- `unsafe-ffm`：仍是 stub，RunAfter 含本 id（[PassStubCatalog.cs:130-139](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L130-L139)）✓；
- `member-desugar`：**已翻转落地**（[PassCatalog.cs:230-241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L230-L241)，Implemented、默认关、verify build/tests 默认开），其 `RunAfterIds = { PassStubCatalog.Ids.StructValueSemantics, PassStubCatalog.Ids.ClosureLift }`——即**仍以本 id 为锚**，但条目已从 stub 表迁至 Implemented 表，原文引的「PassStubCatalog.cs:191-200」现在是 `ExtensionMethods` 条目（**核订纠错**）。这带来一条实务影响：本 pass 翻转时若把 id 常量迁出 `PassStubCatalog.Ids`，必须同步改 `member-desugar` 的引用（§3.3 已列）。
- 拓扑排序按声明顺序决胜（[PassRegistry.cs:95-142](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs#L95-L142) 的 `TopologicalSort`，核订纠正原文的 L95-133）。

### 3.2 链位与顺序锁

catalog §3 建议链序为 `… → E13 + C07 → C06 → C32+C45 → C04 → … → E14 goto → C08`。**2026-09-29 核订状态更正**：原文「stub 当前与 `eliminate-goto` 同以 `make-readonly` 为唯一锚点」已不成立——C04 落地后 `eliminate-goto` 的锚点为 `{ make-readonly, iterator-lowering }`（[PassCatalog.cs:220-229](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L220-L229)），不再与 C07 共享唯一锚点；而 Implemented 表先于 Stub 表参与决胜这一事实不变，故现状序仍是 goto 先于 C07（C07 仍为 stub，只在 stub 段被排序）。翻转时必须锁定为 **make-readonly → struct-value-semantics → eliminate-goto**（clone 插入与 shim 先行，goto 的变量提升/结构化在后，避免 goto 后的平铺代码再被语句级插桩）：

- 新 descriptor 在 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) `Implemented` 表中**紧接 make-readonly 条目声明**（位于 eliminate-goto 之前），靠声明序决胜；
- 同步在注册表顺序断言测试（foundation plan Step 4 的「拓扑序 == 链顺序」锁）中加入 `…, make-readonly, struct-value-semantics, eliminate-goto, …` 序列；
- 不新增 RunAfter 边（避免给 goto 增加它语义上不需要的依赖）；如评审要求更强约束，备选是给 eliminate-goto 追加 RunAfter 本 id，二选一在实现时定死并写入测试。

### 3.3 翻转清单（stub → Implemented）

1. [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)：`PassIds` 增 `StructValueSemantics = "struct-value-semantics"`；`Implemented` 增描述符（首翻 `defaultEnabled: false`，`verifyBuildByDefault: true`、`verifyTestsByDefault: true`，`NeedsSemanticModel = true`，SupportLibrary=None）。
2. [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)：删除本 id 的 stub 条目；引用 `Ids.StructValueSemantics` 的下游锚点改引 `PassIds.StructValueSemantics`——**核订后的影响面**：stub `unsafe-ffm`（同文件 [L130-139](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L130-L139)）与**已落地**的 `member-desugar`（[PassCatalog.cs:237](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L237)）两处，均为编译期改名、不行为变化。**id 常量归属提示**：现有两手先例——C02 迁到 `PassIds`、C04 保留在 `PassStubCatalog.Ids`（`PassCatalog.cs:210` 反向引用）；建议随 C02，并与既有形态一并评审对齐。
3. [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)：`PassChainConfiguration` 增 `Func<StructValueSemanticsOptions>` 工厂（lazy 加载 overrides，仿 MakerOptionsFactory）；`CreateAdapter` 增 switch 分支；本 pass 无 legacy 二级开关时仅走通用 `--enable-pass/--disable-pass`，并在 `ConvertProjectOptions` 增 `[Option("struct-value-semantics", Default = false)]` + `BuildLegacyToggles` 一行（与 record-readonly 同等待遇）。
4. [CliPreprocessChain.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs)：`CliChainRequest` 增 options 工厂字段并透传。
5. Program.cs：preserved 目录集合按描述符驱动接入（foundation 后指纹令牌对每个注册表 pass 生成 `pass:<id>=on/off`，本 pass 自动纳入；确认 `.cs2j-struct-value-semantics-src` 进 preserve、非启用时零痕迹）；`--list-passes` 状态自动翻为 Implemented。
6. 新增 [Adapters/StructValueSemanticsPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters)（§5）。
7. 更新 CS2J9001 相关测试（本 id 不再报 stub 错误）、注册表校验测试、顺序断言；删除/改写「启用任意 stub 报 CS2J9001」用例中如硬编码本 id 的部分。
8. §8 门槛达成后，描述符翻 `defaultEnabled: true`（一次一行翻转 + 指纹一次性失效，可接受）。

### 3.4 与 make-readonly 共享分析结果的方式

- **不跨进程/不跨目录传产物**：每个 pass 的中间目录只含源码与工程文件（指纹与 preserve 机制依赖此不变式）。
- C07 适配器在自己的 ExecuteAsync 内对 E13 输出树做一次整树 `CSharpCompilation`（compile 方式同 RR：`LanguageVersion.Latest` + 合成 global using 树 + `GetBasicReferences()`、`allowUnsafe: true`，见 [RecordReadOnlyPassAdapter.cs:261-298](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/RecordReadOnlyPassAdapter.cs#L261-L298)）。
- Core 为单一程序集，[StructAnalyzer](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs)、[BoxingReachabilityAnalyzer](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/BoxingReachabilityAnalyzer.cs)、`CallGraphBuilder`、`MethodMigrator` 均为 internal——直接可见。C07 复用方式：
  1. 残余集合：自算（无 readonly 修饰符 + 非 ref struct + 源内声明）；
  2. 装箱可达：直接调 `BoxingReachabilityAnalyzer.ComputeBoxingReachableStructs(compilation)`，结果按 compilation 缓存（仿 [ReadOnlyStructMaker.cs:14-27](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMaker.cs#L14-L27)）；
  3. 变异方法集合/调用图：复用 `CallGraphBuilder` 与 StructAnalyzer 内同名判定（评估把 IsMutating/传递闭包提为 internal 共享方法；若签名耦合过深，则在 `StructValueSemanticsAnalyzer` 内薄封装调用，**不允许复制逻辑**）；
  4. E13 跳过原因（boxing 可达、external ref param、opt-out）仅用于诊断措辞增强，不影响 plan 正确性。
- 若复用需要把 ReadOnlyStructMaker 内个别 private/internal 成员放宽可见性，改动限可见性、不改行为，由 E13 现有测试守门。

---

## 4. Core 组件设计

### 4.1 目录与文件

```
src\CSharpToJava.Core\StructValueSemantics\
  StructValueSemanticsPreprocessor.cs  # 入口：BindCompilation/RewriteFile/ExcludePlans/Plans/
                                       #   TryAttributeDiagnostic/FormatAndValidate（同 RR 契约形状）
  StructValueSemanticsModels.cs        # Options/Statistics/Diagnostic(SV0xx)/FileResult/Severity
  StructValueSemanticsAnalyzer.cs      # Phase 1：残余 struct 集合、变异方法集、全站点点表
  ValueCopyFacts.cs                    # 新鲜值/属性访问/值参只读跨过程分析（自 StructCloneHelper 移植+取消数组特判）
  BoxingSiteClassifier.cs              # Phase 1b：装箱/接口/拆箱站点分类（包 BoxingReachabilityAnalyzer）
  StructValueSemanticsPlan.cs          # ClonePlan/ValueCopySite/SiteKind/PlanBuilder/ShimKind/Outcome
  CloneShimGenerator.cs                # 拷贝构造 + Clone() 合成（嵌套 struct 递归 clone、碰撞处理）
  CloneInsertionRewriter.cs            # Phase 3：VS-1~VS-5 站点重写 + shim 注入
  StructValueSemanticsSyntaxFacts.cs   # 标注、接收者纯度、临时变量命名等小工具
```

公开契约（与 [RecordReadOnlyPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyPreprocessor.cs) 同形，便于适配器逐行复刻）：

- `StructValueSemanticsPreprocessor(StructValueSemanticsOptions? options = null)`
- `void BindCompilation(Compilation compilation)`
- `StructValueSemanticsFileResult RewriteFile(string sourceCode, SemanticModel model, StructValueSemanticsStatistics stats, List<StructValueSemanticsDiagnostic> diagnostics)` → `{ OutputCode, Changed }`
- `void ExcludePlans(IEnumerable<string> planIds)`
- `IReadOnlyList<ClonePlan> Plans`（含 `Id`/`Symbol`/`ShimKind`/`Outcome`/`TouchedFiles`/`FailCode`/`OwnsTouch(file, typeName, memberName)`）
- `bool TryAttributeDiagnostic(Diagnostic d, IReadOnlyDictionary<string,string> rewrittenByFile, out string planId)`
- `internal static string? FormatAndValidate(CompilationUnitSyntax root)`：三候选（区间格式化→整文件格式化→原文），重解析零错误取首个，与 RR/JK 同款。

### 4.2 三阶段整树数据流

1. **分析（整树一次）**：
   - 枚举编译内全部源内 struct 符号（partial 按符号合并；泛型取 `OriginalDefinition`），扣除 ref struct、已 readonly、枚举；扣 options.SkipQualifiedNames；应用 options.ForceQualifiedNames（仅解除「保守不插入」类跳过，不解除 SV040 语言无解项）。
   - 对每个残余 struct：解析零参 `Clone()`/拷贝构造现状；计算变异实例方法集（复用 IsMutating + 传递闭包）；跑 BoxingReachability；枚举**全编译所有文件中**类型为该 struct 的值流站点（声明/赋值/参数/返回/元素访问/装箱转换/接收者变异调用），经 SemanticModel 分类为 ValueCopySite 清单。
   - 不做任何改写；plan 尚未落盘，Infeasible 在计划期定案（SV040 类在本期即定级）。
2. **计划（StructValueSemanticsPlan）**：
   - 一个残余 struct 一个 `ClonePlan`；`ShimKind = GenerateCopyCtorAndClone | ReuseExistingClone | Infeasible`；`TouchedFiles` = 声明部件文件 + 全部站点文件；确定性排序（符号完全限定名）。
   - 全局站点（装箱到 object/dynamic、跨文件值参）归属于值类型 struct 的 plan；接口转换站点归属于运行时 struct 类型 plan；一个文件可被多 plan 共享。
   - shim 碰撞：目标名 `Clone( )`/拷贝构造签名已被用户占用且不满足复用条件 → Infeasible（SV050），不做即兴改名。
3. **重写（逐文件）**：取命中本文件的 active plans，`CloneInsertionRewriter` 单点遍历完成「shim 注入 + 站点插 clone + VS-3 临时提升」，挂 `RewriteAnnotation`，过 `FormatAndValidate`。CLI 驱动整树循环：复制源码树 → 基线错误集 → { 每轮从原始副本重放 → 整树编译 → 逐文件 RewriteFile → 重建编译 → DiffErrors → TryAttributeDiagnostic → ExcludePlans 回退该 plan 的 TouchedFiles } 收敛；不可归属 → 全量回退 SV030。单文件 FormatAndValidate 仅快筛，交付以整树重编译为准。

### 4.3 变异点分类与插入规则（VS-x）

站点判定一律用 `SemanticModel.GetTypeInfo/GetSymbolInfo/GetOperation` 解析出的值类型（含元组降级后 E06 生成 struct、泛型实例化）；类型必须通过 `StructCloneHelper.IsUserDefinedStruct` 同口径谓词（排除 enum/Nullable/ValueTuple/SpecialType/无源声明）。

| 规则 | C# 站点 | 改写形态 | 不插的条件 |
|---|---|---|---|
| VS-1a 局部/字段初始化 | `S x = e;`（local、for 初始化器、临时） | `S x = e.Clone();` | e 为新鲜值；声明为 `ref`/`in` 局部 |
| VS-1b 赋值 | `loc = e;` / `obj.Field = e;` / `obj.Prop = e;`（槽类型为残余 struct） | 右值包 `e.Clone()` | 新鲜值；左值为 ref 别名返回（沿 VS-5b 处理）；属性 setter 已在 C# 侧由本 pass 统一模型保证（auto-property 的 Java 侧 clone 待 E13/C07 后收敛，本 pass 只改 C# 文本） |
| VS-1c 链式赋值 | `a = b = e;` | 提升 `var __cs2jSc0 = e;` 后逆序 `b = __cs2jSc0.Clone(); a = __cs2jSc0.Clone();`（形态取 [LowerStruct.cs:139-166](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerStruct.cs#L139-L166)，语句位置；表达式位置不可提升 → SV043） | 单目标走 VS-1b |
| VS-2a 返回 | `return e;`（方法/属性/索引器/访问器返回残余 struct） | `return e.Clone();` | 新鲜值；当前方法即该类型零参 `Clone()`（防自递归，对齐现行 selfClone 特判） |
| VS-2b 值参 | `M(e)` 形参类型为残余 struct 且形参非只读 | `M(e.Clone())` | 新鲜值；`ValueCopyFacts.ArgDoesNotNeedClone`（被调方只读分析通过）；`e` 为属性取值且 getter 侧已有等价保证时去重（移植 IsPropertyAccess）；ref/out/in 参不走本规则 |
| VS-3 防御性接收者 | `r.Mutate()`：r 为 `in` 参 / readonly 实例字段或 get-only 属性 / foreach 变量 / 非左值 rvalue，且 Mutate 属于该 struct 变异方法集 | 语句位置：`var __cs2jVc0 = r.Clone(); __cs2jVc0.Mutate();`；表达式位置不可提升 → SV043 | r 为可写局部/普通字段/参数（C# 直接变异，无需拷贝）；方法为非变异/readonly 方法 |
| VS-4a 数组元素存 | `arr[i] = e;`（arr 元素类型为残余 struct） | `arr[i] = e.Clone();` | 新鲜值 |
| VS-4b 数组元素取 | `arr[i]` 流入 VS-1/VS-2/VS-5 消费点 | 在消费点插 clone（`M(arr[i].Clone())`）——**取消现行 W4 特判** | 元素出现在左值（VS-4a/自增目标）时不插 |
| VS-4c 结构体数组自增/复合 | `arr[i].Mutate();` 合法直改 | 不插（元素槽即存储，与 C# 一致） | — |
| VS-5a 装箱 | 赋给/传参/返回 `object`/`ValueType`/`dynamic`、显式 cast 到 object-like、`string.Format` 等无害格式化汇除外（沿用 E13 的 IsHarmlessFormattingSink 口径） | 装箱点包 `e.Clone()` | 序列化族泛型汇（`Serialize<T>(T)`/`Deserialize<T>`/`typeof(T)`）：这些点反射写箱不回写原变量，同样插 clone；可达性集合仅用于 plan 必选判定 |
| VS-5b 接口转换 | `IFoo f = s;` / 传 `IFoo` 参 / 显式转接口，IFoo 由该 struct 实现且存在变异接口成员 | 转换点 `((IFoo)s.Clone())` 形态 | 接口全部成员 readonly/纯查询 → 别名无观测差异，不插（plan 期可证） |
| VS-5c 拆箱消费 | `(S)o` / `is S x` / `as` 后作为值源流入 VS-1/VS-2 | 消费点 clone（C# 拆箱即副本） | pattern 中 `is S x` 的 x 在 C# 已是独立局部，等同 VS-1a 处理 |
| VS-6 字段声明默认值 | struct 类型实例字段无初始化器 | 不处理（转换器/运行时零值语义另由既有路径负责） | 仅登记，供 W 系列审计 |

插入去重总原则：消费点单点包裹（不在链上每层重复 clone）；判定顺序 VS-5 → VS-3 → VS-4 → VS-2/VS-1；宁可少插不可错插——少插由整树测试与 Java 半区测试暴露，错插（对 ref 别名插 clone）直接制造语义错误，错插类全部走 plan 期 Infeasible 或 SV043，不允许「插了再说」。

### 4.4 Clone shim 合成细节

对 `ShimKind.GenerateCopyCtorAndClone` 的 struct `S`（以非泛型示意，泛型保持类型形参）：

```csharp
private S(S other)
{
    _primitiveField = other._primitiveField;     // 基元/enum/string/引用类型/数组：直接赋值（浅，== C# struct 复制语义）
    _mutableStructField = other._mutableStructField.Clone(); // 字段类型 ∈ 残余可变 struct 且有 Clone 成员：递归深拷贝
    _readonlyStructField = other._readonlyStructField;       // 已 readonly 化/E11/E13 产物：不可变，直接赋值
    // 静态字段、const 不出现；事件字段、fixed 缓冲区 → plan Infeasible（SV051/SV042）
}

public S Clone() => new S(this);
```

要点：

- 嵌套深度正确性来自递归 `Clone()`；数组/引用类型字段保持浅拷贝（C# 值复制本就共享数组本体与引用目标），与 Java `clone()` 的直觉一致且与 C# 语义逐条对齐。
- 拷贝构造用 `private`（仅本类型 Clone 与同嵌套类型友元需要；InternalsVisibleTo 场景由 E13 已有的 [NormalizeFriendAssemblyAccess](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/ReadOnlyStructPassEngine.cs#L810) 同款 csproj 归一保证测试可访问性；若测试工程需直接调拷贝构造，用 internal + IVT，实现期按 fixture 最小放开）。
- 转换器侧契约：`Clone()`→Java `public S clone()`（既有命名映射与 [MemberwiseCloneRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Java/Rewriters/MemberwiseCloneRewriter.cs) 已覆盖方法体形态；拷贝构造直译为 Java 拷贝构造，`new S(this)` 是合法 Java clone 实现，不依赖 Object.clone 的 checked exception）。
- 空 struct（无实例字段）仍生成 shim（站点统一可插）；含只读字段、get-only 自动属性后备字段均可在拷贝构造中赋值。
- 字段集取实例字段（排除 static/const）：自动属性的 implicit 后备字段是真实存储，必须逐字段复制；事件后备字段按 SV051 处理。
- shim 注入位置：主声明部件、按 E13 同口径 trivia/格式化；disabled `#if` 区域不补（与 E13 MarkDisabledRegionStructsReadonly 的边界分开记录，C07 v1 不处理非活动区域，活动区域定义变化时由整树重编译兜底）。

### 4.5 Options / Statistics / Diagnostic 模型

`StructValueSemanticsOptions`（对齐 RR/JK 习惯）：

- `IReadOnlySet<string> SkipQualifiedNames`（overrides.skip，默认空）
- `IReadOnlySet<string> ForceQualifiedNames`（overrides.force，仅解除保守插入跳过，不解除语言无解 Error）
- `bool Strict`（存在 Error 级诊断时退出码非零；默认 true——本 pass 默认即「响亮失败」口径）
- `bool ReportInfo = true`
- 预留 `bool InsertAtValueArguments = true`、`bool InsertAtBoxingSites = true`（分批落地/二分定位用，默认全开；不作为长期 API 承诺）

`StructValueSemanticsStatistics`：`FilesScanned/FilesTransformed/FilesSkippedClean/StructsScanned/StructsPlanned/StructsReusedClone/ShimsGenerated/SitesRewritten`（再分 `LocalInitSites/AssignSites/ChainedAssignSites/ReturnSites/ArgumentSites/DefensiveReceiverSites/ArrayStoreSites/ArrayLoadSites/BoxingSites/InterfaceSites/UnboxSites`）、`PlansSkipped/PlansRolledBack/SkippedByReason: Dictionary<SVCode,int>`。

---

## 5. CLI 适配器与集成

新增 [Adapters/StructValueSemanticsPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/StructValueSemanticsPassAdapter.cs)，**整树自驱动**（`RewriteFileAsync` 抛 NotSupportedException，同 [MakeReadonlyPassAdapter.cs:216-217](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/MakeReadonlyPassAdapter.cs#L216-L217)）：

- `Descriptor` 取 PassCatalog 新条目；`CreateParseOptions` 用 `LanguageVersion.Latest`（同 RR）；`DeleteBuildOutputsAfterRestore = true`（同 make-readonly）。
- ExecuteAsync 顺序复刻 RR 适配器：复制 .cs → 基线错误集 → 迭代（重放原件 → 整树编译 BindCompilation → ExcludePlans → 逐文件 RewriteFile/回写（BOM 保持） → 重建编译 → DiffErrors → 归属/回退）→ Restore → 置 context.Success；verify build/tests 用 `context.VerifyBuildAndTestsAsync()`（descriptor 默认开）。
- 复用 runner 阶段助手：`PassTreeContext`、`ProjectPreprocessSource.ResolveRestoreTarget`、`ProjectPassRunner.DeleteBuildOutputs`；不自行复制树准备/restore/过滤逻辑。
- 摘要行：`struct-value-semantics: N struct(s) planned, M shim(s) generated, K site(s) rewritten, A skipped, B rolled back`；诊断行 MSBuild 格式，Info 仅 verbose。
- 无支撑库：不需要 AfterFilesAsync 注入工程，不需要过滤 compat 命名空间（`CompatNamespacePrefixes` 沿用全局配置即可）。
- overrides：`config/StructValueSemanticsOverrides.json`（`{ "skip": [], "force": [] }`，默认空/文件可选），加载路径逻辑仿 [Program.cs LoadReadOnlyForceOverrides](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L3110-L3135)（CWD→向上查找）；JSON 解析失败 → Warning 按空集继续，不致命。
- 入口面：convert-project `--struct-value-semantics`（默认 false）+ 通用 `--enable-pass struct-value-semantics`/`--disable-pass`；独立运行走通用 `preprocess --pass struct-value-semantics -s … -d …`（不为本 pass 新建独立 verb，与通用基建方向一致）；`--list-passes` 展示 Implemented/默认关/目录/说明。
- 单文件模式（如未来接入 `-i/-o`）：plan 的 TouchedFiles 超出输入文件即排除并 SV090（同 JK090/RR090）；v1 可只开放项目模式，单文件在通用 verb 下受同一闸门约束。

---

## 6. 诊断策略（不可保持值语义的构造显式报错）

码段 `SV0xx`，MSBuild 格式 `file(line,col): error/warning SV0xx: [Ns.S] message`，码位只追加不改号。

| 码 | 级别 | 含义 |
|---|---|---|
| SV001 | Info | struct 已纳入 plan（含 shim 形态/站点数） |
| SV010 | Info | 复用用户既有 `Clone()`，未生成 shim |
| SV011 | Info | ref struct 排除（交 C01/C06） |
| SV012 | Info | 已 readonly struct 不参与（理论残余集为空，防御性登记） |
| SV013 | Info | 表达式树 lambda 内站点跳过（共享判定） |
| SV014 | Info | dynamic 绑定站点跳过 |
| SV020 | Warning | plan 原子跳过（原因码随消息；overrides.skip/保守不可证） |
| SV030 | Error | 整树新增错误无法归属单一 plan → 本次运行全部回退（仿 RR030/JK030） |
| SV031 | Warning | 新增错误归属某 plan → 该 plan 原子回退（仿 RR031） |
| SV040 | Error | **this 按引用逃逸**（ref/out this 外传、ref 返回 this/可变字段、逃逸 lambda/迭代器/async 捕获）：clone 模型无法保持值语义 |
| SV041 | Error | 结构体内 `this = expr` 赋值出现在残余可变 struct（E13 未覆盖的落网形态） |
| SV042 | Error | unsafe/指针/fixed 与值副本边界相交且无法判定（交 C06 前的显式拒绝）；ref struct × 普通 struct 混叠点 |
| SV043 | Error | 表达式位置必须提升临时变量而语法上不可提升（链式赋值/防御性接收者出现在字段初始化器、特性实参、查询表达式等） |
| SV050 | Warning | 既有 `Clone()`/拷贝构造签名不兼容，plan 跳过（返回 object、带参、不可访问） |
| SV051 | Warning | struct 含事件字段/fixed 缓冲区等 shim 无法逐字段复制的成员，plan 跳过 |
| SV060 | Error | `dotnet restore` 失败（沿用各 pass 惯例） |
| SV090 | Warning | 单文件模式：plan 非自包含（跨文件声明/站点），跳过 |

策略说明：

- SV040/SV041/SV042/SV043 是「语言层面 clone 无法表达值语义」的显式拒绝，默认 Error 使 pass 失败、convert-project 中止——对应 catalog §4 风险 12「禁止注释/占位/原样透传」。用户确认该构造不进入实际值语义敏感路径时，可逐类型写入 overrides.skip（回退到 Java 侧 StructCloneHelper 代偿，SV020 留痕），即 Error 有**具名逃生口但默认关闭**。
- Warning 类（SV020/SV050/SV051/SV031/SV090）不阻断中间树交付；`Options.Strict`（默认 true）下存在 SV040 类 Error 时退出码 1（v1 不新增独立 CLI flag，随 options 模型保留未来放开口）。
- 所有跳过按 `SkippedByReason` 分桶进统计与摘要，便于 sdk 基线盘点。

---

## 7. 测试计划

### 7.1 Core 单测（内联源码 → CSharpCompilation → RewriteFile → 文本断言 + AssertOutputCompiles）

新建 `tests/CSharpToJava.ProjectTests/StructValueSemanticsUnitTests.cs`（仿 JavaKeyword/RecordReadOnly 单测），**可变 struct 矩阵**：

1. VS-1a `S b = a;` → `a.Clone()`；新鲜值 `new S()`/`default(S)`/`a + b`/字面量不插。
2. VS-1b 局部赋值、类字段槽赋值、属性槽赋值各一；嵌套成员链 `o.Inner.S = x`。
3. VS-1c `a = b = c;` 语句位置 → temp + 双 clone；表达式位置 → SV043。
4. VS-2a `return s;` 插 clone；`return new S();` 不插；`Clone()` 内 return 不产生 `this.clone()` 自递归（回归 W6/StructCloneReturnThis 场景）。
5. VS-2b 值参：被调方只读 → 不插；被调方变异/外部无体 → 插；ref 转发链（安全/不安全各一、环）；属性取值实参去重；**数组元素实参 `M(arr[i])` 必须插（W4 回归）**。
6. VS-3：`in S p; p.Mutate();`、readonly 字段接收者、foreach 变量、`GetS().Mutate()` 各一 → temp 提升；纯查询方法不提升；不可提升位置 SV043。
7. VS-4：`arr[i] = s` 插 clone；`arr[i].Mutate()` 不插；`var q = arr[i];` 插；锯齿数组。
8. VS-5：object 装箱、dynamic 声明、接口转换（有/无变异接口成员两例）、`(S)o` 消费、`is S x` pattern、序列化族泛型 sink（`JsonSerializer.Serialize<T>(T)` 形态桩）；`string.Format` 无害 sink 不插。
9. shim：无 Clone 的 struct 生成拷贝构造 + `Clone()`；含基元/string/数组/嵌套可变 struct/嵌套 readonly struct 五类字段（断言嵌套字段递归 `.Clone()`、readonly struct 字段不调 Clone 保证可编译）；含只读字段；空 struct；泛型 struct `<T>`；已有合规 Clone() → 复用 SV010；返回 object 的 Clone → SV050；事件字段 → SV051。
10. this 逃逸四例（ref this 外传、`ref S Foo() => ref _f`、逃逸 lambda、迭代器方法）→ SV040 且 Changed=false；`this = default` → SV041；unsafe 取址站点 → SV042；ref struct 声明与 Span 站点 → 排除/SV011/SV042 边界用例。
11. 幂等：输出二次 RewriteFile 零 diff；干净项目零改动字节级一致（含 BOM 保持）。
12. 与 E13 共处一树：L5 迁移后的 readonly struct 不被插桩；其 WithXxx 调用点不被误改；E13 装箱跳过的可变 struct 被 C07 接管。

### 7.2 装箱可达专项

复用/对照 [BoxingReachabilityAnalyzer](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/BoxingReachabilityAnalyzer.cs) 的根集合：object 赋/传/回、cast、TypeArgumentList 泛型 sink、typeof(T) sink、成员图传递（`Dictionary<int, Payload>`、`List<Point>`）、源内类型成员图传播、BCL 成员图不爆炸。断言这些类型的装箱站点文本与编译结果。

### 7.3 跨文件方法迁移 / fixture sln

- 新建 `tests/SampleStructValueSemanticsSolution/`（Lib + App + Tests 三工程 + `.sln`，net10.0，Content Include 进 ProjectTests，仿 SampleRecordSolution/SampleJavaKeywordSolution）：
  - Lib：一组残余可变 struct（坐标/构建器风格：public 字段 + 变异方法、属性 setter、嵌套 struct 字段、struct 数组、object 装箱、接口变异实现、链式赋值、in/readonly 接收者）；一个带合规 Clone() 的 struct；一个事件字段 struct（SV051）；一个 this 逃逸反例（默认不编入 App 编译集或编入独立文件供错误断言）。
  - App：跨文件调用点、数组元素改值、属性返回 struct 后变异。
  - Tests：xunit 断言副本隔离（`b.X=1` 不影响 `a`、数组元素传参不回写、箱内变异不影响原值）。
- 项目级测试 `StructValueSemanticsProjectTests.cs`：`result.Success`、统计计数、中间树文本关键断言、**中间树 `dotnet build` 与 `dotnet test -c Release` 退出码 0**（.NET 半区证明 clone 恒等，即测试同时在「插了 clone 的 C#」上验证值语义行为——若 clone 点错成别名/漏插，测试会红）；fixture 干净工程字节复制用例。

### 7.4 Release build/test 与三工程

- `dotnet build -c Release`（CSharpToJavaConverter.slnx 全绿）。
- `dotnet test -c Release` 三个工程：CSharpToJava.Tests、CSharpToJava.ProjectTests、CSharpToJava.ConversionTests。

### 7.5 residual 闸门（零静默透传）

1. 扫描型测试：对残余集合中每个 struct，断言其属于 Planned/ReusedClone/显式诊断三类之一，不存在「无 plan 无诊断」类型。
2. SV040 反例集：每个 this 逃逸形态断言 pass 失败、退出码非零、无中间目录残留（全量回退）。
3. 既有 fixture 回归：SampleProject、SampleRefOutInSolution、SampleArraySolution、SampleRecordSolution、SampleJavaKeywordSolution 等在启用本 pass 后中间树 build/test 全绿；make-readonly 统计在所有既有 fixture 上**逐字段不变**（C07 不改变 E13 决策）。
4. 转换器侧回归选样：把 StructCloneReturnThisTests / JaggedArrayOutParamLinqTests 中依赖 Java 侧代偿的代表形态沉淀到 fixture 的「预处理后形态」断言（C# 文本含显式 Clone），为 §8 Java 半区收敛做准备（本特性不改转换器，不断言 Java 产物变更）。

### 7.6 CLI / 一致性测试

- 默认关：convert-project 不产生 `.cs2j-struct-value-semantics-src`、无耗时、链序不变。
- `--enable-pass struct-value-semantics` 与 `--struct-value-semantics` 等价；`--list-passes` 显示 Implemented/默认关/目录；preserved 目录注册；摘要与诊断输出；SV030/SV031 回退演练（注入一个只在整树链接期报错的 plan，可用 force/skip 配置构造）；CS2J9001 不再对本 id 触发。
- `ProjectPreprocessorsConsistentTests` 追加类型与目录名断言。

---

## 8. 默认开启验收门槛

默认关落地后，满足以下全部条件才翻 `defaultEnabled: true`：

1. slnx `dotnet build -c Release` 全绿；三个测试工程 `dotnet test -c Release` 全绿（含新单测/fixture/CLI/一致性）。
2. fixture sln 中间树 build + test 全绿；幂等、字节复制、residual 闸门测试全绿。
3. 在 csharp-sdk（训练集）上跑启用扫描：产出 `StructsPlanned/SitesRewritten/SkippedByReason` 分布与 SV040/SV042/SV043 命中清单；**SV040 类命中须逐条人工定性**（真实语言无解 vs 分析器过保守），过保守项修到清零或进入明确的后续里程碑；SKIP 总量与原因可评审。
4. sdk 基线不退化：启用后各既有 fixture 与 sdk 抽样工程的中间树 build/test 结果不劣于未启用；E13/E11 统计零变化；convert-project 总耗时增量可接受（restore/build verify 串行成本纳入 fingerprint 缓存评估，catalog §4 风险 11）。
5. Java 半区验证（可与转换器侧 StructCloneHelper 退役评审联动，但不是本特性合并的硬前置）：fixture 的 Java 产物可编译、双端测试通过；审计「double clone」不增加（C# 显式 Clone 与 Java 侧 W6/W7 插入的叠加点去重表成立）。
6. overrides/逃生口、文档（README/ROADMAP 条目）与 `--list-passes` 描述齐备。

未达门槛前长期保持默认关，并在每个评审周期复跑第 3/4 项，避免 catalog §4 风险 10 的「多套事实标准」固化。

---

## 9. 里程碑

| 里程碑 | 内容 | 预估规模 |
|---|---|---|
| M1 | Core 骨架：Models（Options/Statistics/SV 诊断）、Preprocessor 契约、FormatAndValidate、SyntaxFacts；适配器骨架 + 注册表翻转（默认关、verify 开）+ CLI 开关/目录/指纹接线；空 pass 在链上端到端跑通（零改动字节复制、--list-passes） | Core ~250 行，CLI ~350 行 |
| M2 | Analyzer + Plan：残余集合、BoxingReachability 接线、变异方法集复用、站点全枚举与 plan/TouchedFiles/归属；shim 决策（生成/复用/Infeasible）+ CloneShimGenerator；单测覆盖 §7.1-9 | ~700 行 |
| M3 | CloneInsertionRewriter：VS-1/VS-2/VS-4（含链式展开、数组存取）+ ValueCopyFacts 移植（取消 W4 特判）；单测 §7.1-1~5、7 | ~600 行 |
| M4 | VS-3 防御性接收者提升 + VS-5 装箱/接口/拆箱；SV040/SV041/SV042/SV043 错误族；表达式树/dynamic 跳过；单测 §7.1-6/8/10 与装箱专项 | ~600 行 |
| M5 | 整树回退循环打磨（SV030/SV031 演练）、overrides、BOM/trivia、幂等审计；SampleStructValueSemanticsSolution + 项目级/CLI/一致性测试；Release build/test 全绿；sdk 扫描脚本与 SkippedByReason 报表 | ~400 行 + fixture |
| M6 | sdk 命中定性与分析器收敛、文档（本设计定稿 + plan 文档）、§8 门槛评审；通过后翻默认开（单行） | 文档/数据 |

---

## 10. 风险

1. **过插 clone 的性能/栈风险**：E13 已有「外部 ref 参深变异致热路径帧膨胀、324 层深嵌套测试栈溢出」的先例（[StructAnalyzer.cs:66-76](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs#L66-L76) 的判定注释与 [StructAnalyzer.cs:219-225](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs#L219-L225) 的保守跳过）。缓解：新鲜值判定 + 值参只读跨过程分析默认启用；VS-3 提升仅在变异方法集命中时；sdk 基线统计 clone 点密度，热点类型可经 skip 逃生。
2. **别名误判（错插 clone）**：ref 返回属性/索引器、`ref` 局部、`Unsafe.As`/`Interlocked` 类身份敏感 API 上插 clone 会制造错误且编译能过（测试不一定抓到）。缓解：plan 期对 ref 族一律走「别名不插/SV040」保守路线；别名信息全部来自 SemanticModel，禁止语法猜测。
3. **this 逃逸判定的假阴/假阳**：假阳（误报 SV040）只阻塞转换、可 skip；假阴（漏报）产出看似编译通过但语义错误的 Java——方向性取舍是「宁可假阳」，并以 sdk 命中定性（§8-3）压制假阳率。
4. **整树迭代成本**：共享文件被多 plan 触碰时每轮全量重放+重编译（RR 同款，上限 1024 轮）；大 sln 耗时风险。缓解：站点与归属信息在分析期一次算全，稳态应一轮收敛（仅新错误触发迭代）；保留轮次与耗时统计。
5. **嵌套 struct 字段深拷贝完备性**：字段类型为外部程序集 struct（无 Clone、无源声明）时无法递归；缓解：按 IsUserDefinedStruct 同口径——仅对源内残余 struct 字段递归，外部 struct 字段直接赋值（与 C# 经外部类型自身值语义复制的边界一致），并登记 Info 供审计；枚举/Nullable/E06 tuple struct 各自走既定映射，不调 Clone。
6. **E13/L5 形态耦合**：WithXxx、getXxx()、covariant 接口桥等 E13 产物若被误判为可变点会错插。缓解：残余集合以 `readonly` 修饰符为硬边界，E13 产物全部带 readonly；单测 §7.1-12 与 E13 统计零变化守门。
7. **与 C11-C14 成员去糖的次序**（**2026-09-29 核订：已落地，风险转为核对项**）：member-desugar 锚定本 pass 之后（现状 `RunAfterIds = {struct-value-semantics, closure-lift}`，两者均为 stub，故它在链尾 [PassCatalog.cs:230-241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L230-L241)）；若属性/索引器被先去糖为普通方法，站点形态变化——本 pass 锚位在前是正确方向 ✓。落地后新增的实际核对项：member-desugar 的属性/索引器规则会把属性改写为 `get_X/set_X` + 后备字段（并按 `Properties/PropertyRule.cs:263` 路由成员级 attribute），因此 C07 若先跑则站点形态是**去糖前的属性语法**，两者必须约定「C07 的站点枚举只认 C# 属性/索引器语法，去糖后的方法形由 member-desugar 自行保证值语义不劣化」；在本 pass 落地前该核对以 fixture 断言（§7.1-12 的 E13 共处一树用例可扩为三 pass 共处）。
8. **表达式树/dynamic 跳过的共享工具尚不存在**（catalog §4 风险 8）：v1 内部先实现判定，JK/RR/C07 各持一份的技术债登记，待统一引擎期合并；跳过站点不插 clone 的残余风险由 SV013/SV014 计数可见。
9. **拆箱/接口分派的运行时花样**：显式/隐式实现、泛型接口、变体接口下 box 副本语义细致，VS-5 规则可能欠逼近；以 §7.2 可达性矩阵 + Java 半区 fixture 双保险，欠逼近先 SV040 不硬插。

---

## 11. 并行协调

- **与 E13 make-readonly（已实现）**：C07 是其直接续作，消费其输出树与 internal 分析资产（§3.4）；不回改 E13 行为；E13 统计零变化是硬回归项。ReadOnlyStructPassEngine 多阶段（call-site/field-read/ref-hoist/interface bridge/ctor normalize/disabled region）产出的节点 C07 必须能识别（readonly 修饰符 + WithXxx 形态），不为其打补丁。
- **与 E11 record-readonly（已实现、opt-in）**：record struct 被 E11 转 readonly 后不进残余集；E11 跳过/回退（RR020/RR031）的 record struct 落为普通可变 struct 形态，由 C07 同规则接管；plan/回退/整树基线范式直接复刻 RR，不新造机制。
- **与 C01 ref struct 子项边界**：ref struct 声明、`RefStructByRef<T>` holder、ref 字段链（C01 设计 Q2/Q5/CS2J3303-CS2J3304）全部不属 C07；交汇点（普通 struct 经 ref holder 传递、Span<S>）C07 发 SV042 显式拒绝，等 C01 形态定案后再谈收编。
- **与 C06 unsafe 边界**：指针/fixed/stackalloc/`&` 存储的身份别名与 clone 互斥；含 fixed 缓冲区字段的 struct 不建 plan；unsafe 上下文中的值流站点不插。unsafe-ffm 的 RunAfter 锚点已在 C07 之后，合顺序天然成立。
- **与 C04 struct this 捕获（iterator-lowering）边界**：可变 struct 的实例迭代器/async 方法把 `this` 提升进状态机（装箱捕获），C07 判 this 逃逸 SV040；C04 在链位上晚于 C07，落地后这些方法会被先降级为显式状态机类，SV040 命中面随之收缩——两 pass 不共享代码，以 SV040 清单作为衔接台账，C04 落地后回归该清单。
- **与 C32/C45 闭包提升**：lambda 捕获可变 struct 局部时，显示类持有的是变量还是值决定 clone 点；C07 v1 对「捕获后被外部再赋值/逃逸」按 SV040 处理，纯查询捕获允许（局部副本仍可插 clone）；closure-lift 落地后与 C04 同路径收敛。
- **worktree 合并顺序**：建议 C07 先于 unsafe-ffm 合并（其 RunAfter 锚定本 id，id 常量改名会产生编译期冲突，属预期一次性解突）。**2026-09-29 核订状态同步**：原文列作「同期其它 stub 翻转任务」的 **C02/C03/C04 均已落地入主干**（连同 C01、C08、C11-C14），故本 pass 现在是**唯一的先行者**——翻转时须 rebase 最新 `dev-3.x`，冲突热点文件与原文一致（PassStubCatalog.cs / PassCatalog.cs / PassChainFactory.cs / CliPreprocessChain.cs / Program.cs，另加 PassChainOrderTests.cs）；各自分支只增自己的条目，不合改他人条目；合并以「rebase 到最新主干 + 注册表校验测试 + 链序断言」为门禁。**registry 现状提示**：`member-desugar` 已引用本 pass 的 id 常量（§3.1），翻转时两处引用必须同批改完，否则注册表校验直接抛 `PassRegistryException`。

---

## 12. Worktree 分配

- 分支：`feature/preproc-c07-struct-value-semantics`
- 目录：`.worktrees/preproc-c07-struct-value-semantics`
- base：`30d13211`（`feat(preprocessing): C31 Index/Range 落地 + 已落地 pass 默认开启收口`；2026-09-30 自 f68a783c 更新为 dev-3.x 最新提交，已核实存在）
- 创建命令（在仓库根、PowerShell 下执行；本设计阶段不执行）：

```powershell
git worktree add .worktrees/preproc-c07-struct-value-semantics -b feature/preproc-c07-struct-value-semantics 30d13211
```

- worktree 内交付面遵循 foundation plan「并行开发交付包模板」：Core `StructValueSemantics/`、CLI 单适配器 + 注册表翻转、本设计文档与后续 plan 文档、Core 单测 + fixture sln + Release build/test + residual 闸门 + CLI 测试；不新建支撑工程；不触转换器代码。
