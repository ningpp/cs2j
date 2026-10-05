# C25 dynamic 去糖设计（长期观察：可证明子集显式化 + 其余显式报错）

> **校订（2026-09-29，证据核对修订，仅修本文档）**：
> 1. 行号重锚：stub 实际在 [PassStubCatalog.cs#L259-L268](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L259-L268)（原误引 L281-290）；java-keywords 描述符在 [PassCatalog.cs#L243-L258](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L243-L258)（RunAfter 数组 L251-257，原误引 L225-232）；catalog C25 在 [L611-L621](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L611-L621)（原误引 L555-564）、风险 8 在 [L1106](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1106)（原 L927）、风险 12 在 [L1110](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110)（原 L931）；PassChainFactory/PassChainConfiguration/JavaKeywordOptionsFactory/BuildAllDisabledToggles/CliPreprocessChain/UnsupportedDomainPasses 等引用按当前行号修正。
> 2. catalog 不存在「§3.3 平台/动态边界族 C42+C25」：§3.3 平台边界族（[L1090](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1090)）成员是 C42+C06+C10，全文 C25 仅出现于 L62/L611-621/L1034/L1069/L1106；链尾三 pass 相邻的真实依据是 foundation plan stub 表 [L101-L103](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L101-L103) 与 PassCatalog 三尾锚 RunAfter。
> 3. 同日复核 grep：`src/CSharpToJava.Core` 大小写不敏感 `dynamic` 现为 **29 文件 87 处**（原盘点 17 文件 37 处已过时）：MemberDesugar（8 文件）已落地属性/索引器/事件/运算符四类 dynamic 接收者闸门（CS2J5118/5136/5154/5174）与共享谓词 `MemberDesugarSyntaxFacts.IsDynamic`，PatternDesugar（2 文件）对 dynamic governing 走类型测试形态；`Transformers/` 目录复核仍仅 1 行命中（ElementAccess 注释 L275），catalog「Transformers 内几乎无处理」论断 2026-09-29 仍成立。
> 4. ExpressionTreeFacts 真实 API 仅 `IsExpressionTreeLambda(model, lambda)` 一个方法；「节点是否在表达式树内」由调用方自走祖先（JK/GD 先例），原文「直接复用」表述已修正。
> 5. 翻转清单按 IteratorLowering 真实先例改正：id 常量**保留**在 `PassStubCatalog.Ids`（不迁 PassIds）；stub 现 21 个，翻转后 20。
> 6. JK-8 协调补实：dynamic 分支行号修正，补 `KeywordRenamingRewriter` L439 动态绑定不改名分支与 JK020 发射点；tests 内 dynamic 代码站点实为 3 处（首盘漏记 MemberDesugarEventUnitTests L666 的 EventDynamicSite 用例），另有 3 处注释。
> 7. overrides 查找约定按 [Program.cs#L3188-L3201](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L3188-L3201) 实况修正（CWD/config/ 优先，再自 AppContext.BaseDirectory 向上）。

**日期**: 2026-09-29
**范围**: 新增注册表 pass `dynamic-desugar`（Core `DynamicDesugar/` + CLI `DynamicDesugarPassAdapter`），只做 C#→C# 层处理：M1 全树 dynamic 站点具名审计（不重写），M2 仅对编译期流推断可证明具体类型的子集做具体化改写，其余站点显式 CS2J 诊断；本特性不做 DLR 仿真、不生成反射分派、不新增 Java compat 代码
**状态**: 设计稿，待评审
**注册表锚点**: stub `dynamic-desugar` 已登记于 [PassStubCatalog.cs#L259-L268](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L259-L268)（catalogItems `C25`，Stage=`Abi`，RunAfter=`eliminate-goto`，默认关，目录 `.cs2j-dynamic-desugar-src`，needsSemanticModel 取记录默认值 true，见 [PassDescriptor.cs#L76](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassDescriptor.cs#L76)）；链尾 `java-keywords`（已 Implemented、默认关、FinalNaming）的 RunAfter 三尾锚（platform-isolation/concurrency-normalize/dynamic-desugar）已含本 pass id（[PassCatalog.cs#L251-L257](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L251-L257)）
**catalog 对应**: `prompts/preprocessor-catalog.md` C25（[L611-L621](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L611-L621)，P3「可证明子集 + 其余报错」在 [L619](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L619)）；链尾相邻依据为 foundation plan stub 表 platform→concurrency→dynamic 三行（[L101-L103](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L101-L103)；catalog §3.3 [L1090](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1090) 的平台边界族是 C42+C06+C10，**不含 C25**）；§4 风险 8 表达式树/dynamic 共同禁区（[L1106](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1106)）与风险 12 静默错误输出（[L1110](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1110)）；foundation plan 并行交付包模板（[L108-L117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L117)）
**skip/报错纪律范本**: [2026-09-28-java-keywords-preprocessor-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-java-keywords-preprocessor-design.md)（JK-8 不可行站点显式跳过、禁止静默透传）
**结构范本**: [2026-09-29-c21-anonymous-type-lift-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c21-anonymous-type-lift-design.md)（同为注册表时代的 stub→Implemented 整树 pass）

---

## 1. 背景与目标

### 1.1 全仓 dynamic 盘点（2026-09-29 首盘，同日复核更新）

对 `src/CSharpToJava.Core` 执行 `dynamic`（大小写不敏感）全量 grep：首盘为 **17 个文件、37 处命中**；**2026-09-29 复核为 29 个文件、87 处命中**（同日外部落地的 member-desugar/pattern-desugar 引入新增动态闸门，见行 10/11），两次复核均无一是 DLR 降级实现。按性质分类：

| # | 位置 | 命中性质 | 与本 pass 的关系 |
|---|---|---|---|
| 1 | [TypeMappingService.cs#L679-L684](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/TypeMappingService.cs#L679-L684) | **转换器侧唯一显式 dynamic→Java 落点**：`IDynamicTypeSymbol` 直接映射为 `"Object"`，仅发一条 Warning `"Dynamic type converted to Object"`；不追踪调用点、参数、运算的动态绑定 | 核心要消灭的**静默擦除面**：类型被擦成 Object 后，后续成员访问/调用全部失去静态类型，产出具名方法缺失或运行时错误的 Java |
| 2 | [ElementAccessTransformer.cs#L275-L279](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs#L275-L279) | 注释明示 `Fallback for unknown/dynamic/unresolved types`：类型不可判时统一发 `expr.get(idx)`（string 仅按显示名兜底 `charAt`）。**2026-09-29 复核：整个 `Transformers/` 目录 grep `dynamic` 仍仅此 1 行**，catalog C25「Transformers 内几乎无处理」论断成立 | catalog 风险 12 同族的**静默错误输出**：dynamic 索引落到 `.get`，无诊断、无占位说明 |
| 3 | [RecordReadOnlyAnalyzer.cs#L119-L120](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyAnalyzer.cs#L119-L120)、[#L526-L537](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyAnalyzer.cs#L526-L537)、[RecordConversionPlan.cs#L548-L550](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs#L548-L550)、[RecordReadOnlyModels.cs#L42](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/RecordReadOnlyModels.cs#L42) | 既有**保守闸门**：全树发现 dynamic 接收者或反射 `SetValue` 即 `HasDynamicOrReflectionMutation`，RR057/MS-14 原子阻断全部 record plan（`dynamic receiver or reflection SetValue usage cannot be rewritten statically`） | 判定经验可复用（`TypeKind.Dynamic` 全树扫描）；行为互不冲突——它阻断自身改写，本 pass 负责把站点具名化 |
| 4 | [JavaKeywordAnalyzer.cs#L310-L315](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/JavaKeywordAnalyzer.cs#L310-L315)、[#L470-L512](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/JavaKeywordAnalyzer.cs#L470-L512)（dynamic 判定 L492-L512）、[JavaKeywordPlan.cs#L64-L65](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/JavaKeywordPlan.cs#L64-L65)、[KeywordRenamingRewriter.cs#L439-L441](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/KeywordRenamingRewriter.cs#L439-L441)、[JavaReservedWordPreprocessor.cs#L45-L69](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/JavaReservedWordPreprocessor.cs#L45-L69) | JK-8 闸门：布尔站点条件（if/while/do/for/`?:`/when/二元）`TypeInfo.Type.TypeKind == Dynamic` 即标 `IsDynamic`，**运算符 plan** 判 Infeasible 并发 JK020 原子跳过（改名 plan 永不 Infeasible）；标识符改写器对动态绑定站点原样返回不改名（注释 JK-4c/JK-8） | 链尾协同面：C25 严格重写后 dynamic 站点消失，JK-8 不再触发；审计模式下两层诊断并存（§11）；注意 dynamic 接收者上的 Java 关键字标识符今天也不改名，具体化后该面才闭合 |
| 5 | [BoxingReachabilityAnalyzer.cs#L378-L392](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/BoxingReachabilityAnalyzer.cs#L378-L392) | `IDynamicTypeSymbol` 计入 object-like 装箱面，struct 迁移据此判不可迁移 | 保守分析输入，无需改动 |
| 6 | [TupleShapeRegistry.cs#L291](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleShapeRegistry.cs#L291)（另 L348/L391）、[TupleToStructPreprocessor.cs#L1737](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleToStructPreprocessor.cs#L1737) | E06 生成 struct 时 `IDynamicTypeSymbol → global::System.Object` 形状模板，**生成代码内静默擦除** | 本 pass 在链尾审计最终树时会把这些已物化的 object 成员按静态 object 处理（dynamic 已不存在，不可追溯）——C25 锚晚的代价之一（§3.3、R4） |
| 7 | [GenericDeepenSyntaxFacts.cs#L66](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericDeepenSyntaxFacts.cs#L66) | 简单类型名识别表含 `"dynamic"`（词法名单，无任何动态语义处理） | 无关行为，仅证据完整性 |
| 8 | `ProjectConversionPipeline.cs` / `ConversionPipeline.cs` / `NewConversionPipeline.cs` / `ProjectCompilationBuilder.cs` / `LocalFunctionLiftPreprocessor.cs`（共 5 处） | 全部为 `OutputKind.DynamicallyLinkedLibrary` 或注释中的 dynamic assembly emission | 无关命中 |
| 9 | [StructAnalyzer.cs#L953](file:///d:/code/cs2j/src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs#L953)、L999 | 英文注释 `dynamic dispatch`（描述虚分派语义） | 无关命中 |
| 10 | **MemberDesugar（8 文件，首盘后新落地，2026-09-29 复核补）**：[MemberDesugarSyntaxFacts.cs#L26-L34](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarSyntaxFacts.cs#L26-L34)（dynamic 接收者谓词）、[PropertyRule.cs#L128-L136](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs#L128-L136)（另 L363-365，CS2J5118）、[IndexerRule.cs#L71-L81](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerRule.cs#L71-L81)（CS2J5136）、[EventRule.cs#L211-L222](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Events/EventRule.cs#L211-L222)（CS2J5154）、[OperatorRule.cs#L252-L259](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Operators/OperatorRule.cs#L252-L259)（另 L376-381，CS2J5174，strict Error 且 force 不解除）；码位枚举见 [MemberDesugarModels.cs#L30-L65](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarModels.cs#L30-L65) | **已实现（默认关）的同族保守闸门**：属性/索引器/事件/运算符四类规则对 dynamic 接收者/运算元原子跳过各自 plan，性质与 RR057/JK-8 相同——「本 pass 因此跳过」，不具名盘点 dynamic 站点本身 | 协调面：诊断并存不互相去重（§6.3、R9）；其 `IsDynamic` 谓词只看 TypeKind（接收者/节点），无 CandidateReason/`[Dynamic]` 面，M1 的 `DynamicSiteFacts` 应成为其超集并推动收敛（§4.1、§11 开放问题） |
| 11 | **PatternDesugar（2 文件，首盘后新落地，2026-09-29 复核补）**：[PatternReducer.cs#L203-L205](file:///d:/code/cs2j/src/CSharpToJava.Core/PatternDesugar/PatternReducer.cs#L203-L205)、[#L356-L359](file:///d:/code/cs2j/src/CSharpToJava.Core/PatternDesugar/PatternReducer.cs#L356-L359)（PD010）、[PatternDesugarComponents.cs#L155-L168](file:///d:/code/cs2j/src/CSharpToJava.Core/PatternDesugar/PatternDesugarComponents.cs#L155-L168) | **非闸门、是主动处理**：object/dynamic governing 的常量/关系模式改走类型测试 + 合成绑定形态（避免裸 `==` 装箱引用比较的静默错值）；无语义模型时 PD010 Warning 不报动态站点本身 | 与本 pass 正交：pattern-desugar 在链中段先跑，处理后的树 dynamic 站点仍由 C25 在链尾盘点；M2 对合成绑定标识符的来源识别列入观察 |

**边界面盘点（grep `Microsoft.CSharp|RuntimeBinder|ExpandoObject|DynamicObject|DynamicAttribute|DynamicDispatch|System.Dynamic`）**：

| 面 | 结果 |
|---|---|
| `src/` 全量 | **零命中**——无 Microsoft.CSharp 绑定器引用、无 RuntimeBinderException 处理、无 ExpandoObject/DynamicObject 识别、无 `[Dynamic]` 元数据特判、无动态分派辅助 |
| [config/TypeMappings.json](file:///d:/code/cs2j/config/TypeMappings.json) | **零映射**：无 Microsoft.CSharp / ExpandoObject / DynamicAttribute 条目（`config/java/**/Dynamic*.json` 是 JDK 自身 API 描述符如 `java.lang.constant.DynamicConstantDesc`、`javax.management.DynamicMBean`，与 C# dynamic 无关） |
| java compat（[java/csharptojava-compat](file:///d:/code/cs2j/java/csharptojava-compat/pom.xml)） | 无 C# dynamic 对应物：仅有 [DynamicMethod.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DynamicMethod.java)（`System.Reflection.Emit.DynamicMethod` 反射发存根）与 [RuntimeFeature.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/RuntimeFeature.java)（`IsDynamicCodeCompiled/IsDynamicCodeSupported` 恒 false 的 AOT 语义镜像），均属反射发出面，非 DLR |
| 测试（2026-09-29 复核：全 `tests/**/*.cs` 有 **3 处** `dynamic` 关键字代码站点，另有 3 处注释提及——首盘只记录了其中 2 个代码站点与 1 个注释） | ①[RecordReadOnlyRewriterUnitTests.cs#L1185](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs#L1185) `public void M(dynamic d) { d.X = 1; }`——RR057 闸门用例；②[OperatorSamples.cs#L7](file:///d:/code/cs2j/tests/SampleJavaKeywordSolution/SampleJavaKeywordLib/OperatorSamples.cs#L7)——**注释**中的 JK-8 说明（非代码站点）；③[OverrideRuntimeClassParamTests.cs#L21-L36](file:///d:/code/cs2j/tests/CSharpToJava.Tests/OverrideRuntimeClassParamTests.cs#L21-L36)——**钉死当前静默擦除成功路径**：`private static dynamic CreateFieldSetter(...)` 被转成 `Object` 返回且断言 `result.Success`。本 pass 默认关期间该测试零影响；开启严格模式后该输入将具名报错（§7.5、R2）；④[MemberDesugarEventUnitTests.cs#L660-L675](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/MemberDesugarEventUnitTests.cs#L660-L675)（关键字在 L666 `dynamic d = c;`；同文件 L12/L21 为注释）——**首盘漏记**，member-desugar 事件规则对 dynamic 接收者发 EventDynamicSite（CS2J5154）并原子跳过的钉死用例 |
| `tools/`、DualTargetCorpus、SampleProject | 零命中 |

### 1.2 Java 无 dynamic 的事实

C# `dynamic` 不是运行时类型，而是编译期标记：编译器把动态操作编译为经 `System.Runtime.CompilerServices.CallSite<T>` 缓存、由 `Microsoft.CSharp.RuntimeBinder` 在首次执行时按 **C# 规则**做重载解析的调用（隐式/显式转换、可空提升、params、泛型元数、ref/out、用户自定义转换、COM `IDispatch`、`DynamicObject`/`ExpandoObject` 兜底），失败抛 `RuntimeBinderException`。元数据中它表现为 `System.Object` + `[System.Runtime.CompilerServices.Dynamic]`（`DynamicAttribute`）。

JVM 侧虽有 `invokedynamic` 与 `java.lang.invoke`（`MethodHandle`/`MutableCallSite`/`SwitchPoint`/`LambdaMetafactory`），但：①不存在与 C# 绑定器兼容的重载解析/转换规则实现；②Java 引用类型无 C# 式隐式数值/可空转换链，反射调用不做参数强转、可访问性与异常形态不同；③没有 `RuntimeBinderException`/`ExpandoObject`/`DynamicObject` 对应契约。结论：**Java 语言与现有 compat 层不存在动态子类型**，这与 E17 设计把 dynamic 站点列为不可行（JK-8）的判定一致。

### 1.3 目标：审计显式化，而非全量降级

catalog 定调（[L619](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L619)）：静态不可证场景多，P3 长期只做「可证明子集 + 其余报错」。本 pass 的目标按两个里程碑递进：

1. **M1 审计显式化（核心交付）**：对最终中间树做整树语义扫描，把每个 dynamic 站点（声明/调用/运算/成员访问/索引/参数/边界 API）以**具名诊断 + 机器可读报告**列出，分级 Error/Warning/Info，消灭「dynamic 悄悄变 Object/.get」这一无人知情的状态。审计模式不改变任何源码。
2. **M2 可证明子集重写**：仅对编译期流推断能确定具体类型的 dynamic 声明/站点做 C#→C# 具体化（`dynamic d = new Foo();` → `Foo d;`），由重编译自动重新绑定全部使用点，并以中间树 `dotnet build/test -c Release` 作为 .NET 侧等价性预言机（foundation plan §5.1 的两半区原则）；不可证站点**显式 CS2J 报错**，唯一豁免通道是源码受控的审计登记 manifest。

**非目标**：

- 不做 DLR/CallSite/绑定器仿真，不生成 `java.lang.invoke` 或反射形式的自动降级（D9 论证）；
- 不改 C#→Java 转换代码（[TypeMappingService.cs#L679-L684](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/TypeMappingService.cs#L679-L684) 与 ElementAccess 兜底在本特性中保留为直连路径安全网，退役是默认开门之后的独立 PR，§11）；
- 不处理表达式树内 dynamic——C# 编译器本身禁止（CS1963，属基线编译错误）；共享判定 [ExpressionTreeFacts.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs) 的真实方法面仅 `IsExpressionTreeLambda(SemanticModel, LambdaExpressionSyntax)`（[L12-L19](file:///d:/code/cs2j/src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs#L12-L19)），判定「某 lambda 是否转为 `Expression<TDelegate>`」；「任意节点是否位于表达式树内」需调用方自走祖先找最近 lambda 再调它（先例：[JavaKeywordAnalyzer.cs#L479-L490](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/JavaKeywordAnalyzer.cs#L479-L490)、[GenericDeepenAnalyzer.cs#L456](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericDeepenAnalyzer.cs#L456)）。C25 不直接改写表达式树，CS1963 站点走基线编译错误路径（§4.3），仅在需要时沿用同一「祖先 lambda + 谓词」模式；
- 不追求跨过程/跨程序集的完备流分析（M2 只做保守可证子集，宁假阳，D11）。

---

## 2. 关键设计决策表

> 标注「**M3 观察项**」的条目默认不实现，须由真实语料（csharp-sdk / EF Core 补扫）数据支撑后排期。

| # | 决策点 | 结论 | 依据与备注 |
|---|---|---|---|
| D1 | 总体策略 | **审计先行、重写长期 opt-in**：M1 只盘点不改写；M2 只具体化可证明子集；不可证一律具名诊断，绝不让 dynamic 站点无诊断地流入 Java | catalog P3 定调；风险 12 第一原则「C# 侧无法等价表达时显式报错，禁止注释/占位/原样透传」 |
| D2 | 可证明子集定义 | **P1（M2 实现）**：方法内 `dynamic` 局部（含 `var` 推断为 dynamic 的隐式局部），其全部可达赋值点的编译期静态类型为同一具体类型 T，且变量未以 ref/out/in 逃逸、未被 lambda/局部函数以需保守处理的方式捕获（M2 统一按捕获即不可证处理）、T 为 sealed（含值类型/sealed BCL 类型）。**P2（仅审计标记，不重写）**：用户已写显式转换的站点 `((T)d).M()`——该成员访问本就是静态绑定，审计标 Info「已显式」。**P3（M3 观察项）**：private 非虚方法的 dynamic 参数/返回，且全编译调用图单态闭合 | P1 限定 sealed 是为了消去「隐藏成员（`new`）在动态分派与静态分派下解析不同」的唯一系统性语义差；虚成员在 sealed 前提下运行时类型恒为 T，静态/动态分派等价。.NET 侧 build/test 是等价性闸门而非论证替代品 |
| D3 | 不可证站点策略 | rewrite 模式默认 **Error**（含文件:行列、kind、绑定符号、不可证原因、手工改写建议），站点原样保留且本次运行失败；audit 模式默认 **Warning**，链继续；`--strict` 把 Warning 与 manifest 豁免一并升 Error | 分级与平台边界探测在测试工程降级 Warning 的既有做法同型（[UnsupportedDomainPasses.cs#L169-L172](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L169-L172)；第二处同款三元在 L190-192） |
| D4 | dynamic 方法参数/返回 | M1 全部具名盘点（DS011）。M2 **默认不改签名**：参数/返回是 ABI 面（虚方法、接口、外部程序集、序列化、COM 均可能依赖），即使编译单元内可见全部调用点也不改；P3 私方法单态闭合作为 M3 选项，默认关 | 与 foundation plan 横切约束一致：改签名通道必须基于全编译符号表；先做收益最小风险最大的面不符合 P3 定位 |
| D5 | dynamic 字段/属性/事件/索引器 | **永不自动重写**，DS012：字段可被反射/序列化/外部程序集/跨程序集写法触达，过程内流分析不可证；record/匿名类型上的 dynamic 属性由 C21 保真（其 AL030 与本码同族） | C21 D9 已约定 `new { X = (dynamic)1 }` 保留 dynamic 属性；C25 在链尾对生成类同样按 DS012 盘点 |
| D6 | dynamic 与 var 的区别 | `var` 是编译期推断的静态类型；`var x = 返回 dynamic 的方法()` 的推断结果本身是 dynamic（`TypeInfo.Type.TypeKind == Dynamic`）。盘点与证明器对显式 `dynamic` 局部和隐式 dynamic 局部一视同仁；`object` 局部**不**计入（object 是真实静态类型，成员访问本就静态绑定，只是无可用成员） | 防止把 `dynamic x = ...` 与 `object x = ...` 混报；二者元数据同为 object，但源内语义模型可区分（TypeKind.Dynamic vs SpecialType.System_Object） |
| D7 | ExpandoObject / DynamicObject / 反射 / JSON | 不做自动改写，逐站点具名诊断并给手工配方：`ExpandoObject`/`System.Dynamic.DynamicObject`（DS017，建议手工改为具名类型或 `Map<String,Object>`，注意 Expando 默认绑定器对成员名有大小写回退且实现 `INotifyPropertyChanged`，直接换 Map 不行为等价，须人工确认）；对 dynamic 接收者的反射调用（DS013 备注）；Newtonsoft `JObject/JToken/DeserializeObject<dynamic>`、System.Text.Json dynamic 反序列化（DS017 子因，建议改 DTO/`JsonNode`） | 这些场景的真实类型只存在于运行时数据，任何静态具体化都是猜测；先显式盘点，配方写进诊断消息与报告 JSON |
| D8 | RuntimeBinderException 语义 | 不模拟、不包装：动态成员缺失/转换失败在 C# 抛 `Microsoft.CSharp.RuntimeBinder.RuntimeBinderException`（按站点缓存、首调触发）；任何反射/MethodHandle 改写都会改变异常类型、触发时机与缓存语义。诊断消息中显式提示「改写后异常形态变化由调用方负责」 | 杜绝「生成一个看起来能跑的反射版本」的诱惑 |
| D9 | 不做 DLR 仿真的论证 | 三条候选路线全部否决：①运行时支撑库仿真 CallSite+binder = 用 Java 重写 C# 重载解析（隐式转换/可空提升/params/泛型/ref/COM/DynamicObject 兜底），规模不可控且与 .NET 行为漂移无法用单测穷尽；②`java.lang.invoke`（invokedynamic/MutableCallSite）只有缓存机制没有 C# 绑定规则，仍需自写 binder 且 java 侧无对应类型系统；③反射自动降级不做参数强转与可访问性仿真，静默语义漂移最严重，违背 D1。结论：**不注入任何 dynamic 支撑库**（descriptor `SupportLibrary = None`） | catalog C25 目标措辞为「显式反射/对象分派（仅在符号可静态推断处）」；本设计将其收窄为「仅具体化可证站点 + 对其余报错」，与 P3 备注「可证明子集 + 其余报错」一致 |
| D10 | M2 改写动作集 | **A1（默认动作）**：把 P1 局部的声明类型语法（`dynamic` 或隐式 dynamic 的 `var`）替换为 T 的最小可显示类型语法；声明点一改，同一编译内成员访问/调用/运算/参数传递经重编译自动静态重绑，无需逐站点改文本。**A2（M3 观察项，默认关）**：对声明无法改（如 dynamic 参数）但用户在 manifest 中逐站点授权的场景插入显式强转 `((T)d).M()`——会改变动态绑定与异常时机，必须逐站点头条登记，不做批量动作 | A1 与 C21「创建点一改、重编译重绑」同构，避免半改写不可编译窗口；A2 不进 M2 |
| D11 | 证明器近似口径 | FlowTypeProver 做**保守的语法+语义混合分析**，只用公共 Roslyn API（`SemanticModel.GetTypeInfo/GetSymbolInfo/GetDeclaredSymbol/GetConversion`，不依赖内部 `Conversions`）：文本收集方法内全部赋值点做并集判定（任一点为 dynamic/异类型/不可解析即不可证，分支与回边不需 CFG 即可保持健全），逃逸分析（ref/out/in 实参、M2 中捕获即拒）、sealed 校验。**宁假阳**：拿不准一律报不可证 | 仓内既有 pass（RR/JK/Boxing）全部基于语法树遍历 + 语义模型查询，不引 `Microsoft.CodeAnalysis.FlowAnalysis` 私有 API；假阳成本是 Warning/manual，假阴成本是错误 Java |
| D12 | 元数据 dynamic 识别 | 除源内 `IDynamicTypeSymbol`/`TypeKind.Dynamic` 外，还须识别外部程序集经 `object` + `[Dynamic]` 流入的站点：成员访问/调用的 `SymbolInfo.CandidateReason == CandidateReason.Dynamic`（动态绑定节点 Symbol 为 null 的标准信号），以及 object 类型符号上带 `System.Runtime.CompilerServices.DynamicAttribute` 的形参/字段/返回（DS019）。源内手写 `[Dynamic] object x` 同样会被编译器按 dynamic 处理，单测据此构造 | 全仓 grep 证实当前零 DynamicAttribute 特判；仅看 `dynamic` 关键字 token 的 residual 闸门会漏掉元数据入口，闸门必须语义+语法双扫（§6） |
| D13 | 唯一豁免通道：审计登记 manifest | 不提供 skip 列表式静默开关。rewrite 模式下允许一个源码受控的 `Cs2jDynamicAudit.json`（位于源根、随仓库提交），逐条登记人工审计过的残余站点（文件+行+span+kind+符号+原因+审计人/日期）；匹配站点由 Error 降为 DS060 Warning（显式知情残留），不匹配任何条目的残余仍为 Error；manifest 中找不到站点的陈旧条目 → DS061 Warning（防腐烂）。`--strict` 下 DS060 仍为 Error，即「dynamic = 0 或全部已审计登记」两态合法 | 学 JK overrides 的文件化思路但语义相反：skip 是「别告诉我」，manifest 是「我已知情并留痕」；residual 闸门据此判定 |
| D14 | 产物可编译/幂等 | 每次改写后单文件 `FormatAndValidate`（改动区间格式化→整文件格式化→原文三候选，复刻 E06/JK）+ adapter 默认整树 verify build/tests；输出再跑一遍本 pass：P1 声明已不存在 dynamic，clean 文件字节复制，报告零改写 | 与所有已落地语义 pass 同一硬不变式：中间树永远可编译；多轮跑指纹稳定（catalog 风险 11） |
| D15 | 单文件模式 | **不支持单文件模式**：证明与盘点以整树编译为前提（赋值点/调用图/元数据引用），只提供项目树模式（convert-project 链与独立 `preprocess --pass dynamic-desugar`） | 与 C21 同口径；JK 的 JK090 单文件跨文件豁免机制不复制 |

---

## 3. 注册表与链位契约

### 3.1 stub 现状

[PassStubCatalog.cs#L259-L268](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L259-L268)：id 常量在 [Ids.DynamicDesugar L43](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L43)，显示名 "Dynamic desugaring"，目录 `.cs2j-dynamic-desugar-src`，`PassStage.Abi`，`PassStatus.Stub`，`defaultEnabled: false`，`runAfterIds: { PassIds.EliminateGoto }`，`needsSemanticModel` 未显式传入、取记录默认 true（[PassDescriptor.cs#L76](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PassDescriptor.cs#L76)），catalogItems `C25`。Stubs 表现共 **21 个** stub（2026-09-29 点算），本 pass 是表中最后一条；禁用时零成本（无目录、不进执行计划）；显式启用即 [CS2J9001](file:///d:/code/cs2j/src/CSharpToJava.Core/Preprocessing/PreprocessDiagnosticCodes.cs#L12) 失败。

### 3.2 翻转后目标描述符

| 字段 | 值 | 理由 |
|---|---|---|
| Id | `dynamic-desugar` | 不变 |
| Stage | `Abi` | 维持：dynamic 是类型系统/ABI 边界（object+DynamicAttribute），与 generic/ref-out 同族；Stage 仅信息性 |
| Status | `Implemented` | M1 审计模式落地即翻转（审计本身是完整可用实现）；M2 以同一 id 的模式选项演进而非二次翻转 |
| DefaultEnabled | **false 长期保持**；§8 门槛满足后可评估以 **Mode=Audit** 默认开（Warning 不阻断）；**rewrite 模式长期 opt-in** | catalog P3「长期观察」；风险 10 反双轨；rewrite 默认开会把现有「dynamic→Object 成功」的工程（如 [OverrideRuntimeClassParamTests](file:///d:/code/cs2j/tests/CSharpToJava.Tests/OverrideRuntimeClassParamTests.cs#L21-L36) 形态）直接变红，必须先有数据 |
| RunAfterIds | `{ "eliminate-goto" }`（维持） | 见 §3.3 |
| IntermediateDirectoryName | `.cs2j-dynamic-desugar-src`（维持） | |
| NeedsSemanticModel | true | 全部判定依赖语义模型 |
| SupportLibrary | `None` | D9，不注入支撑库 |
| VerifyBuildByDefault | **true**（rewrite 模式生效；audit 模式 adapter 内跳过 verify，见 §5） | 对齐 C21 新通道标准 |
| VerifyTestsByDefault | **true**（同上） | A1 改写的等价性由 .NET 侧测试预言 |

### 3.3 为何放在这么晚（eliminate-goto 之后）

1. **审计对象是最终中间树**：本 pass 是链尾 ABI 边界族（与 `platform-isolation`、`concurrency-normalize` 同锚 eliminate-goto，stub 表相邻）。前置 pass 完成全部结构去糖后，盘点结果才是「真正会流入 Java 转换器的 dynamic 面」，不被糖形式重复计数，也不会对即将被消除的节点（goto/模式/迭代器糖）误报。
2. **证明器需要最终控制流形态**：P1 的赋值点并集与逃逸分析在 goto 消除、局部函数提升等结构重塑之后做，输入最简单确定。
3. **不阻碍前序表达式族**：dynamic 站点对前序 pass 的影响由共享判定 `DynamicSiteFacts`（§4.3）在各 pass 内部处理（在 dynamic 操作子树内不重写），不依赖本 pass 先跑。
4. **晚于所有支撑库注入 pass**：本 pass 不注入库、不改名，与 unsigned/array/ref-out 产物无冲突。
5. **必须早于 java-keywords**：[PassCatalog.cs#L251-L257](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L251-L257) 已用 RunAfter 边保证 C08 在本 pass 之后（FinalNaming 恒为最后一环，描述符 [L243-L258](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L243-L258)）；rewrite 严格模式后 JK-8 的 dynamic 分支在链上自然不可达。

**已知代价（R4）**：未来 C32/C45 闭包提升若把捕获的 dynamic 局部物化为 holder 的 object/dynamic 字段，晚链位会丢失「它曾是可证局部」的出处。缓解：①独立 `preprocess --pass dynamic-desugar` 可在**原始源码**上单跑审计（独立 verb 只挑选中 pass，见 §5.2），供人工提前盘点；②M2 证明器对 `[CompilerGenerated]` holder 字段做来源识别列为 M3 候选项；③在 C32/C45 设计评审中以本文件为协调依据。

拓扑并列确定性：本 pass 与 `platform-isolation`、`concurrency-normalize` 均仅锚 eliminate-goto，三者相对顺序由注册表插入顺序确定（现状 platform → concurrency → dynamic），翻转时须在注册表顺序断言中锁定该相对位置。

### 3.4 Stub→Implemented 翻转清单

1. [PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)：仅删除 Stubs 表条目（[L259-L268](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L259-L268)）；**`Ids.DynamicDesugar` 常量（[L43](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L43)）保留不迁**——这是 IteratorLowering 翻转留下的真实先例：其常量仍在 `PassStubCatalog.Ids`（L32），Implemented 描述符（PassCatalog L210）、`CreateAdapter` switch（PassChainFactory L227）与 java-keywords RunAfter（L256）一律以 `PassStubCatalog.Ids.*` 引用。翻转后 Stubs 表 21 → 20 条。
2. [PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)：java-keywords RunAfter（[L251-L257](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L251-L257)）中的 `PassStubCatalog.Ids.DynamicDesugar` 引用**保持不变**；`Implemented` 表按 §3.2 增加描述符，id 同样写 `PassStubCatalog.Ids.DynamicDesugar`（仿 [IteratorLowering 描述符 L210-L219](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L210-L219)：`runAfterIds: new[] { PassIds.EliminateGoto }`，verify 双开，defaultEnabled=false，SupportLibrary 取默认 None），插入位置在 eliminate-goto（L221-229）之后、member-desugar/java-keywords 之前，保持 platform → concurrency → dynamic 的相对次序。
3. [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)：`CreateAdapter` switch（[L98-L257](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L98-L257)，switch 体 L101-256）增加 `PassStubCatalog.Ids.DynamicDesugar => new DynamicDesugarPassAdapter(...)`（case 写法仿 IteratorLowering [L227-L234](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L227-L234)）；`PassChainConfiguration`（[L29-L53](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L29-L53)）增加 `Func<DynamicDesugarOptions>` 懒加载工厂（仿 [JavaKeywordOptionsFactory L48-L49](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L48-L49)）。无 legacy toggle（不新增 `--no-dynamic-desugar`，启停走描述符默认值 + 通用 `--enable-pass/--disable-pass`）——generic-deepen/pattern-desugar 两个默认关已实现 pass 均未在 `BuildLegacyToggles`（L64-85）登记，支持此选择；IteratorLowering 在 [L81](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L81) 硬编 false 属历史兼容例外，不照搬。
4. 新增 CLI `Adapters/DynamicDesugarPassAdapter.cs`（§5）。
5. [.gitignore](file:///d:/code/cs2j/.gitignore#L89-L101)：增加 `.cs2j-dynamic-desugar-src/`（`.cs2j-*-src/` 登记块现为 L89-L101；L102 是 `.cs2j-test-output/`）；审计 manifest 文件名 `Cs2jDynamicAudit.json` **不**忽略（随源码受控）。**开放问题**：近年翻转的 pattern-desugar/generic-deepen/iterator-lowering/member-desugar 中间目录均未登记进 .gitignore，登记惯例本身已不统一——实施时确认是补登本 pass 一条还是顺带统一（本设计不改 .gitignore 之外的既有条目）。
6. 注册表顺序断言/`--list-passes` 测试更新：Stub→Implemented（21→20）、默认值、目录、RunAfter=eliminate-goto 且先于 java-keywords；CS2J9001 对该 id 的行为消失。
7. 不新增 `Project*Preprocessor` 薄包装 facade（stub 先行的 pass 无历史 verb，对齐 C21/C05 口径）。

### 3.5 目录与产物约定

- 中间树根：`.cs2j-dynamic-desugar-src/`（descriptor 驱动，runner 自动建/清/preserve，接入现有指纹机制：pass 令牌 `pass:dynamic-desugar=on/off`）。
- 审计报告：中间树根 `dynamic-audit-report.json`（两种模式都写；audit 模式它是主交付物），结构为运行元信息 + 站点数组（文件/行列/span/kind/绑定符号显示名/provability/reason/诊断码/是否 manifest 命中），UTF-8 无 BOM，站点按文件+span 排序保证跨运行字节稳定。
- 审计登记：源根 `Cs2jDynamicAudit.json`（D13）。查找约定须注意实况：record-readonly/JavaKeywords overrides 的现行逻辑是**显式路径参数优先 → `CWD/config/<Name>.json` → 自 `AppContext.BaseDirectory` 逐级向上找 `config/<Name>.json`**（[Program.cs#L3188-L3201](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L3188-L3201)，ReadOnlyStruct 同款 L3125-3141），并非「源根 CWD 向上」。本 manifest 设计为随源码受控、落源根而非 config/，与现行约定形状不同——实施时二选一并在 `--dynamic-audit-manifest` 帮助文本写明（开放问题，见 §13）。

---

## 4. Core 组件设计

### 4.1 新文件清单（`src/CSharpToJava.Core/DynamicDesugar/`）

命名空间 `CSharpToJava.Core.DynamicDesugar`，组织对齐 `JavaKeywords/`、`RecordReadOnly/`：

```
DynamicDesugar/
  DynamicDesugarPreprocessor.cs   # 公共入口：
                                  #   DynamicDesugarPreprocessor(DynamicDesugarOptions?)
                                  #   void BindCompilation(Compilation)
                                  #   DynamicSiteInventory BuildInventory()（整树一次，缓存）
                                  #   DynamicFileResult RewriteFile(string sourceCode,
                                  #       SemanticModel, DynamicDesugarStatistics,
                                  #       List<DynamicDesugarDiagnostic>)
                                  #   DynamicAuditReport CloseReport()
                                  #   internal static FormatAndValidate(...)（三候选，复刻 E06）
  DynamicSiteInventory.cs         # DynamicSiteKind/DynamicSiteRecord/DynamicSiteKindClassifier
                                  # 整树扫描：声明点 + 动态操作点 + 边界 API；去重与归并
  FlowTypeProver.cs               # P1 局部具体化证明（保守近似，宁假阳，D11）
                                  # AssignmentFact/EscapeAnalysis/ProvenDeclaration
  DynamicSiteRewriter.cs          # A1：声明类型具体化重写（M2）；统一 RewriteAnnotation
  DynamicDesugarOptions.cs        # Mode(Enum)/Strict/AuditManifestPath/AllowA2(M3)/SealedOnly
  DynamicDesugarStatistics.cs     # 计数模型（§4.6）
  DynamicDesugarDiagnostic.cs     # DynamicDesugarSeverity/Code/Diagnostic/FileResult
  DynamicAuditManifest.cs         # manifest 加载、键匹配、陈旧条目检测；报告 JSON 模型
```

另新增**跨 pass 共享判定**（不属于本 pass 目录，落点 `src/CSharpToJava.Core/Analysis/`，与 [ExpressionTreeFacts.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs) 并列）：

```
Analysis/
  DynamicSiteFacts.cs             # IsDynamicExpression(model,node)：
                                  #   TypeInfo.Type/ConvertedType 为 Dynamic，或
                                  #   SymbolInfo.CandidateReason == Dynamic，或
                                  #   祖先为 dynamic 操作（二元/调用任一运算元动态则全动态）
                                  # IsDynamicDeclaration(model,declarator)
                                  # HasDynamicAttribute(ISymbol)：object + [Dynamic]
```

catalog 风险 8（[L1106](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1106)）要求「各通道共享表达式树节点内跳过判定」，`ExpressionTreeFacts.IsExpressionTreeLambda` 已落地（方法面仅一个 lambda 谓词，「节点在树内」由调用方自走祖先，现引用方为 JavaKeywordSyntaxFacts 转发壳 [L56-L58](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/JavaKeywordSyntaxFacts.cs#L56-L58)、JavaKeywordAnalyzer L479-490、GenericDeepenAnalyzer L456）；`DynamicSiteFacts` 是其 dynamic 对偶，供 C23/C24/C35/C36/C14 等表达式族在 dynamic 子树内跳过各自改写（在动态操作子树内改写会改变绑定器行为），M1 即交付本工具，即使本 pass 默认关，前序 pass 也可先行引用。另注意 member-desugar 已有私有谓词 `MemberDesugarSyntaxFacts.IsDynamic`（[L26-L34](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarSyntaxFacts.cs#L26-L34)，仅 TypeKind 接收者/节点两面），`DynamicSiteFacts` 的方法面应设计为其超集，收敛/替换该私有谓词列入 §13 开放问题，不在 M1 强制改既有 pass。

### 4.2 站点分类模型

`DynamicSiteKind`（诊断码与报告 JSON 共用同一分类词汇）：

| 族 | Kind | 语法/语义判定 |
|---|---|---|
| 声明 | `DynamicLocalDecl` | 局部声明显式 `dynamic`，或 `var` 声明的 `TypeInfo.Type.TypeKind==Dynamic`（含 foreach 迭代变量、catch 变量、解构变量中的 dynamic 分量） |
| 声明 | `DynamicParameter` | 方法/索引器/委托/lambda/本地函数的 dynamic 形参（DS011） |
| 声明 | `DynamicReturn` | 方法/委托/索引器 dynamic 返回类型（DS011） |
| 声明 | `DynamicFieldOrProperty` | 字段/属性/事件的 dynamic 类型（DS012） |
| 操作 | `DynamicInvocation` | `InvocationExpressionSyntax` 且 `GetSymbolInfo` 无 Symbol、`CandidateReason==Dynamic`（DS013） |
| 操作 | `DynamicMemberAccess` | 成员访问同上（含方法组绑定、事件订阅）（DS013） |
| 操作 | `DynamicElementAccess` | dynamic 索引（DS014），即 ElementAccess 静默 `.get` 兜底的对应面 |
| 操作 | `DynamicOperation` | 二元/一元/复合赋值/自增自减，表达式 `TypeInfo.Type` 为 Dynamic（任一运算元动态即整式动态）（DS015） |
| 操作 | `DynamicConversion` | 向 dynamic 的隐式转换实参/赋值右值（`GetConversion` 标示 T→dynamic 边界，记录来源类型 T 供报告与 P1 证明）（DS016）；`(T)d` 反向显式转换标 P2 Info 不报问题 |
| 边界 API | `ExpandoOrDynamicObject` | 符号标识为 `System.Dynamic.ExpandoObject` / `System.Dynamic.DynamicObject`（含 using 与构造）（DS017） |
| 边界 API | `JsonDynamic` | `Newtonsoft.Json.Linq.JObject/JToken` 动态用法、`JsonConvert.DeserializeObject<dynamic>` 等（DS017 子因，消息给 DTO/JsonNode 建议） |
| 边界 API | `RuntimeBinderApi` | `Microsoft.CSharp.RuntimeBinder.*`（RuntimeBinderException/CallSite/Binder 显式引用）（DS018） |
| 元数据 | `DynamicAttributeEntry` | 外部程序集 object+`[Dynamic]` 形参/字段/返回在源内的使用点（DS019） |

每个 `DynamicSiteRecord` 携带：`Kind`、`Location`（文件/起止 span/行列）、语法节点种类、接收者/绑定符号显示名（能取到时）、`OriginType`（dynamic 边界处的编译期来源类型）、`Provability`（`ProvenP1/P2Info/Unprovable`）、`UnprovableReason`（枚举：`SealedNotSatisfied`/`MultipleAssignmentTypes`/`DynamicAssignment`/`ByRefEscape`/`Captured`/`CrossBoundarySignature`/`FieldLikeStorage`/`MetadataOnly`/`UnresolvedSymbol`）。

**归并与去重**：一个声明点与其使用点分别登记（声明一条、操作若干），报告中按声明归组展示；同位置同 kind 经 `BoundaryDiagnosticCollector` 同款键去重（仿 [UnsupportedDomainPasses.cs#L469-L494](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs#L469-L494) 的 `code|category|path|line|col|message` 键）。

### 4.3 DynamicSiteInventory（扫描）

- 编译口径与 JK/RR 一致：`CSharpParseOptions(LanguageVersion.Latest)` + `ProjectUnsignedTypePreprocessor.GetBasicReferences()`（JK 适配器即如此取引用集，见 [JavaKeywordsPassAdapter.cs#L307-L311](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/JavaKeywordsPassAdapter.cs#L307-L311)）+ allowUnsafe DLL 编译；adapter 负责合成 ImplicitGlobalUsings（global using 工程中 dynamic 可见性相同，复刻 JK `BuildCompilation` 内的合成段，§5.1）。
- 两遍：第一遍收集声明与边界 API；第二遍对 `DescendantNodes` 中的调用/成员/索引/运算/实参节点用 `DynamicSiteFacts` 判定操作点。基线已有编译错误的树（含 CS1963 表达式树内 dynamic）只盘点不重写，且诊断不与编译器错误叠加噪声——操作点判定要求语义模型可返回 TypeInfo，error 类型节点走 `UnresolvedSymbol` 原因保守入账但不重复编译器错误。
- `DynamicSiteFacts.IsDynamicExpression` 的祖先传播规则：以最近的调用/二元/赋值边界为准——dynamic 实参使整个 invocation 动态（DynamicInvocation），但不使不含该实参的兄弟语句动态。
- `[Dynamic]` 识别只认 BCL 全名 `System.Runtime.CompilerServices.DynamicAttribute`，不认同名自定义特性。

### 4.4 FlowTypeProver（保守近似，宁假阳）

输入：inventory + 语义模型；输出：`ProvenDeclaration` 集合（声明节点 → 证明类型 T）与每个不可证声明的 `UnprovableReason`。M2 规则（P1）：

1. 候选 = 方法体内的 dynamic 局部（显式或隐式）。参数/字段/属性直接 `CrossBoundarySignature`/`FieldLikeStorage`，不进入证明。
2. **赋值并集**：文本收集该变量在声明方法内的全部赋值（声明初始化器、赋值表达式、`++/--`、foreach 迭代、解构分量）；对每个右值取 `GetTypeInfo(rhs).Type ?? ConvertedType`：
   - 任一为 Dynamic/Error/null 不可解析 → `DynamicAssignment`/`UnresolvedSymbol`；
   - 全部为同一 T（`SymbolEqualityComparer.IncludeNullability` 外的默认相等；可空差异 M2 保守按异类型处理）→ 进入下一步；
   - 出现两个及以上不同静态类型（含派生类型）→ `MultipleAssignmentTypes`（不为「共同基类」合并，宁假阳）。
3. **逃逸分析**：变量作为 ref/out/in 实参 → `ByRefEscape`；被匿名函数/局部函数捕获 → M2 一律 `Captured`（不做闭包存活分析）；在 `fixed`/`lock`/`using`/`await` 动态等待等特殊位置 → `Captured`。
4. **sealed 校验**：T 为 sealed 类、值类型、`string`、sealed BCL 类型或 sealed 泛型实例 → 通过；开放类型 → `SealedNotSatisfied`（P1 的唯一系统性语义差是成员隐藏，M2 不做逐成员虚/隐藏判定，留 M3）。
5. 通过者产 `ProvenDeclaration(SyntaxNode, ITypeSymbol T)`；失败者保留原声明并由诊断侧发 DS010（原因入消息）。

P2 标记（不重写）：`CastExpressionSyntax` 目标类型为静态 T、运算数为 dynamic → 记 Info，提示该站点已由用户显式化；`is T`/`as T` 模式变量在 M2 不参与收窄（M3 可利用分支收窄扩大 P1）。

### 4.5 DynamicSiteRewriter（M2，A1 唯一动作）

- 仅对 `ProvenDeclaration`：把声明类型语法替换为 `T.ToMinimalDisplayString(model, position)` 解析所得 `TypeSyntax`（`dynamic`/`var` token 同路径替换）；挂本文件内定义的 `RewriteAnnotation`（不落盘文本，供区间格式化与幂等识别，对齐 JK 的 `RewriteAnnotation` 用法）。
- **不逐站点改使用点**：声明一改，成员访问/调用/运算/实参在重编译时静态重绑——与 C21「创建点替换 + 重编译重绑」同型，避免声明改了用点没改的半改写窗口。
- 不生成新文件、不注入支撑库、不改任何签名。
- 每个改写文件经 `FormatAndValidate` 三候选重新 parse 取零 error 之首；全失败则原文保留 + DS040 Error。
- 幂等：改写后输出无对应 dynamic 声明，dirty 快检（文件内无 dynamic token 且无 CandidateReason.Dynamic 节点）为 clean 的文件字节复制。

### 4.6 Options / Statistics / Diagnostic 模型

- `DynamicDesugarOptions`：`DynamicMode Mode { Audit, Rewrite } = Audit`；`bool Strict = false`；`string? AuditManifestPath = null`（null 时按约定查找 `Cs2jDynamicAudit.json`，缺省=无）；`bool SealedOnly = true`（M2 恒 true，预留 M3）；`bool AllowCastInsertion = false`（A2/M3，M2 不读取其 true 路径）。
- `DynamicDesugarStatistics`（通用字段对齐各 pass：`FilesScanned/FilesTransformed/FilesSkippedClean`）：`SitesTotal`、`SitesByKind: Dictionary<DynamicSiteKind,int>`、`DeclarationsClassified`、`ProvenDeclarations`、`DeclarationsRewritten`、`UnprovableSites`、`ManifestSuppressed`、`ManifestStaleEntries`、`ResidualSites`、`ReportsWritten`。
- `DynamicDesugarDiagnostic`：`Severity(Info/Warning/Error)`、`Code(DS0xx)`、`Message`、`FilePath/Line/Column`，`ToMsBuildString()` 与 JK/RR 同格式（`file(line,col): warning DS0xx: ...`）。

---

## 5. CLI 适配器与集成

### 5.1 DynamicDesugarPassAdapter

新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/DynamicDesugarPassAdapter.cs`，继承 [ProjectPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs)，走标准 spine（不 override `ExecuteAsync`——该方法在基类 [L268](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassAdapter.cs#L268) 已封完整流程；与已实现的 IteratorLowering/Tuple/JavaKeywords 适配器同型，C21 设计稿同口径）：

- `Descriptor => PassCatalog.ImplementedById(PassStubCatalog.Ids.DynamicDesugar)`（id 常量留 PassStubCatalog，§3.4）；`LogPrefix "[dynamic-desugar]"`；
- `CreateParseOptions`：`LanguageVersion.Latest`；`CompilationName "dynamic-desugar-preprocess"`；
- `CreateAdditionalSyntaxTrees`：复刻 [JavaKeywordsPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/JavaKeywordsPassAdapter.cs#L289-L305) 的 ImplicitGlobalUsings 合成段（其 `BuildCompilation` 为 private static，不能直接调用；真正可复用调用是 `ImplicitGlobalUsings.BuildForProject`）；
- **Audit 模式**：`RewriteFileAsync` 恒返回 null（全部文件字节复制，不触碰源码）；在 `CompilationReadyAsync` 构建 inventory（每文件诊断在 RewriteFile 阶段按本文件站点产出，保证 `RelativePath` 前缀复用既有管线）；`AfterFilesAsync` 写 `dynamic-audit-report.json`；`VerifyBuild/VerifyTests` 返回 false（审计不 restore、不 build、不 test，避免无谓耗时）；`DeleteBuildOutputsAfterRestore = false`。不可证站点默认 Warning，`HasErrors` 仅在 `Strict` 下为真。
- **Rewrite 模式**：`RewriteFileAsync` 调 Core `RewriteFile`，Changed=false → null（字节复制），Changed=true → `PassFileRewrite { PreserveBom = true }`；`AfterFilesAsync` 同样写报告；`VerifyBuild/VerifyTests` 取构造入参（descriptor 默认双开，受全局 `--no-verify-*` 控制）；`DeleteBuildOutputsAfterRestore = true`（verify 型 pass，对齐 enum/C21）。
- `OnRestoreFailed` → DS050 Warning（沿用各 pass 惯例）；`OnBuildFailed/OnTestsFailed` → DS051/DS052 Error；
- `BuildChainSummaryLine`：`dynamic-desugar: N site(s) (P proven, U unprovable, S manifest-suppressed), R declaration(s) concretized, K residual; mode=Audit/Rewrite`；诊断行经 `BuildChainDiagnosticLines` 输出（Info 仅 verbose）。
- 失败语义走 runner 既有机制（[CliPreprocessChain.cs#L173-L187](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs#L173-L187)）：诊断行经 `BuildChainDiagnosticLines` 输出，`run.Success` 为假时 `Failed(...)` 中止本环与链、退出码非 0。

### 5.2 开关 / 指纹 / 独立 verb

- 翻转后默认关；`--enable-pass dynamic-desugar` 开启，**默认即审计模式（`--dynamic-report-only`）**——只盘点出报告、源码字节不变；重写模式经 `--dynamic-mode Rewrite` 显式选择（两模式也可用 `--dynamic-mode <Audit|Rewrite>` 统一指定，`--dynamic-report-only` 是 `Audit` 的等价显式开关）。convert-project 选项新增 `--dynamic-report-only`、`--dynamic-mode <Audit|Rewrite>`、`--dynamic-strict`、`--dynamic-audit-manifest <path>`，经 [PassChainConfiguration](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L29-L53) 的懒加载工厂传入（仅本 pass 启用时构造）。
- 独立入口：通用 `preprocess --pass dynamic-desugar`（all-disabled toggles + 显式 id 机制，[PassChainFactory.BuildAllDisabledToggles](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L91-L92)）携带相同三个模式选项；**独立跑可直接对原始源码审计**（§3.3 R4 缓解）。
- 指纹：注册表全集令牌自动含 `pass:dynamic-desugar=on/off`；audit 模式输出树与输入字节一致，下一环 `opts.Source` 串联与指纹回填照常规走；无支撑库过滤项（`IsExecutedSupportLibraryProject` 不改）。
- `--list-passes` 显示 Implemented/默认关/目录/RunAfter=eliminate-goto。

### 5.3 verify 与失败语义

- Audit 模式不改变可编译性（只复制+报告），不存在 verify 失败面。
- Rewrite 模式下 A1 改写导致的任何编译/测试失败在中间树当场暴露（build/test 非 0 → DS051/DS052 → 链失败），不留到 Java 产物；这是 .NET 半区等价性预言，与 foundation plan §5.1 一致。
- 不可证站点的 Error 在 rewrite 阶段即中止，不产生「部分具体化、其余透传」的交付物。

---

## 6. 诊断策略（核心交付：每个不可证站点具名报错，消灭静默 fallback）

码位 DS0xx（MSBuild 风格，只追加不改号）：

| 码 | Audit 默认 | Rewrite 默认 | 含义 |
|---|---|---|---|
| DS001 | Info | Info | P1 声明已具体化（消息含声明位置、`dynamic → T`） |
| DS002 | Info | Info | P2 站点已由用户显式转换（`(T)d`），本就静态绑定 |
| DS010 | Warning | Error | dynamic 局部/变量不可证（消息必带 `UnprovableReason`：sealed 不满足/多赋值类型/动态赋值/引用逃逸/捕获/符号不可解析） |
| DS011 | Warning | Error | dynamic 方法参数或返回类型（跨边界签名，M2 不改；M3 私方法单态候选项） |
| DS012 | Warning | Error | dynamic 字段/属性/事件/索引器存储（永不自动重写） |
| DS013 | Warning | Error | dynamic 调用/成员访问（`CandidateReason.Dynamic`），消息含接收者表达式与来源类型（可知时）；对 dynamic 接收者反射调用追加反射风险备注 |
| DS014 | Warning | Error | dynamic 元素访问（即 ElementAccess `.get` 静默兜底面对的站点） |
| DS015 | Warning | Error | dynamic 运算（二元/一元/复合赋值/自增自减/dynamic await） |
| DS016 | Warning | Error | T→dynamic 隐式边界（动态实参/赋值），消息记录来源类型 T（往往就是人工具体化的改写提示） |
| DS017 | Warning | Error | `ExpandoObject`/`DynamicObject`/JSON dynamic（JObject/JToken/DeserializeObject\<dynamic\>）；消息附手工配方与「异常/绑定语义不等价」提示 |
| DS018 | Warning | Error | `Microsoft.CSharp.RuntimeBinder.*` 显式引用（RuntimeBinderException 语义无 Java 对应，D8） |
| DS019 | Warning | Error | 元数据 `object + [Dynamic]` 入口站点（外部程序集 dynamic 成员） |
| DS030 | Info（不闸门） | Error（strict 下 Error，普通 rewrite 模式：无 manifest 匹配的残余触发） | residual 闸门违规（§6.2） |
| DS040 | — | Error | 改写后三候选语法校验全失败，原文保留（对齐 E06/JK invalid-syntax 保护） |
| DS050 | Warning | Warning | 中间树 `dotnet restore` 失败 |
| DS051/DS052 | — | Error | 中间树 build / test 失败 |
| DS060 | Info | Warning | 残余站点命中 manifest 条目（显式知情残留；`--strict` 升 Error） |
| DS061 | Warning | Warning | manifest 陈旧条目（源内已无对应站点），强制登记维护 |
| DS070 | Info | Info | 报告产物写出（路径、站点数） |

### 6.1 消息内容纪律

每条 DS01x 必须具名且可操作，统一句式：`dynamic <kind> at <表达式或签名摘要>: <为什么不可证>; <建议动作>`。建议动作按 kind 给：局部→「把声明改为具体类型 T（来源类型：…）或在 Cs2jDynamicAudit.json 登记」；参数/返回→「改为具体类型或 object 并由调用方显式转换」；Expando/JSON→「改为具名 DTO/Map，见消息配方」；RuntimeBinder→「移除显式绑定器依赖并捕获/替换异常处理」。**禁止**只说「unsupported dynamic」而不给位置与原因。

### 6.2 residual 闸门（rewrite 模式收口）

改写完成后对中间树做双扫：

1. **语法扫**：非注释/非字符串内的 `dynamic` 关键字 token 计数（含 `var` 隐式 dynamic 靠语义扫兜底）；
2. **语义扫**：全树 `TypeKind.Dynamic` 节点、`CandidateReason.Dynamic` 符号查询、object+`[Dynamic]` 使用点。

任一残余站点必须命中 manifest 条目（文件+span+kind 匹配），否则 DS030 Error、本次运行失败；命中者 DS060 Warning。合法终态只有两个：**dynamic 站点为 0**，或 **全部残余均已审计登记**（D13）。陈旧登记 DS061。

### 6.3 与既有静默路径的关系

- [TypeMappingService.cs#L682](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/TypeMappingService.cs#L682) 的 `"Dynamic type converted to Object"` Warning 与 [ElementAccessTransformer.cs#L275-L279](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs#L275-L279) 兜底**本特性不动**：单文件直转（不经预处理）仍需安全网；pass 默认关期间全链行为零变化。
- Rewrite 严格模式交付的树理论上使这两条路径不可达；其退役（改为遇 dynamic 直接 fail-fast）是默认开门并完成语料验证后的独立 PR（§11），与 C21 退役转换器侧临时代码同节奏。
- 与 RR057/JK020/member-desugar 既有诊断不重复计错：RR/JK/MemberDesugar 的诊断描述的是「本 pass 因此跳过改写」（RR057、JK020，以及 member-desugar 的 CS2J5118/5136/5154/5174 四码，码位见 [MemberDesugarModels.cs#L30-L65](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarModels.cs#L30-L65)），DS0xx 描述的是「dynamic 站点本身」，两者消息面不同；同工程同时开启时允许并存（先既有 pass 跳过、C25 再具名盘点），不做跨 pass 去重。

---

## 7. 测试计划

### 7.1 Core 单测矩阵（`tests/CSharpToJava.Tests/DynamicDesugarUnitTests.cs`）

内联源码 → 整树 `CSharpCompilation` → `BindCompilation`/`RewriteFile` → 忽略空白文本断言 + 输出可编译断言（仿 JavaKeywords/AnonymousTypeLift 单测）。

**M1 审计（分类与分级）**：

- `dynamic d = new SealedFoo();` 直线路径 → 1 条 DynamicLocalDecl（P1 可证，仅审计不改）+ 其成员访问 1 条 DynamicInvocation/DS013 审计记录；
- 不可证分类各一：多赋值类型（DS010 `MultipleAssignmentTypes`）、赋值右值为 dynamic 方法返回（DS010 `DynamicAssignment`）、ref 实参逃逸（DS010 `ByRefEscape`）、lambda 捕获（DS010 `Captured`）、开放类型 T（DS010 `SealedNotSatisfied`）；
- 参数、返回（DS011）；字段/属性/事件（DS012）；dynamic 索引（DS014）；二元/复合赋值/自增（DS015）；静态实参传入 dynamic 形参（DS016，记录来源类型）；
- `((Foo)d).M()` → DS002 Info 且不产 DS013；
- `ExpandoObject`、`DynamicObject` 子类、`using Microsoft.CSharp.RuntimeBinder;` + `RuntimeBinderException`（DS017/DS018）；Newtonsoft 动态 JSON 用例以最小源码形态判定（无程序集引用时按名字保守识别并标注符号不可解析路径）；
- 元数据入口：源内手写 `[Dynamic] object x`（DS019）与普通 `object x`（不计数）对照；
- `var x = (dynamic)1;` 与 `object o = 1;` 对照（D6）；
- audit 模式输出字节不变、报告 JSON 内容/排序稳定（两次运行字节一致）、默认 Warning 下 `Success==true`、`--strict` 下 `Success==false`；
- 表达式树内 dynamic 是 C# 编译错误：基线错误树上不叠加重复噪声诊断。

**M2 重写**：

- 正例：sealed 类/值类型/string 局部单点赋值 → 声明变 T、使用点经输出重编译静态绑定、输出零 dynamic token；多使用点（成员/调用/运算/实参/foreach）一次性全部静态化；
- 派生类型赋值即拒（`dynamic d = new SealedFoo(); d = new Derived();`，Derived 赋值使集合异类型 → DS010，文件不变）；
- 开放类型局部即使单赋值也不改（`SealedNotSatisfied`，宁假阳）；
- 捕获/ref 逃逸不改；
- FormatAndValidate 三候选与失败保留；BOM 保持；clean 文件字节复制；**幂等**（输出再跑零改写、报告零 DS001）；
- manifest：登记条目匹配 → DS060 + 闸门通过；未登记残余 → DS030 失败；陈旧条目 → DS061；`--strict` 下 DS060 升 Error。

### 7.2 项目级 fixture（`tests/SampleDynamicDesugarSolution/`）

仿 SampleRecord/SampleJavaKeywordSolution：Lib + App + Tests + sln，纳入 ProjectTests `Content Include`，net10.0：

- **Lib**：P1 正例（sealed 类型局部具体化后的方法链、int/string 运算）、不可证局部一处（开放类型，供 strict 验证）、dynamic 参数/返回、ExpandoObject 一处（Lib 加 `Microsoft.CSharp` PackageReference，用于 DS017 真符号判定）、`[Dynamic] object` 元数据形态一处；
- **App**：跨文件 dynamic 调用与 JSON 风格边界 API 调用；附一份随 fixture 提交的 `Cs2jDynamicAudit.json` 精确登记允许残留的站点；
- **Tests 工程**：P1 改写的中间树 xUnit 行为断言（具体化后方法链/运算结果与原 dynamic 行为一致）；
- 断言矩阵：audit 模式 `Success`、统计分 kind 计数、报告 JSON 存在且站点齐全、中间树字节等于输入；rewrite 模式下中间树零未登记 dynamic、manifest 站点 DS060、`dotnet build -c Release` 退出码 0、`dotnet test -c Release` 通过；strict 模式按预期失败；关闭 pass 时链输出与今天字节一致（clean 工程零触碰）。

### 7.3 Release build + test 与全量回归

- `dotnet build -c Release`（CSharpToJavaConverter.slnx）；Tests/ProjectTests/ConversionTests 三工程 `dotnet test -c Release` 全绿或红灯集不扩大（本设计仅文档，命令由实施 PR 执行）；
- 链序组合专项：与 java-keywords 同树（具体化后 JK-8 dynamic 分支不可达）、与 C21 同树（含 dynamic 属性的匿名类型 → AL030 + DS012 各记各的）、与 platform-isolation（stub 占位期仅验证注册拓扑）。

### 7.4 residual 闸门

语义+语法双扫断言（§6.2）：rewrite 后中间树 dynamic 站点 = 0 或全部命中 manifest；用「一个未登记残余」负例钉 DS030，用「全部登记」正例钉 DS060，用「登记后人工删站点」钉 DS061。

### 7.5 CLI / 一致性 / 既有测试

- `--enable-pass dynamic-desugar` 启停、`--dynamic-report-only`/`--dynamic-mode Audit\|Rewrite`/`--dynamic-strict`/`--dynamic-audit-manifest`、`--list-passes`、preserve 目录、verify 失败退出码、独立 `preprocess --pass dynamic-desugar` 对原始源码出报告；
- [ProjectPreprocessorsConsistentTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/ProjectPreprocessorsConsistentTests.cs) 追加 adapter/描述符/目录断言；注册表拓扑顺序断言锁定「eliminate-goto → dynamic-desugar → java-keywords」及与 platform/concurrency 的相对位置；CS2J9001 对该 id 消失；
- [OverrideRuntimeClassParamTests.cs#L21-L36](file:///d:/code/cs2j/tests/CSharpToJava.Tests/OverrideRuntimeClassParamTests.cs#L21-L36)：默认关期间保持现状不动；在本 pass 测试中以同一源码补一条「audit 报 DS011、strict 失败」对照，不修改原测试断言；
- [RecordReadOnlyRewriterUnitTests.cs#L1185](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/RecordReadOnlyRewriterUnitTests.cs#L1185) 的 `M(dynamic d)` 用例补审计侧覆盖（DS011+DS013），RR057 原断言不动；
- DualTargetCorpus 增补一个 P1 可证局部（sealed 接收者）双目标用例，验证 .NET/Java 两侧行为一致。

---

## 8. 默认开启验收门槛

**Audit 模式可以较早默认开**（只复制+出报告、Warning 不阻断、产物字节不变），但须同时满足：

1. 三工程 `dotnet build/test -c Release` 全绿，红灯集不扩大；pass 关闭时链字节一致（默认关落地→默认开是一次单独翻转）。
2. csharp-sdk 样本补扫（不杜撰数字，先跑后填）：dynamic 站点密度、kind 分布（声明/操作/边界 API 占比）、P1 可证率、Expando/JSON/RuntimeBinder 命中数；Warning 噪声量可接受（每站点一行、不刷屏重复）。
3. 额外一次整树复制的耗时在基线样本实测可接受（catalog 风险 11）；指纹/增量缓存验证通过；audit 报告字节稳定。
4. 误报复核：样本中的 DS010 分类原因分布经人工抽检，`SealedNotSatisfied`/`Captured` 等保守拒绝无事实错误（假阳可接受，事实错误不可接受）。
5. DS017/DS018/DS019 在样本中的命中全部有人工分类结论与消息配方，无「unknown」类兜底消息。

**Rewrite 模式长期 opt-in**，除上述外还须：

6. SampleDynamicDesugarSolution 在 rewrite + strict 下 build/test 全绿；P1 改写的 .NET 行为等价断言齐全。
7. sdk 样本中 P1 可证站点经 manifest 为 0 的 strict 跑通（即：要么被具体化，要么被显式登记），无一例漏报（假阴）通过——抽样人工复核 DS001 改写点。
8. §11 转换器静默路径退役方案经评审（退役 PR 独立于翻默认开）。
9. EF Core 补扫（foundation plan §5.1 前置行动）给出 dynamic 面数据后再评审；EF 动态表达式/EFCore 工具代码中 dynamic 命中形态未证实前不翻 rewrite 默认开。

---

## 9. 里程碑

| 里程碑 | 内容 | 粗估规模 |
|---|---|---|
| M1 | **纯审计**：`DynamicSiteFacts` 共享判定（先行交付）+ Models/Options + DynamicSiteInventory 全 kind 分类（含 `[Dynamic]`/CandidateReason/边界 API）+ 诊断 DS010–DS019/DS061 + 报告 JSON + adapter Audit 模式（标准 spine、零改写）+ 注册表翻转（Implemented、默认关）+ 单测分类矩阵 + CLI/一致性/拓扑测试 | Core ~700 行 + CLI ~250 行 |
| M2 | **可证子集重写**：FlowTypeProver P1（赋值并集/逃逸/sealed）+ DynamicSiteRewriter A1 + FormatAndValidate + manifest（D13）+ residual 闸门 DS030/DS060 + adapter Rewrite 模式（verify 双开）+ SampleDynamicDesugarSolution + Release build/test 验证 + 幂等/双目标用例 | Core ~500 行 + CLI ~150 行 + fixture |
| M3（观察项，数据驱动） | 开放类型的逐成员虚/隐藏判定放宽 sealed 限制；`is/as` 分支收窄；P3 私方法单态签名具体化；A2 逐站点强转（manifest 授权）；holder 字段来源识别（C32/C45 协调）；转换器侧 Object/.get 静默路径退役；audit 默认开翻转评审 | 独立排期，任一子项都须语料数据支撑 |

---

## 10. 风险

| # | 风险 | 证据/说明 | 缓解 |
|---|---|---|---|
| R1 | 假阴（错误具体化）：隐藏成员、用户自定义转换、动态运算的运行时重载与静态绑定不一致 | P1 sealed 限制消去成员隐藏；值类型装箱/可空提升差异仍须测试覆盖；证明器只认同一 T 不做基类合并 | 宁假阳（D11）；.NET 侧 build/test 预言；strict 零容忍；DS001 抽检入门槛 7 |
| R2 | 现有工程依赖「dynamic→Object 静默成功」，开启即变红 | [OverrideRuntimeClassParamTests.cs#L21-L36](file:///d:/code/cs2j/tests/CSharpToJava.Tests/OverrideRuntimeClassParamTests.cs#L21-L36) 钉死该路径；真实代码中 dynamic 返回后当 object 用的情况可能不少 | 默认关 + audit 先行（只 Warning）；manifest 留痕豁免；不改既有测试；rewrite 长期 opt-in |
| R3 | 假阳过多导致 audit 噪声淹没、用户流失 | sealed-only + 捕获即拒在动态密集工程（JSON/脚本互操作）可能几乎全拒 | 报告 JSON 机器可读、按 kind/原因聚合；M3 按数据定向放宽；audit 默认开门槛 2/4 以噪声实测为准 |
| R4 | 晚链位丢失出处：前序 pass（E06 已把 dynamic 元组元素物化为 object；未来 C32/C45 可能物化 dynamic 捕获字段）使站点在最终树不可追溯 | [TupleShapeRegistry.cs#L291](file:///d:/code/cs2j/src/CSharpToJava.Core/TupleToStruct/TupleShapeRegistry.cs#L291) 现状；闭包提升是 stub 未实现 | 独立 verb 可对原始源码审计（§3.3）；M3 holder 来源识别；与 C32/C45 设计评审对齐；审计报告同时面向「原始树跑一次」的使用姿势 |
| R5 | 元数据入口漏判：只扫 `dynamic` 关键字会漏掉 object+`[Dynamic]`（跨程序集 dynamic 成员的主要存在形式） | 全仓零 DynamicAttribute 特判（§1.1） | 双扫闸门（语法+语义，§6.2）；DS019 单测用源内 `[Dynamic]` 构造 |
| R6 | ExpandoObject/JSON 配方被用户当作等价改写：Expando 名匹配回退、INPC、JObject 转换语义与 Map/DTO 均不同 | D7/D8 | 消息只给「人工评估」配方并声明不等价；本 pass 不提供任何自动改写动作 |
| R7 | manifest 腐烂成新的静默通道（复制旧条目、span 漂移） | D13 | 键含 span+kind+符号；陈旧条目 DS061 Warning；manifest 随源码受控评审；strict 下一票否决 |
| R8 | audit 模式额外整树复制的耗时与指纹成本 | catalog 风险 11；每 pass 一环复制是现状架构 | audit 不 restore/verify；clean 字节复制；门槛 3 实测耗时；未来统一引擎合批（catalog §5.4）一并解决 |
| R9 | 与 RR057/JK020/member-desugar（CS2J5118/5136/5154/5174）诊断并存造成「同一 dynamic 多处报错」的体验问题 | 各 pass 各有职责面（§6.3）；member-desugar 已实现、默认关，其 dynamic 闸门与 RR/JK 同型 | 码位与消息明确分工（别人是「我因此跳过」，DS 是「站点本身」）；报告 JSON 聚合去重视图；不做跨 pass 抑制 |
| R10 | 基线编译错误树上语义查询返回动态/错误混合，误报 | 表达式树内 dynamic（CS1963）等 | 基线 error 节点走 `UnresolvedSymbol` 保守入账且不叠加编译器错误；rewrite 仅在基线零新增错误前提下交付（整树 verify） |
| R11 | Newtonsoft/System.Text.Json 程序集不在引用中时边界 API 识别退化 | fixture 之外的工程可能缺引用 | 符号识别优先（TryResolveSymbolIdentity 模式），失败时按限定名文本保守识别并在消息标注「未解析引用，请人工确认」，宁假阳 |

---

## 11. 并行协调

| 相邻工作 | 边界 | 顺序/依赖 |
|---|---|---|
| **C42 `platform-isolation`（stub）** | 同锚 eliminate-goto 的链尾相邻 pass（注意 C42 Stage=Abi，本 pass 与之一致；concurrency-normalize Stage=Declarative，仅链位相邻）；C25 复用 CS2J3001/3101/3102/3201-3203 一族的诊断形状（码位实况见 [UnsupportedDomainPasses.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/UnsupportedDomainPasses.cs)：CS2J3001/3101/3102/3201/3202/3203，`BoundaryDiagnosticCollector` 去重、MSBuild 字符串、Error/Warning 分级、测试探测降级）但**新建 DS0xx 码位**，不改 C42 码位 | 无功能依赖；注册表插入顺序锁定 platform → concurrency → dynamic；共享「边界诊断基建」在 C42 落地时抽取为公共组件的话，C25 改为消费它，不重复建设 |
| **C21 `anonymous-type-lift`（设计稿，仍为 stub）** | C21 保真 dynamic 属性（不改写、AL030 Info）；C25 在链尾审计到 `Cs2jAnonymous` 生成类上的 dynamic 属性时按 DS012 处理 | 无 RunAfter 依赖；AL030（C21 设计 [L342](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c21-anonymous-type-lift-design.md#L342)）与 DS012 分类词汇对齐；C21 M1 已承诺验证「转换器对 dynamic 属性是否必然产坏 Java」，结论同步本 pass 的严格度 |
| **C08/E17 `java-keywords`（已实现，默认关，FinalNaming）** | JK-8 已对 dynamic 布尔站点显式跳过：[JavaKeywordAnalyzer.cs#L492-L512](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/JavaKeywordAnalyzer.cs#L492-L512) 标 `IsDynamic`、Phase 3 [L310-L315](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/JavaKeywordAnalyzer.cs#L310-L315) 判 Infeasible，JK020 由 [JavaReservedWordPreprocessor.cs#L45-L69](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/JavaReservedWordPreprocessor.cs#L45-L69) 发射；另 [KeywordRenamingRewriter.cs#L439-L441](file:///d:/code/cs2j/src/CSharpToJava.Core/JavaKeywords/KeywordRenamingRewriter.cs#L439-L441) 对动态绑定的标识符原样不改名；C25 严格重写后链上无 dynamic 可触发 JK-8，动态接收者上的关键字改名面也随之闭合 | RunAfter 三尾锚边已存在（dynamic 先于 java-keywords，[PassCatalog.cs#L251-L257](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L251-L257)）；审计模式双层诊断并存是预期（R9）；C25 不改 JK 代码 |
| **C11/C12/C13/C14 `member-desugar`（已实现，默认关）** | 已落地四类 dynamic 接收者保守闸门：属性 CS2J5118（[PropertyRule.cs#L128-L136](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Properties/PropertyRule.cs#L128-L136)）、索引器 CS2J5136（[IndexerRule.cs#L71-L81](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Indexers/IndexerRule.cs#L71-L81)）、事件 CS2J5154（[EventRule.cs#L211-L222](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Events/EventRule.cs#L211-L222)）、运算符 CS2J5174（[OperatorRule.cs#L252-L259](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/Operators/OperatorRule.cs#L252-L259)，strict Error、force 不解除）；其私有谓词 [MemberDesugarSyntaxFacts.IsDynamic L26-L34](file:///d:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarSyntaxFacts.cs#L26-L34) 与 `DynamicSiteFacts` 重叠但更窄 | member-desugar 在链中先跑（描述符锚 StructValueSemantics/ClosureLift，[PassCatalog.cs#L231-L241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L231-L241)）；C25 不改其代码；M1 交付后 DynamicSiteFacts 是否回收替换该私有谓词，列入 §13 开放问题 |
| **C03 `pattern-desugar`（已实现，默认关）** | 对 object/dynamic governing 的常量/关系模式主动改类型测试形态（[PatternReducer.cs#L203-L205](file:///d:/code/cs2j/src/CSharpToJava.Core/PatternDesugar/PatternReducer.cs#L203-L205)），不跳过也不具名 dynamic；无语义模型时 PD010 | 无 RunAfter 依赖；pattern-desugar 在链中段，产物中的 dynamic 站点仍由 C25 链尾盘点；M3 评估对其合成绑定变量的来源识别 |
| **表达式族 C23/C24/C35/C36/C14（设计/观察项）** | 各 pass 在 dynamic 操作子树内的改写都会改变绑定器行为；catalog 风险 8（[L1106](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1106)）要求共享跳过判定 | M1 即交付 [ExpressionTreeFacts.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Analysis/ExpressionTreeFacts.cs) 的对偶 `Analysis/DynamicSiteFacts.cs`，各表达式 pass 落地时引用；不要求各 pass 等 C25 翻转 |
| **C32/C45 闭包提升（stub）** | dynamic 局部被捕获后可能物化为 holder 字段，晚链位出处丢失（R4） | 无 RunAfter 依赖（closure-lift 在链前段）；其设计须把「dynamic 捕获字段」列为已知产物并引用本文件；M3 做来源识别 |
| **C26 `linq-channel`（stub）/ E16 嵌入式 LINQ** | 表达式树禁 dynamic 是编译器规则；LINQ 链上的 dynamic 结果（如 JSON Select）走 DS013/DS017 | 无直接依赖；C26 三路分流门与本 pass 独立；EF Core 补扫数据两边共享 |
| **E11 record-readonly（已实现，默认关）** | RR057/MS-14 已把 dynamic 接收者作为自身改写的阻断条件（[RecordConversionPlan.cs#L548-L550](file:///d:/code/cs2j/src/CSharpToJava.Core/RecordReadOnly/RecordConversionPlan.cs#L548-L550)） | 行为互补无冲突；判定模式（全树 `TypeKind.Dynamic` 扫描）是 `DynamicSiteFacts` 的需求来源之一 |
| **转换器静默路径退役（M3）** | 默认开门后两处成为可达性可证的死路径：①[TypeMappingService.cs#L679-L684](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/TypeMappingService.cs#L679-L684) dynamic→Object 擦除（改为显式 fail-fast 或保持仅直连单文件入口）；②[ElementAccessTransformer.cs#L275-L279](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs#L275-L279) unknown/dynamic `.get` 兜底 | 独立 PR，在 rewrite 默认开评审之后；退役前保留默认关回退；同步修订受影响测试 |

**热文件清单**（落地时预期冲突面）：[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)、[PassCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)、[PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs)、[.gitignore](file:///d:/code/cs2j/.gitignore)、注册表顺序断言测试；新增 `Analysis/DynamicSiteFacts.cs`（各表达式族将先后引用）；M3 才触碰 [TypeMappingService.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/TypeMappingService.cs) 与 [ElementAccessTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs)。

---

## 12. Worktree 分配

worktree 已于 2026-10-05 按下述分配创建（base 已从 `953293b9` 更新为 `dev-3.x-26100507` 当时最新提交）：

- 分支：`feature/preproc-c25-dynamic-desugar`
- 目录：`.worktrees/preproc-c25-dynamic-desugar`（`.worktrees/` 已在 [.gitignore#L77](file:///d:/code/cs2j/.gitignore#L77) 忽略）
- base：`ba7a0897`（`feat(c49): default-on extension-block materialization`，即创建时 `dev-3.x-26100507` 最新提交，已核验存在：`ba7a0897069331fe3466c452ba3359283beea2cb`）

```powershell
git worktree add .worktrees/preproc-c25-dynamic-desugar -b feature/preproc-c25-dynamic-desugar ba7a0897
```

---

## 13. 开放问题（2026-09-29 证据核对后挂起，拿不准不杜撰）

1. **DynamicSiteFacts 与 MemberDesugarSyntaxFacts.IsDynamic 的收敛**：后者已在已实现 pass 中落地（TypeKind 两面），新共享工具是否在 M1 即替换它、还是先并存后收敛，取决于 member-desugar 团队节奏；本设计只承诺 DynamicSiteFacts 方法面做成超集。
2. **manifest 落位/查找**：D13 设想源根 `Cs2jDynamicAudit.json`，而现行 overrides 查找是 `CWD/config/` + `AppContext.BaseDirectory` 上溯（[Program.cs#L3188-L3201](file:///d:/code/cs2j/src/CSharpToJava.CLI/Program.cs#L3188-L3201)）；实施前定其一并保持「随源码受控」语义。
3. **.gitignore 惯例不统一**：pattern-desugar/generic-deepen/iterator-lowering/member-desugar 四个已翻转 pass 的中间目录均未登记（仅老 pass 登记于 [L89-L101](file:///d:/code/cs2j/.gitignore#L89-L101)）；本 pass 单独补登还是统一补，实施时定。
4. **Implemented 表插入点与 member-desugar 的相对次序**：member-desugar 描述符现夹在 eliminate-goto 与 java-keywords 之间（[PassCatalog.cs#L231-L241](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L231-L241)），与本 pass 无 RunAfter 关系；翻转时拓扑断言须同时锁定 goto→dynamic→java-keywords 与 dynamic↔member-desugar 的确定次序。
5. **catalog 自身状态滞后**：catalog E17（[L261-L269](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L261-L269)）/C08（[L378-L387](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L378-L387)）仍写「缺失/设计待实施」，实际 java-keywords 已 Implemented；C11-C14 对应 catalog 条目状态同样可能滞后。属 catalog 文档维护问题，本次按授权只改本 spec，不替 catalog 改状态，提请 catalog 维护者统一刷新。
6. **D15「与 C21 同口径」无实现先例**：C21 目前仍是 stub，单文件模式不支持的具体做法应对照已实现的 IteratorLowering/PatternDesugar 适配器，而非 C21 设计稿。
7. **ExpressionTreeFacts 方法面**：现仅 lambda 谓词；若后续多 pass 需要「任意节点是否位于表达式树内」，是否把「祖先 lambda + 谓词」提升为该类的第二个公共方法（而非各 pass 自走祖先），在首个消费方出现时决定。
