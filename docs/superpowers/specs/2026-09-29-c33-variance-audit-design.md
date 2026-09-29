# C33 泛型型变与约束前置审计设计（审计型通道：使用点枚举与安全分类先行，use-site 改写另立项）

**日期**: 2026-09-29
**范围**: 新增注册表 pass `variance-audit`（Core `VarianceAudit/` + CLI `VarianceAuditPassAdapter`），只做 C#→C# 层**只读审计**：枚举 `out T`/`in T` 声明-site 型变的全部使用点并做生产者/消费者安全分类，逐项盘点 `struct`/`unmanaged`/`new()`/`notnull`/`default`/`class` 约束及其语义依赖站点，出具名诊断 + 机器可读报告；本特性不做任何源码改写、不发射 Java 通配符、不新增 compat 代码
**状态**: 设计稿，待评审
**注册表锚点（2026-09-29 主树实读核实）**: stub `variance-audit` 登记于 [PassStubCatalog.cs#L122-L131](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L122-L131)（catalogItems `C33`，Stage=`Declarative`，**RunAfter=`generic`**，默认关，目录 `.cs2j-variance-audit-src`）。注意：foundation plan 原表曾把 C02/C30/C33 合并在 `generic-deepen` 一个 id 下（[preprocessor-foundation_plan.md#L87](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L87)），主树实际注册已拆为三个 id——`generic-deepen` 已 Implemented（默认关，catalogItems 仅 `C02`，[PassCatalog.cs#L154-L164](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L154-L164)）、`closed-metadata` 为 stub 锚 `generic-deepen`（C30，[PassStubCatalog.cs#L112-L121](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L112-L121)）、`variance-audit` 为 stub **仍锚 `generic`**。本设计以主树现状为准，维持 RunAfter=`generic`（§3.3 论证）
**catalog 对应**: `prompts/preprocessor-catalog.md` C33（[L653-L664](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L653-L664)）、C02 关系（[L269-L280](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L269-L280)）、§3.3 泛型擦除补偿族 E10+C02+C09+C30+C33（[L908](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L908)）、§4 风险（[L918-L939](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L918-L939)，尤险 10/11/12）；foundation plan [L60-L139](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L60-L139)
**审计型 pass 范本**: [2026-09-29-c25-dynamic-desugar-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c25-dynamic-desugar-design.md)（report-only 模式、报告 JSON、adapter 钩子纪律）；同族设计 [2026-09-29-c30-closed-metadata-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c30-closed-metadata-design.md)、[2026-09-28-c02-generic-deepen-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c02-generic-deepen-design.md)

---

## 1. 背景与目标

### 1.1 静默丢弃证据（2026-09-29 主树实读）

| # | 现状 | 位置 | 诊断 |
|---|---|---|---|
| 1 | 接口 `out T`/`in T`：类型参数照样发射、variance 关键字丢弃，发一条笼统 Warning | [InterfaceTransformer.cs#L89-L104](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/InterfaceTransformer.cs#L89-L104) | CS2J1003 |
| 2 | 委托同上（`Converter<in TInput, out TResult>` 每个型变参数一条） | [DelegateTransformer.cs#L93-L108](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/DelegateTransformer.cs#L93-L108) | CS2J1003 |
| 3 | 类的语法回退路径（`symbol==null`）同样告警；但**语义主路径**（`symbol!=null`，正常工程转换都走这条）用 `CreateTypeParameter` 静默丢弃，**一条告警都没有** | [ClassTransformer.cs#L175-L192](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L175-L192) | 回退路径才有 CS2J1003 |
| 4 | `where T : struct` → `WarnConstraintDropped`，Bounds 不加 | [ClassTransformer.cs#L228-L229](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L228-L229) | CS2J1002 |
| 5 | `where T : new()` → `WarnConstraintDropped` | [ClassTransformer.cs#L231](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L231) | CS2J1002 |
| 6 | `notnull`：显式 `break`，**静默忽略** | [ClassTransformer.cs#L218-L219](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L218-L219) | 无 |
| 7 | `default` 约束（泛型 override 还原约束）：`DefaultConstraintSyntax` 分支直接 `break`，**静默忽略** | [ClassTransformer.cs#L232](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L232) | 无 |
| 8 | `class` 约束：空分支静默忽略（注释「Java 默认即引用类型」） | [ClassTransformer.cs#L230](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L230) | 无 |
| 9 | `unmanaged`：全 `src/` grep `unmanaged` **零命中**。语法上它是伪类型标识符，落入 `TypeConstraintSyntax` 通用分支（L220-L227），`GetTypeInfo` 解析不到真实类型 → 无 Bounds、无任何诊断。**比 catalog L659 所述「仅 WarnConstraintDropped」更差：unmanaged 是纯静默**（M1 单测钉死 GetTypeInfo 实际返回） | [ClassTransformer.cs#L220-L227](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L220-L227) | 无 |
| 10 | 全 `src/` grep `ITypeParameterSymbol.Variance`/`VarianceAnnotation` **零命中**——型变判定全部只读语法 token，语义模型的型变信息从未被消费 | — | — |

约束处理只有一份实现：接口与委托都转调 `ClassTransformer.ApplyTypeParameterConstraints`（[InterfaceTransformer.cs#L108](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/InterfaceTransformer.cs#L108)、[DelegateTransformer.cs#L235-L239](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/DelegateTransformer.cs#L235-L239)）。

### 1.2 Java 侧无 use-site variance 发射能力（重写不能顺手做）

- 全 `src/` grep `? extends`/`? super`/`Wildcard`：**没有任何有界通配符发射**。命中的 `<?>` 全是硬编码字符串（`Class<?>`、`Iterable<?>`、`List<?>`、`CompletableFuture<?>` 等，[ConversionContext.cs#L467-L473](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionContext.cs#L467-L473)、[ArgumentTransformer.Coercion.cs#L400](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.Coercion.cs#L400)）或 import wildcard（与类型实参无关）。
- 声明头基类型里的 `<?>` 还会被**主动抹成 `<Object>`**：`WildcardToObjectTypeArg`（[ClassTransformer.cs#L557](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L557)，调用点 L134/L143/L160/L164；[InterfaceTransformer.cs#L59](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/InterfaceTransformer.cs#L59)、L75）。
- 泛型实参按位映射、不携带型变（`TypeMappingService` 对 `INamedTypeSymbol.TypeArguments` 逐元素 `MapTypeForGeneric`），`JavaTypeParameter` 只有声明侧 `Bounds`（`T extends X`），没有类型实参侧的通配符模型。

结论：use-site 改写需要先在 Java 发射层新建通配符类型实参模型 + 捕获转换分析，不是本通道能顺带交付的；见 D1/D11。

### 1.3 语义风险：为什么不是「丢了就丢了」

1. **协变依赖转换在 Java 侧不成立（显式破坏）**：C# 允许 `IEnumerable<string>` 赋给 `IEnumerable<object>`（CLR 在声明-site 保证安全）；Java 不变发射后 `CSharpIterator<String>` 不是 `CSharpIterator<Object>` 的子类型 → javac 报错。今天这类代码没有任何前置提示，要走到 Java 编译才暴露。
2. **消费端违规写入（隐式破坏，catalog 点名）**：C# 只读协变契约（如 `IReadOnlyList<out T>` 形态）映射到的 Java 类型若暴露 C# 契约外的变更成员（`add`/`set`），Java 侧代码可以对「生产只读」容器执行写入——CLR 用声明型变在类型系统层禁止的动作，在 Java use-site 不检查的世界里无人拦截。
3. **值类型约束语义依赖**：`default(T)` 对类型参数 T 最终落到 [TypeOperationTransformer.cs#L153](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs#L153) 的兜底 `"null"`；对 `where T : struct`，C# 的 `default(T)` 是零值实例而非 null，装箱身份/可空语义全错。E10 仅把 `default(T)` 归一成 `default` 字面量（[GenericPreprocessOptions.cs#L6-L10](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessOptions.cs#L6-L10)），不改变这个 Java 侧结果。
4. **unmanaged 面零感知**：指针/`sizeof`/封送相关代码在 struct/unmanaged 约束下的假设无人盘点（与 C06 unsafe 族相邻）。

### 1.4 定位：审计型通道

catalog 定调（[L661](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L661)）：「**审计型**通道（先做 C# 侧显式告警/拒绝 + 安全校验，再决定是否做使用-site 改写）」；P3（[L662](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L662)：丢失形态是「声明降级」而非编译失败）。

目标按两个里程碑递进：

1. **M1 纯审计（核心交付）**：整树语义扫描，枚举每个型变声明点与全部使用点（生产者/消费者/两者/型变转换/嵌套组合），逐项盘点约束及其语义依赖站点，出 VA0xx 具名诊断 + `variance-audit-report.json`。不改一个字符源码。
2. **M2 不安全站点 Error 闸门**：`--variance-strict` 把不安全类别的 Warning 升 Error、中止链条；仍然零改写，为「带闸转换」与后续默认开启提供门。use-site 通配符改写**不在本期**，另立项（D11）。

非目标：

- 不做 `? extends/super` 使用-site 改写，不碰 Java 发射层（§1.2）；
- 不做 C# 侧「去型变」改写（声明-site 型变是 CLR 合法且安全的元数据，C# 侧无可等价表达的消去形式）；
- 不接管 E10 的 `new T()`/`default(T)`/`new T[n]` 重写，只做交叉引用与残余站点盘点（D6）；
- 不接管 C02 的 `typeof(T)`/`is-as T`/静态泛型字段、C30 的闭型元数据；
- 不改转换器现有 CS2J1002/CS2J1003 行为（默认关期间链行为零变化；退役是默认开门之后的独立 PR，D14）。

### 1.5 与泛型族分工（catalog §3.3 泛型擦除补偿族，[L908](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L908)）

| 成员 | 职责 | 与 C33 边界 |
|---|---|---|
| E10 `generic`（已实现，默认开） | `default(T)`→`default`、`new T()`→`Activator.CreateInstance<T>()`、`new T[n]`→`Array.CreateInstance`、override Class token 哨兵（[catalog L175-L183](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L175-L183)） | C33 锚其后；new() 构造点归 E10，C33 只审计声明、交叉引用 E10 产物、盘点 E10 不覆盖的残余（D6） |
| C02 `generic-deepen`（已实现，默认关） | `typeof(T)`/`is-as T`/类型参数 catch/静态泛型字段/嵌套 token 传播 | 无站点重叠；C33 不复用其 token，只在报告中提示同编译单元已开/未开 C02 |
| C30 `closed-metadata`（stub） | Java 反射侧消费闭型 `java.lang.reflect.Type` | C33 的型变/约束是**声明-site 安全性**问题，C30 是**运行时闭型信息**问题；报告 JSON 词汇对齐，数据不互依赖 |
| C09 `thread-static`（stub） | ThreadLocal 隐藏状态 | 同锚 `generic`、同 Stage=Declarative，仅注册表顺序协调 |

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据与备注 |
|---|---|---|---|
| D1 | 首期只做审计、不做 use-site 改写 | **M1/M2 零改写**。论证：①Java 发射层无通配符模型（§1.2），改造面横跨 `MapType`/声明头/实参发射/overload 解析，规模远超 P3 通道；②Java use-site 不做声明-site 安全校验，改写正确性依赖逐使用点流分析（PECS + 通配符捕获），假阴成本是「产出能编译但运行时写错对象的 Java」；③先把使用点枚举与安全分类做扎实，报告本身就是未来改写器的输入规格，且现在就能以 Warning/Error 闸门消灭「无人知情」状态 | catalog P3 + 审计型定位；险 12「无法等价表达时显式报错，禁止静默透传」 |
| D2 | 使用点三分类 | 对每个型变类型参数 T 在每个具体使用位置归类：**Producer（输出位）**/ **Consumer（输入位）**/ **Both（两者）**，外加两个横切标签：**VarianceConversion**（该位置依赖 C# 型变隐式转换）、**NestedComposition**（嵌套泛型中的组合型变，D7） | 与 Java use-site 词汇一一对应：Producer→`? extends`、Consumer→`? super`、Both→不变（D11 留给未来改写器） |
| D3 | 安全判定规则（保守，宁假阳） | ①声明成员签名内 T 的出现位：返回值/out 参数/getter=Producer；值/in 参数/setter=Consumer；ref 参数/同时有 get+set 的索引器或属性=Both；②使用点（局部/字段/形参/返回类型为型变闭型 `X<A>`）：汇总该值在作用域内的全部成员访问，用接收者闭型替换后的成员符号判定 A 的流向；见到任一消费形迹即不得再标纯 Producer；成员访问解析不出/动态/表达式树内 → 保守 **Both+Warning**；③型变转换检测见 D3a；④CLR 已保证声明本身合法，C33 不重新验证声明-site 合法性（基线编译错误树另按 D12 处理） | 假阳成本=人工看 Warning；假阴成本=违规写入流入 Java |
| D3a | 型变隐式转换检测（近似） | Roslyn 公共 API 不直接暴露 variance conversion 标志，采用**结构化判定**：对赋值/return/实参传递节点取源/目标类型，二者为同一 `OriginalDefinition` 的 `INamedTypeSymbol`（或同秩数组）时，按各类型参数的 `ITypeParameterSymbol.Variance` 逐对比较实参的继承关系——协变位上源实参派生、逆变位上目标实参派生即判为依赖型变转换；方法组/委托转换同法比较委托闭型。解析不全的候选（dynamic、错误类型、自定义隐式转换混淆）一律保守入账 VA015 | 方法组转换与嵌套委托是主要漏检面，宁报 VA015 不漏 VA010/011 |
| D4 | 型变信息以语义模型为准 | 声明点统一从 `ITypeParameterSymbol.Variance`（`VarianceAnnotation.Out/In/None`）+ `DeclaringType`（接口/委托）读取，**不再读 `VarianceKeyword` 语法 token**；符号缺失时回退语法并在报告标注 `SymbolResolution=Fallback`。这同时解释了类转换语义主路径为何连 CS2J1003 都没有（§1.1#3）——C33 的扫描不经过该转换器路径，无此盲区 | C# 仅接口/委托允许型变；类上的 out/in 是基线编译错误，按 D12 |
| D5 | 约束逐项策略 | 见下表（D5a）。原则：**编译期可擦除的（notnull/default/class）只 Info 显式化；带运行时语义假设的（struct/unmanaged/new()）Warning + 依赖站点关联** | 现状 CS2J1002 只说「dropped」不说影响面，C33 报告必须回答「丢了之后哪些使用点失去保证」 |
| D5a | 约束矩阵 | **struct**（`HasValueTypeConstraint`）：VA020 Warning，关联该 T 的全部值语义依赖站点（D5b）。**unmanaged**（语义 `IsUnmanagedType` + 语法伪类型「unmanaged」双确认；4.12 若提供 `HasUnmanagedConstraint/ConstraintKind` 则作交叉验证，单测钉死）：VA021 Warning（当前零诊断，§1.1#9），附加指针/sizeof/Marshal 站点提示（交叉引用 C06，不代为判定）。**new()**（`HasConstructorConstraint`，仓内已用，[ObjectCreationTransformer.cs#L151-L152](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs#L151-L152)）：VA022 Info，构造点归 E10（D6）。**notnull**：VA024 Info（NRT 仅编译期）。**default**：VA025 Info（override 约束还原，纯签名元数据）。**class/class?**（`HasReferenceTypeConstraint` + nullable annotation）：VA026 Info（当前静默）。类型上界约束（`where T : IComparable<T>`）不盘点——它已被正常映射为 Java bounds（[GenericConstraintDiagnosticsTests.cs#L54-L76](file:///d:/code/cs2j/tests/CSharpToJava.Tests/GenericConstraintDiagnosticsTests.cs#L54-L76) 钉死） | enum/Delegate 特殊约束同样是类型上界，不进盘点 |
| D5b | struct/unmanaged 语义依赖站点（M1 枚举集） | ①`default(T)`/`default` 字面量且转换目标为该 T → VA027（Java 侧兜底 `null`，具体错误，§1.3#3）；②`new T()` 站点（E10 已重写的 → 与 VA022 关联 Info；E10 不覆盖的带初始化器/带参形式 → VA023 Warning，D6）；③T 上的装箱身份依赖（`(object)x`、`lock(x)`、引用相等、`as/is object`）M1 仅 Info 计数，不逐站点判错；④`T[]`、`Nullable<T>`、`T?` M1 登记计数。③④的逐站点安全判定列 M3 | M1 必须零假阴覆盖 ①②（这两站今天确定产错/可能失败），③④ 先量化 |
| D6 | new() 与 E10 划界 | E10 只重写**无参、无初始化器**的 `new T()`（[GenericPreprocessor.cs#L307-L338](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericPreprocessor.cs#L307-L338)）。C33 在链上跑在 E10 之后：①对 `new()` 约束声明发 VA022；②识别 `global::System.Activator.CreateInstance<T>()` 形态，关联回约束声明（Info，注明构造保证已由 E10 显式化、运行时由 Activator 反射满足、异常形态为运行时而非编译期）；③E10 未覆盖的残余 `new T()`（带 object initializer 或参数）发 VA023 Warning（这些站点直转路径行为未定义）。C33 **不**改写任何构造点；独立 verb 对原始源码跑时看到的是重写前形态，报告以 `RewrittenBy=generic(pass present)` 标注差异 | 锚 `generic` 的核心理由之一；`new T[n]` 同理由 E10 覆盖，仅登记 |
| D7 | 型变组合（嵌套泛型） | 嵌套出现位的有效型变 = 外层位置型变 ∘ 内层参数声明型变，标准复合规则：co∘co=co、contra∘contra=co、co∘contra=contra；任一环节为 Both 则结果 Both。例：`IEnumerable<Action<T>>` 出现在方法**参数**位（Consumer）→ Action 的 `in T`（contra）与外层 Consumer（contra）复合为 co：T 在该点整体是 Producer。判定沿 `INamedTypeSymbol.TypeArguments` 与对应 `TypeParameters[i].Variance` 逐层下推；报告记录完整组合链（每一层的位置方向×声明方向），供未来改写器直接消费 | 这是 use-site 改写最易出错的面，M1 先把数据枚举对 |
| D8 | 委托型变 | 委托是第二类合法声明载体：`Action<in T>`、`Func<in T1,...,out TResult>` 及用户自定义泛型委托。①声明点同样盘点；②使用点除 D3 的 Invoke 签名流向（参数 Consumer、返回 Producer）外，重点枚举**委托构造/方法组转换/委托赋值**中的型变转换（D3a），例如把 `Action<object>` 传作 `Action<string>`；③委托组合（`+=`）两侧型变不匹配在 C# 即编译错误，不进安全判定 | [GenericConstraintDiagnosticsTests.cs#L140-L158](file:///d:/code/cs2j/tests/CSharpToJava.Tests/GenericConstraintDiagnosticsTests.cs#L140-L158) 已有声明侧测试 |
| D9 | 映射后可变性扩大（违规写门面） | C# 协变/只读契约映射到的 Java 类型若带有 C# 契约不存在的变更成员，即构成 catalog 所述「不被发现的违规写入」。M1 实现一张**可配置的映射可变性表**（种子：`TypeMappings.json` 中 BCL 只读/协变接口与其 Java 映射类型的成员差，如只读接口→可变 Java 集合）；对 Producer 分类的使用点，若映射类型存在 `add/set/remove/put` 等变更成员 → VA012。表未覆盖的映射保守按「可能扩大」处理（Warning），绝不默认安全 | 宁可表不全报假阳，不假装映射保持只读；表随语料迭代 |
| D10 | 报告分级 | **Error**：仅 `--variance-strict` 下 VA010/011/012/015/020/021/023/027/030（M2 闸门）；M1 普通模式这些码均为 Warning，链继续。**Warning**：型变依赖转换、可变性扩大、struct/unmanaged 丢弃、E10 残余构造点、default(T) 错误兜底。**Info**：声明点登记、纯 Producer/Consumer 使用点（通配符候选）、嵌套组合、new()/notnull/default/class 约束、报告写出。Info 仅 verbose/报告 JSON 可见，不刷屏 | 审计模式默认不阻断（对齐 C25 D3） |
| D11 | use-site 改写接口预留但不实施 | 报告 JSON 的每个 useSite 携带未来改写所需全部字段：`suggestedWildcard(Extends/Super/Invariant)`、`compositionChain[]`、`mappedJavaType`、`mutatingMembers[]`、`enclosedBy(ExpressionTree/Dynamic)`。Core 不出现任何 `? extends/super` 字符串拼接、不提供 Rewriter 类、Options 不含 Rewrite 模式。未来立项时以本报告为输入规格，另做 Java 发射层通配符模型 + 捕获转换验证 | 险：Java 通配符捕获（capture conversion）会改变重载解析与可赋值性，机械插入通配符可制造新的不可编译 Java；必须独立设计 |
| D12 | 基线编译错误树 | 类上写 out/in、非法型变位置等本就是 C# 编译错误：审计照常枚举声明/使用点（语法树可遍历），但对该文件**不叠加**安全分类 Warning（避免与 CS19xx 双报），报告条目标 `BaselineHasErrors=true`；VA027 等纯语义查询类诊断在错误类型节点上同样抑制 | 与 C25 同纪律 |
| D13 | 报告形状与字节稳定 | `variance-audit-report.json`：run 元信息（mode/strict/passPresent 位图：generic/generic-deepen 是否在链）+ `declarationSites[]` + `useSites[]` + `constraintSites[]` + 汇总计数；UTF-8 无 BOM；全部数组按 文件+span 排序；不含时间戳/绝对路径前缀外的易变字段（路径用相对 SourceRoot 的形式），保证同输入两次运行字节一致 | 对齐 C25 报告纪律，便于指纹与跨运行 diff |
| D14 | 与 CS2J1002/CS2J1003 的关系 | M1/M2 **不动**转换器三个告警点（单文件直转不经预处理链，仍是唯一安全网）。码位分工：CS2J1003=声明点一句话（现有），VA001=声明点结构化登记 + VA01x=使用点（新增维度）；CS2J1002=约束丢弃一句话，VA020+=约束 + 依赖站点。默认关期间零重复（pass 不跑）；审计开启后允许并存（[GenericConstraintDiagnosticsTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/GenericConstraintDiagnosticsTests.cs) 原断言不动）。转换器告警退役（改为由 pass 专属或 fail-fast）是 audit 默认开门并完成语料验证后的独立 PR | 与 C25 §6.3 同节奏 |

---

## 3. 注册表与链位契约

### 3.1 stub 现状（主树实读）

[PassStubCatalog.cs#L122-L131](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L122-L131)：id `variance-audit`，显示名 "Variance and constraint audit"，目录 `.cs2j-variance-audit-src`，`PassStage.Declarative`，`PassStatus.Stub`，`defaultEnabled: false`，`runAfterIds: new[] { PassIds.Generic }`，`needsSemanticModel` 默认 true，catalogItems `C33`。禁用时零成本；显式启用即 CS2J9001 失败。

**核实结论（回应任务疑点）**：C02 翻转提交确实把原合并条目拆成了 `generic-deepen`（Implemented，C02）+ `closed-metadata`（stub，C30，锚 generic-deepen）+ `variance-audit`（stub，C33）三个注册项；`variance-audit` 的锚**没有**随 C02 翻转为 generic-deepen，主树至今锚 `generic`。本设计维持该锚（理由 §3.3）。

### 3.2 翻转后目标描述符

| 字段 | 值 | 理由 |
|---|---|---|
| Id | `variance-audit` | 不变 |
| Stage | `Declarative` | 维持：审计对象是类型声明及其签名契约；Stage 仅信息性 |
| Status | `Implemented` | M1 审计本身是完整可用实现（与 C25 同口径）；M2 strict 闸门以 Options 演进而非二次翻转 |
| DefaultEnabled | **false 落地**；§8 门槛满足后评估 **audit 模式默认开**（Warning 不阻断、产物字节不变） | P3；先取 csharp-sdk 语料数据 |
| RunAfterIds | `{ "generic" }`（维持） | 见 §3.3 |
| IntermediateDirectoryName | `.cs2j-variance-audit-src`（维持） | |
| NeedsSemanticModel | true | 全部判定基于 SemanticModel |
| SupportLibrary | `None` | 审计不注入任何代码 |
| VerifyBuildByDefault / VerifyTestsByDefault | **false / false** | 审计永不改写、输出树与输入字节一致，restore/build/test 无意义（省一整轮耗时，险 11）；M2 的 Error 闸门来自审计结果而非 verify |

### 3.3 为何锚 generic 而非 generic-deepen

1. **审计依赖 E10 的产物形态可见性**：`new T()`→`Activator.CreateInstance<T>()`、`default(T)`→`default` 已由 E10 完成，C33 跑在其后才能把「E10 已处理/未覆盖」分类做对（D6）；generic-deepen 的 typeof/is-as/静态字段改写与型变/约束无站点交集，先后皆可，故不需要强边。
2. **generic-deepen 默认关**：锚它会让本 pass 在拓扑上隐入一条 opt-in 链，而 C33 的审计价值在默认链上同样成立。
3. **只读 pass 对顺序不敏感**：不改写、不生成文件，与 record-readonly/array-method（同锚 generic）即使拓扑相对顺序不同，审计结论也一致（二者不改变型变签名与约束）。
4. 与 `thread-static`（stub，C09，Declarative，同锚 generic）无功能依赖。

### 3.4 Stub→Implemented 翻转清单

1. [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)：删除 `Ids.VarianceAudit` 常量（L29）与 Stubs 表条目（L122-L131）；常量迁至 [PassCatalog.cs#L6-L25](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L6-L25) 的 `PassIds`。
2. [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)：`Implemented` 表在 GenericDeepen 条目之后插入 `variance-audit` 描述符（`runAfterIds: new[] { PassIds.Generic }`，verify 双 false，defaultEnabled=false，SupportLibrary=None，catalogItems "C33"）。插在 GenericDeepen 之后、RecordReadOnly 之前，锁定拓扑相对序；`java-keywords` 的 RunAfter 无需改动（本 pass 不是其锚点）。
3. [PassChainFactory.cs#L93-L237](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L93-L237)：switch 增 `PassIds.VarianceAudit => new VarianceAuditPassAdapter(config.VarianceAuditOptionsFactory(), ...)`；`PassChainConfiguration` 增 `Func<VarianceAuditOptions> VarianceAuditOptionsFactory`（懒加载，仿 JavaKeywordOptionsFactory，L47-L48）。无 legacy toggle（不新增 `--no-variance-audit`）。
4. 新增 CLI `Adapters/VarianceAuditPassAdapter.cs`（§5）。
5. [Program.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs) 的 `ConvertProjectOptions` 与 parser：增 `--variance-strict`、`--variance-report-info`（§5.2）；通用 `preprocess --pass variance-audit` 由现有 all-disabled toggles 机制天然支持，不新增 verb。
6. [.gitignore#L89-L101](file:///d:/code/cs2j/.gitignore#L89-L101)：增 `.cs2j-variance-audit-src/`（同区登记；顺带记录 `.cs2j-generic-deepen-src/` 当前也未登记，由 C02 侧自行处理，不在本 pass 范围）。
7. 注册表测试：`ProjectPreprocessorsConsistentTests` 与拓扑顺序断言更新（Stub→Implemented、默认值、目录、RunAfter=generic、位于 generic-deepen 之后）；CS2J9001 对该 id 的行为消失；`--list-passes` 行断言。
8. 不新增 `Project*Preprocessor` facade（stub 先行的 pass 无历史 verb/测试依赖，对齐 C21/C25 口径）。

### 3.5 目录与产物约定

- 中间树根 `.cs2j-variance-audit-src/`（descriptor 驱动，runner 建/清/preserve；全部文件字节复制；指纹令牌自动含 `pass:variance-audit=on/off`）。
- 报告：中间树根 `variance-audit-report.json`（主交付物，D13）；不引入 manifest（无改写即无豁免对象；若 M2 在存量工程上噪声不可接受，再按 C25 D13 形态单独立项，M3 候选）。

---

## 4. Core 组件设计

### 4.1 文件清单（`src/CSharpToJava.Core/VarianceAudit/`）

命名空间 `CSharpToJava.Core.VarianceAudit`，组织对齐 `GenericPreprocess/`、`JavaKeywords/`：

```
VarianceAudit/
  VarianceAuditPreprocessor.cs   # 公共入口：
                                 #   VarianceAuditPreprocessor(VarianceAuditOptions?)
                                 #   void BindCompilation(CSharpCompilation)
                                 #   VarianceAuditAnalysis Analyze()（整树一次，缓存）
                                 #   VarianceFileAudit AuditFile(SemanticModel)（逐文件诊断，不改写）
                                 #   string BuildReportJson()（CloseReport，D13 排序/无 BOM）
  VarianceSiteInventory.cs       # 声明点（VariantDeclarationRecord）+ 使用点（VarianceUseRecord）枚举
                                 # VarianceKind(Out/In)、SiteContainer(Interface/Delegate)、
                                 # UseFlow(Producer/Consumer/Both)、UseTag(VarianceConversion/NestedComposition)
  VariancePositionFacts.cs       # 成员签名内位置方向判定：
                                 #   返回/参数(ref/out/in/值)/属性/索引器/事件
                                 # 嵌套组合下推（D7）、基线错误树抑制（D12）
  UseSiteSafetyClassifier.cs     # 使用点流向汇总（D3）、型变转换结构化判定（D3a）、
                                 # 委托/方法组转换（D8）、suggestedWildcard 计算（D11）
  TypeMutabilityTable.cs         # D9 映射可变性表（种子数据 + 保守默认 + 可配置覆盖路径预留）
  ConstraintAuditor.cs           # D5a 约束逐项识别 + D5b 依赖站点枚举（default/new/装箱/数组/Nullable）
                                 # E10 产物识别（Activator.CreateInstance<T>/Array.CreateInstance）
  VarianceAuditOptions.cs        # Strict/ReportInfo（§4.5）
  VarianceAuditStatistics.cs     # 计数模型（§4.5），通用字段 FilesScanned/FilesSkippedClean 等
  VarianceAuditDiagnostic.cs     # VarianceSeverity(Info/Warning/Error)/VarianceCodes(VA0xx)/Diagnostic/ToString
```

不新增跨 pass 共享文件（型变/约束判定词汇为 C33 专有；C30/C02 落地时如需共用再按 catalog §3.3 族内抽取，不预先建设）。

### 4.2 编译口径

与 C02/JK/RR 一致：`CSharpParseOptions(LanguageVersion.Latest)` + runner 的 `GetBasicReferences()` + allowUnsafe；adapter 负责合成 ImplicitGlobalUsings（复用 [JavaKeywordsPassAdapter.BuildCompilation](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/JavaKeywordsPassAdapter.cs#L275-L312) 同款）。整树一次编译、一次 `Analyze`；逐文件产出诊断（保证 RelativePath 前缀复用 runner 管线），报告在 `AfterFilesAsync` 统一落盘。

### 4.3 VarianceSiteInventory（声明点 + 使用点）

- **声明点**：遍历编译内 `INamedTypeSymbol`（接口/委托，用户源内声明；外部程序集定义的 BCL 型变类型如 `IEnumerable<out T>`/`Func`/`Action` 通过使用点自然出现，不登记声明点而登记 `Origin=Metadata`）。记录：容器种类、类型显示名、类型参数名、`Variance`、约束种类数组、文件/span。
- **签名内出现位**：对该类型的全部成员（含接口基接口列表中的传递出现）按 VariancePositionFacts 计算每个 T 出现的方向与组合链。
- **使用点**：全树 `DescendantNodes` 枚举型变闭型（`INamedTypeSymbol.TypeParameters.Any(p => p.Variance != None)` 的构造类型，含 `IArrayTypeSymbol` 引用型数组协变）出现于：局部/字段/属性/事件声明、形参、返回类型、base 列表、对象创建/转换目标、lambda 形参；每个使用点挂：
  - `Flow`：按作用域内成员访问汇总（D3②）；形参/返回/字段这类跨边界站点没有「作用域内使用」可汇总时，按声明类型位置与跨边界保守规则标位（跨边界且无更多信息 → Both）；
  - `VarianceConversion`：所在转换边的结构化判定（D3a）；
  - `CompositionChain`：D7；
  - `SuggestedWildcard`：Extends/Super/Invariant（D11，仅入报告，驱动不了任何改写）；
  - `EnclosedBy`：ExpressionTree（复用 [ExpressionTreeFacts.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs) 的判定，表达式树内不改变任何东西，只标 enclosed）、Dynamic。

### 4.4 UseSiteSafetyClassifier 安全判定输出

每个使用点最终得到三级安全结论之一：

- `Safe`：方向与声明型变一致且无转换依赖（如纯 Producer 地使用 `IProducer<out T>` 的产出，或映射类型无扩大面）→ Info（VA013/VA014）；
- `NeedsWildcard`：该位置依赖型变隐式转换，今天不变发射会导致 javac 拒绝 → VA010（协变）/VA011（逆变），消息给出方向（「use site requires `? extends X` / `? super X`; rewrite is out of scope, manual redesign needed」）；
- `Unsafe`：Both 且依赖转换（VA015）、映射类型可变性扩大（VA012，D9）、解析不全的保守候选（VA015）。

委托使用点同样三态，Invoke 流向 + 转换边双证据（D8）。

### 4.5 Options / Statistics / 报告模型

- `VarianceAuditOptions`：`bool Strict = false`（M2：§6 表中 Error 列生效）；`bool ReportInfo = true`（VA013/014/016/022/024/025/026/070 是否进诊断流；报告 JSON 永远含全部级别）；`bool CheckMappedMutability = true`（D9 表开关，应急关停用）。不设 Mode（本 pass 没有 Rewrite 模式，D1/D11）。
- `VarianceAuditStatistics`（通用字段对齐各 pass）：`VariantDeclarations`（按 Out/In × Interface/Delegate 分桶）、`MetadataVariantTypesUsed`（BCL 型变类型种类计数）、`UseSitesTotal`、`UseSitesByFlow`（Producer/Consumer/Both）、`VarianceConversions`（协变/逆变/数组分桶）、`NestedCompositionSites`、`UnsafeUseSites`、`ConstraintsByKind`（struct/unmanaged/new/notnull/default/class）、`ConstraintDependentSites`（default/new/boxing/array/nullable 分桶）、`BaselineErrorFilesSkipped`、`ReportsWritten`。
- 报告 JSON（D13）：`{ schemaVersion: 1, passPresent: {generic, genericDeepen}, options: {strict, reportInfo, checkMappedMutability}, declarationSites: [], useSites: [], constraintSites: [], totals: {} }`；路径相对 SourceRoot；跨运行字节稳定。
- `VarianceAuditDiagnostic`：`Severity/Code/Message/FilePath/Line/Column`，`ToString()` 为 MSBuild 风格（`file(line,col): warning VA010: ...`），与 GD/JK/RR 同格式。

---

## 5. CLI 适配器与集成

### 5.1 VarianceAuditPassAdapter

新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/VarianceAuditPassAdapter.cs`，继承 [ProjectPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L117-L269)，走标准 spine（不 override `ExecuteAsync`）：

- `Descriptor => PassCatalog.ImplementedById(PassIds.VarianceAudit)`；
- `CreateParseOptions`：`LanguageVersion.Latest`；`CompilationName "variance-audit-preprocess"`；`CreateAdditionalSyntaxTrees` 复用 JK 同款 global using 合成；
- `CompilationReadyAsync`：`BindCompilation(compilation)` + `Analyze(options)`，缓存 inventory；
- `RewriteFileAsync`：调 Core `AuditFile(model)` 收集本文件诊断并入统一个 `List<VarianceAuditDiagnostic>`（补 RelativePath），**恒返回 null**（全部文件字节复制，PassFileRewrite 永不产生）；
- `AfterFilesAsync`：写 `variance-audit-report.json`（UTF-8 无 BOM，D13）；
- `VerifyBuild => false`、`VerifyTests => false`、`DeleteBuildOutputsAfterRestore => false`（审计不 restore/不 build/不 test）；
- `OnRestoreFailed/OnBuildFailed/OnTestsFailed`：形式实现（spine 不可达，保留码位 VA050-052 定义以防未来模式扩展）；
- `HasErrors => _options.Strict && _diagnostics.Any(d => d.Severity == Error)`——普通模式永远成功（Warning 不阻断），strict 模式 Error → runner 既有机制中止链、退出码非 0（[CliPreprocessChain.cs#L169-L183](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L169-L183)）；
- `BuildChainSummaryLine`：`variance-audit: D variant declaration(s), U use site(s) (P/C/B), V variance conversion(s), K unsafe; C constraints (struct/unmanaged/new/...); report=variance-audit-report.json`；
- `BuildChainDiagnosticLines(verbose)`：Warning/Error 常显，Info 仅 verbose。

### 5.2 开关 / 指纹 / 独立入口

- 翻转后默认关；`--enable-pass variance-audit` 开启即审计（无模式选择）。
- convert-project 选项：`--variance-strict`（M2 闸门）、`--variance-report-info`（默认 true，`--variance-no-report-info` 抑制 Info 诊断流），经 `PassChainConfiguration.VarianceAuditOptionsFactory` 懒构造。
- 独立入口：通用 `preprocess --pass variance-audit`（[BuildAllDisabledToggles](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L86-L87) 机制）携带相同选项；**可直接对原始源码出报告**——此时 E10 未运行，报告 `passPresent.generic=false`，`new T()`/`default(T)` 以原始形态入账（D6 已内置两种形态识别）。这是「链位较晚丢失出处」顾虑（E10 已改写站点）的标准缓解姿势。
- 指纹：全集令牌自动含 `pass:variance-audit=on/off`；输出树字节等于输入，下一环 `opts.Source` 串联零特殊处理；无支撑库过滤项。

### 5.3 失败语义

- Audit（默认）：零失败面，只复制 + 报告；唯一失败是报告写盘 IO 错误（按 Error 处理）。
- Strict（M2）：不安全站点在审计阶段即中止，**不产生也不修改任何源码**——失败交付物是报告本身，不存在「半改写」窗口。

---

## 6. 诊断策略（具名报码取代笼统告警）

码位 VA0xx（MSBuild 风格，只追加不改号）：

| 码 | audit 默认 | strict（M2） | 含义 |
|---|---|---|---|
| VA001 | Info | Info | 型变声明点登记（out/in × interface/delegate；消息含类型名、参数名、容器） |
| VA010 | Warning | **Error** | 协变型变依赖转换使用点（`I<D>`→`I<B>` 方向），Java 不变发射将不可编译/需 `? extends`（D3a） |
| VA011 | Warning | **Error** | 逆变型变依赖转换使用点（`Action<B>`→`Action<D>` 方向），需 `? super`（委托/方法组同族，D8） |
| VA012 | Warning | **Error** | 映射后 Java 类型暴露 C# 契约外变更成员（违规写门面，D9；消息列出 mutatingMembers） |
| VA013 | Info | Info | 纯 Producer 使用点（通配符候选 extends，D11 预留） |
| VA014 | Info | Info | 纯 Consumer 使用点（通配符候选 super） |
| VA015 | Warning | **Error** | Both 使用点且依赖型变转换，或成员访问/转换解析不全的保守候选（D3②/D3a） |
| VA016 | Info | Info | 嵌套泛型组合型变站点（消息含 compositionChain，D7） |
| VA020 | Warning | **Error** | `where T : struct` 丢弃（关联 D5b 依赖站点数；消息可操作） |
| VA021 | Warning | **Error** | `where T : unmanaged` 丢弃（**当前零诊断**面，§1.1#9；附指针/sizeof/封送提示与 C06 交叉引用） |
| VA022 | Info | Info | `new()` 约束声明；构造点由 E10 处理（关联 Activator 站点数，D6） |
| VA023 | Warning | **Error** | E10 不覆盖的残余 `new T()`（带初始化器/参数），直转行为未定义（D6） |
| VA024 | Info | Info | `notnull` 约束（NRT 仅编译期，当前静默） |
| VA025 | Info | Info | `default` 约束（override 还原，纯签名元数据，当前静默） |
| VA026 | Info | Info | `class`/`class?` 约束（当前静默；区分 nullable annotation 入报告） |
| VA027 | Warning | **Error** | struct/unmanaged 约束 T 上的 `default(T)`/`default` 站点（Java 侧兜底 `null`，确定语义错误，§1.3#3） |
| VA028 | Info | Info | struct/unmanaged 的其他依赖站点计数（装箱身份/`T[]`/`Nullable<T>`/`T?`；M1 只量化不判错，D5b） |
| VA030 | — | **Error** | strict 汇总闸门（不安全站点数 >0 时的收口诊断，便于 CI 单点匹配） |
| VA050/051/052 | — | — | restore/build/test 失败码位预留（审计 verify 恒关，M1/M2 不可达） |
| VA070 | Info | Info | 报告写出（路径、各类计数） |

消息纪律：每条 VA010-015/020-023/027 必须含**位置 + 分类 + 原因 + 动作建议**（如「site `<IEnumerable<string> values>` relies on covariant conversion to IEnumerable<object>; Java emits invariant types and this use will not compile; redesign at the boundary or await the use-site rewrite pass」），禁止只说「variance dropped」。VA013/014/016 的措辞明确是「未来 use-site 改写候选」，不暗示当前输出正确。

与既有诊断关系：CS2J1003（三个转换器声明点告警）与 CS2J1002（struct/new 笼统告警）M1/M2 不动（D14）；单文件直转路径仍只靠它们兜底。

---

## 7. 测试计划

### 7.1 Core 单测（`tests/CSharpToJava.Tests/VarianceAuditUnitTests.cs`）

内联源码 → 整树 `CSharpCompilation` → `BindCompilation`/`Analyze`/`AuditFile` → 诊断断言 + 报告 JSON 断言（仿 GenericDeepen/JavaKeywords 单测）。

**型变声明与使用点**：

- `interface IProducer<out T> { T Produce(); }`：VA001 + 纯产出调用 VA013（Producer），无 VA01x；
- `interface IConsumer<in T> { void Consume(T x); }`：VA001 + VA014（Consumer）；
- 协变依赖转换两侧：`void M(IProducer<object> x)` 传 `IProducer<string>` → VA010；`IEnumerable<string>→IEnumerable<object>` 同；
- 逆变：`Action<object>` 赋给 `Action<string>` 形参 → VA011；方法组转换同型一例；
- 索引器 get+set 型（用户接口内 Both 本是 C# 编译错误 → 基线错误树抑制，D12）；合法的「同一值既被产出又被消费」局部使用 → Flow=Both（VA015 仅在同时依赖转换时）；
- `delegate TResult Conv<in TIn, out TOut>(TIn x);`：双声明点 + Invoke 流向 + 委托赋值型变转换；
- 嵌套组合：`interface IX<out T> { void M(IEnumerable<Action<T>> x); }` 型用例，断言 compositionChain（Consumer∘out(IEnum)∘in(Action)=Producer，D7）；`IFoo<out T>` 套 `IBar<in T>` 正反各一例；
- 引用类型数组协变赋值（`string[]→object[]`）登记；struct 数组不登记型变转换；
- 映射可变性表：构造一个种子表覆盖的只读→可变映射用例 → VA012 且消息含变更成员名；表未覆盖类型走保守 VA012/VA015 一正例；
- 表达式树内使用点：`Expression<Func<...>>` 场景只标 enclosed、不产 Error（不碰树）；
- 语义模型缺失回退（Fallback）一例如可构造；`class C<out T>` 基线错误树不叠加 VA01x（D12）。

**约束逐项**：

- `where T : struct` → VA020 + 依赖站点关联；`default(T)`/`default` 字面量在该 T 上 → VA027（对照无约束 T 的 default：不报 VA027）；
- `where T : unmanaged` → VA021（**钉死当前零诊断现状下本码是唯一信号**；同时钉死 GetTypeInfo 对「unmanaged」伪类型的实际返回）；
- `where T : new()` + `new T()` → E10 形态两种跑法：先过 GenericPreprocessor 再审计 → VA022 + Activator 关联 Info；独立对原始源码审计 → 原始 `new T()` 入账且 `passPresent.generic=false`；`new T(){ X=1 }` 残余 → VA023；
- `notnull` → VA024（不产 bounds，对照 [ProjectRegressionShapeTests.cs#L217-L267](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectRegressionShapeTests.cs#L217-L267) 既有断言不受影响）；`where T : default` → VA025；`class`/`class?` → VA026；
- `where T : IComparable<T>` 不产任何 VA 码（已映射 bounds 的对照负例）；
- 多约束子句混合（`where T : struct, IComparable<T>, new()`）分桶不重复。

**工程性**：

- audit 输出字节不变（逐文件 null rewrite）、报告 JSON 两次运行字节一致（排序/相对路径/无时间戳）；
- 默认模式 `Success==true`（Warning 不阻断）；`Strict=true` 下构造 VA010/VA027 负例 → `HasErrors==true` 且含 VA030；Info-only 用例 strict 下仍成功；
- 零型变/零约束工程：零诊断、零 VA001、报告仍写出（VA070）、文件全 clean。

### 7.2 项目级 fixture（`tests/SampleVarianceAuditSolution/`）

仿 SampleJavaKeywordSolution：Lib + App + Tests + sln，net10.0，纳入 ProjectTests：

- **Lib**：协变生产接口及其实现（`IProducer<out T>`/`IReadOnlyList` 风格只读接口）、逆变消费接口、`in/out` 混合委托、`struct`/`unmanaged`/`new()`/`notnull`/`default` 约束各一；嵌套泛型组合一处；
- **App**：型变依赖转换的正/负用法（协变赋值、`Action<object>→Action<string>`、数组协变）、default(T) 值语义点、`new T(){...}` 残余点；
- **Tests 工程**：只验证 .NET 侧行为基线（本 pass 不改写，测试保证 fixture 自身长期可编译/通过）；
- 断言矩阵：audit 下 `Success==true`、统计各桶计数、报告 JSON 站点齐全且与 fixture 一一对应、中间树字节等于输入；strict 下按预期失败（退出码非 0、VA030 可匹配）；关闭 pass 时链输出与今天字节一致；preserve/指纹二次运行命中缓存。

### 7.3 Release build + test 与全量回归

- `dotnet build -c Release`（CSharpToJavaConverter.slnx）+ Tests/ProjectTests/ConversionTests 三工程 `dotnet test -c Release`（本设计仅文档，命令由实施 PR 执行）；
- 链组合：与 E10 同树（Activator/default 形态关联）、与 generic-deepen 同树（默认关/开两态，结论互不影响）、与 record-readonly 同树（只读 pass 与整树回滚 pass 并存）。

### 7.4 CLI / 一致性 / 既有测试

- `--enable-pass variance-audit`、`--variance-strict`、`--variance-no-report-info`、`--list-passes`、preserve、独立 `preprocess --pass variance-audit` 对原始源码出报告（`passPresent` 位图正确）；
- [ProjectPreprocessorsConsistentTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs) 追加 adapter/描述符/目录断言；拓扑断言锁定 `generic → generic-deepen → variance-audit → record-readonly`；CS2J9001 对该 id 消失；
- [GenericConstraintDiagnosticsTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/GenericConstraintDiagnosticsTests.cs) 全部原断言**不动**（转换器行为零变化）；在本 pass 测试中以同批源码补审计侧对照（CS2J1003 与 VA001/013 并存、CS2J1002 与 VA020/022 并存，D14）。

---

## 8. 默认开启验收门槛

**Audit 模式可以较早默认开**（只复制 + 报告 + Warning、字节不变），门槛刻意比重写型通道低：

1. 三工程 `dotnet build/test -c Release` 全绿、红灯集不扩大；pass 关闭时链字节一致（默认关落地 → 默认开是单独一次翻转）。
2. csharp-sdk 样本补扫（不杜撰数字，先跑后填）：型变声明密度（Out/In × interface/delegate）、BCL 型变类型使用分布（`IEnumerable`/`Func`/`Action`/`IReadOnly*` 占比）、使用点 Flow 分布、VarianceConversion 命中率、VA012 命中率、约束分桶（struct/unmanaged/new/notnull/default/class）、VA027 实际命中数。
3. Warning 噪声量可接受：每使用点至多一条主诊断 + 报告聚合视图；Info 不进常规输出。
4. 多一轮整树复制 + 语义编译的耗时在基线样本实测可接受（险 11），指纹/增量缓存验证通过，报告字节稳定。
5. VA010/011/015 的人工抽检：假阳可以接受，**事实错误不可以**（把真 Producer 报成 Consumer 一类）；D3a 漏检率有抽样结论（方法组/动态/错误类型三面上的保守率）。

**Strict 闸门（M2）opt-in 长期保持**，除上述外还须：

6. SampleVarianceAuditSolution 在 strict 下负例全部按预期失败、正例（零不安全站点）strict 通过。
7. sdk 样本上全部 Error 类码有人工分类结论；VA027/VA023 命中站点逐一确认确属今天的错误输出面（这两类是「确定产错」，最适合作为首批 strict 推广对象，可考虑分批 strict：先只开 VA023/027 子集）。

**use-site 改写不在任何默认开门范围内**（D1/D11）；其立项门槛另定（Java 发射层通配符模型 + 捕获转换原型 + 真实语料 PECS 分类正确率）。

---

## 9. 里程碑

| 里程碑 | 内容 | 粗估规模 |
|---|---|---|
| M1 | **纯审计报告**：VarianceSiteInventory（声明点+使用点）+ VariancePositionFacts（含嵌套组合 D7）+ UseSiteSafetyClassifier（D3/D3a/D8）+ TypeMutabilityTable 种子 + ConstraintAuditor（D5/D6 双形态）+ VA001-028/070 诊断 + 报告 JSON（D13）+ adapter（标准 spine、恒 null rewrite、verify 双关）+ 注册表翻转（Implemented、默认关）+ §7 单测/fixture/CLI/一致性/拓扑测试 | Core ~650 行 + CLI ~200 行 + fixture |
| M2 | **不安全站点 Error 闸门**：strict 语义收口（Options 已先行就位，M2 做闸门联调与 VA030）+ Sample 负例 + 分批 strict 策略（VA023/027 先行）+ sdk 语料补扫与 §8 门槛数据 + audit 默认开翻转评审 | Core ~100 行（诊断升级/汇总）+ 语料工程 |
| M3（另立项，不在本特性） | use-site `? extends/super` 改写（Java 发射层通配符模型、捕获转换、重载回归）；D9 映射可变性表全量化；D5b③④ 装箱/数组/Nullable 逐站点判错；CS2J1002/CS2J1003 转换器告警退役；manifest 豁免（如确有需要）；独立 verb 的原始树报告纳入常规流程 | 独立设计/排期 |

---

## 10. 风险

| # | 风险 | 证据/说明 | 缓解 |
|---|---|---|---|
| R1 | 假阴：违规写入门面漏判（catalog 点名的核心风险） | D9 的映射差异表不可能首日完备；成员访问流向汇总遇到 dynamic/错误符号会断链 | 断链保守 Both+VA015（D3）；表未覆盖默认「可能扩大」；M1 不产任何「Safe」的强断言写入产物（只审计）；VA012 随语料迭代 |
| R2 | 假阳淹没：保守策略在泛型密集工程（SDK 风格代码大量 `IEnumerable<T>`/`Func`）噪声高 | Producer-only 是绝大多数真实形态 | VA013/014 仅 Info+报告；只有依赖转换/可变性扩大才 Warning；报告按文件/类型聚合；门槛 3 实测噪声 |
| R3 | D3a 型变转换检测近似：Roslyn 公共 API 无 variance 标志，结构化比较可能漏方法组/自定义转换纠缠、嵌套委托多态 | C25 D11 同款「只用公共 API、宁假阳」原则 | 候选不全即 VA015；单测覆盖方法组/委托/数组三面；报告保留原始源/目标类型显示名供人工复核 |
| R4 | E10 链位导致出处变化：审计在 generic 之后，`new T()` 已变 Activator、`default(T)` 已变 `default` | D6 | Core 双形态识别 + `passPresent` 位图；独立 verb 对原始树出报告（§5.2）；不强求改锚 generic-deepen（§3.3） |
| R5 | 未来 use-site 改写的通配符捕获复杂性：capture conversion 改变可赋值性与重载解析，机械插入 `? extends` 可造新坏 Java；逆变参数在返回闭型中翻转等组合极易错 | D7/D11 已枚举组合链 | 本期只产数据不碰发射层；改写另立项且必须有 Java 编译+行为双预言；报告字段即其规格 |
| R6 | 语义模型对 unmanaged/notnull/default 伪类型的 API 行为随 Roslyn 版本差异 | 当前 4.12（[CSharpToJava.Core.csproj#L12](file:///d:/code/cs2j/src/CSharpToJava.Core/CSharpToJava.Core.csproj#L12)）；`HasUnmanagedConstraint/ConstraintKind` 可用性需实测 | 语法伪类型 + 长期稳定 API（HasValueTypeConstraint/HasConstructorConstraint/IsUnmanagedType）双轨，新 API 仅交叉验证；单测钉死 |
| R7 | 与 CS2J1002/CS2J1003 双报造成「同一处多条告警」体验 | D14 | 码位分工不同（声明一句话 vs 使用点/依赖面）；审计默认关；报告提供聚合视图；不做跨 pass 抑制；退役随默认开门 |
| R8 | 审计多一轮整树复制与编译耗时 | 险 11；verify 双关已省 restore/build/test | clean 文件字节复制；门槛 4 实测；未来统一引擎合批（catalog §5.4） |
| R9 | struct/unmanaged 依赖面 M1 枚举不全（装箱身份、`T[]` 协变、Nullable、泛型数学） | D5b 把 default/new 之外降为 VA028 Info 计数 | 先量化再判错；M3 扩展；VA027/023 这两个确定错误面零假阴优先 |
| R10 | BCL 元数据型变类型（外部程序集定义的 `out/in`）与源内声明混报 | `Origin=Metadata/Source` 区分；统计分桶 | 声明点只登记源内；使用点两类都枚举并在报告标来源 |

---

## 11. 并行协调

| 相邻工作 | 边界 | 顺序/依赖 |
|---|---|---|
| **E10 `generic`（已实现，默认开）** | 构造点/默认表达式重写归 E10；C33 审计声明与残余、识别其产物（Activator/Array.CreateInstance/default 字面量）并交叉引用，不重复重写 | RunAfter=generic 强边已在 stub；同树组合测试（§7.3）；若 E10 未来扩大 new() 覆盖（带初始化器），VA023 分类同步收窄 |
| **C02 `generic-deepen`（已实现，默认关）** | typeof(T)/is-as/静态字段/token 传播与本 pass 无站点交集；同属泛型擦除补偿族（[catalog L908](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L908)） | 无强依赖（不锚它，§3.3）；复用其 residual scan 的「strict 升 Error + 统计分桶」范式（[GenericDeepenPassAdapter.cs#L89-L130](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/GenericDeepenPassAdapter.cs#L89-L130)）；报告 `passPresent.genericDeepen` 位 |
| **C30 `closed-metadata`（stub，设计稿）** | C30 消费 C02 token 做 Java 反射闭型贯穿；C33 做声明-site 型变安全与约束显式化。二者可能在同一泛型密集工程同时开启 | 无 RunAfter 边；报告 JSON 词汇（totals/sites/severity）互相看齐；未来若抽公共审计报告组件，二者为首批消费者 |
| **C09 `thread-static`（stub）** | 同 Stage=Declarative、同锚 generic | 仅注册表插入顺序协调（翻转时拓扑断言各自锁定），无功能交互 |
| **C25 `dynamic-desugar`（设计稿，审计型范本）** | 共享 audit-only pass 的 adapter 范式（恒 null rewrite、verify 双关、报告 JSON、strict 闸门、独立 verb 缓解晚链位） | 无依赖；本设计直接沿用其纪律（D12/D13/R3 同款）；若 C25 先落地公共报告写盘工具，C33 改为消费 |
| **C06 `unsafe-ffm`（stub）** | unmanaged 约束下的指针/stackalloc/Marshal 站点的实际改写归 C06 | C33 只在 VA021 消息/报告中交叉引用，不判指针 lowering 正确性；C06 落地时反向引用本 pass 的 unmanaged 声明清单 |
| **E09 unsigned（已实现）** | struct 约束的 T 实参可能是无符号类型，E09 在链更早处类化 | C33 看到的是 E09 之后的树；测试组合确认审计不把 unsigned 支撑类型误判为值类型违规 |
| **转换器（M3 才触碰）** | CS2J1003（[ClassTransformer.cs#L186-L190](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L186-L190) 等 5 处）与 CS2J1002（L229/L231/L234-236/L243-246）的退役 | 独立 PR，在 audit 默认开门、语料验证后；退役前单文件直转路径保留兜底，原测试断言不改 |

**热文件清单**（落地冲突面）：[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)、[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)、[PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)、[Program.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs)、[.gitignore](file:///d:/code/cs2j/.gitignore)、[ProjectPreprocessorsConsistentTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs) 与注册表拓扑断言；新增全部隔离在 `src/CSharpToJava.Core/VarianceAudit/` 与单个 adapter 文件；M1/M2 **零转换器改动**。

---

## 12. Worktree 分配

只写不执行（本设计未执行任何 git 写操作；以下命令在实施 PR 中由执行者运行）：

- 分支：`feature/preproc-c33-variance-audit`
- 目录：`.worktrees/preproc-c33-variance-audit`（`.worktrees/` 已在 [.gitignore#L77-L85](file:///d:/code/cs2j/.gitignore#L77-L85) 忽略）
- base：`953293b9`（`docs: update doc base commit`，已只读核验存在：`953293b908e9143437c45f287fb4277e890c398e`）

```powershell
git worktree add .worktrees/preproc-c33-variance-audit -b feature/preproc-c33-variance-audit 953293b9
```
