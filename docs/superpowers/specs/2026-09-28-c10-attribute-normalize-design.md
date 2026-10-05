# C10 JSON / Attribute 归一预处理器设计

**日期**: 2026-09-28（2026-09-29 核订：行号全量重定位、C02 landed 使 §3.2 前提失效、member-desugar 落地后的 attribute 覆盖面重述）
**范围**: 新增注册表 pass `attribute-normalize`（Core `AttributeNormalize\` + CLI `Preprocessing/Adapters/AttributeNormalizePassAdapter.cs`）。v1 做三件事：①Caller* 族（`CallerMemberName`/`CallerFilePath`/`CallerLineNumber`/`CallerArgumentExpression`）编译期调用点注入语义 → 在调用点显式填参并从声明端归一；②JSON/序列化注解**保留 + 全量盘点 + 桥接面核对**（不做结构改写）；③P/Invoke/平台注解**只盘点透传**，拒绝权归 C06/C42。实施 PR 不改 C#→Java 转换器
**状态**: 已核订（2026-09-29 对照 dev-3.x@2d25700e 逐条核验全部代码证据并修正：catalog 与注册表引用行号全量重定位；`AttributeListSyntax` 的 grep 口径纠正为 `AttributeLists`（实测 Transformers 恰 3 文件，与原结论一致）；**C11-C14 member-desugar 已带成员级 attribute 路由落地**（§11 原「必须携带」契约已履行，§1.1/§1.2 的覆盖面重述）；**C02 generic-deepen 已落地使 §3.2「stub 期拓扑处理」前提失效**——`RunAfterIds` 的锚点与 `LockedChain` 槽位迁移从「等 C02」变为本 pass 自查项；CS2J13xx 码段经实测确认空闲；仍待实施）
**Catalog**: C10（P2，分类 1.6 反射/序列化）；[preprocessor-catalog.md L404](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L404)（核订重定位）；§3.3「平台边界族」[L1090](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1090)；§4 风险 7 [L1105](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1105)、风险 12 [L1110](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110)、风险 14 [L1112](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1112)、风险 16 [L1114](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1114)
**注册表现状**: stub 已登记——[PassStubCatalog.cs L170-L179](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L170-L179)（核订重定位），id `attribute-normalize`，Stage=`Declarative`，RunAfter=`generic-deepen`，目录 `.cs2j-attribute-normalize-src`，默认关

## 1. 背景与目标

### 1.1 三个 attribute 族的现状

进入本 pass 视野的 attribute 按语义分三族，现状待遇完全不同：

**族 A：调用点注入类（Caller* 族）——语义在转换器中不存在**

- 全仓对 `CallerMemberName|CallerFilePath|CallerLineNumber` 的全部命中只有 E08（strip-default）的**排除判定**两处：[DefaultParameterVerifier.cs L16-L21/L104-L123](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterVerifier.cs#L16-L21) 与 [DefaultParameterDeclarationRewriter.cs L1528-L1553](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterDeclarationRewriter.cs#L1528-L1553)——携带 Caller 参数的方法被**整个方法**豁免默认值剥离（连同同方法其它普通可选参数）。
- `CallerArgumentExpression` 全仓**零命中**（catalog 四次补全要求同通道处理；**2026-09-29 核订复验**：`grep CallerArgumentExpression src/ tests/ config/` 仍为零命中，即该族至今无任何转换或测试覆盖——本设计的 AN-C5 是唯一处理面；原文引的 catalog L376 已漂移，该注记现位于 C10 条目内）。
- Transformers 目录对 Caller 族零命中：声明端 attribute 被静默丢弃，调用点也**永远不会出现**编译器本应注入的实参。`void Log(string msg, [CallerMemberName] string caller = null)` 在 Java 侧等价于「caller 恒为默认值」，且无任何诊断——典型的静默语义丢失（catalog §4 风险 12 第一原则的打击对象）。

**族 B：序列化契约类——桥接厚但覆盖面窄，靠 compat 注解硬桥**

- 属性/字段级零星处理（全 Transformers 目录仅 3 个文件读 attribute **语法列表**。**2026-09-29 核订实测**：`grep -rl AttributeLists src/CSharpToJava.Core/Transformers/` 命中 `PropertyTransformer`/`ClassTransformer`/`EnumTransformer` 三个文件，与原结论一致；原文以 `AttributeListSyntax`（类型名）为 grep 口径实际零命中，口径已纠正——`FieldTransformer`/`MethodTransformer` 均为 0 次，字段级处理走**符号属性** `GetAttributes()` 而非语法树）：
  - [PropertyTransformer.cs L142-L170](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L142-L170)：XML 四注解（`XmlAttribute/XmlIgnore/XmlArray/XmlArrayItem`）+ `JsonExtensionData`/`JsonIgnore(Condition)`/`JsonNumberHandling`/`DebuggerBrowsable`/`JsonInclude`/`JsonPropertyName`/`JsonConverter` 经符号读出发 compat 注解；converter 类型解析 [L179-L192](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs#L179-L192)。
  - [FieldTransformer.cs L166-L218](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L166-L218)：字段级 `JsonNumberHandling`/`JsonInclude`/`JsonPropertyName` + 非 public 标记。
  - [EnumTransformer.cs L59-L71](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/EnumTransformer.cs#L59-L71)：`[Flags]` 双路判定（语义符号 + 语法名）。
- compat 桥接面：[CompatJsonIgnore.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CompatJsonIgnore.java)、[CompatJsonPropertyName.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CompatJsonPropertyName.java)、`CompatJsonInclude`/`CompatJsonConverter`/`CompatJsonNumberHandling`/`CompatJsonExtensionData`、`CompatAttributeTypes`、`DefaultContractResolver`（Newtonsoft 契约）、`DataContractSerializer`、`ObsoleteAttribute`、`DefaultValueAttribute` 等。映射配置 [TypeMappings.json L86](file:///d:/code/cs2j/config/TypeMappings.json#L86) 与 [L328](file:///d:/code/cs2j/config/TypeMappings.json#L328)（`System.Text.Json.JsonSerializer` 两条）、[L377](file:///d:/code/cs2j/config/TypeMappings.json#L377)（`System.Text.Json.Serialization.JsonConverter` → `Object`）——**核订纠错**：原文引的「L361-L365（JsonConverter<T> → Object）」现已被 **C01 落地的 `Cs2jByRef.RefStructByRef\`1` 映射**占据，JsonConverter 条目实际在 L377。
- **2026-09-29 核订补录（本设计写作后落地的第三条通道）**：**C11-C14 member-desugar 已落地（默认关），且自带成员级 attribute 路由**——`MemberDesugar/` 下 8 个文件读 `AttributeLists`：`Properties/PropertyRule.cs:263`、`Properties/PropertyDeclarationRewriter.cs`、`Events/EventRule.cs:178`、`Events/EventFacts.cs`、`Events/EventDeclarationExpander.cs:41,69,86`（含 `RouteAttributeLists` 把字段级/方法级 attribute 分流，L130）等。含义有二：① 属性/索引器/事件去糖后 attribute 的**归属决策已由 C11-C14 自己承担**——§11 的 C11-C14 行原要求「C11-C14 落地时必须在其 plan 中携带成员级 attribute 迁移规则」，**该契约已履行**，实施 C10 时应把该行从「待办契约」改读作「既有实现核对」；② C10 在链上先跑（现拓扑：C10 早于 member-desugar），inventory 看到的是**去糖前**的属性声明形态，与 §11 既定分工一致。
- E08 已对 JSON 注解做副作用避让：[DefaultParameterDeclarationRewriter.cs L1053-L1054/L1155-L1195](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterDeclarationRewriter.cs#L1155-L1195) 用文件名后缀表识别 `JsonConstructor` 等七类 attribute 的文件，改变默认值剥离策略。
- 回归测试：`JsonNumberHandlingAnnotationProjectTests.cs`（项目管线，类型级注解）、`ReflectionAttributeCompatTests.cs`、`SystemTextJsonDeserializeTypeReferenceTests.cs`、`DataContractSerializerCompatibilityTests.cs`。
- **映射存在 ≠ 语义可用**（§4 风险 14 点名 C10）：compat 注解只**物化转换器实际读取的构造参数**。例如 [CompatJsonIgnore.java L19-L26](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CompatJsonIgnore.java#L19-L26) 自述对象初始化器语法被丢弃、`Condition` 默认 `Always`；Newtonsoft `[JsonProperty("x")]`、DataContract `[DataMember(Name=…, EmitDefaultValue=…)]` 在转换器中**没有对应符号读取分支**（全仓 grep 无 `JsonPropertyAttribute`/`DataMemberAttribute` 的消费点）——属性存在、注解类也在 compat，但名字/参数到不了 Java 运行时。

**族 C：平台/互操作类——拒绝基建已存在，归一归 C06/C42**

- 平台边界诊断已落地于 [UnsupportedDomainPasses.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs)：
  - CS2J3001 不支持命名空间（[L31-L73](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L31-L73)）；
  - CS2J3101 `SupportedOSPlatform`/`UnsupportedOSPlatform`/`*Guard` 属性（[L143-L154](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L143-L154)）；CS2J3102 平台特定 API（[L156-L229](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L156-L229)）；
  - CS2J3201 `DllImport`/`LibraryImport`/`ComImport`/`MarshalAs`/`UnmanagedCallersOnly`/`GeneratedComInterface` 属性（[L262-L273](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L262-L273)）；CS2J3203 extern 方法（[L275-L287](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L275-L287)）；CS2J3202 `Marshal`/`NativeLibrary`/`HandleRef`/`SafeHandle`（[L289-L355](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L289-L355)）。
- 转换器侧另有一条矛盾路径（§4 风险 16）：[ClassTransformer.cs L346-L350](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L346-L350) 对 extern/DllImport 直接抛异常，[L390-L392](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs#L390-L392) `IsExternLike` 文本判名；而 [UnsupportedDomainPasses.cs L373-L381](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L373-L381) `IsPInvokeContainerFile` 的注释却称「ClassTransformer 会发 stub」。C01 已把 DllImport/LibraryImport extern 视为锁定符号（[RefOutInAliasAnnotator.cs L278-L295](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInAliasAnnotator.cs#L278-L295)，核订重定位；`IsLocked` 谓词本体在 [L265](file:///d:/code/cs2j/src/CSharpToJava.Core/RefOutInPreprocessor/RefOutInAliasAnnotator.cs#L265)）。

### 1.2 静默丢失面汇总

| 面 | 现状 |
|---|---|
| 方法/返回值/参数/类型（除 §1.1 特例）/程序集级自定义 attribute | 无任何发射路径；[MethodTransformer.cs L98](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs#L98) 唯一 attribute 相关代码 `BuildDeploymentItemAnnotation(...) => null`；grep 口径纠正为 `AttributeLists` 后，Transformers 内**恰 3 个文件**命中（[EnumTransformer.cs L67-L70](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Type/EnumTransformer.cs#L67-L70)、`ClassTransformer`、`PropertyTransformer`）；**member-desugar（C11-C14，默认关）是链上第二批 attribute 消费者**（8 文件，见 §1.1 族 B 核订补录），但它是**改写期**的迁移路由而非「发射到 Java」——本行「无发射路径」的结论对两者在 v1 都成立 |
| Caller* 族 | 属性丢弃 + 调用点不注入，无诊断（§1.1 族 A） |
| Newtonsoft / DataContract 名契约 | compat 有序列化器与属性类壳，但成员级 `Name` 等参数无读取分支，按名契约静默不生效 |
| STJ 注解的未读参数 | 转换器按属性类名白名单读取固定构造参数/具名参数；其余实参（对象初始化器形式、未列名具名参数）静默丢弃 |
| P/Invoke/平台 | 有显式诊断（CS2J3xxx），但 `NativeMethods.cs` 文件名豁免与 ClassTransformer 抛异常行为自相矛盾（风险 16），且 C10 不应重复报同一节点 |

ROADMAP #7「attribute(json)」登记待前置化（[ROADMAP.md L7](file:///d:/code/cs2j/ROADMAP.md#L7)，**核订纠错**：原文引 L8，实际 L8 是「method parameter default value」，`7. attribute(json)` 在第 7 行）。

### 1.3 目标、非目标与与 C06/C42 的分工

**目标**：

1. **Caller* 族**：对全编译中每个「省略了 Caller 形参对应实参」的源内调用点，用语义模型算出编译器本会注入的值，显式插入实参；随后从形参声明移除 Caller attribute（默认值表达式保留，见 §2）。产物是语义显式、可编译、可在 .NET 半区跑源项目测试的 C#（catalog §5.1 两半区方法论）。
2. **序列化注解族**：v1 **保留全部属性语法不动**，产出一份按符号身份分类的 `AttributeInventory`（桥接状态 + 实参物化核对），把「映射存在但语义不可用」的具体命中（未实化的构造/具名参数、无消费分支的注解族）报成显式诊断；为后续「显式化注册代码」形态（M2+）准备桥接面清单。
3. **平台注解族**：只盘点、只透传、只在报告中引用主管辖 pass；不新增任何 CS2J3xxx 诊断、不改写、不剥离。

**非目标（v1）**：不改转换器/compat 任何代码；不做 JSON 契约的显式注册代码改写；不做 P/Invoke→FFM 归一（C06）与平台隔离（C42）；不处理成员去糖（C11-C14 落地前属性仍挂在原语法成员上）；不做全宇宙自定义 attribute 的语义推理。

**分工红线**（catalog §3.3 平台边界族 [L912](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1090) 与 C06 设计 §11 既有约定）：

- **CS2J3001/3101/3102/3201-3203 的发射权在平台边界族（C42/C06）**：C10 只消费这些码的语义边界做盘点分类，绝不在同一节点上重复诊断；
- C06 负责 extern 的归一/白名单/UF060 判定，C42 负责非互操作输入的隔离与拒绝基建，「已归一 facade」共享谓词由 C06/C42 落地；C10 产出的 inventory 是它们的**输入数据**之一，不是判定分支；
- 反过来，Caller* 调用点填名与 JSON 名契约核对是 C10 独占面，C06/C42 不碰。

## 2. 关键设计决策表

| 决策点 | 结论 | 备注 |
|---|---|---|
| Caller* 总形态 | **调用点显式填参 + 声明端去属性**：以「被调方法符号 + 形参序号」为 plan 键，全部源内省略点插入编译期常量实参，随后移除形参上的 Caller attribute；plan 原子（全转或全不转） | 禁止文本匹配；绑定一律走 SemanticModel/IOperation |
| 调用点值的取得方式 | 走 IOperation：`IInvocationOperation`/`IObjectCreationOperation` 的 `Arguments` 中 `IsImplicit && ArgumentKind == DefaultValue` 的实参即省略点，`Parameter` 直接给形参符号；读形参 `GetAttributes()` 判 Caller 族 | 天然处理重载解析、扩展方法、可选参数居中省略、命名实参 |
| 插入实参的语法形态 | 原实参表全为位置实参 → 位置插入；原表含命名实参 → 插入实参一律命名形（`memberName: "X"`）；实参追加在 `params` 实参之前；构造初始化器 `this(...)`/`base(...)` 同形处理 | 命名/位置混用在 C# 中位置实参不能出现在命名实参之后 |
| `CallerMemberName` 取值 | 调用点所在成员名：方法→方法名；属性访问器→属性名；事件 add/remove→事件名；索引器访问器→`"Item"`；实例构造→`".ctor"`；静态构造→`".cctor"`；终结器→`"Finalize"`；局部函数→函数名；lambda/匿名方法→**外层**成员名；顶层语句→`"$"`；字段/属性初始化器→按 Roslyn lowering 归到 `.ctor`/`.cctor`；无法静态确定的上下文 → CS2J1304 | 取值表以 fixture 与 Roslyn 实测逐行锁定（§7.1），不确定不猜 |
| `CallerFilePath` 取值 | 默认策略 `FreezeAnalyzed`：冻结**分析树**（C10 输入树）的 `SyntaxTree.FilePath` 字面量；选项 `Empty`（插 `""`）/`Error`（CS2J1304 跳过 plan） | 路径是中间树路径而非用户原始路径（前序 pass 可能已复制/换行），这是诚实可得值，限制见 §10-3；字面量经 `SyntaxFactory.Literal` 转义 |
| `CallerLineNumber` 取值 | 调用语法节点 `GetLineSpan().StartLinePosition.Line + 1`，int 字面量；插入与改写不新增换行（格式化导致的行位移不影响已冻结值——值本就描述 C10 输入树的行） | 见 §10-4 .NET 半区断言注意事项 |
| `CallerArgumentExpression` 取值 | 特性构造实参必须是常量字符串形参名；取绑定形参对应**显式实参表达式**的 `ToString()`（含内部 trivia、不含外层 trivia）插为字符串字面量；实参也被省略/不存在 → CS2J1304 跳过该 plan | 与 Roslyn 实测对照（空白/注释边缘），分歧即保守跳过 |
| 声明端默认值 | **保留**原默认值表达式（`= null`/`= 0`/`= ""`），仅移除 Caller attribute 列表项（同列表其它 attribute 保留） | E08 已先跑且整体豁免过这些方法；保留默认值使外部程序集调用/dynamic 残余路径行为不劣于现状；M2 评估复用 E08 Core 对「解冻后方法」补重载综合（开放问题 Q1） |
| 外部程序集目标 | 调用点在源内、被调方法在外部程序集（日志库等高频场景）：只改调用点、无声明可改；plan Kind=`ExternalTarget`，残余闸门不要求其声明属性消失 | 这是 Caller* 价值最大的形态 |
| 源内目标 | plan Kind=`SourceDeclaration`；含任一不可行站点（dynamic/表达式树/不可确定上下文）→ **整 plan 跳过**：声明属性与全部站点都不动，出 CS2J1301/1302/1303 | 只去属性不填站点会改变残余站点行为，禁止半改 |
| JSON/序列化注解 v1 形态 | **保留**（不移动、不包装、不改写）；只做 inventory + 桥接面核对：每个命中登记「属性符号身份/目标符号/构造与具名参数/转换器是否读取/compat 物化位」 | 显式化注册代码（catalog 列举的另一种归一形态）列 M2+，需 Java 半区方法面齐备（风险 14） |
| 桥接面核对判据 | 对 inventory 中每个属性使用点，核对其实参是否都在转换器/compat 的**已读取集合**内；属性族无消费分支（Newtonsoft `JsonProperty`、DataContract `DataMember` 等）或实参未被读取 → CS2J1310 | 「映射存在 ≠ 语义可用」的可执行化检查 |
| P/Invoke/平台注解 | **盘点 + 透传**：登记 DllImport/LibraryImport/ComImport/MarshalAs/UnmanagedCallersOnly/GeneratedComInterface 与 SupportedOSPlatform* 出现点（Info AN003 + inventory 报告），树零改动；永不发射 CS2J3xxx | 拒绝/归一归 C06/C42；矛盾现状（风险 16）由 C42 澄清，C10 只提供数据 |
| 自定义 attribute 策略 | 符号身份白名单（`KnownAttributeCatalog` 内置 + overrides 增补）；白名单外的自定义 attribute 默认 **CS2J1320 Warning**（`--strict` 升级 Error），不静默丢弃；源码生成器类（编译后无运行时语义）可经 overrides 标 `ignore` | 程序集级 `AssemblyInfo`、测试框架 attribute 走过滤/分类，不刷警告 |
| 不可桥接 attribute | 维护一小张「已知无桥且语义可能被依赖」拒识表（denylist）：命中即 CS2J1321 Error 并指明人工/归属 pass；已有主管辖 pass 的（Flags→enum 族、Conditional→conditional、ThreadStatic→C09、平台族→C06/C42）归 `GovernedElsewhere` 不报错 | 风险 12：显式报错优先于静默透传 |
| 反射 / 按名契约 | 本 pass **不改名、不改签名、不改 JSON 名字符串**；`nameof` 不动；`GetCustomAttribute`/`GetMethod("…")`/XAML/序列化按名反射静态不可见，沿用 JK §9 同款限制声明；依赖运行时 attribute 发现的类型可 overrides.skip | catalog §4 风险 7 |
| 表达式树 / dynamic | 表达式树内调用点：填参会改变树节点形态 → plan 跳过 CS2J1303；dynamic 绑定/符号不可解析的调用点 → CS2J1302 跳过；判定谓词 v1 内联最小实现，未来与 JK/C02 收敛共享 `ExpressionTreeFacts` | catalog §4 风险 8 |
| 转换器改动 | 本特性 PR **零改动**；转换器侧 JSON 特例的退役/收口以默认开启为前置门，另开后续 PR（§5.4） | 与 C02/C08/C09 同一范式 |

## 3. 注册表与链位契约

### 3.1 描述符

翻转时从 [PassStubCatalog.cs L170-L179](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L170-L179)（核订重定位）删除 stub，在 [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) 的 `Implemented` 表中**紧随 `make-readonly` 描述符之后**插入（声明序选择，兼顾 stub 期拓扑结果与 C02 落地后的迁移，见 §3.2；注册表拓扑顺序由 [PassChainOrderTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs) 锁死；[PassRegistry.cs L95-L142](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassRegistry.cs#L95-L142) 的 `TopologicalSort` 以**声明序为确定性 tiebreak**，禁用的 stub 仍作为排序节点存在；核订纠正原文引的 L95-L133——该文件的 Kahn 排序现跨 L95-L142）：

```text
Id                          = "attribute-normalize"
DisplayName                 = "Attribute normalization"
IntermediateDirectoryName   = ".cs2j-attribute-normalize-src"
Stage                       = PassStage.Declarative
Status                      = PassStatus.Implemented
DefaultEnabled              = false（opt-in；过 §8 门槛后翻 true）
RunAfterIds                 = { "generic-deepen", "make-readonly" }（语义锚 + stub 期确定性锚，见 §3.2）
SupportLibrary              = PassSupportLibrary.None
NeedsSemanticModel          = true
VerifyBuildByDefault        = true
VerifyTestsByDefault        = true
CatalogItems                = "C10"
Description                 = "Fill Caller* info arguments at call sites; inventory JSON/serialization and P/Invoke attributes."
```

### 3.2 拓扑处理（**2026-09-29 核订：前提已变——C02 generic-deepen 已落地**）

> **核订结论（本节前提失效）**：原文写作时 generic-deepen 自身是 stub，故本节按「C10 先于 C02 翻转」设计了两阶段迁移。**C02 已于 db9bbd08 落地**：`generic-deepen` 现为 [PassCatalog.cs L167-L178](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L167-L178) 的 Implemented 条目（`defaultEnabled: false`、`PassStage.Abi`、`RunAfterIds = {generic}`），其 id 常量已在 `PassIds.GenericDeepen`（[PassCatalog.cs:20](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L20)），stub 表中**已无该节点**。
>
> 因此：**本 pass 翻转时直接引用 `PassIds.GenericDeepen`**（不必再走 `PassStubCatalog.Ids.GenericDeepen`），并且原文交给「C02 翻转 PR」的 `LockedChain` 槽位迁移**改由本 pass 自己完成**——即 `attribute-normalize` 在 `LockedChain` 中应插在 **`make-readonly` 之后、`eliminate-goto` 之前**（而非原文 §3.2-2/§7.6 所写的「eliminate-goto 与 java-keywords 之间」）。
>
> 以下 4 条原文规则保留作历史与推导说明，其中第 1、3 条已被上述结论取代（标注 ✅已落地/⚠️已过时）：

1. ⚠️**已过时**——**id 常量归属**：C02 设计 §3.2 已约定其翻转时把 `Ids.GenericDeepen` 迁到 [PassIds](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L6-L27)（现为 L6-L27，核订重定位）。**C02 已按约定落地**：常量在 `PassIds.GenericDeepen`，本 pass 的 `RunAfterIds` 直接引用 `PassIds.GenericDeepen` 即可，原文的「先引 stub 常量、后由 C02 rebase」两步合并为一步。
2. **执行位置**：`RunAfterIds = { generic-deepen, make-readonly }` 的**结论保留**（双锚更稳，且 `generic-deepen` 现已可解析为已实现节点）。但需注意：原「C10 被 stub 节点推到 eliminate-goto 之后」的推导已失效——generic-deepen 落为已实现条目、紧随 `generic`（[PassCatalog.cs:167](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L167)），故 C10 的实际落点由 `make-readonly` 锚决定（在 `record-readonly`/`array-method`/`make-readonly` 之后），catalog 建议区（`… E13/C07 → … → C09/C10 → C11-C14 …`）成立 ✓。
3. ✅**已达成**——**位置迁移**：generic-deepen 现为已实现 pass，其 stub 节点消失，C10 不再被推迟到 stub 扫描段；已实现子序列中的落点即 **make-readonly 之后、eliminate-goto 之前**（前提：C10 描述符在 Implemented 表中插在 make-readonly 之后）。语义不变量（晚于 generic-deepen/make-readonly、早于 java-keywords/member-desugar）成立。**唯一遗留动作**：`LockedChain` 的槽位需由**本 pass 的翻转 PR**写入（原设计记的「由 C02 翻转 PR 一并改写」已不成立——C02 的翻转提交 26ea367c/db9bbd08 只处理了自己的槽位）。
4. **不变量**（不变）：始终晚于 E08 strip-default（Caller 豁免逻辑的载体，经 generic 传递满足）；始终早于 java-keywords（FinalNaming 锁尾）；始终早于 member-desugar。**核订实测现状**：member-desugar 已在 [PassCatalog.cs L230-L241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L230-L241) 落地（`RunAfterIds = {struct-value-semantics, closure-lift}`——两个 stub，故它被拓扑推到链尾），`java-keywords` 在 L242-L258（`PassStage.FinalNaming`）；两者现状均晚于 C10 ✓。顺序断言只锁「已启用子序列」中 C10 的这三条相对关系，不断言禁用 stub 的穿插。

### 3.3 Stub→Implemented 翻转清单

| 文件 | 改动 |
|---|---|
| [PassStubCatalog.cs L170-L179](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L170-L179)（核订重定位） | 删除 `AttributeNormalize` stub 条目；`Ids.AttributeNormalize` 常量迁到 [PassIds](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L6-L27)（全仓仅 stub 表自引用，迁移无外部点）。**核订提示**：id 常量归属现有两手先例——C02 迁到了 `PassIds`（`PassIds.GenericDeepen`），C04 保留在 `PassStubCatalog.Ids`（`PassCatalog.cs:210` 反向引用 `PassStubCatalog.Ids.IteratorLowering`）；建议随 C02，并在实施时与既有形态对齐 |
| [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs) | 按 §3.1 插入 Implemented 描述符；`PassIds` 增 `AttributeNormalize` 常量 |
| `src/CSharpToJava.CLI/Preprocessing/Adapters/AttributeNormalizePassAdapter.cs` | **新增**适配器（§5） |
| [PassChainFactory.cs L98](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L98)（`CreateAdapter` 现起于 L98，核订重定位） | `CreateAdapter` switch 增 `PassIds.AttributeNormalize => new AttributeNormalizePassAdapter(...)`；**不进 `BuildLegacyToggles`**（现为 [L64-L85](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L64-L85)）（无历史开关），经通用 `--enable-pass/--disable-pass attribute-normalize` 与 `preprocess --pass attribute-normalize` 控制（同 C09 先例） |
| [PassChainConfiguration](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L29-L53)（核订重定位：类现跨 L29-L53，已含 `MemberDesugarOptionsFactory`）/ [CliPreprocessChain.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs)（配置组装现于 [L113-L126](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L113-L126)） | 增 `AttributeNormalizeOptionsFactory`（lazy 加载 overrides，仿 `JavaKeywordOptionsFactory`/`MemberDesugarOptionsFactory`）并透传 |
| `config/AttributeNormalizeOverrides.json` | **新增、可选、默认缺省**：`{ "keepAttributes": [], "ignoreAttributes": [], "skipTypes": [], "callerFilePathPolicy": "FreezeAnalyzed" }`；查找逻辑同 record-readonly/java-keywords（CWD 起向上找） |
| `src/CSharpToJava.Core/AttributeNormalize/AttributeNormalize*.cs` | 新增 Core 组件（§4） |
| 测试 | [PassStubCatalogTests.cs L24](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L24) 的 stub 计数断言——**核订实测更正**：原文记「23 → 22」已过时，`PassStubCatalog.Stubs` 现**实际 21 条**（`Ids` 常量 22 个），而该行断言仍写着 **22**（26ea367c 移除 member-desugar stub 时 22→21 漏改数字，**该断言今天即红**）；本 pass 翻转后应为 **20**，建议顺带把现有偏差一并修正；PassChainOrderTests 顺序断言；Core 单测、项目 fixture、residual 闸门、CLI 测试（§7） |

不需要改的：`Program.cs` 指纹逻辑（全集 `pass:<id>=on/off` 令牌自动覆盖，翻转造成一次性缓存失效，foundation plan 既定决策）；保留目录由实际执行集合驱动；stub 的 CS2J9001 拒绝路径翻转后自然消失；通用 verb 自动可达。

### 3.4 中间目录与验证

- 中间目录 `.cs2j-attribute-normalize-src`，输入为前序 pass（generic-deepen 或 make-readonly）产物树；干净文件字节级复制（含 BOM 保持，同各 pass 惯例）。
- Caller 改写是跨文件操作（声明在 A 文件、调用点可能遍布全树），采用 **JK 同款自定义 `ExecuteAsync`**（整树迭代重放 + plan 归属回退），不走逐文件标准骨架（§4.2）。
- `VerifyBuild/VerifyTests` 默认开（最严档），`DeleteBuildOutputsAfterRestore = true`；只新增「一环一棵树 + 回退重放轮次」，遵守 catalog §4 风险 11。
- 无支撑库、无生成文件分发：不新增 `PassSupportLibrary` 枚举值；Caller 填参只用 BCL 常量字面量（string/int），JSON 盘点不改树。

## 4. Core 组件设计

目录：`src/CSharpToJava.Core/AttributeNormalize/`，命名空间 `CSharpToJava.Core.AttributeNormalize`。

### 4.1 文件清单

| 文件 | 职责 |
|---|---|
| `AttributeNormalizeOptions.cs` | `record AttributeNormalizeOptions`：`bool Strict`（残余/未知项升级 Error）、`bool ReportInfo`、`CallerFilePathPolicy CallerFilePathPolicy`（FreezeAnalyzed/Empty/Error）、`IReadOnlySet<string> KeepAttributes`（增补白名单，完全限定元数据名）、`IgnoreAttributes`（源码生成器类，不盘点不告警）、`SkipTypes`（类型完全限定名，整体不分析不改写） |
| `AttributeNormalizeStatistics.cs` | 通用字段 `FilesScanned/FilesTransformed/FilesSkippedClean/FilesDirtyNoRewrite/ResidualCount`；特有：`CallerPlansDetected/CallerPlansApplied/CallerPlansSkipped`、`CallerSitesFilled`（按四族分计数）、`CallerAttributesRemoved`、`AttributesInventoried`（按 family 分桶字典）、`BridgeGaps`、`UnknownAttributes`、`PlatformAttributesInventoried`、`PlansRolledBack`、`SkippedByReason` |
| `AttributeNormalizeDiagnostic.cs` | `AttributeNormalizeSeverity`（Info/Warning/Error）、`AttributeNormalizeDiagnosticCode`（CS2J13xx 与 AN00x 常量）、`AttributeNormalizeDiagnostic(Severity, Code, Message, FilePath?, Line?, Column?, PlanId?)` + `ToMsBuildString()` |
| `AttributeNormalizeResult.cs` | `record AttributeNormalizeFileResult(string OutputCode, bool Changed, IReadOnlyList<AttributeNormalizeDiagnostic> Diagnostics, AttributeNormalizeStatistics Statistics)` |
| `AttributeInventory.cs` | `enum AttributeFamily { CallerInfo, JsonSystemTextJson, JsonNewtonsoft, DataContract, XmlSerialization, Debugger, Platform, Interop, Tests, Flags, Compiler, SourceGenerator, Custom, Other }`；`enum BridgeStatus { Bridged, InventoriedPassthrough, BridgeGap, GovernedElsewhere, Unknown, Ignored }`；`sealed record AttributeOccurrence(string AttributeMetadataName, AttributeFamily Family, BridgeStatus Status, ISymbol? TargetSymbol, Location Location, IReadOnlyList<string> MaterializedArguments, IReadOnlyList<string> UnreadArguments, string? OwnerPass)`；`AttributeInventoryReport`（确定性排序：文件+span） |
| `KnownAttributeCatalog.cs` | 内置元数据名常量表（§4.4），每条携带：family、默认 BridgeStatus、**转换器/compat 已读取实参集合**（证据指针，如 `PropertyTransformer.cs:L158` + `CompatJsonIgnore`）、主管辖 pass（GovernedElsewhere 项）；提供 `Classify(INamedTypeSymbol attributeType, AttributeSyntax, AttributeNormalizeOptions)` 与 `KnownCallerAttributeNames` |
| `CallerInfoPlan.cs` | `sealed class CallerInfoPlan`：`string Id`（确定性：`caller:<目标方法元数据名>(<参数索引集合>)`）、`IMethodSymbol TargetMethod`、`PlanKind Kind`（SourceDeclaration/ExternalTarget）、`IReadOnlyList<CallerSite> Sites`（语法节点路径+span+实参索引+计算出的常量值+插入形态）、`Location? DeclarationLocation`、`Outcome`（Active/Skipped）、`FailCode/FailReason`、`TouchedFiles`、`OwnsTouch(file, span)`；plan 是回退最小单位 |
| `CallerInfoAnalyzer.cs` | **整树一次**：取四枚 Caller 属性符号（`Compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.Caller*Attribute")`，含 `CallerArgumentExpressionAttribute`）；遍历每个语义模型的 IOperation 树枚举 `IInvocationOperation`/`IObjectCreationOperation`（含构造初始化器、扩展方法、委托 Invoke），收集隐式省略实参中形参带 Caller 属性者；按目标方法符号聚合 plan；源内目标补声明位置；同步跑全树 attribute inventory；产出确定性排序的 plan 列表与报告 |
| `CallerInfoRewriter.cs` | `CSharpSyntaxRewriter`：按本文件 active plans ①在调用点插入显式常量实参（§2 形态规则）②移除源内目标形参上的 Caller attribute（同列表其它 attribute/trivia 保留）；改动节点统一挂 `RewriteAnnotation = new("Cs2jAttributeNormalizeRewrite")`；只接受语义绑定命中，禁止文本替换 |
| `AttributeResidualScanner.cs` | 重写后扫描：①输出中残余 Caller* attribute 数与 skip plan 集合对账（非登记残余 → CS2J1322）；②inventory 复扫分类稳定性（防御性：改写不得新增/吞掉 attribute 出现点） |
| `AttributeNormalizePreprocessor.cs` | 对外入口：`BindCompilation(CSharpCompilation)`/`Analyze()`/`ExcludePlans(ids)`/`IReadOnlyList<CallerInfoPlan> Plans`/`AttributeInventoryReport Inventory`/`RewriteFile(string, SemanticModel, stats, diags)`/`bool TryAttributeDiagnostic(Diagnostic, rewrittenByFile, out planId)`/`ResidualScan(Compilation)`/`internal static string? FormatAndValidate(CompilationUnitSyntax)`（三候选：改动区格式化→整文件格式化→原文，与 JK/RR 同款） |

编译构造沿用 JK 自驱适配器同款配方（runner 的 [BuildStandardCompilation L136-L155](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L136-L155) 只装配 `CreateAdditionalSyntaxTrees`，global using 需自建）：`CSharpParseOptions(LanguageVersion.Latest)` + `NETCOREAPP` 预处理符号 + `ProjectUnsignedTypePreprocessor.GetBasicReferences()` + `allowUnsafe: true` 的 DLL 编译 + 按项目合成 `__cs2j_global_usings.g.cs`（`ImplicitGlobalUsings.BuildForProject`，逐行复制 [JavaKeywordsPassAdapter.cs L275-L312](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/JavaKeywordsPassAdapter.cs#L275-L312) 的 BuildCompilation）。

### 4.2 数据流（自定义 ExecuteAsync，跨文件 plan/回退）

1. **复制 + 基线**：适配器把全部 .cs 复制进中间树，用原始内容建一次全编译，取基线错误集（流程逐行同构 [JavaKeywordsPassAdapter.cs L56-L131](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/JavaKeywordsPassAdapter.cs#L56-L131)）。
2. **Bind + Analyze（整树一次）**：`BindCompilation` 后 `Analyze()` 产出 Caller plan 列表与 `AttributeInventoryReport`（后者不随回退变化——它描述输入树）。plan 一次成型；overrides/选项在适配器构造时应用。
3. **Plan 原子性**：源内目标 plan 含任一不可行站点（dynamic、表达式树、取值不可静态确定、外部站点不可见导致的契约不闭合）即 `Skipped + FailCode`，声明属性与全部站点都不动；ExternalTarget plan 的不可行站点只影响该站点所在 plan（整 plan 同样跳过，保证目标方法所有源内调用点形态一致）。
4. **迭代重放**：每轮从原始副本重放 → 建编译 → `ExcludePlans(excluded)` 后逐文件 `RewriteFile`（调用点与声明点在同一遍内按 plan 表改；跨文件经同一 `CSharpCompilation` 的符号反查闭环，无需特殊编排）→ 重建编译 → DiffErrors → `TryAttributeDiagnostic` 归属唯一 plan → 排除并回退其 TouchedFiles → 收敛；无法归属单 plan → CS2J1300 本次运行全部回退（与 JK030/JK031、CS2J1100、CS2J1200 同构；插实参在 C# 类型系统下稳态预期零回退，回退通道主要防御绑定误判）。
5. **AfterFiles 等价步**：回退收敛后跑 `ResidualScan` 与 inventory 诊断发射（CS2J1310/1320/1321/1322、AN002/AN003）；strict 决定 Warning/Error 升级。
6. restore/build/test 由适配器复用 `context.Restore()`/`VerifyBuildAndTestsAsync()` 收尾。
7. **单文件 verb 模式**：plan 的 TouchedFiles（源内目标含声明文件 + 站点文件；ExternalTarget 含站点文件）越出输入文件者排除 + CS2J1390（仿 JK090/CS2J1290/CS2J1190）。

### 4.3 规则全集（AN-x）

**Caller 族（AN-C）**

- **AN-C1 省略点识别**：仅处理 IOperation 判定为「隐式默认值实参」且对应形参带 Caller* 属性的位置；显式实参（含显式传 `null`/`""`/`0`）永不覆盖。
- **AN-C2 形参属性识别**：以属性**符号身份**判定（`SymbolEqualityComparer` 对比 `GetTypeByMetadataName` 取得的符号），不做显示名字符串匹配（不沿用 E08 的 `name.Contains(...)` 启发式，[DefaultParameterVerifier.cs L113-L115](file:///d:/code/cs2j/src/CSharpToJava.Core/DefaultParameterStripper/DefaultParameterVerifier.cs#L113-L115) 仅作历史兼容参考）；`AttributeUsage` 不合法的位置本身是基线编译错误，不产生 plan。
- **AN-C3 填参**：按 §2 形态规则插入字面量；四个属性同形参多轮命中互不干扰（同一形参至多一个 Caller 属性，否则基线编译错误）。
- **AN-C4 声明归一**：SourceDeclaration plan 成功时移除形参上的 Caller attribute（含四族全部），默认值表达式与同列表其它 attribute 保留；移除后若参数表/属性列表为空，空列表语法按 Roslyn 规范清理。
- **AN-C5 `CallerArgumentExpression` 专项**：构造参数为常量形参名 → 取该形参当前调用的显式表达式文本；命名实参调用同样按绑定形参取表达式；目标表达式缺失（被调方要求的形参在本调用也被省略）→ CS2J1304。
- **AN-C6 重载/隐藏/接口**：以绑定到的 `IMethodSymbol` 的形参属性为准——经接口分派且接口方法形参**未**带属性时 C# 本就不注入，本 pass 不插；override 不复刻属性时同理（绑的是派生方法就不注入）；显式接口实现、隐藏方法按各自绑定符号独立建 plan。
- **AN-C7 构造初始化器/委托/索引器**：`this(...)`/`base(...)` 正常插（成员名按 `.ctor`）；委托调用按 `Invoke` 形参属性处理；索引器访问器形参（可选索引参数）按调用点 `ElementAccess` 的 IOperation 形态处理，fixture 锁定。
- **AN-C8 禁区**：表达式树 lambda 内调用点 → CS2J1303 跳过 plan；dynamic/`CandidateReason` 不可绑定 → CS2J1302 跳过 plan。
- **AN-C9 ExternalTarget**：目标方法 Locations 无源位置 → 只填站点；目标程序集引用缺失导致形参属性不可见时按普通调用处理（信息不足即无 plan），不猜。
- **AN-C10 幂等**：输出树二次运行零改动（属性已移除、实参已显式）；残余扫描保证。

**序列化/自定义注解族（AN-J/AN-U，v1 只盘点与诊断）**

- **AN-J1 全量盘点**：遍历树中全部 `AttributeSyntax`，经 `SymbolInfo`/`AttributeData` 解析到属性类型符号，按 `KnownAttributeCatalog` + overrides 分类；解析失败（缺引用、源码内未编译过的属性类型）归 `Custom` 并标语法名。
- **AN-J2 桥接面核对**：对 `JsonSystemTextJson`/`XmlSerialization`/`Debugger` 等已桥族，逐使用点核对构造参数与具名参数是否在转换器读取集合内；未读 → CS2J1310（消息含属性名、未读参数名、目标成员、证据指针）。
- **AN-J3 无消费分支族**：`JsonNewtonsoft`（`JsonPropertyAttribute` 等）、`DataContract`（`DataMemberAttribute` 的 `Name`/`EmitDefaultValue` 等）v1 一律 BridgeGap → CS2J1310（默认 Warning），消息说明「compat 有序列化器但成员参数无发射分支」，为 M2 显式化/补桥提供清单；命中为零的属性不刷诊断。
- **AN-J4 名契约保护声明**：本族 attribute 的一切字符串实参原样保留（这是反射按名契约数据），本 pass 不做任何转义/重命名/迁移。
- **AN-U1 未知自定义 attribute**：白名单（内置 Bridged/GovernedElsewhere/Tests/SourceGenerator + overrides.keep/ignore）之外 → CS2J1320 Warning（strict Error），每使用点一条、去重；消息提示三选一：确认为源码生成器/编译期-only → overrides.ignore；需要运行时语义 → 登记补桥或转人工；确认无运行时语义 → overrides.keep 消音。
- **AN-U2 已知不可桥接 denylist**：小而硬的拒识表（v1 候选：无主管辖 pass 且改变运行时行为的行为型 attribute，实施时按 sdk 测量结果填充，宁少勿滥）→ CS2J1321 Error。
- **AN-U3 过滤面**：`*.AssemblyInfo.cs` 程序集级属性（沿用 [BoundaryAnalysisHelpers.IsGeneratedAssemblyInfoFile](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L369-L371) 同款判定）、测试框架 attribute（xUnit/NUnit/MSTest，测试项目内）分类但不告警；`[Flags]`/`[ThreadStatic]`/`[Conditional]` 等 GovernedElsewhere 项只登记归属。

**平台/互操作族（AN-P，只盘点）**

- **AN-P1**：DllImport/LibraryImport/ComImport/MarshalAs/UnmanagedCallersOnly/GeneratedComInterface 与 SupportedOSPlatform* 出现点登记 inventory（family=Interop/Platform，OwnerPass=C06/C42），Info AN003（仅 verbose），树零改动、零 CS2J3xxx 发射、零 CS2J1320/1321。
- **AN-P2**：inventory 报告对每个互操作属性附带现有诊断码引用（3201/3101），供 C42 澄清风险 16 矛盾时做交叉核对；不因 `NativeMethods.cs` 豁免差异自行豁免/收紧。

### 4.4 内置白名单分类表（v1，实施时以代码为准逐项补证据指针）

| Family | 元数据名（示例） | v1 状态 | 依据/消费点 |
|---|---|---|---|
| CallerInfo | `System.Runtime.CompilerServices.CallerMemberName/CallerFilePath/CallerLineNumber/CallerArgumentExpressionAttribute` | 改写后移除 | 本 pass 独占 |
| JsonSystemTextJson | `JsonIgnoreAttribute`、`JsonPropertyNameAttribute`、`JsonIncludeAttribute`、`JsonConverterAttribute`、`JsonNumberHandlingAttribute`、`JsonExtensionDataAttribute`、`JsonConstructorAttribute` | Bridged（逐参数核对） | PropertyTransformer L154-L170、FieldTransformer L166-L218、6 个 CompatJson* |
| JsonNewtonsoft | `Newtonsoft.Json.JsonPropertyAttribute`、`JsonIgnoreAttribute`、`JsonConverterAttribute`、`JsonObjectAttribute` | BridgeGap（AN-J3） | compat `JsonConvert`/`JsonSerializerSettings`/`DefaultContractResolver` 存在，成员级无符号读取分支 |
| DataContract | `System.Runtime.Serialization.DataContractAttribute`/`DataMemberAttribute`/`IgnoreDataMemberAttribute` | BridgeGap（AN-J3） | [StructAnalyzer.cs L541-L556](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs#L541-L556) 仅用于只读分析；compat `DataContractSerializer` |
| XmlSerialization | `System.Xml.Serialization.XmlAttribute/XmlIgnore/XmlArray/XmlArrayItemAttribute` 等 | Bridged（逐参数核对） | PropertyTransformer L147-L153 |
| Debugger | `DebuggerBrowsableAttribute`、`DebuggerDisplayAttribute` | 部分 Bridged | PropertyTransformer L163-L164；compat 有对应类 |
| Flags | `System.FlagsAttribute` | GovernedElsewhere（C05/enum 族） | EnumTransformer L59-L71 |
| Interop/Platform | §4.3 AN-P1 清单 | GovernedElsewhere（C06/C42） | UnsupportedDomainPasses |
| Compiler/BCL | `System.ObsoleteAttribute`、`System.ComponentModel.DefaultValueAttribute`、`System.SerializableAttribute`、`System.Runtime.CompilerServices.*`（非 Caller） | InventoriedPassthrough | compat 有 `ObsoleteAttribute`/`DefaultValueAttribute` 壳；无运行时行为依赖 |
| Tests | xUnit/NUnit/MSTest 注解 | InventoriedPassthrough（测试项目内不告警） | E08 同样豁免测试方法 |
| ThreadStatic | `System.ThreadStaticAttribute` | GovernedElsewhere（C09） | C09 设计 |
| Conditional | `System.Diagnostics.ConditionalAttribute` | GovernedElsewhere（conditional/E01） | conditional strip pass |

## 5. CLI 适配器与集成

### 5.1 AttributeNormalizePassAdapter

新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/AttributeNormalizePassAdapter.cs`（预计 250-350 行），骨架复制 [JavaKeywordsPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/JavaKeywordsPassAdapter.cs)（自定义 `ExecuteAsync`：复制 → 基线集 → 迭代重放 → 归属回退 → restore），差异点：

- `Descriptor => PassCatalog.ImplementedById(PassIds.AttributeNormalize)`；`VerifyBuild/VerifyTests => true`；`DeleteBuildOutputsAfterRestore => true`；`CreateParseOptions => new CSharpParseOptions(LanguageVersion.Latest)`。
- 持有 `AttributeNormalizePreprocessor`（每轮新建，同 JK）、累计 accepted 统计/诊断；构造注入 options、verbose、filter、compat prefixes。
- 编译构建在适配器内自建私有 `BuildCompilation`（复制 JK 的 parse + 合成 global using + GetBasicReferences 配方，§4.1）；`context.BuildStandardCompilation()` 不适用（其默认不装 global using）。
- `RewriteFileAsync` 抛 `NotSupportedException`（自驱文件循环，同 JK L239-L240）。
- `OnRestoreFailed/OnBuildFailed/OnTestsFailed` → CS2J1330/1331；`HasErrors`；`BuildChainSummaryLine` 至少含：Caller plan 应用/跳过数、填参站点数（分四族）、inventory 属性总数与 BridgeGap/Unknown 数、回退 plan 数、残余数；`BuildChainDiagnosticLines`（Info 仅 verbose，MSBuild 格式）。
- 不新增门面类；测试与独立执行走 runner + 通用 `preprocess` verb。

### 5.2 选项与 overrides

- convert-project：默认不跑（descriptor 默认关）；`--enable-pass attribute-normalize` 开启，`--disable-pass` 可关；`--strict` 复用链上既有 strict 口径（各 pass 共享）；overrides 路径经通用链配置传 `AttributeNormalizeOptionsFactory`（缺省找 `config/AttributeNormalizeOverrides.json`，缺文件=全默认，JSON 解析失败 Warning 后按空集继续，仿 record-readonly）。
- 独立 verb：`preprocess --pass attribute-normalize -s .. -d ..` 自动可达；单文件模式自包含闸门 CS2J1390。

### 5.3 指纹、保留目录、支撑库

- 指纹：翻转自动纳入全集令牌（`pass:attribute-normalize=false→true` 一次性失效，接受）。
- 保留目录：`.cs2j-attribute-normalize-src` 由已执行集合自动 preserve。
- `SupportLibrary.None`：不注入/不过滤任何支撑工程，[IsExecutedSupportLibraryProject](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L264-L272)（核订重定位）无改动。

### 5.4 Java 半区消费清单（转换器后续 PR 的输入，本 pass 不实施）

1. Caller* 归一后，转换器输入中不再有 Caller attribute、调用点全部显式带参——转换器无需认识该族；E08 的 Caller 豁免分支（两处 `HasCallerAttribute`）在「以本 pass 预处理过的项目」路径上变为不可达，退役留后续 PR（默认开启为前置门）。
2. JSON 族：AN-J2/J3 产出的 BridgeGap 清单是补桥工单——优先 Newtonsoft `JsonProperty(Name)` 与 DataContract `DataMember(Name)` 的成员级读取分支（当前按名契约静默失效）；补桥方式仍走 compat 注解（`CompatJsonPropertyName` 同款路线），不在 C# 侧生成注册代码。
3. 「显式化注册代码」形态（把反射信息改写为启动期注册调用）是否启动，取决于 §8 门槛阶段 sdk 命中率与 Java 半区注册面盘点结果，单独立项（M2+）。
4. 转换器不删除任何既有 JSON 特例；本 pass 不要求转换器识别 inventory。

## 6. 诊断策略

新增 **CS2J13xx** 码段（CS2J10xx 泛型、CS2J11xx C02、CS2J12xx C09、**CS2J50xx C11-C14 member-desugar（本设计写作后落地）**、CS2J30xx/31xx/32xx 平台、CS2J33xx C01、CS2J40xx Java 元数据、CS2J90xx pass 基建，本段不冲突；码位只追加不改号）。**2026-09-29 核订实测**：`grep -rhoE "CS2J1[0-9]{3}" src/` 仅命中 CS2J1001-1003/1100-1106/1110/1120/1130-1131/1190，**13xx 段完全空闲** ✓；同时实测确认 50xx 已被 member-desugar 占用（CS2J5100-5183），故补入上面的枚举。MSBuild 格式：`文件(行,列): warning/error CS2J13xx: [规则/目标限定名] 描述`。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J1300 | Error | 整树重编译新增错误且无法归属单一 plan → 本次运行全部回退（同 JK030） |
| CS2J1301 | Warning（strict Error） | Caller plan 原子跳过（含不可行站点综合原因；消息分列站点） |
| CS2J1302 | Warning（strict Error） | dynamic/不可绑定调用点，调用点值不可静态判定，plan 跳过 |
| CS2J1303 | Warning（strict Error） | 调用点位于表达式树 lambda 内，填参会改变树形态，plan 跳过 |
| CS2J1304 | Warning（strict Error） | Caller 值上下文无法静态确定（含 CallerFilePathPolicy.Error、CallerArgumentExpression 目标实参缺失/文本分歧） |
| CS2J1310 | Warning | 序列化注解桥接面缺口：属性族无消费分支或所用构造/具名参数未被转换器/compat 读取（风险 14 的显式化；消息附证据指针） |
| CS2J1320 | Warning（strict Error） | 白名单外自定义 attribute 使用点（每点一条、去重；三选一处置指引） |
| CS2J1321 | Error | 命中已知无桥且语义可能被依赖的 denylist attribute，需人工处理或归属主管辖 pass |
| CS2J1322 | Warning / strict Error | residual 闸门：输出中残留无对应 skip 登记的 Caller* attribute |
| CS2J1330 | Error | 中间树 `dotnet restore/build` 失败 |
| CS2J1331 | Error | 中间树 `dotnet test` 失败 |
| CS2J1390 | Warning | 单文件模式：plan 非自包含（声明/调用点跨文件），排除 |
| AN001 | Info | Caller plan 已应用（目标限定名、站点数、分族计数），仅 verbose |
| AN002 | Info | attribute inventory 汇总（分 family/状态计数 + BridgeGap 明细头部），仅 verbose |
| AN003 | Info | 平台/互操作 attribute 已盘点（引用 3201/3101 主管辖），仅 verbose |

**与 CS2J3xxx 平台诊断的边界**：

1. 同一语法节点上，CS2J3201/3203/3101/3102/3001 的发射权只属于现有平台边界分析（及 C42 落地后的统一基建）；C10 对这些节点的输出上限是 AN003（verbose Info），且消息只引用码号、不复述拒绝。
2. C10 不读取也不修改平台分析的启用/豁免状态（包括 `NativeMethods.cs` 文件名豁免）；风险 16 的行为矛盾不在 C10 内裁决。
3. C06 落地后，对「已归一 facade」的共享判定谓词由 C06 提供；C10 inventory 的 Interop 分类消费该谓词做去重标注，不自己定义归一态。
4. 原则（风险 12）：跳过必有诊断痕迹，禁止注释/占位流入下一环节；Warning 经 `--strict` 升级 Error；CS2J1321/1330/1331/1300 不可降级。

## 7. 测试计划

### 7.1 Core 单测（Caller* 矩阵）

新增 `tests/CSharpToJava.Tests/AttributeNormalizeTests.cs`（内联源码 → `CSharpCompilation`（Latest + GetBasicReferences + allowUnsafe）→ Bind/Analyze/RewriteFile → 忽略空白文本断言 + `AssertOutputCompiles`，仿 EnumPreprocessorUnitTests/GenericPreprocessTests 编译 helper）：

- **可选参数矩阵**：省略（位置缺口在末位/居中）、显式传值不覆盖、多 Caller 参数同族组合、Caller 参数后接普通可选参数、与 `params` 同表（实参插在 params 前）、命名实参调用 → 插入实参转命名形。
- **四族取值**：CallerMemberName 覆盖 §2 取值表全部上下文（方法/属性 get-set/事件 add-remove/索引器/`.ctor`/`.cctor`/终结器/局部函数/lambda 取外层名/顶层语句 `$`/字段初始化器）；CallerFilePath 路径字面量与转义；CallerLineNumber 行号；CallerArgumentExpression 取 `a + b`、带空白/注释实参、命名实参绑定、目标形参省略 → CS2J1304。
- **重载**：重载组中仅一个带 Caller 形参；按绑定符号只改应注入的站点。
- **接口/虚分派**：接口方法不带属性、实现方法带（经接口调用不插、经具体类型调用插）；override 不复刻属性；显式接口实现；base 调用。
- **跨文件**：声明与调用点分两个编译单元，一次 Bind 后两边都改；ExternalTarget（外部程序集方法，用合成引用模拟日志库）只改站点不碰声明。
- **构造初始化器**：`this(...)`/`base(...)` 链上的省略点；对象创建 `new Foo(x)`（省略 y）。
- **委托**：自定义委托类型带 Caller 形参的 `d(x)` 调用点。
- **声明端**：属性移除后同列表其它 attribute 与默认值保留；多变量/trivia/XML doc 不丢；幂等（二跑 Changed=false、输出字节一致）。
- **反例/诊断码**：表达式树内站点 → CS2J1303 整 plan 不动（原子性断言：声明属性仍在、其它站点也没改）；dynamic → CS2J1302；不可确定上下文 → CS2J1304；回退通道（注入必然编译错误的可测 hook）→ CS2J1300 全回退、产物与输入字节一致。
- **inventory**：STJ 七注解分类为 Bridged 且 MaterializedArguments 正确；构造一个「读不到的具名参数」样本 → CS2J1310；Newtonsoft `[JsonProperty("z")]` → CS2J1310；DataContract → CS2J1310；自定义 `[MyAnno]` → CS2J1320，overrides.keep/ignore 后消音；denylist 样本 → CS2J1321；`[DllImport]` 样本 → 仅 AN003、树零改动、无 CS2J13xx 告警；`*.AssemblyInfo.cs` 与 xUnit `[Fact]` 不告警；`[Flags]`/`[ThreadStatic]`/`[Conditional]` 归 GovernedElsewhere。
- 干净文件字节级保留；BOM 保持。

### 7.2 项目级 sln fixture

新增 `tests/SampleAttributeNormalizeSolution/`（`SampleAttributeNormalizeLib` + `SampleAttributeNormalizeApp` + `SampleAttributeNormalizeSolution.sln`，按 [SampleJavaKeywordSolution](file:///d:/code/cs2j/tests/SampleJavaKeywordSolution/SampleJavaKeywordSolution.sln) / SampleRecordSolution 范式以 Content 挂入 `CSharpToJava.ProjectTests.csproj`，net10.0）：

- Lib：一个迷你日志接口 `Notify(string msg, [CallerMemberName] string member = null, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0, [CallerArgumentExpression("msg")] string expr = null)`，记录四元组；属性/索引器/构造/lambda/局部函数/跨类调用点全覆盖；一个外部目标模拟（Lib 内定义「被 App 当外部库用」的日志类）；一个 STJ DTO（含 `JsonPropertyName`/`JsonIgnore(Condition=…)`/`JsonNumberHandling`，并故意加一个转换器读不到的具名参数样本）；一个 Newtonsoft DTO 对照；一个自定义注解对照；一个 `NativeMethods`-style DllImport 声明（验证树零改动 + AN003）。
- App：.NET 半区行为测试——调用日志接口后断言四元组等于显式填参后的常量（成员名/文件名/行号/表达式文本），证明 .NET 半区降级正确；JSON 序列化往返测试。
- 断言：`result.Success`、统计计数；中间树不含 Caller* attribute（skip 对照项除外）、调用点含对应字面量、DllImport 原样；`dotnet build -c Release` 退出码 0、`dotnet test -c Release` 全绿；干净树字节级复制与 BOM 用例。

### 7.3 JSON 注解 fixture（回归锁定，不依赖 Java 半区）

- 复用现有回归语义：`JsonNumberHandlingAnnotationProjectTests`（类型级注解经项目管线挂 compat 标记）在本 pass 开启后必须保持结果不变（属性保留 → 转换器读取路径不变）；`ReflectionAttributeCompatTests`、`SystemTextJsonDeserializeTypeReferenceTests`、`DataContractSerializerCompatibilityTests` 同样作为开启/关闭双跑对照。
- 新增 fixture：BridgeGap 报告快照测试（给定属性集 → inventory 分类与 CS2J1310 消息稳定），防止白名单漂移。

### 7.4 Release build/test 与 sdk 基线

- fixture 与 `D:\csharp-sdk`（只读输入）预处理树分别 `dotnet build -c Release` / `dotnet test -c Release`；sdk 基线 = pre-existing 失败集快照，新增失败为零。
- M0 测量轮先跑：sdk 中四族 Caller attribute 命中数、调用点省略率、ExternalTarget 占比、表达式树/dynamic 占比；STJ/Newtonsoft/DataContract/自定义 attribute 使用点分族计数与 BridgeGap 比例；DllImport/平台注解点数（交 C06/C42 对账）。

### 7.5 residual 闸门测试

- 输出中 Caller* attribute 数 = 有 skip 诊断（CS2J1301-1304）登记的 plan 数；strict 下 CS2J1322 非零即退出非零。
- inventory 出现点总数 = 输入树出现点总数（改写不允许吞掉非 Caller attribute）；Caller attribute 移除数与 AN-C4 计数一致。
- 平台/互操作属性出现点在输出树中原样保留（逐字节比对该声明区间）。

### 7.6 CLI / 注册表测试

- 翻转前：`--enable-pass attribute-normalize` → CS2J9001 退出 1（现状回归）；翻转后：`--list-passes` 状态 Implemented、目录名、默认关时无目录无耗时；`--enable-pass` 后链上执行并串给下一环；`--disable-pass attribute-normalize` 可关；单文件 verb 跨文件 → CS2J1390；`--strict` 升级矩阵。
- 注册表：[PassStubCatalogTests.cs L24](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs#L24) 计数 23 → 22 并移除常量引用断言；[PassChainOrderTests.cs L18-L40](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs#L18-L40)（核订重定位）的 `LockedChain`（现含 20 个 Implemented id，条数已随 C03/C04/C02/C11-C14 落地增长）在 **`make-readonly` 与 `eliminate-goto` 之间**插入 `attribute-normalize`——**核订修正槽位**：原文写「eliminate-goto 与 java-keywords 之间」，其推导前提是 generic-deepen 仍为 stub；C02 已落地（见 §3.2 核订），正确槽位是 make-readonly 之后、eliminate-goto 之前（与 §3.2-3 一致）；另加单测断言 C10 在全序列中晚于 generic-deepen 与 make-readonly、早于 member-desugar 与 java-keywords（这四条不变量在 C02 落地前后都成立，具体 LockedChain 槽位的迁移由 C02 翻转 PR 改写，见 §3.2-3/§11）；stub 期穿插不锁死。
- 指纹令牌含 `pass:attribute-normalize=...`。

## 8. 默认开启验收门槛

`defaultEnabled: false → true` 必须同时满足：

1. **中间树正确性**：fixture sln 与 `D:\csharp-sdk` 预处理树 `dotnet build -c Release` 零错误、`dotnet test -c Release` 全绿；sdk 相对基线新增失败为零；Caller 四元组行为测试（§7.2）全绿。
2. **Caller 残余闸门**：sdk 中除显式 CS2J1301-1304 登记 skip 站点外零 Caller* attribute 残余；skip 站点分类有主、比例经 M0 数据评审可接受；strict 模式可过。
3. **注解盘点可运维**：sdk 全量 CS2J1310/1320/1321 清单完成人工 triage；BridgeGap 项要么补桥（§5.4 后续 PR）、要么显式接受风险并在 overrides/denylist 登记；无「未分类」attribute 出现点。
4. **全仓测试不回归**：`CSharpToJavaConverter.slnx` `dotnet build -c Release` + 三个测试工程 `dotnet test -c Release` 全绿（既有红灯数量不增加）；§7.3 四个 JSON/注解回归在开关双跑下结果一致。
5. **平台边界零干扰**：C10 开启前后，CS2J3001/3101/3102/3201-3203 在同一输入上的诊断集合**逐条一致**（含 NativeMethods.cs 现状行为，即使其自相矛盾——矛盾留给 C42）。
6. **文档**：本设计 + 对应 plan（`docs/superpowers/plans/2026-09-28-c10-attribute-normalize.md`）定稿；转换器后续 PR 清单（§5.4：E08 豁免退役、Newtonsoft/DataMember 补桥）登记。

不满足时任保持 opt-in（catalog §4 风险 10）。

## 9. 里程碑

| 里程碑 | 内容 | 验证 |
|---|---|---|
| **M1：Caller* 先行** | Core 骨架（options/statistics/diagnostic/result/plan/入口/FormatAndValidate）+ KnownAttributeCatalog 最小表（仅 Caller 四族 + GovernedElsewhere 平台项）+ CallerInfoAnalyzer/Rewriter（AN-C1~AN-C10）+ 适配器（自定义 ExecuteAsync + 回退通道）+ 注册表翻转（Implemented、默认关）+ Core 单测主体 + SampleAttributeNormalizeSolution 四元组行为测试 + 注册表/CLI 测试 + sdk 测量轮（Caller 部分） | `--list-passes` 可见；fixture build/test 全绿；Caller 残余闸门零残余（除登记 skip） |
| **M2：JSON/序列化注解盘点** | inventory 全族分类表（§4.4）+ AN-J1~J4/AN-U 规则 + overrides 文件与加载 + CS2J1310/1320/1321/1322 全集 + JSON fixture 与四个既有回归双跑 + sdk BridgeGap triage；过门槛后候选默认开评审 | inventory 快照测试稳定；sdk 无「未分类」点 |
| **M3：平台注解盘点收口** | AN-P1/P2 报告面（与 C06/C42 诊断码交叉引用）、CS2J3xxx 双跑一致性测试（门槛第 5 条）、与 C06 spec 的 facade 谓词对接预留点；转换器后续 PR 清单（E08 豁免退役、补桥工单）整理交付 | 开关前后平台诊断集合逐条一致 |
| M2+（不在本 pass 承诺） | JSON 契约显式化注册代码形态（风险 14 方法面齐备后立项）；Caller 解冻后复用 E08 Core 补重载综合（Q1）；`CallerFilePath` 路径还原策略（Q2）；共享 `ExpressionTreeFacts` 收敛 | 独立条目 |

## 10. 风险

1. **反射/字符串契约**（catalog §4 风险 7）：移除 Caller attribute 后，运行时反射读取该属性的代码（自定义日志/拦截框架）观察不到；JSON/按名契约虽不改字符串，但 CS2J1310 暴露出的 BridgeGap 本质也是「名字到不了运行时」。缓解：本 pass 不改名、nameof/字符串实参原样；反射依赖类型可 overrides.skip；限制声明与 JK §9 同款。
2. **映射存在 ≠ 语义可用**（catalog §4 风险 14 点名 C10）：compat 注解类齐备不等于参数被物化（CompatJsonIgnore 的 Condition  dropping 已是实证）。缓解：AN-J2 把核对做成可执行检查（Materialized/Unread 参数集合），默认开启门槛第 3 条要求 triage 清零；M2 不做显式化重写，避免生成不存在的方法面。
3. **CallerFilePath 路径语义**：冻结的是 C10 输入树（中间目录）绝对路径，与用户原始路径不同；Java 运行时拿到该字符串属预期（Java 侧本无注入机制，冻结值严格优于恒 null）。缓解：策略可配（Empty/Error）；fixture 锁行为；路径还原策略列开放问题 Q2，不承诺。
4. **CallerLineNumber 与 .NET 半区守门**：前序 pass 可能改变行号，冻结值反映 C10 输入树；在中间树上跑源项目测试时，若原测试断言「调用处源码行号」，需以 C10 输入树行号为基准——这与「前序 pass 后行号本已变化」是同一事实，非 C10 新引入偏差。缓解：插入实参不新增换行；fixture 用相对行断言并记录该限制。
5. **plan 原子性导致整方法跳过**：一个表达式树/dynamic 站点会冻结目标方法的全部 Caller 改写（声明属性保留 → 残余闸门可见）。缓解：这是有意的正确性超逼近；skip 有诊断、可审计、strict 可升级；ExternalTarget plan 与源内声明 plan 分离，外部高频形态不受内部不可行站点牵连。
6. **白名单维护漂移**：内置分类表是手工证据集，可能随转换器演进过时。缓解：表项携带证据指针（文件:行/compat 类）；inventory 快照测试与 sdk triage 双守门；未知项默认 Warning 而非静默，过时报错而不是漏报。
7. **平台风险 16 的矛盾**：ClassTransformer 抛异常与 `IsPInvokeContainerFile` 注释不可能同时为真。缓解：C10 不裁决、不豁免、不重复诊断（AN-P 只盘点），门槛第 5 条用双跑一致性防止本 pass 意外改变现状；澄清工作明确挂在 C42。
8. **表达式树/dynamic 共同禁区**（catalog §4 风险 8）：v1 内联判定，未来必须与 JK/C02/C09 收敛共享谓词，禁止四份漂移。
9. **中间树成本**（catalog §4 风险 11）：Caller 跨文件改写需自定义整树迭代（复制/reparse/restore + 回退重放轮次），比逐文件 pass 重；与 JK/RR 同构，指纹/preserve 既有机制覆盖，不新增缓存模型。
10. **热文件冲突**：注册表三文件被多个并行 pass 同时修改，见 §11。

## 11. 并行协调

| 并行条目 | 关系 | 合并顺序与冲突点 |
|---|---|---|
| **C02** generic-deepen（锚点 pass） | 引用其 id 作 RunAfter 锚 | **2026-09-29 核订：C02 已落地**（[PassCatalog.cs L167-L178](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L167-L178)，默认关），故原文「谁先合都行」的分支收敛为一条路径：C10 直接引用 `PassIds.GenericDeepen`。**原分配给 C02 翻转 PR 的动作需改由本 pass 完成**——`LockedChain` 中 C10 槽位插到「make-readonly 与 eliminate-goto 之间」（§3.2-3/§7.6 核订修正），C02 的翻转提交（db9bbd08/26ea367c）只写了自身槽位。**禁止**两边各自新建同名字符串常量 |
| **C06** unsafe-ffm / **C42** platform-isolation（平台边界族） | 共享 CS2J3001/3101/3102/3201-3203 诊断基建（catalog §3.3 [L912](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1090)）；C06 spec §11 已定「C42 负责隔离拒绝基建、已归一 facade 共享谓词」 | C10 只产 inventory/AN003，永不发 3xxx；C06/C42 落地时消费 inventory 的 Interop/Platform 分类做数据交叉核对，不要求 C10 改 Core 接口（报告为只读产物）。合入顺序任意；C42 澄清风险 16 后把结论回灌 §4.3 AN-P2 |
| **C11-C14** member-desugar（属性声明交互） | JSON/序列化 attribute 今天挂在属性/事件/索引器声明上；成员去糖后属性应跟随到 GetX/SetX/后备字段的正确目标（JsonPropertyName 跟 getter、JsonIgnore 跟访问器等） | **契约（核订：已履行）**：C10 在链路上先跑（早于 member-desugar ✓ 现状拓扑成立），v1 保留属性原位不动 = 不提前替 C11-C14 做归属决策。**2026-09-29 核订实测**：C11-C14 已落地（默认关）并且**自带成员级 attribute 路由**——`Properties/PropertyRule.cs:263`、`Events/EventDeclarationExpander.cs:41,69,86`（`RouteAttributeLists` 分流字段级/方法级，L130）、`Events/EventRule.cs:178` 等 8 个文件消费 `AttributeLists`，即原「落地时必须携带迁移规则」的要求已满足；实施 C10 时改为**核对**这些路由与 inventory 的归属标注是否一致。Caller 参数属方法/索引器形参，去糖不改变其形参归属，无冲突。热文件不重叠（各改各的 Core 目录） |
| **E08** strip-default | Caller 方法当前被 E08 整体豁免默认值剥离；C10 移除 Caller attribute 后这些方法在 C10 输出树中「看起来可剥」但 E08 不会重跑 | v1 接受默认值留在树中（§2），下游行为不劣于现状；Q1（C10 内复用 E08 Core 补综合）在 M2+ 评估，需先确认转换器对「C10 后残留默认值」的消费路径，不阻塞 M1 |
| **C09** thread-static | 同属 Declarative 阶段 attribute 驱动 pass；ThreadStatic 归 GovernedElsewhere，C10 inventory 只登记不改 | 无实现冲突；两 pass 对同一字段不同 attribute 各管各；若同时启用，链序由各自 RunAfter 决定（C09 锚 generic，C10 锚 generic-deepen+make-readonly） |
| **C08/E17** JavaKeywords | 链尾兜底；C10 不改名，插入的字面量与 `__cs2j` 临时量（本 pass 基本不生成临时量）不引入保留字 | 无冲突；原成员名是 Java 保留字时由 C08 统一逃逸，CallerMemberName 冻结值取**C# 原名**（字符串内容不逃逸，与 nameof 字面量化同原则） |
| 公共热文件 | [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)、[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)、[PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)、PassChainConfiguration/CliPreprocessChain、[PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs)/PassChainOrderTests | 各 worktree 仅追加各自 id 的行/各自的 factory 注入点；stub 计数测试按合入顺序改数字（**核订更正**：现实际 21 条而断言仍写 22——先修正既有偏差，本 pass 翻转后为 20）；合并顺序任意，rebase 解冲突；禁止顺手重排既有 pass 顺序 |

## 12. Worktree 分配

- 分支：`feature/preproc-c10-attribute-normalize`
- 目录：`.worktrees/preproc-c10-attribute-normalize`
- base：`ba7a0897`（`feat(c49): default-on extension-block materialization`，分支 `dev-3.x-26100507` 最新提交，2026-10-05 更新；完整哈希：`ba7a0897069331fe3466c452ba3359283beea2cb`）
- 创建命令：

```powershell
git worktree add .worktrees/preproc-c10-attribute-normalize -b feature/preproc-c10-attribute-normalize ba7a0897069331fe3466c452ba3359283beea2cb
```

实施约束（交实施 worktree，不在本文档执行）：仅在上述 worktree 内改码；不创建额外 worktree；按 §9 里程碑逐提交；默认关先行，过 §8 门槛才翻默认开；转换器/compat/E08 代码改动（§5.4）留后续 PR。
