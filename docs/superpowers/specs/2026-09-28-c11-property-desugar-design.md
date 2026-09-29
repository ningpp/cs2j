# C11 Property 去糖（member-desugar 锚点任务）设计

**日期**: 2026-09-28
**范围**: 新增 CLI 级 C#→C# 预处理器族 `member-desugar` 的共享基建与 property 规则（Core `MemberDesugar/` + CLI 单适配器），把属性归约为「后备字段 + `get_X`/`set_X` 普通方法」的可编译 C#；本特性不改任何 C#→Java 转换代码
**状态**: 设计已复核修正（2026-09-29 对锚实现核验：overrides 查找次序、Core 成员可见性、restore/verify 三步复刻 MakeReadonly 而非 JK），待实施
**族属**: catalog P2「成员去糖族」（C11 property / C12 indexer / C13 event / C14 operator，未来并入 C46/C27/C39；[preprocessor-catalog.md §3.3](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L904-L904)）。**C11 是该族锚点任务**：共享基建与注册表翻转在本任务完成，C12-C14 基于本任务合入后的 pass 扩展

---

## 1. 背景与目标

### 1.1 property 在 Java 侧的问题

Java 没有属性语法，C# 属性必须在产物中表现为字段 + 访问器方法。当前这件事完全压在主转换器的单文件路径上：

- [PropertyTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs) 共 192 行高密度单行代码，一个类内耦合：自动属性后备字段、full property、表达式体属性、`init`（[L21](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L21-L21) 仅把 init 当 set、[L73-L74](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L73-L74) 只补一条 doc 注释）、显式接口实现（L12）、hiding（[L110-L128](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L110-L128) 的基类扫描 + `_newSlot` 后缀）、struct clone（L36/L62-L64）、访问器可访问性（L88-L98）、yield 访问器（L48-L55）、XML/JSON 注解分发（L142-L183），文件开头 40 行即有 10+ 分支（catalog C11 证据，[preprocessor-catalog.md:384-394](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L384-L394)）。
- 使用点改写散落在表达式层且依赖**首字母大写启发式**而非纯符号：[ExpressionTransformerFacade.cs:246-254](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs#L246-L254) 对「大写开头」成员名凭空合成 `getXxx()`；[L352-L366](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/ExpressionTransformerFacade.cs#L352-L366) 经 `IPropertySymbol` 生成 `getX`；[AssignmentTransformer.cs:81-96](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs#L81-L96) 处理 set/复合赋值，并对无 setter 的只读属性直接按 Java 侧字段名写后备字段（[L88-L92](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs#L88-L92)，依赖 [JavaNaming.GetAutoPropertyBackingFieldName](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/JavaNaming.cs#L99-L127)）；构造器内同类写入在 [StatementTransformer.ExpressionAndReturn.cs:507](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs#L507-L507)。
- 回归密集且多处是「转换器内补丁」：[PropertyHidingTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/PropertyHidingTests.cs)（hiding 时干脆不生成派生类 getter，靠丢成员保 Java 分派）、[ExplicitInterfacePropertyConflictTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ExplicitInterfacePropertyConflictTests.cs)（同名显式接口属性与类属性碰撞，靠 `_newSlot` + `@CompatPropertyName` 注解续命）、[PropertySetterExpressionBodyTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/PropertySetterExpressionBodyTests.cs)（setter 表达式体的 pre-statement 曾被丢弃产出非法 Java `_chainVal`）、[AutoPropertyFieldAccessTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/AutoPropertyFieldAccessTests.cs)。
- 接口属性在 [InterfaceTransformer.cs:170-193](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/InterfaceTransformer.cs#L170-L193) 抽成抽象 getter/setter（剥离字段、清空方法体）；C# 8 默认接口实现（DIM）经 [L159-L165](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/InterfaceTransformer.cs#L159-L165) 加 Java `default`。record 位置属性由 [RecordTransformer.cs:248-272](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/RecordTransformer.cs#L248-L272) 单独生成 getter 并跳过同名字面属性。
- `init`/`required` 在转换器侧没有系统处理：init 与 set 同路径（仅注释差异），`required` 全仓只在 [RecordConversionPlan.cs:319](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs#L319-L319) 与 [DeclarationRewriter.cs:203-204](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/DeclarationRewriter.cs#L203-L204) 被 E11 用来**制造** required，消费侧无对应逻辑。

实验形态 [LowerProperty.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerProperty.cs)（255 行）已证明「属性访问 → 方法调用」的目标形态可行（[L166-L177](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerProperty.cs#L166-L177) 默认 `getX/setX`，含 Array.Length/TypeMappings/Count 特判）；同目录 LowerIndexer/LowerEvent/LowerOperator 即 catalog 所称「Lowering 四件套」。但依据 [catalog §4 风险 9](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L928-L928)，Lowering 仅被 Java2 对比测试引用、生产不可达，**本 pass 只借形态，不复用代码**。

### 1.2 目标

在 C#→C# 层把全部可处理属性归约为：

1. **普通方法**：getter `get_X()`、setter `set_X(T value)`（C# 元数据规范访问器名，详见 §2 决策 1），修饰符/可访问性/属性/文档/trivia 平移；
2. **后备字段**：自动属性与带初始化器的属性合成确定性命名的源内字段（§2 决策 3）；
3. **使用点全改写**：读/写/复合赋值/`++`/`--`/方法组/`base.X`/对象初始化器/`nameof(X)`，全部以 SemanticModel 绑定为准，禁止文本替换；
4. 产物仍是可编译 C#，由中间树 `dotnet build -c Release` + `dotnet test -c Release` 证明 .NET 半区等价（catalog §5.1 两半区方法论）。

覆盖形态：自动属性（含 get-only、`private set`、static、初始化器）、full property（块体/表达式体/计算属性）、`init`、`required`、显式接口实现、hiding（`new`/隐式）、virtual/abstract/override/sealed、接口属性与 DIM、访问器可访问性、ref 返回以外的常规类型。

明确不做（留边界）：索引属性 `this[...]`（C12）、事件（C13）、运算符（C14）、`with` 表达式（C27）、终结器（C39）、new 隐藏/显式接口/协变返回的 **Java 分派收口**（C46）、位置 record 参数属性（v1 显式跳过，见 §4/§6）、Java 侧 `isX` 布尔命名决策（§2 决策 2）。

### 1.3 硬不变式

1. 中间源码树预处理后永远可编译（整树重编译相对基线无新增错误），plan 为原子单位（全转或全不转），范式与 record-readonly/java-keywords 完全相同；
2. 不静默丢任何访问器：get/set 各有归宿（方法体、abstract、显式接口实现或显式诊断），禁止注释/占位/原样透传（[catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L931-L931)）；
3. 一切站点判定来自全编译 SemanticModel（[catalog §3 横切约束](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L896-L896)：凡改名/改签名通道必须基于全编译符号表）；
4. 幂等：产物二次执行零 diff；干净项目字节级复制（含 BOM 保持）。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| 1 | 访问器方法命名 | **C# 规范名 `get_X`/`set_X`**（小写前缀 + 下划线 + 属性原名）；使用点 `obj.X`→`obj.get_X()`、`obj.X=v`→`obj.set_X(v)` | 这是 C# 编译器对属性访问器的真实成员名（`IPropertySymbol.GetMethod.Name == "get_X"`），语言级保证属性 `X` 存在时用户不可能再声明 `get_X`（CS0102 同槽），去糖后该槽位确定性释放——**天然零碰撞**。否决 Pascal `GetX/SetX`（catalog L388 措辞）：C# 允许属性 `X` 与用户方法 `GetX()` 合法共存，选 Pascal 名会在这类既有类型上制造碰撞，且会撞上 [MethodTransformer.JavaName](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L178-L187) 的 `GetType→getClass` 等特例表 |
| 1b | 与转换器 JavaName 派生的一致性 | C11 期**不动转换器**；默认开启前必须完成「Java 半区消费」改造：`get_X/set_X` 经转换器应派生为 `getX/setX` | 现状 [MethodTransformer.cs:122-129](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L122-L129) 与 [L83-L90](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L83-L90) 把 `get_/set_` 前缀**整体剥成 `X`**（旧遗留路径，[CSharpXmlCompileRegressionTests.cs:602](file:///d:/code/cs2j/tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs#L602-L602) 还锁定了该行为）。去糖树喂给该路径会产出错误 Java 名，故此消费改造是 §8 默认开门禁之一（后续独立 PR，登记在 §5.4） |
| 2 | 布尔属性 isX 惯例 | **C# 半区类型无关**：布尔属性同样是 `get_X`，不引入 is 前缀 | 转换器现状对布尔一律 `getX`（[JavaNaming.GetPropertyGetterName](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/JavaNaming.cs#L26-L30) 无布尔分支，全仓无 `isX` 发射点）。是否改 `isX()` 影响 Java Bean 内省/Jackson 契约，属 Java 半区决策，记入开放问题，C11 不预设 |
| 3 | 后备字段确定性命名与防撞 | **直接复用** [JavaNaming.GetAutoPropertyBackingFieldName](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/JavaNaming.cs#L99-L127) 的算法：属性名 camelCase + Java 关键字逃逸（`Value` 后缀）+ 源序首个声明者保留裸名、后撞者加 `Value` + 显式同名字段防撞；去糖期在共享命名预留表中登记本族全部待生成字段后一次算定 | 复用保证「预处理前转换器隐含的 Java 字段名」==「去糖 C# 显式字段名」，Java 半区零命名漂移；同类型 `Age`/`age`、字段 `age` 共存等碰撞场景现有算法已覆盖（[JavaNaming.cs:108-L126](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/JavaNaming.cs#L108-L126)）。不采用编译器 `<X>k__BackingField`（源内非法标识符） |
| 4 | init-only | init 访问器归约为**普通 `set_X` 方法**（C# 源码无法在方法级表达 ExternalInit modreq）；构造期/初始化器外的写入在中间树合法（编译期约束有意放宽，合法程序运行时行为不变）；plan 统计 InitAccessorsWidened 计数 | init 是纯编译期约束，放宽不改变任何能通过原编译的程序的行为；与 E11 制造 init（[DeclarationRewriter.cs:212](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/DeclarationRewriter.cs#L212-L212)）的方向互逆。对象初始化器调用普通 setter 即可编译 |
| 5 | required 成员 | `required` **不向方法传递**（RequiredMemberAttribute 仅 Property/Field 有效）；对象初始化器（required 成员唯一强制点）按决策 7 重写为 set 调用序列；required 编译期强制在中间树解除，plan 统计 RequiredConstraintsDropped | 同 init，属于编译期约束放宽；`[SetsRequiredMembers]` 构造器无需特殊处理（本来就不初始化）。不把 required 挪到私有后备字段（跨类型初始化器对私有字段不可见，不可行） |
| 6 | virtual/abstract/override/sealed/static/new | 修饰符逐一向方法平移：virtual→virtual、abstract→abstract（无方法体、无字段）、override→override、sealed override→sealed override、static→static、`new`（含隐式 hiding）→`new` 方法 | C# 方法 hiding 语义与属性 hiding 同构，去糖后**由 C# 语言自身保住非虚分派**；Java 侧「Java 万物虚」的分派收口归 C46，本 pass 不造 `_newSlot` 之类 Java 注解 |
| 7 | 对象初始化器 | `new C { A = 1, B = 2 }` 在可插入语句上下文重写为 `var __cs2j_tmp = new C(); __cs2j_tmp.set_A(1); __cs2j_tmp.set_B(2);`（嵌套初始化器逐级提升，接收者只求值一次）；不可提升上下文（字段初始化器、表达式体、查询表达式、特性实参等）→ CS2J5119，plan 原子跳过 | 与 JK-6d 同一「复杂运算元临时提升」纪律；临时名沿用全仓 `__cs2j` 前缀惯例（参 [GotoEliminator/StateMachineBuilder.cs:921-924](file:///d:/code/cs2j/src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs#L921-L924)）。集合初始化器（`.Add` 形态）不属属性面，不动 |
| 8 | 接口属性与 DIM | 接口内属性 → 接口方法声明 `RetType get_X();`/`void set_X(T value);`；带体的 C# 8 默认实现 → 带体的默认接口方法（C# 接口方法天然可带默认实现）；显式接口实现属性 → 显式接口方法实现 `RetType IFoo.get_X() { ... }` | C# 显式接口方法与类自身同名方法可共存（限定名槽位独立），[ExplicitInterfacePropertyConflictTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ExplicitInterfacePropertyConflictTests.cs) 的碰撞场景在 C# 半区天然合法；接口/实现两侧必须在同一 plan 的 TouchedFiles 内联动 |
| 9 | 索引属性边界 | `IPropertySymbol.IsIndexer` 及一切 `ElementAccessExpression` 绑定站点**原样保留**，归 C12 | C12 还需改调用点 `obj[i]` 形态，且 C17 复合赋值依赖其先方法化（[catalog C12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L395-L405)）；共享 Analyzer 会看见但不产出 indexer plan |
| 10 | 事件/运算符边界 | `IEventSymbol`（含字段式事件、add/remove）、`MethodKind.UserDefinedOperator/Conversion` 全部原样保留，归 C13/C14 | 同一符号表索引但规则与文件分目录（§4.5） |
| 11 | nameof/反射契约 | 绑定到被去糖属性的 `nameof(X)`（含 `nameof(C.X)` 限定链右段）替换为原名常量字符串 `"X"`（与 JK-4a 同法）；反射字符串契约（`GetProperty("X")`、XAML、JSON 按名匹配）**不保证**，沿用 E17 同款限制声明与 overrides 机制 | [catalog §4 风险 7](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L926-L926)。去糖后运行时不再有 `PropertyInfo X`，这是属性去糖的固有反射面变化；nameof 字面量化保住编译与最常见字符串契约 |
| 12 | 表达式树跳过 | 属性读写点位于 `Expression<Lambda>` 树内时，该属性 plan 原子跳过（CS2J5117），属性与全部站点原样保留 | 成员访问节点 → 方法调用节点会改变表达式树形态（[catalog §4 风险 8](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L927-L927)）；overrides.force 可在用户知情下解除（同 JK-8 force 语义）。dynamic 站点同理 CS2J5118（force 不解除） |
| 13 | 支撑库 | **无**：不注入 C# 支撑工程，也不新建 Java compat 类 | 目标形态全是 BCL 合法 C#；无注入/过滤/指纹豁免成本 |
| 14 | attribute 分发 | 按 AttributeUsage 做目标感知路由：`[field: X]`→后备字段；访问器上的 attribute→对应方法；`[return:]`→getter 返回值；默认/`[property:]` 特性：Field 目标允许则路由到后备字段，Method 目标允许则路由到访问器方法，仅 Property 可施用 → CS2J5113 该 plan 跳过 | 序列化特性（`[JsonPropertyName]`/`[DataMember]`/`[JsonIgnore]` 等）多为 Property\|Field，路由到字段在 C# 侧可编译；C10 attribute-normalize 落地后消费其归一标记，本规则对原始特性先成立 |
| 15 | 执行形态 | 三阶段（分析→计划→逐文件重写）+ 整树重编译 + plan 级原子回退；适配器整树自驱动（同 JavaKeywords/RR），不走逐文件标准 spine | 使用点跨文件、plan 需按文件回退，逐文件 spine 不适用 |

---

## 3. 注册表与链位契约

### 3.1 现状（stub）

[PassStubCatalog.cs:192-201](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L192-L201) 已登记唯一条目：

| 字段 | 值 |
|---|---|
| Id | `member-desugar`（常量 `PassStubCatalog.Ids.MemberDesugar`） |
| DisplayName | `Member desugaring` |
| IntermediateDirectoryName | `.cs2j-member-desugar-src` |
| Stage | `PassStage.Declarative` |
| Status | `Stub`（显式启用 → CS2J9001，[PreprocessDiagnosticCodes.cs:12](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PreprocessDiagnosticCodes.cs#L12-L12)） |
| DefaultEnabled | `false` |
| RunAfterIds | `{ "struct-value-semantics", "closure-lift" }`（两者当前同为 stub） |
| CatalogItems | `"C11, C12, C13, C14, C27, C39, C46"` |
| Description | `Desugar accessor-syntax members (properties, events, indexers, finalizers) into methods and backing fields.` |

### 3.2 链位与顺序锁（stub 未实现时的锚点方案）

- **保留两条 RunAfter 锚边不动**，即使锚点 pass（struct-value-semantics=C07、closure-lift=C32/C45）尚未翻转：stub 参与拓扑排序但不执行，锚边只登记未来位置，不改变当前执行序（[PassRegistry.cs:95-133](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs#L95-L133) Kahn + 声明序决胜）。
- C11 翻转后，在「仅 Implemented」子序列中预期位置为 **`…, eliminate-goto, member-desugar, java-keywords`**：member-desugar 移入 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) Implemented 表并声明在 java-keywords 条目之前；全局序中它须等 struct/closure 两个 stub 节点发出后才可发（Implemented 节点在 concat 中先于 Stub 节点），而 java-keywords 被 platform-isolation/concurrency-normalize/dynamic-desugar 三个晚 stub 阻塞，故 Implemented 过滤序中 member-desugar 恰落在二者之间。**实现时必须以实测拓扑序为准**更新顺序断言（§3.3-7），不新增任何指向已实现 pass 的 RunAfter 边。
- 默认关期间该 pass 不进执行计划：无目录、无耗时、链行为零变化（[PassStubCatalogTests.cs:49-55](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L49-L55) 模式）。
- 语义依据：属性是 struct 变异迁移主战场，必须晚于 E13/C07（[catalog C11 通道关系](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L391-L391)）；lambda/显示类提升（C32/C45）产生的属性访问点需先成形再统一去糖。

### 3.3 翻转清单（stub → Implemented，锚点职责）

1. [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)：`PassIds` 增 `MemberDesugar = "member-desugar"`；`Implemented` 表在 java-keywords 条目前增描述符：`defaultEnabled: false`、`verifyBuildByDefault: true`、`verifyTestsByDefault: true`、`needsSemanticModel: true`、`supportLibrary: PassSupportLibrary.None`、RunAfter 仍为 `{ PassStubCatalog.Ids.StructValueSemantics, PassStubCatalog.Ids.ClosureLift }`（锚点翻转时再编译期改引，见下）；**CatalogItems 收窄为 `"C11"`**；Description 改为 property 规则描述（如 `Desugar properties into get_X/set_X methods and backing fields.`）。
2. [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)：删除 member-desugar stub 条目（L191-200）与 `Ids.MemberDesugar` 常量（全仓无其它 RunAfter 引用，已核实）。
3. [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)：`PassChainConfiguration` 增 `required Func<MemberDesugarOptions> MemberDesugarOptionsFactory`（lazy 加载 overrides，仿 [JavaKeywordOptionsFactory](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L46-L47)）；`CreateAdapter` 增 `PassIds.MemberDesugar` 分支。
4. [PassChainFactory.BuildLegacyToggles](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L59-L78)：增 `[PassIds.MemberDesugar] = opts.MemberDesugar`（[PassChainOrderTests.cs:168-180](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L168-L180) 要求每个 Implemented pass 都有 legacy toggle）；`ConvertProjectOptions` 增 `[Option("member-desugar", Default = false)]`。
5. [CliPreprocessChain.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs)：`CliChainRequest` 增 options 工厂字段（[L29-L35](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L29-L35) 模式）并透传进 PassChainConfiguration（L119-L121 模式）。
6. Program.cs：增 BuildMemberDesugarOptions 工厂（先查 CWD 下 `config/MemberDesugarOverrides.json`，不存在则从 AppContext.BaseDirectory 向上逐级查找，仿 [BuildJavaKeywordOptions](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L3236-L3284)，JSON 解析失败 Warning 按空集继续）；preserved 目录与指纹令牌已由描述符全集驱动（[Program.cs:923-940](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L923-L940)），无需逐 pass 接线；`--list-passes` 自动显示 Implemented/默认关/目录/说明。
7. 新增 [Adapters/MemberDesugarPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters)（§5）。
8. 链序/注册表测试更新：
   - [PassChainOrderTests.cs:18-36](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L18-L36) `LockedChain` 在 `EliminateGoto` 与 `JavaKeywords` 之间插入 `PassIds.MemberDesugar`；`EnabledByDefault`（L38-L53）**不变**（默认关）；`ChainFactory_creates_an_adapter_for_every_implemented_pass` 自动覆盖新适配器。
   - [PassStubCatalogTests.cs:23](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L23-L23) stub 总数断言 `23 → 22`；移除对 member-desugar 的引用；CS2J9001 用例使用 iterator-lowering/closure-lift，不受影响。
   - [ProjectPreprocessorsConsistentTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs) 追加适配器类型存在性与 `IntermediateDirectoryName == ".cs2j-member-desugar-src"` 断言（仿 record-readonly/enum 段）。
9. §8 门槛达成后单行翻 `defaultEnabled: true`（指纹一次性失效，可接受）。
10. **CatalogItems 后续只追加不改写**：C12 合入时 `"C11" → "C11, C12"`，C13/C14 依次；C27/C39/C46 真并入时再追加。每次仅一行字符串冲突，属预期协调点（§11）。

---

## 4. Core 组件设计

### 4.1 目录与文件所有权总览

```
src\CSharpToJava.Core\MemberDesugar\
  MemberDesugarPreprocessor.cs   # 对外入口：BindCompilation/RewriteFile/ExcludePlans/Plans/
                                 #   TryAttributeDiagnostic/FormatAndValidate（C11 所有，锚点）
  MemberDesugarModels.cs         # MemberDesugarOptions/Statistics/Diagnostic/Severity/FileResult（C11）
  MemberDesugarAnalyzer.cs       # 共享：全编译访问器成员索引 + 站点枚举/分类（C11 创建，C12-C14 只读复用）
  MemberDesugarPlan.cs           # 共享：MemberDesugarPlan 基类/PlanBuilder/Outcome/归属谓词（C11）
  BackingFieldNaming.cs          # 共享：后备字段确定性命名 + 跨规则预留表（包 JavaNaming，C13 复用）
  AccessorsToMethods.cs          # 共享：访问器声明 → 方法声明归约骨架（修饰符/可访问性/体/显式接口）
  MemberDesugarSyntaxFacts.cs    # 共享：RewriteAnnotation、表达式树包含判定、dynamic 判定、
                                 #   临时提升、nameof 字面量化、AttributeUsage 路由
  MemberDesugarRules.cs          # 共享：IMemberDesugarRule/[MemberDesugarRule] 特性 + 程序集发现
  Properties\                    # 【C11 独占】property 规则全部实现
    PropertyRule.cs              # IMemberDesugarRule 实现（Kind=Property, Order=10）
    PropertyPlan.cs              # PropertyPlan/PropertyPlanBuilder（每属性符号一 plan）
    PropertyFacts.cs             # 属性分类谓词（auto/full/computed/init/required/hiding/接口/...）
    PropertyDeclarationRewriter.cs  # PD-1/PD-2：声明 → 字段 + get_X/set_X（含 attribute/文档分发）
    PropertyUseSiteRewriter.cs   # PD-3/PD-5/PD-6：读/写/复合/++--/方法组/base/nameof/构造器直写
    PropertyInitializerRewriter.cs  # PD-4：对象初始化器 → 临时变量 + set 序列（不可提升闸门）
    PropertyDiagnostics.cs       # CS2J5110-5129 码位常量与消息
  Indexers\                      # 【C12 独占，本任务不创建文件，仅在所有权表登记】
  Events\                        # 【C13 独占，同上】
  Operators\                     # 【C14 独占，同上】
```

规则文件**按成员种类分目录**是硬约束：共享文件只放四类成员通用的机制，种类特有的谓词/重写/诊断码一律进各自目录，从物理上消除 C12-C14 与 C11 的文件冲突（§11）。

### 4.2 共享契约（与 RR/JK 同形，便于适配器逐行复刻）

- `public MemberDesugarPreprocessor(MemberDesugarOptions? options = null)`
- `internal void BindCompilation(CSharpCompilation compilation)`：构建一次共享分析索引，发现并装配规则集
- `public MemberDesugarFileResult RewriteFile(string sourceCode, SemanticModel model, MemberDesugarStatistics stats, List<MemberDesugarDiagnostic> diagnostics)` → `{ OutputCode, Changed }`
- `internal void ExcludePlans(IEnumerable<string> planIds)`
- `internal IReadOnlyList<MemberDesugarPlan> Plans`（基类含 `Id`/`Kind`/`Symbol`/`Outcome`/`TouchedFiles`/`FailCode`/`FailReason`/`FailLocation`/`OwnsTouch(file, typeName, memberName)`）
- `internal bool TryAttributeDiagnostic(Diagnostic d, IReadOnlyDictionary<string,string> rewrittenByFile, out string planId)`
- `internal static string? FormatAndValidate(CompilationUnitSyntax root)`：三候选（改动区间格式化 → 整文件格式化 → 原文），重解析零错误取首个，与 RR/JK 同款。

可见性与 RR/JK 完全一致：仅构造器与 `RewriteFile` 为 public，其余 internal（Core 经 `InternalsVisibleTo` 向 CLI/Tests/ProjectTests 开放，[CSharpToJava.Core.csproj](file:///d:/code/cs2j/src/CSharpToJava.Core/CSharpToJava.Core.csproj#L20-L28)）。

### 4.3 三阶段数据流

1. **分析（整树一次）**：`MemberDesugarAnalyzer` 枚举编译中全部源内声明符号（partial 多部件按符号天然合并）：
   - 成员索引：`IPropertySymbol`（拆 `IsIndexer`）、`IEventSymbol`、用户自定义运算符、终结器；每类型成员名表（字段/方法/属性）与跨规则命名预留表；
   - 站点索引：全编译所有文件中绑定到上述符号的读/写/复合/`++--`/方法组/`base`/对象初始化器/`with`/nameof/表达式树内/dynamic 站点，经 `SemanticModel.GetSymbolInfo/GetTypeInfo/GetOperation` 分类；
   - 编译方式与 RR/JK 相同：`CSharpParseOptions(LanguageVersion.Latest)` + 合成 `__cs2j_global_usings.g.cs`（`ImplicitGlobalUsings.BuildForProject`）+ [ProjectUnsignedTypePreprocessor.GetBasicReferences()](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/JavaKeywordsPassAdapter.cs#L310-L311)，DLL、`allowUnsafe: true`。
2. **计划（每属性符号一个 PropertyPlan）**：TouchedFiles = 属性全部声明部件文件 + 全部使用点文件（含接口声明/实现联动文件）；plan 按符号完全限定名 + 源 span 确定性排序；不可行项（§4.4 PD-8）在计划期定案 `Outcome=Infeasible`，改动不落盘；属性 plan 相互独立（同文件多 plan 共享，回退时整文件重放，同 JK 迭代模型）。
3. **重写（逐文件）**：取命中本文件的 active plans，先跑声明重写（成员顺序：字段插在属性原位置，方法紧随；保持 XML doc/trivia），再跑使用点重写，挂 `MemberDesugarSyntaxFacts.RewriteAnnotation`，过 `FormatAndValidate`。CLI 驱动：复制源码树 → 基线错误集 → 迭代 { 每轮从原始副本重放 → 整树编译/Bind → ExcludePlans → 逐文件 RewriteFile/回写（BOM 保持）→ 重建编译 → DiffErrors → TryAttributeDiagnostic 归属 → 回退该 plan 的 TouchedFiles } 收敛，上限 1024 轮；不可归属单 plan → 全量回退 CS2J5100。单文件 FormatAndValidate 仅快筛，交付以整树重编译为准。

### 4.4 property 规则全集（PD-x）

| 规则 | 内容 | 要点/边界 |
|---|---|---|
| PD-1 自动属性归约 | `T X { get; set; }` → 后备字段 + `get_X()`/`set_X(T)`；get-only → readonly 字段 + 仅 getter；初始化器 `= e` 平移到字段；访问器可访问性（`private set` 等）作为方法可访问性 | static 同步；访问器体为空才判 auto（语法+符号双判，防 extern/abstract） |
| PD-2 full/计算属性 | 块体/表达式体访问器 → 同体方法（`value` 即 setter 形参，原生合法）；无后备字段；getter/setter 文档注释按现转换器惯例归 getter | yield 访问器在 C# 侧仍是普通迭代器方法，无需特判（Java 侧 yield 由主转换器既有路径处理） |
| PD-3 init/required | init → 普通 set（决策 4）；required 不传方法（决策 5）；计数留痕 CS2J5120/CS2J5121（Info） | 编译器约束放宽属有意设计，不报错 |
| PD-4 修饰符与接口 | virtual/abstract/override/sealed/static/new 平移；接口属性→接口方法；DIM→带体默认方法；显式接口实现→`T IFoo.get_X()`（决策 6/8） | abstract 不生成字段/方法体；`new` 不造任何 Java 注解，C46 负责 Java 收口 |
| PD-5 后备字段命名 | 调 BackingFieldNaming（决策 3）；跨 plan 预留防撞；字段上的 attribute 路由见 PD-7 | readonly 与否跟随 get-only/init 可写性 |
| PD-6 使用点改写 | 读→`get_X()`；写→`set_X(v)`；复合/`++--`→`set_X(get_X() op v)`，**副作用接收者先提升临时变量**；方法组/委托绑定→`get_X` 方法组；`base.X`→`base.get_X()`；`?.X`→`?.get_X()`（null 条件调用 C# 原生） | 禁止对属性 `ref/out` 传址（C# 本就 CS0206，无站点）；链上既有 pass 的产物（如 E13 的 WithXxx）只按符号绑定，不按文本 |
| PD-7 构造器只读写入 | 无 set/init 访问器的属性，其写入点若位于声明类型构造器内 → 直接写后备字段（复刻现转换器 [AssignmentTransformer.cs:88-92](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/AssignmentTransformer.cs#L88-L92) 的语义） | 跨类型不可见，不可能合法出现；符号绑定兜底 |
| PD-8 对象初始化器 | 决策 7：可提升→临时变量 + set 序列；嵌套逐级提升；不可提升→CS2J5119 plan 跳过 | 仅重写绑定到本 plan 属性的赋值项；同初始化器中字段赋值保持 |
| PD-9 nameof | 决策 11：绑定属性的 nameof → `"X"` 字面量 | 不绑定本 plan 属性的 nameof 不动 |
| PD-10 attribute 分发 | 决策 14：AttributeUsage 感知路由；不可路由→CS2J5113 plan 跳过 | `[field: NonSerialized]` 等目标显式子句优先 |
| PD-11 跳过闸门 | 见 §6 诊断表：表达式树 CS2J5117、dynamic CS2J5118、ref/ref-readonly 返回属性 CS2J5115、partial 属性 CS2J5114、静态抽象/虚接口属性 CS2J5116、`field` 关键字访问器 CS2J5122、`with` 站点 CS2J5124、位置 record 参数属性 CS2J5123 | 跳过=属性声明与全部站点原子不动且必留诊断；禁止只改声明不改站点 |
| PD-12 残余对账 | 输出树中每个非 indexer 属性声明必须属于 Planned/Skipped(有诊断) 两类之一 | strict 下残余 → CS2J5112 Error，§7.5 闸门 |

### 4.5 为 C12/C13/C14 预留的规则接口与所有权划分

规则发现机制刻意避免「注册表热文件」：共享层通过程序集一次性扫描发现所有带 `[MemberDesugarRule]` 特性、实现 `IMemberDesugarRule` 的无参构造类型，按 `Order` 确定性排序；单测断言发现到的规则集合与顺序，漏注册即红。C12-C14 各自只在自己目录加文件，**不修改 MemberDesugar 根目录任何文件，也不修改适配器**。

```csharp
internal enum MemberDesugarKind { Property, Indexer, Event, Operator, Signature /* C46 预留 */ }

internal interface IMemberDesugarRule
{
    MemberDesugarKind Kind { get; }
    int Order { get; }                       // 同 Kind 内确定性顺序
    void Analyze(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder);
}

[AttributeUsage(AttributeTargets.Class)]
internal sealed class MemberDesugarRuleAttribute(MemberDesugarKind kind, int order) : Attribute;
```

文件/符号所有权划分：

| 资产 | C11（本任务） | C12 Indexer | C13 Event | C14 Operator |
|---|---|---|---|---|
| `MemberDesugar/` 根目录 9 个共享文件 | **所有**（含修改） | 只读引用 | 只读引用 | 只读引用 |
| `Properties/` | 独占 | 不改 | 不改 | 不改 |
| `Indexers/` | 不创建 | 独占 | 不改 | 不改 |
| `Events/` | 不创建 | 不改 | 独占 | 不改 |
| `Operators/` | 不创建 | 不改 | 不改 | 独占 |
| 诊断码段 | CS2J5100-5109 共享、CS2J5110-5129 property | CS2J5130-5149 预留 | CS2J5150-5169 预留 | CS2J5170-5189 预留 |
| `MemberDesugarPassAdapter.cs` | 独占 | 不改（规则自动发现） | 不改 | 不改 |
| PassCatalog `CatalogItems` 字符串 | 翻为 `"C11"` | 追加 C12（一行） | 追加 C13（一行） | 追加 C14（一行） |
| 链序测试 | 插入位锁定 | 不涉及（同 pass 内规则） | 不涉及 | 不涉及 |
| overrides JSON 节 | `rules.properties`（v1 仅 skip/force 顶层） | 自有 `rules.indexers` 节 | `rules.events` 节 | `rules.operators` 节 |

共享 BackingFieldNaming 被 C13（委托后备字段）复用；AccessorsToMethods 的修饰符/可访问性/显式接口骨架被 C12（get_Item/set_Item）、C13（add_X/remove_X）复用；若复用需要放宽个别 internal 可见性，只改可见性不改行为，由 C11 单测守门。

---

## 5. CLI 适配器与集成

### 5.1 MemberDesugarPassAdapter

新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/MemberDesugarPassAdapter.cs`（预计 200-300 行），**整树自驱动**（`RewriteFileAsync` 抛 NotSupportedException，同 [JavaKeywordsPassAdapter.cs:239-240](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/JavaKeywordsPassAdapter.cs#L239-L240)）：

- `Descriptor => PassCatalog.ImplementedById(PassIds.MemberDesugar)`；`CreateParseOptions` 用 `LanguageVersion.Latest`；`FilterUnsupportedProjects`/`CompatNamespacePrefixes` 沿用全局配置；`VerifyBuild/VerifyTests => true`（descriptor 双默认开）；`DeleteBuildOutputsAfterRestore => true`（同 enum/make-readonly）。
- ExecuteAsync 顺序复刻 JK 适配器：复制全部 .cs → 基线错误集 → 迭代（原件重放 → BuildCompilation → BindCompilation → ExcludePlans → 逐文件 RewriteFile/回写（BOM 保持，参 [JavaKeywordsPassAdapter.cs:113-116](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/JavaKeywordsPassAdapter.cs#L113-L116)）→ 重建编译 → DiffErrors → TryAttributeDiagnostic 归属 → 回退）→ Restore（`context.Restore()`）→ `await context.VerifyBuildAndTestsAsync()`（verify 双默认开；失败经 OnBuildFailed/OnTestsFailed 记 CS2J5103/5104 Error → HasErrors → run.Success=false）→ `ProjectPassRunner.DeleteBuildOutputs` → 置 context.Success。restore/verify/清理三步同 [MakeReadonlyPassAdapter.cs:206-213](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/MakeReadonlyPassAdapter.cs#L206-L213)（JK 适配器本身 verify 关、不调用 VerifyBuildAndTestsAsync，仅整树循环形态可复刻）。
- 复用 runner 阶段助手（[PassExecutionContext](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L79-L108)），不自造树准备/restore/过滤逻辑。
- 摘要行：`member-desugar: N propert(y/ies) planned, F field(s) generated, R read/W write/C compound site(s) rewritten, I initializer(s), A skipped, B rolled back, K residual`；诊断行 MSBuild 格式，Info 仅 verbose。
- restore/build/test 失败分别映射 CS2J5102/5103/5104。

### 5.2 verify 默认、无支撑库、指纹

- verify：descriptor 双默认开（最严档，.NET 半区守门）；
- 无支撑库：无 AfterFilesAsync 注入、无 compat 命名空间过滤特例、无支撑文件指纹豁免；
- 指纹：翻转后自动纳入全集令牌（`pass:member-desugar=false→true` 一次性失效，接受，[catalog §4 风险 11](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L930-L930)）；保留目录 `.cs2j-member-desugar-src` 由实际执行集合自动 preserve（[Program.cs:929-937](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L929-L937)），非启用时零痕迹。
- 入口面：convert-project `--member-desugar`（默认 false）+ 通用 `--enable-pass member-desugar`/`--disable-pass`；独立运行走通用 `preprocess --pass member-desugar -s … -d …`（不新建独立 verb，与 C07/C09 方向一致）；不提供单文件 `-i/-o`（属性 plan 几乎必然跨文件，单文件模式只在通用 verb 下受 CS2J5109 闸门约束）。
- overrides：`config/MemberDesugarOverrides.json`（v1：`{ "skip": ["Ns.Type" 或 "Ns.Type::Prop"], "force": [...] }`，默认空/文件可选）；force 仅解除表达式树一项保守跳过（决策 12），dynamic/ref/partial/静态抽象/不可提升等语言不可行项 force 不解除。

### 5.3 Java 半区消费清单（转换器后续 PR 的输入，本 pass 不实施）

默认开启前必须逐项核实/补齐（对齐 C09 §5.4 核查纪律，不凭空假设）：

1. [MethodTransformer.cs:122-129](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L122-L129) 与 [L83-L90](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L83-L90) 的 `get_/set_` 剥前缀旧特例：去糖树路径须改为派生 `getX/setX`（保留 getClass→getClassValue 特例，[JavaNaming.GetPropertyGetterName](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/JavaNaming.cs#L26-L30)）；需 triage [CSharpXmlCompileRegressionTests.cs:602](file:///d:/code/cs2j/tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs#L602-L602) 锁定的旧输入来源，确定按「输入是否经 member-desugar」分流还是统一改派生；
2. PropertyTransformer 对去糖树成为不可达路径（输入已无 PropertyDeclarationSyntax），其删除/保留为后续 PR；直连 ConversionPipeline 的单文件入口仍需它作安全网（[AutoPropertyFieldAccessTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/AutoPropertyFieldAccessTests.cs) 等继续守门）；
3. 序列化注解路由（决策 14）后，Json/DataContract 族注解落在后备字段上，需证实 FieldTransformer/注解桥消费面与现 PropertyTransformer 注解发射（[PropertyTransformer.cs:154-170](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L154-L170)）等价；
4. 显式接口方法 `T IFoo.get_X()`、`new` 隐藏方法、接口默认方法三类形态经 MethodTransformer/ClassTransformer 产出可编译 Java（碰撞场景的 Java 分派修复归 C46，本 pass 只保证 C# 形态规范）；
5. 布尔 `isX` 是否引入（开放问题，见 §11/末节），若引入须同步后备字段/getter 命名与 bean 内省契约。

---

## 6. 诊断策略（不可降级属性显式报错，禁止静默丢访问器）

新增 **CS2J51xx** 码段（基建 CS2J90xx、转换器校验已占 CS2J5001-5003，本段不冲突；码位只追加不改号）。MSBuild 格式：`文件(行,列): warning/error CS2J51xx: [Ns.Type::X] 描述`。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J5100 | Error | 整树重编译新增错误且无法归属单一 plan → 本次运行全部回退（仿 RR030/JK030/SV030） |
| CS2J5101 | Warning | 新增错误归属某 plan → 该 plan 原子回退（仿 JK031） |
| CS2J5102 | Error | 中间树 `dotnet restore` 失败 |
| CS2J5103 | Error | 中间树 `dotnet build` 失败 |
| CS2J5104 | Error | 中间树 `dotnet test` 失败 |
| CS2J5109 | Warning | 单文件模式：plan 非自包含（声明/站点跨文件），排除 |
| CS2J5110 | Info | 属性 plan 建立（形态/访问器/TouchedFiles 计数），仅 verbose |
| CS2J5111 | Info | 后备字段生成（原名/字段名/防撞后缀），仅 verbose |
| CS2J5112 | Warning（strict Error） | residual 闸门：输出中存在无对应 skip 诊断的非索引属性残余（PD-12） |
| CS2J5113 | Warning（strict Error） | 特性仅 Property 可施用、无法路由到字段/方法（AttributeUsage），plan 跳过（PD-10） |
| CS2J5114 | Warning（strict Error） | partial 属性（C#13，partial-merge/C20 落地前不支持），plan 跳过 |
| CS2J5115 | Warning（strict Error） | ref/ref-readonly 返回属性（含其写入站点；与 C01 ref 形态耦合），plan 跳过 |
| CS2J5116 | Warning（strict Error） | 接口 `static abstract`/`static virtual` 属性（C#11，C36 防线），plan 跳过 |
| CS2J5117 | Warning（strict Error） | 属性在表达式树 lambda 内被访问，plan 跳过；overrides.force 可解除本项 |
| CS2J5118 | Warning（strict Error） | dynamic 绑定访问站点不可判定，plan 跳过（force 不解除） |
| CS2J5119 | Warning（strict Error） | 对象初始化器/复合赋值位于不可临时提升的表达式上下文，plan 跳过 |
| CS2J5120 | Info | init 访问器放宽为普通 set（plan 聚合计数） |
| CS2J5121 | Info | required 成员编译期约束解除（plan 聚合计数） |
| CS2J5122 | Warning（strict Error） | 访问器体使用 C#13 `field` 上下文关键字（v1 不收编），plan 跳过 |
| CS2J5123 | Warning（strict Error） | 位置 record 参数属性（v1 留 RecordTransformer 既有路径，C27 期再议），plan 跳过 |
| CS2J5124 | Warning（strict Error） | 属性被 `with` 表达式初始化器引用（C27 前置缺口），plan 跳过 |

原则（[catalog §4 风险 12](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L931-L931)）：不可判定即显式诊断并让该属性 plan 整体不动；跳过必须留痕且声明/站点同生同灭；Warning 经 strict 升级 Error（Options.Strict 默认 true，与 enum 同构）；全部跳过按 `SkippedByReason: Dictionary<code,int>` 分桶进统计与摘要，供 sdk 基线盘点。

---

## 7. 测试计划

### 7.1 Core 单测矩阵

新增 `tests/CSharpToJava.ProjectTests/MemberDesugarPropertyUnitTests.cs`（仿 JavaKeyword/RecordReadOnly 单测：内联源码 → `CSharpCompilation` → Bind/RewriteFile → 忽略空白文本断言 + `AssertOutputCompiles`）：

1. **自动属性**：`{ get; set; }` 字段 + 双方法；get-only（readonly 字段、仅 getter）；`private set`/`protected get` 可访问性平移；static；初始化器 `= 42` 平移；`Age`/`age` 同类双属性与显式 `age` 字段三撞命名（首声明裸名、后撞 `Value`，对齐 [JavaNaming.cs:108-126](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/JavaNaming.cs#L108-L126)）；属性名撞 Java 关键字（`@event` 等）字段/方法名两不伤。
2. **full/计算属性**：块体 get/set、表达式体 `=>`、set 表达式体委托另一属性（[PropertySetterExpressionBodyTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/PropertySetterExpressionBodyTests.cs) 同形，断言无 `_chainVal` 类残迹）、getter-only 计算属性无字段。
3. **使用点**：读/写/`+=` 等复合/`++`/`--`/`??=`；副作用接收者 `Get().X += 1` 断言接收者只求值一次（临时提升）；方法组 `Func<int> f = c.X`；`base.X`/`base.X = v`；`c?.X`；同文件与跨文件两类；限定名/global:: 接收者。
4. **构造器只读写入**：get-only 属性在声明类型 ctor 内赋值 → 直写后备字段；init 属性在 ctor 与初始化器中 → set 方法。
5. **修饰符/层次**：virtual/abstract（无体无字段）/override/sealed override/`new` hiding（含隐式 hiding）/static；`new` 同名不同类型属性（[PropertyHidingTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/PropertyHidingTests.cs) 场景）在 C# 半区两方法共存、非虚分派由 C# 自测验证。
6. **接口**：接口只读/读写属性 → 接口方法；DIM 带体属性 → 默认接口方法；显式接口实现 → `T IFoo.get_X()`；显式实现与类自身同名属性碰撞（[ExplicitInterfacePropertyConflictTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ExplicitInterfacePropertyConflictTests.cs) 场景）断言 C# 编译通过且无递归调用；接口与实现跨文件联动。
7. **init/required**：`{ get; init; }` → set；`required` 属性 + 对象初始化器重写（含嵌套初始化器、多属性、字段混排）；初始化器在字段初始化器/表达式体等不可提升位置 → CS2J5119。
8. **nameof**：`nameof(X)`/`nameof(C.X)` → `"X"`；不涉去糖属性的 nameof 不动；字符串进入特性实参仍可编译。
9. **attribute**：`[field: NonSerialized]` 落字段；`[JsonPropertyName("x")]`/`[DataMember]` 路由后备字段；访问器上特性落对应方法；仅 Property 可施用语义的合成特性 → CS2J5113。
10. **跳过族**：表达式树内属性 → CS2J5117 且声明站点全不动；dynamic → CS2J5118；ref 返回属性 → CS2J5115；partial → CS2J5114；静态抽象接口属性 → CS2J5116；`field` 关键字访问器 → CS2J5122；位置 record → CS2J5123；`with` → CS2J5124；force 解除表达式树项单测。
11. **边界零误伤**：索引器声明与 `obj[i]` 站点原样（C12 边界）；事件 `+=`/`-=` 与字段式事件原样（C13）；`operator +` 原样（C14）；普通方法/字段/委托字段（[FieldTransformer.cs:360-383](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L360-L383) 的 delegate 特例在 C# 半区无对应改写）不动。
12. **工程性质**：幂等（二次 RewriteFile 零 diff）；干净文件字节级一致 + BOM 保持；partial 类型多部件按符号合并。

### 7.2 项目级 fixture

新增 `tests/SampleMemberDesugarSolution/`（`SampleMemberDesugarLib` + `SampleMemberDesugarApp` + `SampleMemberDesugarTests` + `.sln`，net10.0，Content Include 进 [CSharpToJava.ProjectTests.csproj](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj)，仿 SampleJavaKeywordSolution/SampleRecordSolution）：

- Lib：DTO 群（自动/get-only/init/required/static/计算属性/hiding/显式接口实现/接口 + DIM/泛型类型属性/`Age`+`age` 碰撞/带初始化器）；一个含表达式树引用的类型（断言跳过且中间树可编译）；一个不可提升初始化器反例（独立文件供错误断言）。
- App：跨文件读写/复合赋值/副作用接收者/对象初始化器/接口分派。
- Tests（.NET 半区行为守门）：值读写语义、hiding 非虚分派、显式接口分派、对象初始化结果、方法组委托调用——全部在「去糖后的 C#」上 xunit 断言，去糖错误会在 .NET 侧直接红。
- 项目级测试 `MemberDesugarProjectTests.cs`：`result.Success`、统计计数、中间树文本断言（含 `get_`/`set_` 方法、不含非跳过属性声明）、**中间树 `dotnet build` 与 `dotnet test -c Release` 退出码 0**；干净工程字节复制与 BOM 用例。

### 7.3 Release build/test

- `dotnet build -c Release`（CSharpToJavaConverter.slnx 全绿）；
- `dotnet test -c Release` 三个工程：CSharpToJava.Tests、CSharpToJava.ProjectTests、CSharpToJava.ConversionTests。

### 7.4 反射/序列化 DTO 抽样

- 反射面：fixture 内断言去糖后 `typeof(Dto).GetProperty("X")` 返回 null、`GetMethod("get_X")` 非 null——**把新契约钉死为预期**（风险 7 的限制声明，同 E17「反射不保证」）；`nameof(Dto.X) == "X"` 字面量断言。
- 序列化抽样：对带 `[JsonPropertyName]`/`[DataMember]` 的 DTO 在 .NET 侧做 STJ/DataContract 往返抽样（特性路由到字段后形状应保持）；**无注解 DTO 的默认 STJ/Newtonsoft 形状不纳入 .NET 半区正确性断言**（后备字段私有，默认反射看不到——这是属性去糖的固有面，列入开放问题与 §8 门槛的人工 triage 项，不假装等价）。

### 7.5 residual 闸门（property 计数）

1. 扫描型测试：对输出树每个非 indexer `PropertyDeclarationSyntax`，断言其属于 Planned 或带 CS2J51xx skip 诊断两类之一，不存在「无 plan 无诊断」属性；strict 下 CS2J5112 非零即退出码非零。
2. 跳过反例集：§7.1-10 每类断言 plan 原子性（声明与站点同不动）、无中间目录残留（全量回退路径 CS2J5100 演练）。
3. 既有 fixture 回归：SampleProject、SampleRecordSolution、SampleRefOutInSolution、SampleArraySolution、SampleJavaKeywordSolution 等启用本 pass 后中间树 build/test 全绿；上游各 pass（E13/E11/E05/E06 等）统计零变化。

### 7.6 CLI / 一致性 / 红灯对照

- 默认关：convert-project 不产生 `.cs2j-member-desugar-src`、无耗时、链序不变；`--enable-pass member-desugar` 与 `--member-desugar` 等价；`--list-passes` 显示 Implemented/默认关/目录；preserved 目录注册；摘要/诊断输出；CS2J9001 不再对本 id 触发。
- **与现有 PropertyTransformerTests 红灯对照**：不修改 [PropertyHidingTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/PropertyHidingTests.cs)/[ExplicitInterfacePropertyConflictTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ExplicitInterfacePropertyConflictTests.cs)/[AutoPropertyFieldAccessTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/AutoPropertyFieldAccessTests.cs)/[PropertySetterExpressionBodyTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/PropertySetterExpressionBodyTests.cs) 一行（它们走直连单文件管线，是转换器安全网）；为每个用例在 §7.1/§7.2 建 C# 半区对应断言，形成「Java 侧补丁行为 ↔ C# 侧原生形态」对照表，作为 §5.3 转换器消费 PR 与默认开门禁的核对底稿。

---

## 8. 默认开启验收门槛

`defaultEnabled: false → true` 必须同时满足：

1. slnx `dotnet build -c Release` 全绿；三个测试工程 `dotnet test -c Release` 全绿（含新单测/fixture/CLI/一致性/链序）。
2. fixture sln 中间树 build + test 全绿；幂等、字节复制、residual 闸门全绿；CS2J5100/5101 回退演练通过。
3. **Java 半区就绪**：§5.3 五项逐项核实/补齐——尤其 `get_/set_` 派生改造（[MethodTransformer.cs:122-129](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L122-L129)）完成，代表性属性样本（自动/full/init/required/hiding/显式接口/接口 DIM）经完整 C#→Java 链路产出可编译 Java 且双端测试通过；PropertyTransformer 在预处理路径上证实不可达（删除留后续 PR）。
4. csharp-sdk（只读训练集）启用扫描：输出属性总量、Planned/SkippedByReason 分布、CS2J5113-5124 命中清单；**每条 language-infeasible 命中人工定性**（真不可行 vs 分析器过保守），过保守项修到清零或进入明确后续里程碑；反射/序列化 DTO 抽样（§7.4）结论经评审（含无注解 DTO 契约变化的接受决定）。
5. sdk 基线不退化：启用后各既有 fixture 与 sdk 抽样工程中间树 build/test 结果不劣于未启用；上游 pass 统计零变化；convert-project 总耗时增量可接受（verify 串行成本纳入指纹缓存评估）。
6. overrides/逃生口、`--list-passes` 描述、README/ROADMAP 条目齐备。

未达门槛前长期保持默认关，每个评审周期复跑第 4/5 项，避免 [catalog §4 风险 10](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L929-L929) 的「多套事实标准」固化。

---

## 9. 里程碑

| 里程碑 | 内容 | 预估规模 |
|---|---|---|
| M1 锚点基建 | `MemberDesugar/` 共享骨架（Models/Analyzer/Plan/BackingFieldNaming/AccessorsToMethods/SyntaxFacts/Rules 发现）+ Properties/ 空规则 + 适配器 + 注册表翻转（默认关、verify 开）+ CLI 开关/目录/指纹接线；空 pass 端到端跑通（零改动字节复制、--list-passes、链序测试） | Core ~450 行，CLI ~300 行 |
| M2 自动属性 | PD-1/PD-5/PD-6/PD-7：字段合成与命名防撞、读写/复合/`++--`/方法组/base/构造器直写、静态/get-only/可访问性；§7.1-1/3/4 单测 | ~600 行 |
| M3 full 与层次 | PD-2/PD-4/PD-9/PD-10：块体/表达式体访问器、virtual/abstract/override/sealed/new、接口/DIM/显式接口实现（含碰撞）、nameof 字面量化、attribute 路由；§7.1-2/5/6/8/9 单测 | ~600 行 |
| M4 init/required 与闸门 | PD-3/PD-8/PD-11：init 放宽、required、对象初始化器提升、表达式树/dynamic/ref/partial/静态抽象/`field`/`with`/位置 record 跳过族、回退循环打磨、overrides/BOM/幂等审计；fixture sln + 项目级/CLI/一致性测试；Release build/test 全绿 | ~500 行 + fixture |
| M5 接口/虚收口验证与默认开评审 | sdk 扫描与 SkippedByReason 报表、§5.3 Java 半区消费联动、红灯对照表归档、本设计定稿 + plan 文档；门槛通过后单行翻默认开 | 文档/数据 |

---

## 10. 风险

1. **反射/字符串契约变化**（[catalog §4 风险 7](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L926-L926)）：去糖后 `PropertyInfo`/`GetProperty("X")`/XAML/按名 JSON 匹配的目标形态改变。缓解：nameof 字面量化；沿用 E17 限制声明 + overrides；序列化特性 AttributeUsage 路由到字段；无注解 DTO 不做等价承诺，sdk 抽样人工 triage（§7.4/§8-4）。
2. **Lowering 误用风险**（[catalog §4 风险 9](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L928-L928)）：[LowerProperty.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Lowering/LowerProperty.cs) IR 不同、生产不可达。缓解：只借「访问器→方法」形态，Core 新目录零 using Lowering；评审检查 import。
3. **转换器派生不一致**：`get_/set_` 旧特例（MethodTransformer 两处）在消费 PR 前会给出去糖树错误 Java 名。缓解：pass 默认关且 §8-3 为硬门禁；单文件直连路径保留 PropertyTransformer 安全网；不靠「碰巧编译」放行。
4. **init/required 放宽的测试盲区**：中间树合法程序超集，编译期漏初始化不会在 .NET 侧报红。缓解：这是纯编译期约束、运行时行为不变，已在决策表与 CS2J5120/5121 留痕；初始化器重写的正确性由 .NET 行为测试覆盖。
5. **对象初始化器/复合赋值的求值次序**：副作用接收者重复求值或不可提升位置硬改写会制造「编译能过、行为错」的程序。缓解：临时提升纪律同 JK-6d；不可提升即 CS2J5119 跳过，宁假阳不漏改；整树重编译 + .NET xunit 双保险。
6. **整树迭代成本**（同 RR/JK，上限 1024 轮）：大 sln 全量重放重编译。缓解：分析期一次算尽 plan/TouchedFiles，稳态一轮收敛；保留轮次与耗时统计。
7. **与上游 pass 形态耦合**：E13 的 WithXxx/getXxx、E06 生成 tuple struct 属性、E05 委托、E11 record 只读化产物若被误判会错改。缓解：站点一律以 SemanticModel 绑定的 `IPropertySymbol` 为准（E13 产物是普通方法不绑属性符号）；上游统计零变化为硬回归项；对 E13 输出树做共处一树单测（§7.1-12 扩展）。
8. **表达式树/dynamic 共享判定工具尚不存在**（[catalog §4 风险 8](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L927-L927)）：v1 在 MemberDesugarSyntaxFacts 内自持一份，JK/RR/C07 各持一份的技术债登记，待统一引擎期合并；跳过站点经 CS2J5117/5118 计数可见。
9. **C12-C14 并行扩展的回归面**：规则自动发现 + 分目录所有权使合并不碰 C11 文件，但共享 Analyzer/骨架的 internal 演变仍可能影响后续规则。缓解：共享组件一次做厚、只增不改签名；C12-C14 落地时 C11 property 单测全绿为准入。
10. **位置 record/`with` 留白**：v1 跳过（CS2J5123/5124），若 sdk 高命中则默认开受阻。缓解：M0/M5 测量命中率；C27 为明确后续条目，必要时把 record 补充去糖提前。

---

## 11. 并行协调

- **与 C12 Indexer / C13 Event / C14 Operator（同族三个并行 worktree）**：
  - **合入顺序：C11 先行**。C11 合入前 C12-C14 的分支只允许 rebase 预览、不合主干；C11 落地后三者基于新主干 rebase，各自只增 `Indexers/`/`Events/`/`Operators/` 目录与自有测试/fixture，不碰 `MemberDesugar/` 根目录与适配器（§4.5 所有权表）。
  - 共享组件缺口（C12 的多维/ref 返回索引器、C13 的委托后备字段、C14 的 op 名表）若发现骨架不够用：先在 C11 分支（或合入后的跟进小 PR）扩共享层并补 property 回归，再写种类规则；禁止在种类目录复制共享逻辑。
  - 各任务文档须注明「依赖 C11 锚点合入」并引用本设计 §4.5；C12 还需注明 C17 复合赋值依赖、C13 注明消费 E05 产物、C14 注明晚于 E09/C05（catalog 各条通道关系）。
- **热文件冲突解法**：
  - [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)：仅 C11 删除 member-desugar 一条；其它同期翻转任务（C07 等）删各自条目，git 层面行级互不重叠；C07 翻转时其检查清单第 2 条「下游锚点改引」需把 **PassCatalog 中 member-desugar 的 RunAfter** 一并从 `PassStubCatalog.Ids.StructValueSemantics` 改引 `PassIds.StructValueSemantics`（编译期改名，C07/C11 谁先合谁处理，后合者 rebase 解一次）。
  - [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) / [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs) / [CliPreprocessChain.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs) / Program.cs：各分支只增自己的条目/工厂/switch 分支/Option，不合改他人条目；合并门禁 = rebase 最新主干 + 注册表校验测试 + 链序断言全绿（同 C07 §11 约定）。
  - [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs)/[PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs)：C11 一次性改到位（插位 + stub 数 23→22）；C12-C14 不改链序（默认关的同 pass 内规则不进注册表）。
  - CatalogItems 一行字符串是 C12/C13/C14 各自落地时的预期一行冲突，按合入顺序追加，不互相覆盖。
- **与 C27（record with/init/required 补充）**：C11 覆盖属性面的 init/required；`with` 表达式与位置 record 参数属性 v1 显式留白（CS2J5123/5124）；C27 未来以 `MemberDesugar/` 内新规则（Properties/ 内追加或 `Records/` 子目录，落地时定）并入同一 pass，CatalogItems 追加 C27，不新建通道。
- **与 C46（签名收口）**：C11 产出的 `new` 方法/显式接口方法/协变返回 override 是 C46 的输入；C46 未来以 Signature 类规则消费，诊断码段 CS2J5190-5199 建议预留；C11 不预造 Java 注解（`_newSlot`/`@CompatPropertyName` 留在转换器侧）。
- **与 C39（终结器消除）**：未来独立规则（建议 `Finalizers/` 目录 + CS2J52xx 码段），与属性无共享逻辑，仅共用 Analyzer 外壳。
- **与 C10 attribute-normalize**：链位上 C10 先于 member-desugar（catalog §3 链序 C09/C10 → C11）；C11 的 PD-10 直接消费 C# 原始特性也成立，C10 落地后属性面改为消费其归一标记，规则做幂等（两套输入单测）。
- **与 java-keywords（C08/E17，终命名）**：member-desugar 在前、java-keywords 在最后；新方法名 `get_X`/`set_X` 与后备字段名若撞 Java 保留字（如属性 `@yield`），由 JK-1 符号改名统一兜底（其改名种类表含方法/字段/属性，[JK spec §4.2](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-java-keywords-preprocessor-design.md#L139-L153)）；C11 不自行做终命名逃逸（BackingFieldNaming 复用的 JavaNaming 逃逸仅与现有字段名保持一致，JK 仍是最后闸门）。
- **与 E13 make-readonly / C07 struct-value-semantics**：RunAfter 锚点已锁；struct 属性访问器方法化后 E13 的变异迁移早已完成，C11 只翻译形态；C07 文档 §10-7 已登记该次序，C11 以其输出树做回归。
- **与 C32/C45 closure-lift**：显示类中的属性访问点随符号绑定一并改写，无特殊边界；锚点已锁。

---

## 12. Worktree 分配

- 分支：`feature/preproc-c11-property-desugar`
- 目录：`.worktrees/preproc-c11-property-desugar`
- base：`67477551`（`feat(preprocessing): register 23 merged-family stub passes`，已核实存在）
- 创建命令（在仓库根、PowerShell 下执行；本设计阶段不执行）：

```powershell
git worktree add .worktrees/preproc-c11-property-desugar -b feature/preproc-c11-property-desugar 67477551
```

- worktree 内交付面遵循 foundation plan「并行开发交付包模板」（[preprocessor-foundation_plan.md:108-117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L117)）：Core `MemberDesugar/`（共享基建 + Properties/）、CLI 单适配器 + 注册表翻转、本设计文档与后续 plan 文档（`docs/superpowers/plans/2026-09-28-c11-property-desugar.md`）、Core 单测 + fixture sln + Release build/test + residual 闸门 + CLI/链序测试；不新建支撑工程；不触转换器代码（§5.3 消费清单为后续 PR 输入）。

---

## 附：关键开放问题（供评审拍板）

1. **无注解序列化 DTO 的 .NET 半区契约**：后备字段私有时默认 STJ/Newtonsoft 反射形状改变。是接受「反射不保证」限制（同 E17）+ sdk triage，还是对后备字段默认/可选发射 `[JsonInclude]` 等标记（引入 BCL 依赖面）？影响 §8 默认开第 4 项结论。
2. **`get_/set_` 旧剥前缀特例的分流方式**：转换器消费 PR 中按「是否经 member-desugar」分流，还是统一改派生并迁移 [CSharpXmlCompileRegressionTests.cs:602](file:///d:/code/cs2j/tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs#L602-L602) 的旧输入？
3. **布尔 `isX()` 是否引入 Java 半区**：现状全仓 `getX`，引入会改变 bean 内省/序列化契约，需与 Java 侧 compat 注解策略联动决策。
4. **位置 record 属性与 `with` 的 v1 留白命中率**：若 sdk 高命中，C27 部分内容（record 成员补充去糖）是否提前并入 M5。
5. **规则自动发现（程序集扫描 `IMemberDesugarRule`）的取舍**：以一次反射扫描换 C12-C14 零热文件冲突；若评审偏好显式注册，则退化为「每种类一个 partial 文件 + 单测锁顺序」，需在 M1 定死。
