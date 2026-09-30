# C02 Generic 深化预处理器设计：typeof(T) / is-as T / 静态泛型字段

**日期**: 2026-09-28（同日完成代码级评审修订，见 §13）
**范围**: 把注册表 stub `generic-deepen`（catalog C02，P0）从 `Stub` 翻转为 `Implemented`；新增 Core 规则与 CLI 适配器，不修改 C#→Java 转换器代码（转换器侧配合项另立后续 PR，见 §10）
**状态**: **已实现并合入 dev-3.x 主干**（2026-09-30 核实，提交链见「再基线」段；pass 默认关，M4 默认开启评审与 sdk 计数门为挂起后续）
**再基线（2026-09-30）**: 本设计**已在主干实现完成**，无需重新实施。实现提交链（均为 dev-3.x 祖先、75958d7a 已含）：**14fed211**（核心骨架 options/diagnostics/statistics/facts + 共享 ExpressionTreeFacts）→**db9bbd08**（翻转 generic-deepen stub 为 Implemented（C02），补登 closed-metadata/variance-audit stub）→**0c969001**（GD-FIELD 静态泛型字段→holder/accessor，按项目生成文件）→**ecc1376e**（residual 闸门 + SampleGenericDeepenSolution fixture + 项目级端到端测试）。现状：`generic-deepen` 为 Implemented、**默认关**、RunAfter=`generic`、catalogItems `"C02"`（PassCatalog.cs L167-178）；Core 实现位于既有目录 `src/CSharpToJava.Core/GenericPreprocess/`（GenericDeepen* 10 文件），适配器 `Adapters/GenericDeepenPassAdapter.cs`；测试 GenericDeepenTests/GenericDeepenFactsTests/GenericDeepenRegistryTests 随 2026-09-30 Release 基线全绿。2026-09-30 复核：Roslyn 已升级 5.0.0；C11–C14 已合并；被删红测为维护者有意删除。**默认开启评审（M4）与 sdk 计数门**仍为挂起后续。
**对应基线**: 历史代码基线 f68a783c（`feat(preprocessing): register 23 merged-family stub passes`）；实现已合入 dev-3.x（见上提交链）
**关联资料**: `prompts/preprocessor-catalog.md` C02（L269-281）/E10（L30）/C30（L615-626）/C33（L653-665）/§3.3（L898-915）/§4 风险 6/8/9/11（L925-930）/§5（L942-1003）；`.trae/documents/preprocessor-foundation_plan.md` L60-139

---

## 1. 背景与目标

### 1.1 E10 已做 / 未做证据表

E10（`generic` pass，Core 入口 [GenericPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs)）已覆盖 3 个 P0 表达式模式，并为「让转换器看见 token」做了两条隐式补偿；C02 的全部缺口今天要么只统计不改写，要么在转换器侧 fail-fast，要么由转换器内联启发式脆弱代偿。

| # | 模式 | 现状 | 证据 |
|---|---|---|---|
| 1 | `default(T)` | **已做**：安全上下文改写为 `default` 字面量；`==/!=` 与泛型方法推断实参两类上下文保守跳过 | [GenericPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs#L217-L249)（跳过判定 helper L255-300） |
| 2 | `new T()` | **已做**：→ `global::System.Activator.CreateInstance<T>()` | [GenericPreprocess.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs#L307-L338) |
| 3 | `new T[n]` | **已做**：→ `(T[])global::System.Array.CreateInstance(typeof(T), n)` | [GenericPreprocess.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs#L340-L391) |
| 4 | 前向类型参数哨兵 | **已做（隐式补偿）**：对仅出现在泛型转发实参中的方法类型参数，在方法体首注入 `var __cs2j_typeof_T = typeof(T);`，触发转换器注入 leading `Class<T> _cs2j_T` | [GenericPreprocess.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs#L421-L602) |
| 5 | override 兄弟哨兵同步 | **已做（隐式补偿）**：全编译预扫描 override 族，向缺少 `typeof(T)` 的兄弟/抽象（abstract→virtual+抛桩体）注入同款哨兵 | [GenericPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs#L74-L166) |
| 6 | `typeof(T)` | **未做，仅计数**（`TypeOfExpressionsSeen`）：类注释明示「暂不降级（仅统计），避免破坏签名/控制流」 | [GenericPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs#L18)、[#L396-L403](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs#L396-L403) |
| 7 | `is T` / `as T` | **未做，仅计数**（`IsTypeParamSeen`/`AsTypeParamSeen`） | [GenericPreprocessor.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs#L405-L419) |
| 8 | 引用封闭类型参数的**静态字段** | **Core 无规则**；转换器 fail-fast | [FieldTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L112-L118)（`CS2J normalization required: static field referencing enclosing type parameters …`） |
| 9 | 引用类类型参数的静态成员（方法/属性） | Core 无规则；转换器后置校验 CS2J5002 兜底告警（Warning 级诊断；静态方法引用类类型参数 Java 不可表达） | [JavaStaticContextValidationRewriter.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Java/Rewriters/JavaStaticContextValidationRewriter.cs#L124-L146) |
| 10 | `catch (T) where T : Exception` | Core 无规则；转换器 fail-fast | [StatementTransformer.SwitchAndResource.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.SwitchAndResource.cs#L151-L156)（`catch clauses over a type parameter must be lowered by a preprocessor`） |
| 11 | 类型参数声明模式 `x is T v` / switch 中的类型参数模式 | 无前置规则；转换器仅支持具体类型声明模式，未知模式落 `/* TODO: unsupported pattern */ true` 恒真占位（静默错误输出） | [TypeOperationTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L107-L117) |
| 12 | 跨方法/跨文件/嵌套泛型的 token 一致性 | 无系统契约。转换器侧曾有 `DetectTypeOfDefaultUsage`/`MethodLikelyNeedsClassTokens` 双扫描器（见测试注释），现存 [ConversionContext.MethodClassTokenNeedCache](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionContext.cs#L152-L153) 已无任何调用方（全仓 grep 仅剩声明）；相关测试文件 [TrailingClassTokenCallSiteTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/TrailingClassTokenCallSiteTests.cs) 已被掏空（无测试方法，a86e750f 移除），[CrossProjectGenericClassTokenTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/CrossProjectGenericClassTokenTests.cs) 有既存失败——**2026-09-28 实测 4 例中 2 例失败**（其一 `LibProject_GenericMethod_WithLinq_NoSpuriousClassToken`：转换器在 GraphSort.cs L1350-1359 处抛 `NotSupportedException`，见 §7.3） |

转换器对 `typeof(T)`/`is T` 的 Java 形态**已有期望**：`typeof(T)` → `_cs2j_T`（方法签名上的 leading `Class<T>` 参数），`x is T` → `_cs2j_T.isInstance(x)`，封闭类型 → `Rt(...).class`，见 [TypeOperationTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L87-L130) 与 [ConversionContext.GetClassLiteral](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionContext.cs#L441-L479)。即：**Java 侧令牌参数从哪传入，今天的答案是「转换器扫描方法体里的 `typeof(T)` 启发式注入」**；C02 的目标是把这件事从启发式升级为 C# 中间树上的显式、可 .NET 验证、跨项目一致的契约。

### 1.2 命中频率

- 训练集粗扫（2026-09-28，`D:\csharp-sdk`，排除 bin/obj，1296 个 `.cs`）：文本 `typeof(` 共 **2371** 处、`is <大写标识符>` 共 **5803** 处。二者绝大多数为封闭类型，不是本 pass 目标；类型参数口径的精确计数由 E10 已埋点的 `TypeOfExpressionsSeen`/`IsTypeParamSeen`/`AsTypeParamSeen` 给出（[GenericPreprocessDiagnostics.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessDiagnostics.cs#L23-L34) 已在 [GenericPassAdapter.MergeStatistics](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/GenericPassAdapter.cs#L128-L139) 累加，但摘要行未打印）。
- M0 里程碑（§9）第一件事：在 sdk 上跑一次 E10 并输出这三个计数，作为 C02 规则收益与默认开启评审的频率基线；catalog §5.5 同行为「—」（未测），本 pass 负责补上。
- 定性频率：JSON 反序列化簇（`JsonSerializer.Deserialize<T>` / `Activator.CreateInstance<T>` / 嵌套 `List<List<T>>`）与枚举/缓存基础设施几乎必然命中——这是转换器最厚的特判簇（catalog C02 列 20+ 相关测试：ClassToken 系列、`SameTypeErasureConflictTests`、`CrossInheritanceErasureTests` 等）。

### 1.3 目标

在 `generic`（E10）之后新增独立 pass `generic-deepen`：基于 **SemanticModel 全编译符号表**做 Analyze→Plan→Rewrite，把 reified→erased 所需的运行时信息在 C# 侧**显式化**，产物仍是语义等价、`dotnet build -c Release` / `dotnet test -c Release` 全绿的 C#：

1. **token 锚点系统化**：凡方法体（含 override 族、泛型转发链）需要某类型参数运行时 token，在方法入口落地确定性的强类型锚点语句，取代 E10 两条 ad-hoc 哨兵规则的「启发式触发」角色（E10 规则过渡期共存，见 §9 M1）。
2. **`is T`/`as T` 可达**：保留 C# 表达式形态（转换器已能映射），只保证锚点可达；类型参数**声明模式**与 `catch(T)` 显式降级为转换器 1:1 可映射的语句形态。
3. **静态泛型字段/静态成员 holder 化**：把「Java 静态成员不得引用类类型参数」的不可表达形式，降级为 token 键控存储或泛型静态方法访问器，消除 [FieldTransformer.cs:114-118](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L114-L118) 的 fail-fast。
4. **嵌套/递归泛型与跨文件传播**：token 需求按调用图与 override 族闭包传播，调用点与声明点同查一份全编译计划，消灭跨项目不一致。
5. 不可判定站点**显式报错（CS2J11xx）**，禁止注释/占位/原样透传（catalog §4 风险 12 第一原则）。

### 1.4 C02 与 C30 / C33 的边界（不吞并）

三者同属 catalog §3.3「泛型擦除补偿族（E10+C02+C09+C30+C33）」，但是三个独立注册表条目，本 pass 只交付 C02：

| 条目 | 注册表 id | 分工 | 本 pass 关系 |
|---|---|---|---|
| **C02** | `generic-deepen` | 在 **C# 中间树**把运行时类型信息（token 锚点、is-as 判定、静态字段存储、跨文件传播）显式化 | 本文档 |
| **C30** closed metadata | 未来条目（现共用 stub 描述，翻转 C02 时须把 descriptor 的 `catalogItems` 收窄为 `"C02"`，C30 另立 stub 或独立 id，见 §3.5） | 在 **Java 反射侧**消费闭型 `java.lang.reflect.Type`（`TypeHelper.closedCacheKey/createConverterForClosedType`、`ConcurrentHashMapHelper.putIfAbsentUntyped` 均已在 compat） | 一体两面：C02 产出 token，C30 消费闭型；**C02 不发射任何闭型注册代码、不碰 Java、不做 PatchClosed\* 代码考古**（catalog §4 风险 6 是 C30 的前置阻断，不是 C02 的） |
| **C33** 型变/约束审计 | 未来独立条目 | `out/in` 声明型变、`struct/new()` 约束的审计/使用-site 改写；现状为丢弃仅告警 CS2J1003（[ClassTransformer.cs:183-192](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L183-L192)） | 审计型，与 C02 同批不耦合；C02 不删除、不重解释约束子句 |
| C09 ThreadStatic（同族静态字段） | `thread-static` | `[ThreadStatic]`/`ThreadLocal` 显式化 | 静态字段语义面共享，所有权切分见 §11 |

---

## 2. 关键设计决策表

| 决策点 | 结论 | 备注 |
|---|---|---|
| 总体架构 | 全编译 Analyze→Plan→Rewrite 三阶段 + 逐文件 rewriter + 跨文件生成（holder），复刻 JavaKeywords/RecordReadOnly 的计划与回退骨架、Tuple 的生成文件分发骨架；不复用 E10 的「单文件 rewriter + 一个预扫描 HashSet」形态（E10 保留不动，C02 在其后独立成 pass） | §4 |
| token 在 C# 侧的存在形态 | **C# 里 token 永远是 `System.Type`（`typeof(T)` 合法 C#）**；不引入 `Class<T>` 之类 Java 概念。三级显式化分级落地：**L0 方法体锚点语句**（v1 主形态）→ **L1 族/调用图闭包同步锚点**（v1）→ **L2 显式 `System.Type` 形参 + 全调用点传 `typeof(T)`**（后续里程碑，需转换器协同，见 §9 与开放问题 Q1） | §4.3 GD-ANCHOR |
| 锚点规范形 | 方法块首按类型参数声明序插入：`global::System.Type __cs2j_token_<T> = typeof(<T>);`（强类型、确定性命名、`__cs2j_` 保留前缀）；识别 E10 既有 `var __cs2j_typeof_<T> = typeof(<T>);` 为等价锚点，不重复注入；表达式体方法先包块（E10 已建立的先例） | 与转换器现有「方法体扫描 typeof(T)」启发式**超集兼容**，L0/L1 落地不需要转换器改动 |
| `is T` / `as T`（二元表达式） | **保留 C# 原表达式不改写**：转换器已映射为 `_cs2j_T.isInstance(x)` / 三元判定（[TypeOperationTransformer.cs:87-106](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L87-L106)）；C02 只把该方法计入 token 需求并保证锚点存在。值类型 T 语义（装箱 isinst）由 C# 编译与 CLR 保证，不在 C# 侧改写成 `IsInstanceOfType`（会引入装箱/可空差异） | GD-ISAS-A |
| 类型参数声明模式 `x is T v` | 语句层降级，单次求值：`var __cs2j_pat = x; if (typeof(T).IsInstanceOfType(__cs2j_pat)) { T v = (T)(object)__cs2j_pat; <体> }`；`(T)(object)` 对引用 T 恒等、对结构 T 正确拆箱；不可提升上下文（字段初始化器/特性实参/查询子句）→ CS2J1104 显式报错 | GD-ISAS-B；与 C03/C37 的分工：仅处理右类型解析为 `ITypeParameterSymbol` 的模式，具体类型模式不属本 pass |
| `catch (T)` | → `catch (global::System.Exception __cs2j_e) when (typeof(T).IsAssignableFrom(__cs2j_e.GetType())) { T e = (T)(object)__cs2j_e; <原体> }`，过滤器不匹配时自然重抛（保 CLR「T 及派生类」匹配语义与栈）；C# 编译器已保证 T 带 Exception 约束 | GD-CATCH；非类型参数的 `catch-when` 是 C03 范围 |
| 静态泛型字段（字段类型引用类类型参数） | 两形按用途选型，均为纯 BCL 可编译 C#：① **token 键控 holder**（可变/多次赋值/有状态字段，默认形）：在同编译单元生成非泛型 holder 小类 + `Dictionary<Type, object>` 静态存储（**加锁 `lock` 双检懒初始化**；键为各 token 的 `AssemblyQualifiedName` 拼接串）+ 泛型访问器，读写点改写为访问器调用，保证「每个闭型一份」与 C# 语义一致——**已核实 [TypeMappings.json](file:///d:/code/cs2j/config/TypeMappings.json) 无 `System.Collections.Concurrent`/`ConcurrentDictionary` 任何映射，而 `System.Collections.Generic` 有命名空间级映射（L8228-8229 → `dotnet.System.Collections.Generic`），故存储选加锁 `Dictionary` 而非 `ConcurrentDictionary`**（见 §10 风险 11 核实结论）；② **泛型静态方法访问器**（`static readonly` 单次初始化且无赋值的单例形，如 `Empty = new Holder<T>()`）：字段 → `static TField __cs2j_get_<F><T>(System.Type __cs2j_token_T)`，访问点改写——转换器已有此形的内联处理（[StaticGenericFieldTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/StaticGenericFieldTests.cs#L31-L35)） | GD-FIELD；**不采用转换器现存的「降为实例字段」窄形**（按实例共享 ≠ C# 按闭型共享，语义不等价，仅 `Type` 型字段等无 T 字段类型的窄场景被其覆盖，见 [StaticFieldRuntimeClassTokenTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/StaticFieldRuntimeClassTokenTests.cs#L24-L31)） |
| 引用类类型参数的静态方法/属性 | **v1 只改写静态方法**：为方法引入自有类型参数（确定性名 `__cs2j_T`，碰撞则序号后缀），全编译调用点补显式类型实参；**静态方法在 C# 不可 override，无签名族手术面**（`static abstract` 接口成员与已带自有类型参数的方法 → CS2J1102 报错跳过）。**静态属性 v1 显式报 CS2J1102**：属性声明无类型参数载体，属性→访问器化属 C11 member-desugar 语法面，待其落地后协同放开（禁止静默透传） | GD-STATIC；沿用 catalog §3.2 横切约束「改签名必须基于全编译符号表」；静态方法无族（C# 不可虚），风险面远小于 §10.2 的族改写评估 |
| 调用链签名策略 | v1（M1-M4）**不改托管签名**，只做 L0/L1 锚点与静态成员/字段结构性降级；L2（普通实例泛型方法也显式加 `System.Type` 尾参）列为 M5+ 选项，先出原型验证转换器协同与开销，再决定是否替换锚点契约——避免一次性对全树调用点做签名手术 | 开放问题 Q1 |
| 嵌套/递归泛型 | token 需求按「方法体直接使用 + 泛型转发 + override 族 + 字段 holder 访问器」闭包传播；`Outer<T>.Inner<U>` 的静态访问同时取 T/U 锚点；调用点实参为类型参数时由被调方计划反向要求调用方锚点 | GD-NEST |
| 表达式树内判定 | 提取共享谓词 `ExpressionTreeFacts.IsExpressionTreeLambda`（现内联在 [JavaKeywordSyntaxFacts.IsExpressionTreeLambda](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/JavaKeywordSyntaxFacts.cs#L55-L63)）到 Core 共享位置（建议 `Analysis/ExpressionTreeFacts.cs`），JavaKeywords 改为引用；**表达式树 lambda 内禁止改写 `is T`/模式/注入锚点到 lambda 体内**（会改变树节点构成）；方法级锚点语句插在 lambda 之外安全；不可避站点 → CS2J1110 | catalog §4 风险 8 的共享判定要求 |
| dynamic / 不可绑定站点 | 不猜测、不改写；`CandidateReason.Dynamic`/符号不可解析且无语法保守判定的站点 → CS2J1101/1106 | 与 JK-8 同策略 |
| 支撑库 | **不新增支撑库工程**，descriptor `SupportLibrary = None`；holder/访问器是按用户类型生成的源码，经 Tuple 同款「逐项目生成文件 + csproj Compile 接线」分发（[TuplePassAdapter.DistributeStructFiles](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/TuplePassAdapter.cs#L122-L336) 为范本），不进 `PassSupportLibrary` 枚举与支撑工程过滤 | §3.4 |
| 与 E10 的关系 | 不新建通道族：Core 代码放 `src/CSharpToJava.Core/GenericPreprocess/`（新文件，`GenericDeepen*` 前缀）；注册表层是独立 id、独立中间目录、RunAfter=`generic`。E10 的两条哨兵规则在 M1 后由本 pass 的锚点计划取代其触发职责，但 E10 代码删除/默认关闭是后续独立 PR | §9 |
| C#→Java 转换器 | 本特性实施 PR **不改转换器**；锚点为转换器现有启发式的超集输入。转换器侧去启发式/识别 L2 尾参/删除 FieldTransformer fail-fast 均为后续 PR，以本 pass 默认开启为前置门 | §10 |

---

## 3. 注册表与链位契约

### 3.1 描述符

翻转后在 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) 的 `Implemented` 表中、紧随 `generic` 条目之后插入（稳定拓扑序要求注册顺序即链序，顺序有注册表测试锁死）：

```text
Id                          = "generic-deepen"
DisplayName                 = "Generic erasure deepening"
IntermediateDirectoryName   = ".cs2j-generic-deepen-src"
Stage                       = PassStage.Abi
Status                      = PassStatus.Implemented
DefaultEnabled              = false（初版 opt-in；过 §8 门槛后翻 true）
RunAfterIds                 = { "generic" }
SupportLibrary              = PassSupportLibrary.None
NeedsSemanticModel          = true
VerifyBuildByDefault        = true
VerifyTestsByDefault        = true
CatalogItems                = "C02"（从 stub 的 "C02, C30, C33" 收窄，见 §3.5；既有 Implemented 条目均未设置 catalogItems（null），本条目刻意显式设置 "C02" 以免被误读为已覆盖 C30/C33）
Description                 = "Make reified generic runtime information explicit for Java type erasure (typeof(T)/is-as T/static generic fields)."
```

两处落地注意（评审核实）：① 既有 Implemented 条目的 `IntermediateDirectoryName` 取各自门面类的常量（如 `ProjectGenericPreprocessor.IntermediateDirectoryName`）；本 pass 不建门面（§5.1），改为 `GenericDeepenPassAdapter` 上的 `public const string IntermediateDirectoryName = ".cs2j-generic-deepen-src";`，`PassCatalog` 引用该常量（同程序集，CLI→CLI 合法）；② `PassIds` 是 `internal static class`（[PassCatalog.cs:6-24](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L6-L24)），新增常量无可见性问题。

链位语义：`unsigned → generic(E10) → generic-deepen(C02) → record-readonly / array-method / make-readonly → eliminate-goto → java-keywords → 主转换`。catalog 要求「必须在 E09 Unsigned 之后」由 E10 的既有位置传递满足；C02 不重排任何既有 pass。

### 3.2 Stub→Implemented 翻转时改动的文件

| 文件 | 改动 |
|---|---|
| [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L111-L120) | 删除 `GenericDeepen` 的 stub 描述符条目（L111-120）；**保留 `Ids.GenericDeepen` 常量**——[attribute-normalize stub 的 RunAfter 引用了它](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L188)。建议把常量迁到 [PassIds](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L6-L24)（`public const string GenericDeepen = "generic-deepen";`），attribute-normalize 的引用改为 `PassIds.GenericDeepen`，stub 表不再保留 id 常量 |
| [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) | 按 §3.1 插入 Implemented 描述符；`PassIds` 增常量 |
| `src/CSharpToJava.CLI/Preprocessing/Adapters/GenericDeepenPassAdapter.cs` | **新增**适配器（§5.1） |
| [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L91-L218) | `CreateAdapter` switch 增 `PassIds.GenericDeepen => new GenericDeepenPassAdapter(...)`。**不进 `BuildLegacyToggles`**（无历史开关）：缺省启用态取 descriptor，通用 `--enable-pass/--disable-pass generic-deepen` 即可控制 |
| `src/CSharpToJava.Core/GenericPreprocess/GenericDeepen*.cs` | 新增 Core 组件（§4） |
| `src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs` | 新增共享谓词；JavaKeywords 改引用（一处小重构） |
| 测试 | 注册表顺序/`--list-passes` 状态断言、Core 单测、项目级 fixture、residual 闸门、CLI 测试（§7）；[ProjectTests.csproj](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj) 增 fixture 的 Content/链接。**需同步更新的既有锁定测试（评审核实）**：[PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs)（`Registry_order_matches_the_locked_historical_chain` 的 16-id 锁定链 → 17 id，generic 之后、record-readonly 之前；默认启用集合断言不变——本 pass 默认关）、[PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs)（stub 计数 23 → 24：删 generic-deepen、补登 C30/C33 两个 stub，见 §3.5）；[PassListFormatterTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassListFormatterTests.cs) 行数按注册表自动派生，无需改 |

不需要改的：`Program.cs` 的指纹逻辑（[L1765-1774](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L1765-L1774) 已按注册表全集自动生成 `pass:generic-deepen=on/off` 令牌，翻转造成一次性缓存失效，符合预期）、保留目录（由已执行 pass 的 `IntermediateDirectoryName` 驱动）、通用 verb（`preprocess --pass generic-deepen` 自动可达；stub 启用即 CS2J9001 的拒绝路径 [Program.cs:3458-3465](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L3458-L3465) 翻转后自然消失）、`ProjectPassRunner`（标准骨架 + `AfterFilesAsync` 已够用）。

### 3.3 中间目录与验证

- 中间目录 `.cs2j-generic-deepen-src`，输入为 E10 产物树（`opts.Source` 由链接管），输出再交下一环。
- `VerifyBuild/VerifyTests` 默认开（与 E10 一致），经适配器属性与 runner 的 [VerifyBuildAndTestsAsync](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L102-L107) 生效；验证失败映射为 CS2J1130/1131 诊断；`DeleteBuildOutputsAfterRestore = true`（同 GenericPassAdapter）。
- 成本面遵守 catalog §4 风险 11：不多环复制、不绕开指纹；本 pass 只新增「一环一棵树」，与既有 15 环同成本模型。

### 3.4 支撑库与生成文件

- 无支撑库工程：不新增 `PassSupportLibrary` 枚举值、不触发 [PassChainFactory.IsExecutedSupportLibraryProject](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L225-L235) 过滤。
- holder/访问器作为**生成用户源码**写入各使用项目目录（文件名形 `_Cs2jGenericHolders.<ProjectName>.cs`，多项目时仿 Tuple 带项目名后缀），并经同款 `Compile Include/Link` csproj 接线；它们是被转换项目的一部分，正常进入 Java 转换，不做指纹豁免。

### 3.5 C30/C33 的注册善后

翻转 C02 时 `catalogItems` 只写 `"C02"`。C30 与 C33 保留为可见的未来条目：**评审决定当次补登两个独立 stub**（`PassStubCatalog` 新增 id `closed-metadata`（RunAfter `generic-deepen`，`PassStage.Abi`，catalogItems "C30"）与 `variance-audit`（RunAfter `generic`，`PassStage.Declarative`，catalogItems "C33"），默认关，描述注明 P2/P3 与阻塞项），使 `--list-passes` 仍能看到三兄弟且不会误认为 C02 已覆盖它们；stub 总数 23 → 24（-1 generic-deepen +2 新 stub，PassStubCatalogTests 同步）。**不得**让 C02 描述符继续标注三者。

### 3.6 默认关→默认开门槛

见 §8。初版 `defaultEnabled: false`，opt-in 路径：`convert-project --enable-pass generic-deepen` 或 `preprocess --pass generic-deepen -s … -d …`。

---

## 4. Core 组件设计

目录：`src/CSharpToJava.Core/GenericPreprocess/`（与 E10 同命名空间 `CSharpToJava.Core.GenericPreprocess`，文件名 `GenericDeepen*` 前缀隔离）。

### 4.1 文件清单

| 文件 | 职责 |
|---|---|
| `GenericDeepenOptions.cs` | 选项 record：`RewriteTypeOfAnchors`/`RewriteIsAsPatterns`/`RewriteCatchTypeParam`/`RewriteStaticMembers`/`RewriteStaticFieldHolders`/`PropagateNestedTokens`（默认全开）、`Strict`（残余即错误）、`ReportInfo`、`OverridesPath`（force/skip 计划，默认空）、`ExpressionTreePolicy`（跳过/报错） |
| `GenericDeepenStatistics.cs` | 计数（实现 runner 通用投影所需形状；runner 只依赖适配器，Core 统计可自有字段，与 E10 同现状）：`FilesScanned/FilesTransformed/FilesSkippedClean`、`TokenAnchorsInserted`、`AnchorsReconciledFromE10`、`IsAsSitesCovered`、`TypeParamPatternsRewritten`、`CatchTypeParamsRewritten`、`StaticMethodsRewritten`、`StaticFieldHoldersGenerated`、`StaticFieldAccessesRewritten`、`CallSitesUpdated`、`NestedPropagations`、`ExpressionTreeSitesSkipped`、`PlansSkipped/SkippedByReason`、`ResidualsByRule` |
| `GenericDeepenDiagnostics.cs` | `GenericDeepenSeverity`、`GenericDeepenRule`（Anchor/IsAs/Catch/StaticMember/StaticField/Nested/Residual）、`GenericDeepenDiagnostic(Severity, Rule, Code, Message, FilePath?, Line?)`、码常量表（§6） |
| `GenericDeepenResult.cs` | `record GenericDeepenFileResult(string OutputCode, bool Changed, IReadOnlyList<GenericDeepenDiagnostic> Diagnostics, GenericDeepenStatistics Statistics)`；整树结果另含 `GeneratedFiles` 与 `ResidualReport` |
| `GenericDeepenSyntaxFacts.cs` | 锚点名/哨兵识别（`__cs2j_token_`、E10 `__cs2j_typeof_`）、类型参数判定（语义优先 + E10 同款保守语法回退，直接复用/提取 E10 私有 helper，禁止两份漂移）、确定性命名与碰撞后缀、trivia/注释规范 |
| `GenericDeepenAnalyzer.cs` | **全编译一次**：① 类型参数符号表（类级/方法级 `ITypeParameterSymbol` → 容器、约束、序号）；② token 需求图：方法体直接使用（`typeof(T)`/`is T`/`as T`/`new T[n]` 经 E10 后的 `Array.CreateInstance(typeof(T),…)`/模式/catch/holder 访问）、泛型转发边、override 族边（沿用 E10 `PrescanOverrideSiblings` 的族收集但产出符号级计划）；③ 静态成员/字段分类（F0 常量/F1 单例只读/F2 有状态/ThreadStatic 标记排除/反射与序列化名风险标记）；④ 调用点索引（`IMethodSymbol` → 全部源内调用语法，含跨文件、跨项目同编译）；⑤ 表达式树区间标注；⑥ extern/接口虚契约/抽象族闭合性 |
| `GenericDeepenPlan.cs` | 计划类型：`TokenAnchorPlan`（方法符号 + 有序锚点参数集 + TouchedFiles）、`TypeParamPatternPlan`、`CatchTypeParamPlan`、`StaticMemberPlan`（族级：签名改写 + 调用点集）、`StaticFieldHolderPlan`（字段符号、形选型、holder 生成需求、全部读写点、TouchedFiles）、`NestedTokenPlan`；每计划带 `Id/Outcome(Active|Skipped)/FailCode/FailReason/OwnsTouch`，供回退与 skip/force |
| `GenericDeepenRewriter.cs` | `CSharpSyntaxRewriter`：按本文件 active 计划逐节点改写——插锚点、降级声明模式/catch、静态成员签名与调用点、字段读写点；只用语义绑定命中，禁止文本替换；改写节点挂 `RewriteAnnotation` |
| `GenericDeepenHolderGenerator.cs` | 生成 holder 非泛型小类、`Dictionary<Type,object>` 加锁存储（§2 核实结论）、泛型访问器（初始化表达式中对 T 的引用按 E10 同款规则归一：`new T()`→`Activator.CreateInstance<T>()`、`typeof(T)`→token 形参）；产出按项目归属的文件内容与 Compile 接线清单（归属算法复用 Tuple 已验证的项目引用图/可达性逻辑，可考虑提取共享 helper 但不强制） |
| `GenericDeepenResidualScanner.cs` | 后置残余自检（§4.5） |
| `GenericDeepenPreprocessor.cs` | 对外入口：`BindCompilation(Compilation)`/`Analyze()`/`ExcludePlans(ids)`/`Plans`/`RewriteFile(code, SemanticModel, stats, diagnostics)`/`GeneratedFileSpecs`/`ResidualScan(Compilation)`/`FormatAndValidate`（三候选：改动区格式化→整文件格式化→原文，与 JK/RR 同款） |
| `Analysis/ExpressionTreeFacts.cs`（Core 共享） | `IsExpressionTreeLambda(SemanticModel, LambdaExpressionSyntax)` 共享谓词（从 JavaKeywords 提取） |

编译构造沿用 runner 标准：`CSharpParseOptions(LanguageVersion.Latest)` + `NETCOREAPP` 预处理符号 + 合成 global usings（`ImplicitGlobalUsings.BuildForProject`，Tuple 范式）+ `GetBasicReferences()` + `allowUnsafe: true` DLL 编译；逐文件 rewriter 之外，分析与残余扫描都跑在这同一棵全编译上。

### 4.2 三阶段数据流

1. **Analyze（整树一次）**：`CompilationReadyAsync` 时 `BindCompilation` → `GenericDeepenAnalyzer` 产出计划候选；计划按符号确定性排序（文件路径+span），保证跨运行快照稳定。
2. **Plan（规则级原子）**：每条规则产计划；不可行站点直接置 `Skipped + FailCode`；静态成员按族（基方法符号 OriginalDefinition）聚合，族内任一文件不在编译/extern/虚契约不可闭合则整族 skip 并 CS2J1102；overrides 文件路径加入 TouchedFiles。
3. **Rewrite（逐文件）+ Generate（AfterFiles）**：标准骨架 `CompilationReadyAsync → RewriteFileAsync × N → AfterFilesAsync`；holder 文件在 `AfterFilesAsync` 落盘与 csproj 接线（Tuple 范式）；随后 restore/build/test 验证。
4. **回退**：与 JK/RR 一致——整树 build 相对基线新增错误时，按 TouchedFiles/OwnsTouch 归属唯一计划，排除后从原始副本重放收敛；不可归属 → CS2J1100 当次全回退。锚点插入为「只加语句」，稳态不应产生编译错误；签名/holder 改写是回退通道的主要服务对象。

### 4.3 规则全集（GD-x）

- **GD-ANCHOR-1（L0）**：方法体直接出现 `typeof(T)`/`is T`/`as T`/类型参数模式/`catch(T)`/经 E10 后的 `Array.CreateInstance(typeof(T),…)`/`Activator.CreateInstance<T>()` 之一，或经 GD-STATIC/GD-FIELD/GD-NEST 计算需要 token 时，块首按序插 `global::System.Type __cs2j_token_<T> = typeof(<T>);`；表达式体先包块（含 E10 已证的 override 表达式体场景）。锚点变量被转换器当作普通局部（其初始化器即 `typeof(T)`，被现有扫描识别），不改变任何可执行语义（多一次 Type 句柄读取，运行时无副作用差异）。
- **GD-ANCHOR-2（L1 族同步）**：override/abstract/virtual/interface 族按基方法 OriginalDefinition 分组（接口实现族经 `INamedTypeSymbol.FindImplementationForInterfaceMember` 收集）：族内任一实现需要某参数 token，则族内全部成员（含抽象声明→按 E10 先例改 virtual+抛桩体的既有形态保留决策，见开放问题 Q2）取得同序锚点（按类型参数**序位**对齐，各成员用自身形参名，不按名字匹配）；取代 E10 `PrescanOverrideSiblings`（[GenericPreprocessor.cs:74-166](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs#L74-L166)，选项开关 `SyncTypeOfForOverrideSiblings`）的字符串名集合做法。
- **GD-ANCHOR-3（前向/转发）**：方法把自有类型参数仅经泛型调用实参转发给已判明需要 token 的被调方时，本方法补锚点（按被调方第 j 个类型参数的需求 ↔ 调用方第 i 个实参的映射，工作表法迭代至不动点）；取代 E10 的前向哨兵逻辑（`GenericSyntaxRewriter.VisitMethodDeclaration` L421-602 区段，选项开关 `InsertTypeOfForForwardedTypeParams`——注意这是**选项字段名**而非方法名）。E10 同名旧哨兵在本 pass 中被识别为已满足锚点（去重、不双注入）。
- **GD-ISAS-A**：`x is T`/`x as T`（右类型为 `ITypeParameterSymbol`）不改写表达式；计入需求并由 GD-ANCHOR 保锚点。
- **GD-ISAS-B**：类型参数**声明模式**（`is T v`、`is {…}` 中含类型参数的复合形态仅支持单层声明/弃元，and/or/属性/列表模式属 C37，遇之报 CS2J1104 交由姊妹 pass）按 §2 单次求值形态语句化；`switch` 语句中的类型参数 case 同形降级为 if-链（与 C03 共用归约形态时仅取「类型参数 case」子集，若 C03 尚未落地则本规则只处理 `if` 上下文，switch case 报 CS2J1104，不抢语法面）。
- **GD-CATCH**：`catch(T)` → 过滤式 catch（§2）；原 catch 的 `when` 不存在（类型参数 catch 不允许自带过滤），无 C03 交叉。
- **GD-STATIC**：静态方法引用**类级**类型参数 → 提升为方法自有类型参数（`__cs2j_T` 命名、按类参数序），全编译调用点补显式实参 `<...>`；静态方法在 C# 不可虚，无 override 族手术面；`static abstract`/`static virtual` 接口成员、已带自有类型参数的静态方法、静态属性（属性无类型参数载体，待 C11 协同）→ CS2J1102 显式报错跳过；方法级类型参数原已存在的只补差异；调用点在另一程序集（外部符号）不可改写 → CS2J1106。
- **GD-FIELD-1（holder 形）**：有状态/可写/多闭型共享字段 → 生成非泛型 holder + `Dictionary<Type,object>`（加锁双检懒初始化，键 = 各 token `AssemblyQualifiedName` 拼接）+ 泛型 get/set 访问器；读写点 `F` → `__cs2j_get_F<T>(typeof(T))` / `__cs2j_set_F<T>(typeof(T), v)`；初始化迁移到「字典未命中工厂」内，工厂内 T 引用按 token 归一。`[ThreadStatic]` 字段不属本规则（交 C09，见 §11）。
- **GD-FIELD-2（单例访问器形）**：`static readonly` 且仅初始化一次、全文无赋值、初始化为可整体搬运表达式的字段 → 泛型静态方法访问器（转换器已有映射先例）；初始化含多语句/缓存副作用/字段间依赖链时回退 GD-FIELD-1。
- **GD-FIELD-3（排除/报错）**：字段经反射按名访问、出现在 `[JsonPropertyName]` 等按名契约、`FixedBuffer`、extern 持有、可见性跨程序集且调用点不全可见 → CS2J1103（可 overrides.skip 该字段所在类型并退化为人工处理，但**不允许静默透传**）。
- **GD-NEST**：嵌套泛型 `Outer<T>.Inner<U>`、泛型类泛型方法、递归类型（`Node<T> Next`）、`Dictionary<K,List<T>>` 形实参传播：按符号计算每个静态访问/构造/泛型调用点所需 token 闭包，在调用方落锚点并在需要处显式传 `typeof(T)`（为 L2 预留；L0/L1 阶段体现为锚点完备性与残余闸门）。
- **GD-EXPRTREE**：表达式树 lambda 区间内不插不改（§2）；方法级锚点在 lambda 外；无法绕开的改写需求 → CS2J1110 计划跳过。

### 4.4 全编译符号表要点

- 一切「类型参数与否」「同族/调用点/字段读写点」判定以 `SemanticModel.GetSymbolInfo/GetTypeInfo/GetDeclaredSymbol` 与符号比较（`SymbolEqualityComparer.Default`）为准；语法启发式仅在单文件无模型模式兜底（保守，只用于 GD-ANCHOR 的插入判定，不用于签名/holder 改写）。
- partial 类型多部件按符号天然合并；跨文件调用点必须在同一 `CSharpCompilation`（CLI 整树模式满足；单文件 verb 模式下凡 TouchedFiles 越界的计划一律排除 + CS2J1190，仿 JK090）。
- 命名确定性：生成名只依赖符号显示名/序号/源 span，禁用计数器随机名（快照稳定、防与用户标识碰撞——`__cs2j_` 前缀保留，碰撞再追加 2/3 序号）。

### 4.5 residual 后置自检（闸门）

重写后对整树再编译并扫描，残余计数（strict 下 Error，否则 Warning + 统计）：

1. 引用类类型参数的静态字段声明：**必须为 0**（对齐 FieldTransformer fail-fast 消失目标）。
2. `catch (T)`：0；类型参数声明模式残留：0。
3. 含 `is T`/`as T`/`typeof(T)`-token 用途但无对应可见锚点、或 override 族锚点不一致的方法：0。
4. 仅前向转发而无锚点的类型参数：0。
5. 表达式树区内违规改写节点：0（结构性自检）。
6. 生成 holder 的读写点无裸字段残留：0。

残余结果进 `GenericDeepenStatistics.ResidualsByRule` 与摘要行；闸门测试直接断言计数（§7）。

---

## 5. CLI 适配器与集成

### 5.1 GenericDeepenPassAdapter

新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/GenericDeepenPassAdapter.cs`（预计 120-200 行，大于普通适配器，因持有 Analyze/Plan/生成文件状态），仿 [GenericPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/GenericPassAdapter.cs) + [TuplePassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/TuplePassAdapter.cs) 混合形态：

- `Descriptor => PassCatalog.ImplementedById(PassIds.GenericDeepen)`；`VerifyBuild/VerifyTests => true`；`DeleteBuildOutputsAfterRestore => true`；构造签名仿 [GenericPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/GenericPassAdapter.cs#L23-L37)（`options, verbose, filterUnsupportedProjects, compatNamespacePrefixes, verifyBuild, verifyTests` 六参）。
- `CompilationReadyAsync`：`_preprocessor.BindCompilation(compilation); _preprocessor.Analyze();`（计划一次成型，skip/force 在构造适配器时由 options 应用 `ExcludePlans`）。
- `RewriteFileAsync`：调 `RewriteFile`，Changed=false 返回 null（runner 字节级复制）；诊断补 RelativePath；统计合并。
- `AfterFilesAsync`：取 `GeneratedFileSpecs`，按 Tuple 范式落盘 holder 文件 + csproj Compile 接线（项目引用图/归属逻辑可先内部复制最小实现，后续与 Tuple 提取共享）。
- `OnRestoreFailed/OnBuildFailed/OnTestsFailed` → CS2J1130/1131；`HasErrors`/`BuildChainSummaryLine`（打印锚点/is-as/静态字段/holder/残余计数）/`BuildChainDiagnosticLines`（Info 仅 verbose）。
- 不新增 `ProjectGenericDeepenPreprocessor.cs` 门面类（foundation 交付包模板允许「一个适配器文件」；52 处测试依赖的是旧 15 个门面，本 pass 无历史门面）；测试一律走 runner + 通用 `preprocess` verb。

### 5.2 PassChainFactory / 选项

- switch 增 case；**v1 从简：`new GenericDeepenOptions()` 直接构造**（不增 `GenericDeepenOptionsFactory` 延迟加载位，v1 无外部配置面；后续需要时再仿 record-readonly 的 lazy factory 模式加）。
- 无 legacy toggle：`BuildLegacyToggles` 不动；默认态由 descriptor 决定，`--enable-pass/--disable-pass generic-deepen` 与 `--pass generic-deepen` 全经 [PassPlanResolver](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassPlanResolver.cs) 生效。

### 5.3 verify 默认、指纹与保留目录

- verify：descriptor `VerifyBuildByDefault=true`、`VerifyTestsByDefault=true`（与 E10 相同的最严档）。
- 指纹：注册自动覆盖，令牌 `pass:generic-deepen=false→true` 翻转时一次性失效（接受）；holder 生成文件是项目源码而非支撑库文件，正常进指纹快照。
- 保留目录：`.cs2j-generic-deepen-src` 由「实际执行 pass 集合」自动纳入 preserve，无需手写注册。

### 5.4 独立可达性

- `preprocess --list-passes`：翻转后显示 `generic-deepen | Abi | Implemented | default off/on | .cs2j-generic-deepen-src`。
- 单文件模式限制同 JK：跨文件计划（族/调用点/holder）排除并出 CS2J1190。

---

## 6. 诊断策略

新增 CS2J11xx 码段（转换器侧泛型码段为 CS2J1001/1002/1003，pass 基建为 CS2J9001/9002，本段不与既有码冲突；码位只追加不改号）。MSBuild 格式：`文件(行,列): warning/error CS2J11xx: [规则/符号限定名] 描述`。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J1100 | Error | 整树验证新增错误且无法归属单一计划 → 本次运行全部回退（仿 JK030/RR030） |
| CS2J1101 | Warning（strict 下 Error） | token 站点不可判定（dynamic/绑定不可解析），计划跳过；消息含站点与建议改写形 |
| CS2J1102 | Warning/Error | 静态成员族不可闭合（extern/接口虚契约/跨程序集调用点不可见/抽象族无实现可挂锚点），族计划跳过 |
| CS2J1103 | Warning/Error | 静态字段 holder 不可行（反射按名、序列化名契约、ThreadStatic 交 C09、跨程序集可见性） |
| CS2J1104 | Warning/Error | 类型参数模式/catch(T) 位于不可语句提升上下文（字段初始化器、特性实参、查询表达式）；或落入 C37 复合模式范围 |
| CS2J1105 | Warning/Error | catch(T) 降级的约束前提不满足或过滤形与源语义不可证（理论不应到达，防御性） |
| CS2J1106 | Error | 调用点跨编译/外部程序集，签名改写无法闭合（GD-STATIC/GD-NEST） |
| CS2J1110 | Info（strict 下 Warning） | 站点位于表达式树 lambda，按策略跳过（force 不解除） |
| CS2J1120 | Warning / strict Error | residual 闸门发现残余（§4.5 各计数 > 白名单 0） |
| CS2J1130 | Error | 中间树 `dotnet restore/build` 失败 |
| CS2J1131 | Error | 中间树 `dotnet test` 失败 |
| CS2J1190 | Warning | 单文件模式：计划非自包含（跨文件族/调用点/holder），排除 |
| GD001 | Info | 信息明细（锚点插入、字段→访问器、调用点更新计数与符号名），仅 verbose 输出 |

原则（对齐 catalog §4 风险 12）：**不可判定即显式诊断，禁止 `/* TODO */`、注释透传、原样流入 Java**；Warning 可经 `--strict` 升级为 Error；skip 仅经 overrides 按类型/字段限定名显式指定且仍留 Info 痕迹。

---

## 7. 测试计划

### 7.1 Core 单测

新增 `tests/CSharpToJava.Tests/GenericDeepenTests.cs`（内联源码 → `CSharpCompilation`（带 SemanticModel，复用/提取 GenericPreprocessTests 的编译 helper）→ Bind/Analyze/RewriteFile → 文本断言 + 输出可编译断言）：

- GD-ANCHOR：直接 `typeof(T)`/`is T`/`as T` 方法块首锚点形态与顺序；表达式体包块；E10 `__cs2j_typeof_T` 哨兵识别去重（与 E10 串行后不双注入）；override 族兄弟/抽象族同步；前向转发补锚点；嵌套泛型多参数顺序。
- GD-ISAS：二元 `is/as T` 形态保持 + 锚点存在；`x is T v` → 单次求值 if 形（副作用临时变量只出现一次）；`null` 输入语义；结构 T 的拆箱绑定；不可提升上下文 → CS2J1104；`x is Concrete` 不动。
- GD-CATCH：`catch (T)` → 过滤式 catch；不匹配重抛；原体变量绑定正确。
- GD-STATIC：静态方法引用类 T → 自有类型参数 + 同族改写 + 同文件/跨文件调用点显式实参；属性 getter/setter；extern/接口族 → CS2J1102。
- GD-FIELD：有状态静态字段 → holder 字典形（同闭型共享、异闭型隔离的 C# 行为测试）；单例只读字段 → 访问器形；字段间依赖链；`[ThreadStatic]` 字段不进本规则；反射名风险 → CS2J1103；读写点跨文件。
- GD-EXPRTREE：`Expression<Func<...>>` lambda 内站点不改写、CS2J1110；lambda 外方法级锚点正常。
- 回退：注入一处必然编译失败的计划（可测 hook）验证 CS2J1100 全回退、产物与输入字节一致。
- residual 扫描：构造残余样本断言各规则计数与 strict 升级。

### 7.2 项目级 sln fixture

新增 `tests/SampleGenericDeepenSolution/`（`SampleGenericDeepenLib` + `SampleGenericDeepenApp` + `.sln`，按 SampleRecord/SampleStaticGeneric 范式以 Content 挂入 [CSharpToJava.ProjectTests.csproj](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj)，net10.0）：

- Lib：泛型类含 `typeof(T)` 缓存、`is/as T` 分支、类型参数 catch、静态字段三种形态（有状态 holder/单例访问器/ThreadStatic 对照）、泛型 override 族分布在两个文件、`Outer<T>.Inner<U>` 嵌套静态访问、递归节点类型。
- App：跨文件/跨项目泛型调用、静态字段按闭型隔离的运行时断言（xUnit 在中间树跑）。
- 断言：`result.Success`、统计计数、中间树不含 fail-fast 形态、含锚点/访问器/holder 生成文件；`dotnet build -c Release` 退出码 0；`dotnet test -c Release` 全绿；干净树字节级复制与 BOM 保持用例（仿各 pass 的 clean 用例）。

### 7.3 跨文件泛型调用与既有信号

- 复用既有 fixture `tests/SampleStaticGenericSolution/`（Lib `GraphSort.Sort<TEdge>` + App 调用）构造本 pass 开/关对照：本 pass 后中间树仍 build/test 全绿，且不新增无谓 token（锚点只插在被证明需要的方法）。
- [CrossProjectGenericClassTokenTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/CrossProjectGenericClassTokenTests.cs) 的**既有失败是相关信号**（**2026-09-28 实测：4 例中 2 例失败**，其一 `LibProject_GenericMethod_WithLinq_NoSpuriousClassToken` 在转换期于 GraphSort.cs L1350-1359 抛 `NotSupportedException`；转换器侧定义点/调用点双扫描不一致、`MethodClassTokenNeedCache` 已无调用方、TrailingClassTokenCallSiteTests 已掏空）：记录其在本 pass 落地前后的结果作为旁证，但**不属本 pass 修复范围**——它验证的是转换器 Java 产物，修复依赖 L2/转换器协同（§9 M5+、开放问题 Q1），不得为让它转绿而在 C02 PR 里改转换器。**基线：2 failed / 2 passed，本 pass 合入后不得恶化（≤2 失败）。**

### 7.4 中间树 Release build/test 与 sdk 基线

- fixture 与 `D:\csharp-sdk`（只读输入，禁止修改）预处理产物分别 `dotnet build -c Release` / `dotnet test -c Release`；sdk 基线 = pre-existing 失败集快照，本 pass 要求「新增失败为零」（与 foundation Validation 同口径）。
- M0 先跑测量轮输出 E10 三计数器在 sdk 的值（§1.2）。

### 7.5 residual 闸门测试

对 fixture 与合成样本断言 §4.5 六项残余计数；strict 模式下任一 >0 → pass 失败退出码非 0；非 strict 下仅 Warning 且树仍可编译。

### 7.6 CLI / 注册表测试

- 翻转前：`--enable-pass generic-deepen` → CS2J9001 退出 1（现状回归锁定）；翻转后：`--list-passes` 状态 Implemented、目录名正确；默认关时无中间目录/无耗时；`--enable-pass` 后链路执行并串联下一环输入；`--disable-pass generic-deepen`（默认开之后）可关；单文件 verb 跨文件计划 → CS2J1190。
- 注册表：拓扑顺序断言（generic 之后、record-readonly 之前；attribute-normalize stub 仍解析到该 id）、描述符 `catalogItems=="C02"`、无重复 id/目录。
- 一致性：[ProjectPreprocessorsConsistentTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs) 追加本 pass 的类型存在性 + `IntermediateDirectoryName`（`.cs2j-generic-deepen-src`）断言段（仿该文件对 record-readonly/java-keywords/enum 的 L105-126 段）；指纹令牌含 `pass:generic-deepen=...`。

---

## 8. 默认开启验收门槛

`defaultEnabled: false → true` 必须同时满足：

1. **中间树正确性**：fixture sln 与 `D:\csharp-sdk` 预处理树 `dotnet build -c Release` 零错误；含测试工程时 `dotnet test -c Release` 全绿；sdk 相对基线新增失败为零。
2. **残余闸门**：§4.5 六项在 fixture 全 0；sdk 残余仅出现在显式 CS2J11xx 报告站点，且全部站点分类有主（1101-1106/1110），无「未知残余」。
3. **全仓测试不回归**：`CSharpToJavaConverter.slnx` `dotnet build -c Release` + 三个测试工程 `dotnet test -c Release` 全绿（既有失败仅允许 CrossProjectGenericClassTokenTests 等已登记红灯，且数量不增加）。
4. **转换器协同就绪度确认**：锚点形（L0/L1）在代表性 Java 转换冒烟上被现有 `typeof(T)` 扫描正确消费（抽样 JSON 反序列化/override 场景）；FieldTransformer fail-fast 在本 pass 产物上不可达（该 fail-fast 的删除仍留后续 PR，但默认开启前须证明其不再被触发）。
5. **诊断可运维**：sdk 全量跑完 Warning/Error 清单经人工 triage，不可判定站点均有明确建议文案；overrides 机制验证 skip/force 行为。
6. **文档**：本设计 + 对应 plan（`docs/superpowers/plans/2026-09-28-c02-generic-deepen.md`）定稿；E10 哨兵规则与本 pass 的过渡期行为写明。

不满足时任保持 opt-in（对齐 catalog §4 风险 10：避免长期多套事实标准，但也不抢跑）。

---

## 9. 里程碑（规则级可独立提交顺序）

| 里程碑 | 内容 | 可独立提交/验证 |
|---|---|---|
| M0 | Core 骨架（options/statistics/diagnostics/facts/入口空壳）+ 适配器 + 注册表翻转（descriptor Implemented、默认关、规则全空走字节复制）+ 注册表/CLI 测试 + sdk 测量轮（E10 三计数器） | 中间树零差异；`--list-passes` 可见 |
| M1 | GD-ANCHOR-1/2/3：token 需求分析、锚点插入、E10 哨兵去重与族同步（与 E10 开关共存验证）、residual 第 3/4 项 + Core 单测 + fixture 最小集 | build/test 全绿；不改签名 |
| M2 | GD-ISAS-A 覆盖核对 + GD-ISAS-B 声明模式语句化 + GD-CATCH；residual 1/2 项；与 C03 边界用例（switch case 报码而非抢改） | 单测/项目测 |
| M3 | GD-FIELD-1/2/3（holder 生成与分发、访问器、读写点改写）+ GD-STATIC（静态方法/属性签名族改写 + 调用点）；回退通道实战 | fixture 全形态 + sdk 轮 |
| M4 | GD-NEST 闭包传播 + 表达式树共享谓词提取（JavaKeywords 改引用）+ GD-EXPRTREE + 全规则 residual 闸门 + strict + overrides；sdk 基线比对 | 候选默认开评审 |
| M5 | 过 §8 门槛后 descriptor 翻默认开；plan 文档收尾；登记转换器侧后续 PR 清单（L2 显式 `System.Type` 尾参原型、FieldTransformer fail-fast 删除、E10 哨兵规则下线、CrossProjectGenericClassTokenTests 修复） | 默认开 |
| M5+（不在本 pass 承诺） | L2 签名显式化原型与转换器协同；C30 stub 独立化与代码考古（catalog §4 风险 6）；C33 审计 pass | 独立条目 |

---

## 10. 风险

1. **静态字段语义等价性（最高风险）**：C# 静态字段按**闭型**共享（`C<int>.x` 与 `C<string>.x` 是两份），Java 同份。holder 字典以 `Type` 为键复刻该语义，但初始化时序（beforefieldinit 懒初始化 vs 字典首访）、异常缓存、初始化循环依赖、结构 T 的装箱键都需 fixture 与 sdk 测试覆盖；「降实例字段」是转换器现存窄形但语义不等价，本 pass 不采用且需在后续转换器 PR 中收口（catalog C02 证据中的 StaticFieldRuntimeClassTokenTests 代表该窄形）。
2. **签名族改写爆炸面（GD-STATIC/L2）**：~~虚方法/接口/抽象族改方法级类型参数会触碰所有实现与调用点~~（**评审核实收窄**：静态方法在 C# 不可虚，无 override 族；剩余爆炸面为 `static abstract` 接口成员与跨程序集调用点，均走 CS2J1102/1106 显式报错）；跨程序集不可见即报错可能在大库上产生大量 1102/1106。缓解：v1 只对无自有类型参数的普通静态方法做改写，其余显式报错；普通实例泛型方法的 L2 留 M5+ 原型；overrides 可 skip 类型。
3. **表达式树/dynamic 共同禁区**（catalog §4 风险 8）：改写树内节点改变查询提供方语义；依赖共享 `ExpressionTreeFacts`，不各写一份判定；dynamic 站点不猜。
4. **反射/字符串名契约**（catalog §4 风险 7）：字段→访问器改变成员面，`GetField("F")`、JSON 按名、XAML 静态不可见 → GD-FIELD-3 显式报错；holder/访问器生成名（`__cs2j_`）不保证反射可达，文档化限制同 JK §9。
5. **C30 closed metadata 代码考古**（catalog §4 风险 6）：`PatchClosed*` 在本树 grep 无落点、compat 侧 `TypeHelper.closedCacheKey`/`ConcurrentHashMapHelper.putIfAbsentUntyped` 已存在——这是 **C30 的阻断项**；C02 只借「token/闭型从 C# 显式贯穿到 Java 反射」的方向，不引用其补丁、不发射注册码，避免重复建设。
6. **Lowering 资产只借形态**（catalog §4 风险 9）：[Lowering/](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering) 11 个 pass（如 `LowerPatternMatch.cs` 的模式→布尔归约形态）仅 Java2 对比测试可达；GD-ISAS-B 可参考其归约形态，**不得共用 IR 代码**。
7. **中间树成本**（catalog §4 风险 11）：新增一环 = 一次树复制/reparse/restore；必须接入既有指纹/preserve（注册表驱动已满足），不额外跑多轮；holder 生成不触发二次 restore 之外的动作。
8. **E10 双轨期漂移**：M1-M4 期间 E10 哨兵规则与 C02 锚点共存，两者对「需要 token」的判定若不一致会产生分歧锚点；M1 必须把判定事实源收敛到 `GenericDeepenAnalyzer`（E10 helper 被提取复用），M5 后的后续 PR 再下线 E10 注入。
9. **类型参数 `is T` 的值类型语义**：保留 C# 形不重写是正确性前提（自行改写成 `IsInstanceOfType` 会引入 null/Nullable 与装箱差异）；转换器映射 `isInstance` 对结构 T 的行为需在 Java 半区另行验证（不阻断 .NET 半区）。
10. **并发改树热文件冲突**：多 pass 并行开发集中在注册表三文件，见 §11。
11. **BCL 映射依赖假设（已核实，2026-09-28）**：holder 存储原拟用 `System.Collections.Concurrent.ConcurrentDictionary`——**核实结果：[TypeMappings.json](file:///d:/code/cs2j/config/TypeMappings.json) 无任何 `System.Collections.Concurrent` 映射（grep 零命中），不可用**；而 `System.Collections.Generic` 有命名空间级映射（L8228-8229 → `dotnet.System.Collections.Generic`，`Dictionary<,>` 经 `ToDictionary` 等成员映射可达）。**结论：holder 存储形落地为加锁 `Dictionary<Type,object>`（`lock` 双检懒初始化）**，C# 侧语义等价（静态字段本就非并发安全契约），Java 侧映射确定。若实施中发现 `Dictionary<,>` 成员映射仍有缺口（TryGetValue/索引器等），缺口成员改写为 `ContainsKey`+索引器组合后再评估，不引入新依赖假设。

---

## 11. 并行协调

| 并行条目 | 关系 | 合并顺序与文件冲突点 |
|---|---|---|
| **C01** RefOutIn 接线收尾 | 都改方法签名面（holder 形参 vs token 锚点/静态提升），但规则集不相交；链位上 C01（ref-out-in）远早于 generic-deepen | C02 分析器须能识别 C01 产物（ByRef holder 类型不作为类型参数处理）。热文件冲突：`PassChainFactory.cs`/`PassCatalog.cs`/注册表测试——按 id 分区合入即可；Core 目录不重叠。建议 C01 先合（既有位置在前），C02 rebase |
| **C09** ThreadStatic（同族静态字段） | 静态字段语义重叠：`[ThreadStatic] static T x` 同时撞两条规则 | 所有权切死：**C02 排除带 ThreadStaticAttribute 的字段**（分析器显式过滤，登记 Info），交 C09；建议 C09 落地时 RunAfter 增 `generic-deepen`（其 stub 现锚 `generic`）。若 C09 先合，C02 rebase 时补过滤；二者不得各自生成一套字段存储 |
| **C30** closed metadata | 消费 C02 token；P2、有代码考古阻断 | C02 合入不被 C30 阻塞；C30 独立 stub/独立 id（§3.5）；零代码冲突，仅 §3.5 的 catalogItems 收窄需 C30 知情 |
| **C33** 型变/约束审计 | 同族审计型 pass，读同一批符号但不改同一节点 | 无实现冲突；C02 不碰约束子句与型变声明；C33 未来 RunAfter=`generic` 或 `generic-deepen` 均可，独立排期 |
| **C08/E17** JavaKeywords | C02 提取 `ExpressionTreeFacts` 会动 [JavaKeywordSyntaxFacts.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/JavaKeywordSyntaxFacts.cs#L55-L63) | 冲突点仅此一文件一处提取重构；C08 若先合，C02 rebase 时搬谓词；生成名 `__cs2j_*` 不撞 Java 关键字逃逸（下划线标识在 Java 合法，且 C08 跑在链尾会兜底） |
| **C03/C37** 模式去糖 | 类型参数模式语法面相邻 | 切分以「右类型是否 `ITypeParameterSymbol`」为界；switch 类型参数 case 在 C03 未落地前由 C02 报 CS2J1104，不抢语句去糖；C03 落地后可由 C03 归约出 if 链再交 C02 识别 |
| 公共热文件 | `PassStubCatalog.cs`、`PassCatalog.cs`、`PassChainFactory.cs`、注册表/一致性测试 | 各 worktree 仅追加各自 id 的行；合并顺序任意，按 rebase 解冲突；禁止在任一 pass 分支顺手重排既有 pass 顺序 |

---

## 12. Worktree 分配

- 分支：`feature/preproc-c02-generic-deepen`
- 目录：`.worktrees/preproc-c02-generic-deepen`
- 状态（2026-09-30 核实/当日关闭）：该分支历史使命已完成——实现提交（止于 ecc1376e）已在 dev-3.x 主干，分支无独有提交；worktree 快进留档后**已于 2026-09-30 连同分支一并删除**。
- 历史创建命令（已执行，留档）：

```bash
git worktree add .worktrees/preproc-c02-generic-deepen -b feature/preproc-c02-generic-deepen ff216bcc
```

实施约束（交实施 worktree，不在本文档执行）：仅在上述 worktree 内改码；按 §9 里程碑逐提交；默认关先行，过 §8 门槛才翻默认开；转换器代码改动留后续 PR。

---

## 13. 评审修订记录（2026-09-28，代码级核实）

对本设计的全部文件/行号/行为断言做了三路并行代码核实（注册表层 / Core 证据层 / 适配器与测试层），修订如下：

1. **§1.1 证据表**：FieldTransformer fail-fast 实际位于 L112-118；CS2J5002 为 Warning 级诊断（`JavaStaticContextValidationRewriter.CheckTypeForClassTypeParam` L124-146，非 error）；`MethodClassTokenNeedCache` 声明于 ConversionContext.cs L152-153；TypeOperationTransformer 未知模式占位位于 IsPattern L107-117（L115）；default(T) 主体 L217-249 + 判定 helper L255-300。**CrossProjectGenericClassTokenTests 实测 4 例 2 失败**（`dotnet test -c Release --filter`，2026-09-28），已把「既有失败」从传闻升级为实测基线并写进 §7.3 验收口径。
2. **§1.1 行 4/5 命名澄清**：E10 的前向哨兵与族同步逻辑分别是 `GenericSyntaxRewriter.VisitMethodDeclaration`（L421-602 区段）与 `PrescanOverrideSiblings`（L74-166）+ 选项开关 `InsertTypeOfForForwardedTypeParams`/`SyncTypeOfForOverrideSiblings`；原文把选项字段名当方法名引用，已改（§4.3 GD-ANCHOR-2/3）。
3. **holder 存储形（§2/§4.1/§4.3 GD-FIELD-1/§10 风险 11）**：核实 TypeMappings.json **无** `ConcurrentDictionary`/`System.Collections.Concurrent` 映射（原设计风险 11 的乐观假设不成立），存储形定稿为加锁 `Dictionary<Type,object>`（`System.Collections.Generic` 命名空间级映射存在）。
4. **GD-STATIC 收窄（§2/§4.3/§10 风险 2）**：静态方法 C# 不可虚 → 无 override 族手术面；`static abstract` 接口成员、已带自有类型参数的静态方法、静态属性（属性无类型参数载体）→ CS2J1102 显式报错跳过，禁静默。
5. **§3.1/§3.2 落地细节**：无门面类时 `IntermediateDirectoryName` 常量放 `GenericDeepenPassAdapter`；`catalogItems: "C02"` 为 Implemented 表首例显式设置（既有条目均 null）；点名需同步更新的锁定测试（PassChainOrderTests 16→17 id、PassStubCatalogTests 23→24）。
6. **§3.5 决策落定**：当次补登 `closed-metadata`/`variance-audit` 两个 stub。
7. **§5.1/§5.2 定稿**：适配器构造签名六参仿 GenericPassAdapter；v1 不加 OptionsFactory。
8. **§12 worktree base**：f68a783c → ff216bcc（docs-only 增量，含本设计自身）。
9. 其余断言全部核实为真：stub 条目/Ids 常量/attribute-normalize 引用（PassStubCatalog.cs L111-120/L28/L188）、PassCatalog 结构与 16 条 Implemented 顺序、CreateAdapter switch（L94-217）、指纹令牌（Program.cs L1765-1774）、CS2J9001 拒绝路径（L3458-3465 + ProjectPassRunner.StubMessage）、`JavaKeywordSyntaxFacts.IsExpressionTreeLambda`（L55-63，internal 类中 public static，可提取）、E10 三计数器在 MergeStatistics 累加但摘要行未打印、`Analysis/` 目录已存在（仅 TypeParameterBindingAnalyzer.cs）、Tuple 生成文件分发骨架（AfterFilesAsync L122-130 + DistributeStructFiles L169-336 + EnsureCompileItem L421-473）、compat 侧 `ConcurrentHashMapHelper.putIfAbsentUntyped`/`TypeHelper.closedCacheKey` 均在。
10. **2026-09-30 再基线**：核实本设计已实现并合入主干（提交链 14fed211→db9bbd08→0c969001→ecc1376e，见文首「再基线」段与 §12）；ff216bcc→75958d7a 间含该实现及先决批（Roslyn 5.0.0、C11–C14）。
