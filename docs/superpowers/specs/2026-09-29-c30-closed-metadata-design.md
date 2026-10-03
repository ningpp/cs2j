# C30 闭型泛型元数据前置化（closed generic type metadata）设计

**日期**: 2026-09-29
**范围**: 注册表 stub `closed-metadata`（catalog C30，P2）的路线决策与实施设计；本文档只做只读代码考古与设计定稿，不改动任何代码
**状态**: 设计稿（M1 考古收口 + 路线评审为强制门，见 §10）
**状态（M2' 更新）**: M2' 骨架已落地——Core `ClosedMetadata/` 五文件 + CLI 适配器 + 注册表翻转（Implemented、默认关、opt-in，`--enable-pass closed-metadata` 空转字节复制，CS2J1205 链位校验生效）；M1 下游 dotnet-text-json 核实仍开放。
**状态（M3 更新）**: M3 已落地（A1 声明驱动）——Core `ClosedMetadataCollector`（CM-ENUM-1 声明闭型 + CM-ENUM-2 显式 typeof 站点 + CM-DYN CS2J1210，SemanticModel 符号驱动、键字典序确定性）/ `ClosedTypeKey`（规范化键，SymbolEqualityComparer.Default）/ `ClosedMetadataRegistrationGenerator`（每项目 `_Cs2jClosedTypes.<ProjectName>.cs`：CompilerGenerated + 锁字典幂等注册表类 + 显式 bootstrap `__cs2j_register_closed_types()`，纯 C# 不引用 Java/compat 名）/ Preprocessor `GenerateRegistrationFiles` + residual 最小闸门（重复/冲突注册键 → CS2J1220）；CLI 适配器 AfterFiles 分发 + csproj Compile Include/Link 幂等接线（接线算法提取为 `GeneratedFileWiring`，GenericDeepen 同用）；compat `TypeHelper.registerClosedType/resolveClosedType`（D3，零 ThreadLocal，TDD 先行，ClosedTypeMetadataTest 23 全绿）；Core 单测 ClosedMetadataTests 13 例 + fixture `tests/SampleClosedMetadataSolution/` 驱动测试（中间树 Release build/test 全绿）。默认关不变（opt-in）；**converter D2 映射（闭型 typeof 树 → compat Type 构造树）与消费面接线（ConsumptionLinker）留独立后续 PR**。
**对应基线**: `953293b9`（2026-09-29，`docs: update doc base commit`；= HEAD 往前 2 个提交，均为 docs-only）
**关联资料**: `prompts/preprocessor-catalog.md` C30（L680-691）/C02（L289-302）/§3.3 泛型擦除补偿族（L1086）/§4 风险 6（L1104）；旧设计 [2026-09-22-closed-type-metadata-chain-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-22-closed-type-metadata-chain-design.md) 与实施记录 [2026-09-22-closed-type-metadata-chain-plan.md](file:///d:/code/cs2j/docs/superpowers/plans/2026-09-22-closed-type-metadata-chain-plan.md)；C02 设计 [2026-09-28-c02-generic-deepen-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-28-c02-generic-deepen-design.md)；`.trae/documents/preprocessor-foundation_plan.md` L60-139；ROADMAP #10

---

## 1. 背景与目标

### 1.1 嵌套泛型 JSON 问题根因

cs2j 生成的 System.Text.Json Java 移植（下游 dotnet-text-json 仓）在反序列化/序列化嵌套泛型集合成员（C# `List<List<short>>` → Java `CSharpList<CSharpList<Integer>>`）时，内层元素转换器被错误解析成 `StringConverter`，运行时报 `Cannot get the value of a token type 'StartArray' as a string`（MyInt16TwoDimensionList 家族）。

根因是 **reified → erased 的类型信息在元数据链路中只剩擦除 Class**：成员/元素类型沿 `JsonClassInfo.addProperty` → `JsonSerializerOptions.getConverter` → `IEnumerableConverterFactory` 传递时，`List<List<short>>` 与 `List<string>` 擦除后同为裸 `CSharpList.class`。Java 反射在「成员声明签名」上本可保留完整闭型（getter 的 `getGenericReturnType()` 返回携带嵌套实参的 `ParameterizedType`），但移植版的解析路径没有消费它。

### 1.2 ThreadLocal 队列旧架构

compat [TypeHelper.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java) 现存 7 条 ThreadLocal 注册表（2026-09-29 核实）：

| ThreadLocal | 位置 | 作用 |
|---|---|---|
| `TYPE_ARGUMENTS` | L173-174 | 待消费类型实参队列（ArrayDeque，按 raw 类分层） |
| `CONSUMED_ARGUMENTS` | L180-181 | 已消费层回放 |
| `MEMBER_ARGUMENTS` | L187-188 | 成员级单槽簿记，`registerMemberType` 喂入 |
| `REGISTRATION_DEPTH` | L193 | 注册会话深度 |
| `WRAPPER_LAYER_TAKEN` | L206-207 | 包装器层去重 |
| `CALLSITE_ARGUMENTS` | L302-303 | 调用点实参（生命周期长于单次序列化，L213 注释自陈泄漏面） |
| `CONCRETE_SUBTYPES` | L1701-1702 | 具体子类型栈 |

构建期「注册→消费」时序对齐时尚可工作；但元素 ClassInfo 是**读取期懒创建**（`getElementClassInfo()` → `getOrAddClass(裸 Class)`），此刻队列已被后续成员替换，回放 CONSUMED 得到 `[String]`。旧架构经历约 18 轮作用域补丁（PatchElementArgumentScope / PatchMemberArgumentCapture / PatchMemberClassInfoLookup / PatchWriteElementPropertyInfo），只能缓解，且留下「全量跑才复现」的跨用例顺序依赖（0922 plan 记录的 DiagPoison：先反序列化 object 数组再反序列化 hashtable，fresh options 仍被污染，证明毒物经 ThreadLocal 跨会话传播）。

旧链路的数据流可概括为：成员元数据构建期 `registerMemberType(闭型)`（[TypeHelper.java:382-387](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java#L382-L387)）把闭型压入线程局部队列 → 工厂创建期 `getGenericArguments(raw)`（L1241）按消费顺序从队列/回放槽「猜」当前层实参 → 全部解析正确性依赖「注册顺序 == 消费顺序」的时序不变量。该不变量在懒创建、多成员交错、嵌套写入帧三个场景下不成立。

### 1.3 零 ThreadLocal 目标

让闭型的类型实参在 **C# 阶段就显式可追溯**（显式 `System.Type` token / 注册代码），或在 Java 反射侧沿声明签名显式贯穿 `java.lang.reflect.Type`，使类型解析成为闭型的**纯函数**，与反射顺序、调用时序、线程状态全部无关。catalog C30 的定位原文（L683）：「不单独新建 C# 通道；作为 **C02 的产出契约**登记（备选：生成『闭型元数据注册』C# 代码）」。

### 1.4 范围与非目标

**范围内**：①完成 catalog §4 风险 6 要求的代码考古（§2）；②在 A/B/C 三路线间给出有证据的决策（§3）；③登记注册表 stub 的去留契约（§4）；④若选 A，定义 C# 中间树侧的闭型枚举、注册码生成、消费链接的完整组件与规则；⑤冻结 compat 闭型消费契约的演进边界。

**非目标（任何路线下本通道都不做）**：

- 不复活 c8d6d31f 删除的「按硬编码生成文件名（JsonSerializerOptions.java 等）做事后正则替换」形态——该形态已被仓库架构方向否决（§2.2、§11 风险 5）。
- 不重写 compat 已落地的闭型纯函数（§2.4），只在其上新增注册表面（路线 A）或冻结契约（路线 C）。
- 不覆盖运行时合成闭型（dynamic、字符串反射 `Type.GetType`、表达式树、STJ 源生成 `JsonSerializerContext`）——遇之显式诊断（§7）。
- 不解决 C02 的类型参数 token 问题本身（`typeof(T)`/`is T`/静态泛型字段），只消费其产物；不改 C09 的 `[ThreadStatic]` 语义面。
- 不在本仓 CI 内建设下游 dotnet-text-json 仓的端到端流水线（下游半区只定义验收用例与人工核对流程，§8.3）。

---

## 2. ★考古证据汇总（2026-09-29，主树 dev-3.x，HEAD=5e3e796d）

### 2.1 全仓逐项 grep 结论

范围：全仓（src/tests/tools/config/prompts/docs/java，排除 .git、bin/obj、target），模式逐项实测。

| # | 模式 | C# 源码（src+tests，排除 bin/obj） | 其他位置 | 结论 |
|---|---|---|---|---|
| 1 | `PatchClosed` | **1 命中且非代码**：[PassStubCatalog.cs:108](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L108) stub 描述串 `"blocked on PatchClosed* archaeology"` | catalog L687；0922 plan L18；0922 spec L64；C02 spec L57；C02 plan L577 | **当前树无任何 PatchClosed* 实现代码** |
| 2 | `ClosedTypeMetadata` | 0 | catalog L687；0922 plan/spec；compat [ClosedTypeMetadataTest.java:21](file:///d:/code/cs2j/java/csharptojava-compat/src/test/java/io/github/ningpp/compat/ClosedTypeMetadataTest.java#L21)（类注释） | C# 侧零落点；仅文档 + Java 测试 |
| 3 | `ClosedType` / `closedType` | 3 命中，全部为 C02 **测试方法名**（per-closed-type holder 语义，与本通道无关）：[GenericDeepenTests.cs:414](file:///d:/code/cs2j/tests/CSharpToJava.Tests/GenericDeepenTests.cs#L414)、[RuntimeBehaviorTests.cs:9](file:///d:/code/cs2j/tests/SampleGenericDeepenSolution/SampleGenericDeepenTests/RuntimeBehaviorTests.cs#L9)、:19 | compat TypeHelper L1214 等（见 §2.4）；0922 文档；2026-07-02 plan L222（反射探针工具里的局部变量名，无关） | 无 C30 实现落点 |
| 4 | `closed_type` | 0 | 0 | 不存在 |
| 5 | ThreadLocal 队列相关（TYPE_ARGUMENTS / CONSUMED / registerMemberType 等） | **0 落点**。src 中 ThreadLocal 仅两处且都是 C09 面：[FieldTransformer.cs:51-57,121-147](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs#L51-L57)（`[ThreadStatic]` → `ThreadLocal<T>` 字段映射）、[ConversionContext.cs:397-402](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/ConversionContext.cs#L397-L402)（ThreadStatic 字段登记表注释） | compat TypeHelper 7 条 ThreadLocal（§1.2）；config 中 java.base 的 `ThreadLocal.json` 等是 BCL 映射配置 | C# 生成器侧从不直接引用队列；队列的调用方是**生成码产物**（下游 Java） |

### 2.2 git 历史考古（catalog L687 开放问题的答案）

`git log --all -S "PatchClosed"` 完整结果：`2694f975`（引入）→ `c8d6d31f`（删除）→ `92b9013d`/`c508bf26`/`a37c20e3`/`db9bbd08`（仅 docs）。

**PatchClosed* 曾在本仓存在并已被架构性删除**，关键提交：

- **引入**：`2694f975 improve + fix`（2026-09-22 前后约 18 轮补丁的落点）。删除前的 [ProjectPasses.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs) 含门控 `private const bool ClosedTypeMetadataEnabled = true;` 与 8 个补丁方法：`PatchClosedMemberType` / `PatchClosedParameterInfo` / `PatchClosedElementClassInfo` / `PatchClosedOptionLookups` / `PatchClosedMemberConverterResolution` / `PatchClosedFactoryEntry` / `PatchClosedEnumerableFactory` / `PatchClosedObjectFactory`（另有常开的 `PatchFastPathWriteNullGuard`），形态为「锚点正则 + 不匹配即响亮告警」，对按硬编码文件名识别的生成 Java（JsonPropertyInfo.java / JsonClassInfo.java / JsonSerializerOptions.java / JsonConverterFactory.java / IEnumerableConverterFactory.java / ObjectConverterFactory.java）做后处理文本替换并内联 Java 片段。配套测试 `tests/CSharpToJava.Tests/ClosedTypeMetadataPatchTests.cs`（632 行，26 个测试，plan 记录后期 17 个）。
- **删除**：`c8d6d31f refactor: remove hardcoded generated-file patch logic from ProjectPasses`（2026-09-23，宁培培，**是 HEAD 祖先**）。ProjectPasses.cs 约 3000 行（3236）→ **1031 行**（`-2998/+52`，全提交 6 文件）；ClosedTypeMetadataPatchTests.cs 全删（-632）；StructClonePatchTests.cs 同删（-104）。提交说明：删除按硬编码生成文件名键控的逐文件正则补丁、删除 no-op compatibility emit pass、注销按 app 方法名键控的 IR rewriter，代之以有序补丁表并清退死实验开关。
- **删除不是孤立事件**：其后 `3771daf0`（remove Compatibility pack subsystem）、`0e8d06e8`（remove CrossPackageImport*）、`d7246fd1`（移除 TypeScript）、`4b736217`（remove RuntimeClassParameter system）持续沿同一方向清退「后处理补丁/兼容包」架构。
- **现存 ProjectPasses.cs（830 行）内容**：`ProjectLinqDesugarPass`、`ProjectCompilationCheckPass`、`ProjectUnsupportedDomainCheckPass`、`ProjectPlatformBoundaryCheckPass`、`ProjectNativeInteropCheckPass`、`ProjectPartialTypeNormalizationPass`、`ProjectJavaPatternRewritePass`、`ProjectTypeEmitPass`、`ProjectExtensionMethodCheckPass`——全部作用于 **C# 中间树**，无一生成或修补 Java 文件。
- **分支排查**：`git branch -a` 无任何分支保留补丁代码；6 个现存 worktree（c01/c02/c03/c11/c12/c14）均为 preproc 新特性分支。
- **结论**：0922 设计状态标注「已实现(设计定稿)」在 2026-09-22 当日为真；次日其生成码侧即被整体删除，且文档状态从未回改——这就是 catalog L687「grep 不到、现状不明」的完整成因。补丁**不是未合入，而是合入后被架构性回退**。

### 2.3 旧设计声称落点 vs 当前树差异

| 0922 设计/plan 声称 | 当前树（2026-09-29） | 差异 |
|---|---|---|
| §3.2 生成码补丁 8 个 PatchClosed*（门控 `ClosedTypeMetadataEnabled`，位于 ProjectPasses.cs） | ProjectPasses.cs 830 行，全部补丁方法不存在 | **已删除**（c8d6d31f） |
| plan「Gates」：cs2j `dotnet test` 3185/0（26 in ClosedTypeMetadataPatchTests，后期 3176/0、17 个） | ClosedTypeMetadataPatchTests.cs 不存在（-632 行） | **已删除** |
| §3.1 compat API：`PropertyInfo.getGenericPropertyType`、`TypeHelper.declaredGenericType/collectClosureLayers/genericArgumentsInLayers/closedCacheKey/createConverterForClosedType/substituteTypeVariables`、`ConcurrentHashMapHelper.getOrAddUntyped` | **全部存活**（§2.4 实测行号） | 无差异（API 名 plan 后期为 `putIfAbsentUntyped`） |
| §4 退役 4 个 ThreadLocal 补丁（WriteElementPropertyInfo/ElementArgumentScope/MemberClassInfoLookup/MemberArgumentCapture） | 这些补丁本就是 ProjectPasses 正则补丁，随 c8d6d31f 一并消失；但 compat 侧旧 ThreadLocal API（registerMemberType/beginElementArgumentsScope/cachedClassInfoForMember 等）**全部仍在** | 补丁没了，compat 旧 API 仍被**当前生成码**调用，新旧两套并存于 compat |
| §3.3 端到端：下游 dotnet-text-json 仓再生成 + mvn 全量回归 | 下游仓本机不存在：`D:\dotnet-text-json` Test-Path=False（D:\ 仅 YamlDotNet-18.0.0）；本仓无 submodule、无路径配置引用 | **补丁是否被搬入下游仓，无法从本树证实或证伪**（M1 强制门，§10） |
| §3.2 键类型 `ClosedGenericKey(raw, args)` | [TypeHelper.java:880](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java#L880) 的 `ClosedGenericKey` 现存，被旧 `converterCacheKey`（L873 实例化；方法在 L849）与 `cachedClassInfoForMember`（L761）使用；`closedCacheKey`（L1121-1126）现产 **`ClosedStructuralKey`**（L1123） | 两套结构键并存，签名近似，路线 A/C 接线时须明确是否统一（§3 决策 D5、§11 风险 2） |

### 2.4 compat 侧闭型消费 API 现状（已落地、被测试 pin 死）

[TypeHelper.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java) 闭型纯函数区段（L990-1240，区段注释明示「WITHOUT touching any ThreadLocal registry」）：

| API | 位置 | 实测签名/语义 |
|---|---|---|
| `parameterized(Class raw, Type... args)` | L260 | 构造 `ParameterizedType`，闭型 token 的构造原语 |
| `collectClosureLayers(Type)` | L1008 | 闭型按层展开为 `LinkedHashMap<Class, Type[]>`，外层先注册、同 raw 首层胜出；支持 GenericArrayType 下钻（L1031） |
| `genericArgumentsInLayers(Map<Class,Type[]> layers, Class raw)` | L1043 | **实参顺序为 (layers, raw)**（0922 设计正文写作 (raw, layers)，以代码为准）；layers 为 null/empty 委托旧 `getGenericArguments(raw)`；命中层返回各实参 `toRawClass` 擦除类；未命中走 `genericArgumentsWithoutRegistry`（子类型声明→继承链→类型参数界，L1065-1083） |
| `declaredGenericType(MemberInfo)` | L1102 | PropertyInfo 走 `getGenericPropertyType()`，FieldInfo 走 `getGenericType()`，零副作用 |
| `closedCacheKey(Type)` | L1121 | ParameterizedType → `ClosedStructuralKey(raw, 递归规范化 args)`；其余 → 原 Class（Class/TypeReference 路径 key 形态不变） |
| `substituteTypeVariables(Type, Map<String,Type>)` | L1137 | 泛型定义成员按闭型实参重绑定；裸 TypeVariable 查绑定（未解析返回 null）；任一实参未解析返回 null；具体类型原样返回（调用方可引用同一判跳过） |
| `createConverterForClosedType(Object factory, Type closed, Object options)` | L1214 | 反射探测工厂闭型入口 `createConverter(Class, Map, options)`（layers：Class→Type[]），有则调、无则回退旧 `createConverter(Class, options)`；compat 不引用生成类型 |
| `consumeMemberTypeArguments(Type closed)` | L442 | plan fix #9 的 drop-all 卫生函数：清掉闭型传递闭包内每个 raw 的全部 per-raw 簿记 |
| `toRawClass(Type)` | L910 | ParameterizedType→raw；GenericArrayType→擦除组件数组类（L917-923，修过闭型泛型数组误落 Object 的缺陷） |

[ConcurrentHashMapHelper.java](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ConcurrentHashMapHelper.java)：

- `putIfAbsentUntyped(map, key, value)` L71-73：闭型链专用，调用方自备 `closedCacheKey`，纯 `putIfAbsent`，零注册表咨询（L63-69 注释明示键空间与擦除键不相交）。
- `tryAdd` L10-32 / `getOrAdd` L36-61：旧混合路径，key 经 `converterCacheKey`（读 ThreadLocal）改写并驱逐旧擦除项——**这是 catalog L686 所引「ThreadLocal-free 键控」的实际形态：新旧两套并存，按调用方分流**。

[PropertyInfo.java:372-380](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/PropertyInfo.java#L372-L380) `getGenericPropertyType()`（getter 泛型返回型优先，否则 setter 首参泛型型，不触发 registerMemberType）；[FieldInfo.java:55-56](file:///d:/code/cs2j/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/FieldInfo.java#L55-L56) `getGenericType()` 直接镜像。

[ClosedTypeMetadataTest.java](file:///d:/code/cs2j/java/csharptojava-compat/src/test/java/io/github/ningpp/compat/ClosedTypeMetadataTest.java)（318 行）pin 住全部契约：嵌套层展开/同 raw 首层胜出、清 ThreadLocal 后闭型解析正确、null layers 回退、未知 raw 走界、闭型 field/getter 反射无副作用（反射断言 MEMBER_ARGUMENTS 为空，L148-151）、结构键相等性、putIfAbsentUntyped、substituteTypeVariables 五例（含 `T=List<String>` 嵌套替换）、consumeMemberTypeArguments 三例。

**小结**：compat 半区是「已完工且不改即满足」的资产；缺口只在**谁来调用这套纯函数**——c8d6d31f 之后，生成码侧没有任何调用方。

### 2.5 C02 generic-deepen token 现状（主树已落地，默认关）

- 注册表：[PassCatalog.cs:167-178](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L167-L178) `generic-deepen` = `Implemented`、**`defaultEnabled: false`**、`PassStage.Abi`、RunAfter=`generic`、`catalogItems: "C02"`（db9bbd08 翻转，stub 表中已无该 id；closed-metadata stub 的 RunAfter 引用 `PassIds.GenericDeepen`）。
- Core：`src/CSharpToJava.Core/GenericPreprocess/` 10 个 GenericDeepen 文件（Analyzer/Result/Rewriter/HolderGenerator/ResidualScanner/Options/Statistics/Diagnostics/SyntaxFacts/Preprocessor；另有 3 个 E10 遗留 GenericPreprocess* 文件，共 13 文件）；适配器 [GenericDeepenPassAdapter.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/GenericDeepenPassAdapter.cs)；中间目录 `.cs2j-generic-deepen-src`。
- token 产物形态（2026-09-29 实测）：
  - 方法块首锚点 `global::System.Type __cs2j_token_<T> = typeof(<T>);`（[GenericDeepenSyntaxFacts.cs:9](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericDeepenSyntaxFacts.cs#L9) `AnchorPrefix = "__cs2j_token_"`，测试锁定文本见 [GenericDeepenTests.cs:52](file:///d:/code/cs2j/tests/CSharpToJava.Tests/GenericDeepenTests.cs#L52)）；E10 旧哨兵 `__cs2j_typeof_` 被识别去重；碰撞加 `_2` 后缀。
  - 静态泛型字段 holder：生成文件 `_Cs2jGenericHolders.<ProjectName>.cs`（`GeneratedFileStem`），holder 类 `__Cs2jGenericHolder_<T>`，访问器 `__cs2j_get_<F><T>(global::System.Type __cs2j_token_T)`；存储形为**加锁 `Dictionary<string,object>`**，键 = 各 token 的 `AssemblyQualifiedName` 以 `"|"` 拼接（[GenericDeepenHolderGenerator.cs:59-82](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericDeepenHolderGenerator.cs#L59-L82)）。
  - residual 闸门：[GenericDeepenResidualScanner.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericDeepenResidualScanner.cs) 重编译中间树，扫 `static-field` / `catch-type-param` / `type-param-pattern` 三类残余。
- **与 C30 消费契约对齐的关键事实**：C02 锚点承载的是**类型参数 T 的运行时 Class token**（解决 `typeof(T)`/`is T`/静态字段按闭型共享），**不产生具体闭型 `List<List<short>>` 的任何注册物**。stub 描述现写的 "keyed by generic-deepen tokens"（PassStubCatalog.cs:108）是愿景而非现存契约：C# 的 `typeof(List<List<short>>)` 经转换器到 Java 侧，class literal 擦除后只剩 `CSharpList.class`，嵌套实参在 Java 字节码里天然消失——闭型需要的是 `java.lang.reflect.Type` **构造树**（compat `parameterized(raw, args...)` 可造），与 C02 的 `Class<T>` token 同族但不同型。这一缺口决定了路线 A 的核心工作量（§3 决策 D2）。

### 2.6 被删除补丁族的完整清单（git show c8d6d31f^ 核实）

从删除前 ProjectPasses.cs 实测到的开关与补丁调用点（0922 plan「Landing configuration」在代码中逐字存在）：

| 开关（编译期常量） | 删除前状态 | 对应补丁 | 现状 |
|---|---|---|---|
| `ClosedTypeMetadataEnabled` | `true` | 8 个 PatchClosed* + 链上 8 个调用点 | 代码全删；compat 被调 API 存活 |
| `WriteElementPropertyInfoEnabled` | `false`（落地即退役，与闭型链冲突，0922 §4 退役序第 1） | PatchWriteElementPropertyInfo（擦除反查覆盖闭型答案） | 全删 |
| `MemberClassInfoLookupEnabled` | `false` | PatchMemberClassInfoLookup（cachedClassInfoForMember 探测，闭型化后成死代码） | 全删；compat `cachedClassInfoForMember`（L741）仍在 |
| `ElementArgumentScopeEnabled` | `false`（实测 no-op，闭型补丁接管作用域发射） | PatchElementArgumentScope（push/begin scope） | 全删；compat `beginElementArgumentsScope/pushArgumentContext/endElementArgumentsScope`（L575/634/673）仍在 |
| `MemberArgumentCaptureEnabled` | `false`（实测 no-op，残余由 consumeMemberTypeArguments 排空） | PatchMemberArgumentCapture | 全删；compat `rememberMemberArguments/memberArguments`（L699/706）仍在 |
| （常开，不门控） | n/a | PatchFastPathWriteNullGuard（四处快速路径空值守卫，0922 plan fix #8） | 全删；该潜伏 NPE 守卫若下游仓不持有，缺陷会随闭型转换器准确定位而重新显形 |

补丁方法体内联字符串中实测存在 `TypeHelper.closedCacheKey(__cs2jClosedType)` 的调用文本（git show c8d6d31f^ 两处命中），证明当时生成码补丁与 compat 契约的调用形态即 §2.4 API。删除测试 ClosedTypeMetadataPatchTests.cs 的用例名亦完整还原了旧契约细节：`__cs2jErasedRuntimeType`/`__cs2jErasedElement` 擦除回退变量、注解感知注入（必须落在 `@CompatNonPublicProperty/@CompatNonPublicAccessor` 块之上）、伪属性自举（`__cs2jSetRuntimeClassInfo(this)`）、入口路径作用域存活（DeserializeEntries 三处注册表重置）。这些细节是路线 A 设计消费链接（§5.1 ConsumptionLinker）与路线 C 下游核对清单（M1）的现成核对基准。

### 2.7 考古方法与可复现命令（全部只读）

本节证据可用以下只读命令独立复现（Windows PowerShell，仓库根 `d:\code\cs2j`）：

```powershell
# 1. 现树逐模式检索（C# 半区，排除 bin/obj）
Get-ChildItem -Recurse -File -Include *.cs -Path src,tests |
  Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } |
  Select-String -Pattern 'PatchClosed','ClosedTypeMetadata','closedType','closed_type'

# 2. 补丁增删历史与当前归属
git log --all --oneline -S "PatchClosed"
git log --all --oneline -S "ClosedTypeMetadata"
git show c8d6d31f --stat
git merge-base --is-ancestor c8d6d31f HEAD   # 退出码 0 = 删除已在当前分支历史

# 3. 删除前补丁/开关/测试原文
git show c8d6d31f^:src/CSharpToJava.Core/Pipeline/Passes/ProjectPasses.cs |
  Select-String 'PatchClosed|ClosedTypeMetadataEnabled'
git show c8d6d31f^:tests/CSharpToJava.Tests/ClosedTypeMetadataPatchTests.cs

# 4. compat 存活契约
# TypeHelper.java L164-213/L302/L880/L990-1240；ConcurrentHashMapHelper.java L10-73；
# PropertyInfo.java L372-380；FieldInfo.java L55-56；ClosedTypeMetadataTest.java 全文

# 5. worktree/分支现状（确认无分支存留补丁）
git worktree list
git branch -a
```

考古边界声明：本次仅能访问本仓与本机文件系统；下游 dotnet-text-json 仓（`D:\dotnet-text-json` 实测不存在）与任何远端未在本环境核验，相关结论一律标为「待 M1 核实」，不以推测充当事实。

---

## 3. 关键设计决策表

### 3.1 路线 A / B / C 决策（M1 强制评审门输入）

| 路线 | 内容 | 已有资产 | 主要成本/阻断 | 结论 |
|---|---|---|---|---|
| **A. C# 前置注册代码**（本仓新建 preprocessor 规则） | closed-metadata pass 在 C# 中间树枚举全程序闭型，生成显式注册 C# 代码（`System.Type` token 树），转换器把闭型 typeof 树发射为 compat Type 构造/注册调用，Java 反射侧零 ThreadLocal 消费 | C02 全套基建（全编译 Analyze→Plan→Rewrite、holder 文件分发、residual 闸门、六参适配器）；compat `parameterized`/`closedCacheKey`/`putIfAbsentUntyped` | ①转换器需新增「闭型 typeof 树 → Java Type 构造树」发射能力（现不存在，是 C02 契约的扩展而非消费）；②compat 需新增「C# 可推送的闭型注册表」表面（现只有纯函数+缓存键，无 register 入口）；③动态闭型（`List<T>` 的 T 运行时绑定）必须走调用点传播，爆炸面大 | **中期演进主线（备选升格）**：先做声明驱动原型（A1），不承诺默认开 |
| **B. Java 侧反射闭型路线**（0922 原路线） | 不改 C#：生成的 Java 沿 getter/field 泛型签名取闭型，`getOrAddClass(Type)`/`getConverter(Type)` 键控贯穿（0922 设计 §3 全套） | compat 纯函数全部存活且被 JUnit pin 死；有效性已被实测证明（plan：ArrayTests 31E→9E、MyInt16TwoDimension 归零、0 new failure，46726 测试全量回归） | **旧落点形态（ProjectPasses 硬编码文件名正则补丁）已被 c8d6d31f 代表的架构方向明确否决**。复活只有两个形态：B1 改转换器**原生发射**（改动转换器最厚的 JSON/反射面，大改且不可后置）；B2 新建 Java post-conversion 通道（现 preprocess 链 18 环全部是 C#→C#，无 Java 后处理基建，等于新建一条链） | **不以旧形态复活**；B1 是路线 A 在 JSON 专用面上的变体，随 A 评估；不单独建 B2 |
| **C. 维持下游仓补丁 + 本仓只登记契约** | 补丁由下游 dotnet-text-json 仓自行持有（手工/下游生成器分叉）；本仓 closed-metadata stub 长期保留为**审计/契约型**条目，冻结 compat 消费契约，不重建生成码补丁 | compat 已是事实上的冻结契约（测试 pin 死）；c8d6d31f 后本仓架构与该形态天然相容（本仓不再发 Java 补丁，正好让下游自持） | 补丁是否真在下游仓**尚未证实**（本机无下游仓）；风险是双份漂移、compat 演进时下游失配；本仓对「嵌套泛型 JSON」端到端无验证手段 | **近期推荐落点**：M1 在可访问下游仓的环境证实/证伪；证实时按 C 收口，stub 不翻转 |

**总推荐：C 近期收口 → A 中期演进，B 旧形态永久关闭。**

论证：①catalog 已明确「不单独新建 C# 通道，作为 C02 产出契约登记」，路线 C 与之一致，路线 A 是其写明的备选；②compat 半区投入已沉没且实测有效，缺的只是调用方，而 c8d6d31f 表明仓库已选择「调用方不在本仓生成器里」；③路线 A 的独有价值是让 cs2j 产物对**任意 Java 反射消费面**（不止 dotnet-text-json）零 ThreadLocal，值得做但必须等 C02 默认开启（其 token 链是 A 的传播底座）与下游状态核实之后；④在未证实下游状态前重建补丁，存在与下游双份并行的最坏情况。M1 若发现下游仓并未持有补丁且嵌套 JSON 仍在本仓生成器的服务范围内，则路线评审升格 A 为近期主线（仍先 A1 原型）。

### 3.2 路线 A 选定后的子决策（预登记，M3 后才实施）

| 决策点 | 结论 | 备注 |
|---|---|---|
| **D1 注册码形态** | v1 选 **A1 启动期/声明期注册**：按全程序闭型枚举结果生成每项目一份注册文件，在封闭类型的静态初始化或显式 bootstrap 方法中注册；序列化入口（转换生成的 `JsonSerializer.Serialize/Deserialize` 包装）首次调用前确保 bootstrap 已跑。**A2 调用点内联 token**（在每个反射消费调用点展开闭型 Type 构造树）列 M5+，仅用于动态闭型 | A1 可枚举、可去重、可 .NET 侧编译验证；A2 精确但要识别全部反射调用面，爆炸面大；二者不互斥，A1 先行 |
| **D2 C# token 在 Java 侧的存在形态** | 注册物在 C# 里永远是 `global::System.Type`（`typeof(...)` 合法 C#），转换器新增映射：**闭型 typeof 树**（`INamedTypeSymbol.IsGenericType && !IsDefinition`，参数递归）→ compat `io.github.ningpp.compat.TypeHelper.parameterized(raw.class, <args…>)`；裸类型参数 → C02 已有的 `Class<T>` token；**不引入 Java 概念到 C# 源码**。数组闭型 → `TypeHelper` 数组构造原语（对齐 toRawClass 的 GenericArrayType 分支，L917-923） | 与 C02 锚点同前缀族（`__cs2j_token_`/`__cs2j_closed_`）；转换器映射缺口是本路线最大单项工作，单独立 GD/CLOSED-TOKEN 规则并先出原型 |
| **D3 键控格式与 compat 对齐** | 注册/查询统一走 `TypeHelper.closedCacheKey(Type)` 的结构键（raw + 递归规范化 args）；新增 compat 注册表面建议形态：`TypeHelper.registerClosedType(Type closed)`（幂等，内部 putIfAbsentUntyped 入一张新的 `_closedTypes` 表）与 `TypeHelper.resolveClosedType(Class raw, Type... observedArgs)`，**不新增任何 ThreadLocal**；M3 评审时决定是否把 `ClosedGenericKey`（converterCacheKey 旧键）与 `ClosedStructuralKey`（closedCacheKey 现键）统一 | 新 API 必须先在 compat 以 TDD 落地（仿 ClosedTypeMetadataTest），再让生成码调用；禁止生成码直接引用 key 实现类 |
| **D4 闭型枚举（全程序扫描点）** | Analyze 一次，基于 SemanticModel 全编译符号表枚举：①字段/属性声明中的具体闭型（`INamedTypeSymbol` 非泛型定义，递归实参）；②显式 `typeof(闭型)` / `default(闭型)` / `new 闭型()` 站点；③序列化 API 调用点的类型实参（`JsonSerializer.Serialize/Deserialize/DeserializeAsync`、`JsonTypeInfo`/`JsonSerializerOptions.GetTypeInfo`，按符号全名匹配，config 中 STJ 映射前缀一并盘点）；④override/接口族成员签名中的闭型（复用 C02 族收集，避免两份漂移）；⑤C02 holder 访问点暴露的闭型实例化 | 枚举结果按规范化键（raw metadata name + args 递归）全局去重；partial 天然按符号合并；跨程序集只枚举有源符号者，无源符号的闭型不可注册 → CS2J1201 显式诊断 |
| **D5 泛型数组 / nullable / 元组闭型** | ①`T[]`/`T[,]`：数组本身携组件类型，走 Type 构造树的 GenericArrayType 形（多维数组经 E12 array-method 之后已是辅助形，按其产物枚举）；②`Nullable<值类型>`（`short?`）：Java 无 nullable，C#→Java 映射为包装类型，注册前必须先过 unsigned（E09）/nullable 归一结果，**在链上位于这些 pass 之后读取最终符号**，不自行猜测映射；③值元组：tuple pass（E06）之后元组已 struct 化，枚举看到的是生成 struct 闭型，按普通闭型处理；引用元组（`System.Tuple<>`）按普通泛型 | 任何无法在当时符号形态下确定 Java 目标类型的闭型 → CS2J1203，不静默跳过 |
| **D6 生成文件归属与去重** | 复用 C02/Tuple 的 AfterFiles 分发范式：文件名 `_Cs2jClosedTypes.<ProjectName>.cs`，归属「编译到该闭型声明点的项目」；csproj `Compile Include/Link` 接线同 [GenericDeepenPassAdapter.DistributeHolderFiles](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/GenericDeepenPassAdapter.cs#L137-L221)；compilation 级去重表保证同一闭型全树只注册一次；无支撑库工程（`PassSupportLibrary.None`），注册文件是用户源码的一部分 | 不新增 PassSupportLibrary 枚举值；bootstrap 触发点（模块初始化/显式调用）在 M3 原型中用「显式静态方法 + 序列化包装调用前调用」，不依赖 `<Module>` 初始化器（Java 无对应物） |
| **D7 C30 与 C02 职责切分** | C02：类型参数 T 的 Class token 显式化（reified→erased 的**变量**部分）+ 静态字段按闭型存储。C30：**具体闭型**的 Type 构造树/注册（erasure 的**常量结构**部分）+ 与 Java 反射消费契约的对齐。C30 只读 C02 产物（锚点/holder），不修改 C02 生成的节点；C02 不发射任何闭型注册码（C02 设计 §1.4 已锁定） | 两 pass 同 Stage=Abi、RunAfter 链为 generic → generic-deepen → closed-metadata；共享事实源建议提取到 Core `Analysis/`（符号工具/族收集），禁止复制两份分析 |
| **D8 反射消费面清单（路线 A 必须显式登记的消费点）** | 一期只服务：①compat 闭型纯函数的调用方（即 0922 §3.2 六个生成文件位：JsonSerializerOptions.getConverter/getOrAddClass、JsonPropertyInfo.getRuntimeClassInfo、JsonClassInfo 闭型构造器与 getElementClassInfo、JsonConverterFactory 入口、Enumerable/Object 两工厂的层参形）；②转换器自己发射的反射调用（`GetType().GetGenericArguments()` 等，需全仓盘点）。**明确不服务**：表达式树（`Expression<>`，catalog §4 风险 8 共同禁区）、运行时字符串反射（`Type.GetType("List`1")`）、STJ 源生成 `JsonSerializerContext`（AOT 形，遇之 CS2J1204 显式报错交人工） | 消费面清单是注册枚举的输入：不为清单外的消费面做闭型注册（避免无界膨胀） |

### 3.3 路线 C 下的 stub 定位（近期形态）

选 C 时 `closed-metadata` **不翻转为 Implemented**：保留 [PassStubCatalog.cs:100-109](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L100-L109) 的 Stub（默认关、显式启用 CS2J9001），描述更新为「审计/契约型占位：compat 闭型契约见 specs/2026-09-29-c30-...；生成码补丁由下游 dotnet-text-json 仓持有（M1 核实结论链接）」。可选地在 M2 增加一个**只读审计器**（不改树，作为独立 diagnostics 命令或 Implemented 审计型 pass，形态参考 §5.2），扫描中间树闭型站点与转换器发射的反射调用，产出「该程序依赖哪些闭型、当前 Java 产物将走 ThreadLocal 还是反射签名」的报告——为路线 A 提供频率证据，对齐 catalog §4 风险 10（opt-in 条目不长期空转）。

### 3.4 规则全集（路线 A，CM-x 预登记）

- **CM-ENUM-1（声明闭型）**：字段/属性/事件/方法签名中出现的具体闭型（`INamedTypeSymbol.IsGenericType == true && IsDefinition == false`），递归枚举全部实参层；成员经基类/接口隐藏时按符号 OriginalDefinition 去重。
- **CM-ENUM-2（显式站点）**：`typeof(闭型)`、`default(闭型)`、`(闭型)x` 强转、`new 闭型(...)` 对象初始化为枚举来源；其中 `typeof(闭型)` 同时是 D2 映射规则的改写对象（注册局部复用，不重复构造 Type 树）。
- **CM-ENUM-3（序列化调用面）**：按符号全名匹配 `System.Text.Json.JsonSerializer.Serialize/Deserialize/DeserializeAsync<T>`、`JsonSerializerOptions.GetTypeInfo<T>`、`JsonConverter<T>.Read/Write` 的实现/重写点；泛型 T 经调用图闭包传播到具体闭型实参（传播算法复用 C02 GD-NEST 的工作表不动点，只读不共享状态）。
- **CM-ENUM-4（族签名闭型）**：override/接口实现族中任一成员签名含闭型，族内全部成员登记同一闭型集（防止实现/声明两端注册不一致）。
- **CM-TOKEN（D2 映射契约）**：闭型 typeof 树 → compat Type 构造树；裸类型参数节点 → 复用 C02 `__cs2j_token_` 锚点（不另造）；GenericArrayType 节点 → 数组 Type 原语；不可映射节点 → CS2J1203。
- **CM-REG（注册生成）**：去重后闭型集生成 bootstrap 注册语句（D6）；注册顺序按规范化键字典序（快照确定性，禁依赖枚举遍历序）。
- **CM-LINK（消费守卫）**：D8 消费面入口插幂等 bootstrap 守卫；不加锁外的隐式初始化（Java 无 beforefieldinit 等价保证）。
- **CM-DYN（动态闭型排除）**：实参为类型参数且无法在编译期绑定到具体闭型的站点 → CS2J1210 登记，留 A2；dynamic/绑定失败 → CS2J1201。
- **CM-SRCGEN（源生成禁区）**：`JsonSerializerContext` 派生类、`[JsonSerializable(typeof(...))]` 枚举但不注册（AOT 路径不经过反射 options），报告 CS2J1204。
- **CM-AUDIT（路线 C 形态）**：以上 ENUM 规则在审计器中全部可独立运行，只产出报告不产生改写。

### 3.5 术语对照（本设计统一口径）

| 术语 | 含义 |
|---|---|
| 闭型（closed type） | 类型实参全部为具体类型的泛型实例化，如 C# `List<List<short>>`；Java 侧对应带嵌套实参的 `ParameterizedType`（`CSharpList<CSharpList<Integer>>`） |
| 开放泛型 / 泛型定义 | `List<T>` / `List<>`（`IsDefinition`）；类型参数 T 未绑定 |
| raw 类 | 擦除后的裸类型，如 `CSharpList.class`；同一 raw 可对应任意多个闭型 |
| 闭型层（layer）/ 闭包（closure） | compat `collectClosureLayers` 的展开结果：`LinkedHashMap<Class, Type[]>`，外到内一层一键；同 raw 出现在多层时首层胜出 |
| 结构键 | `closedCacheKey` 产出的 `(raw, 递归规范化 args)` 等值对象（`ClosedStructuralKey`）；反射 `ParameterizedType` 自身无 equals，不能直接当 Map 键 |
| token | C# 中间树上的显式 `System.Type` 锚点；C02 的 token 指类型参数 T 的 Class token，C30 的注册物指具体闭型的 Type 构造树 |
| 消费面（consumer surface） | 读取类型实参做运行时决策的代码位置；一期 = STJ 元数据链六个位（D8）+ 转换器发射的反射调用 |
| 入口语义 | Class/TypeReference 显式入口上允许保留的会话级 ThreadLocal 注册（0922 §2.4）；与元素/成员闭型解析严格区分，不在退役范围 |

---

## 4. 注册表与链位契约

### 4.1 现状（不动）

[PassStubCatalog.cs:100-109](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L100-L109)：

```text
Id                          = "closed-metadata"（Ids.ClosedMetadata，L27）
DisplayName                 = "Closed-type metadata"
IntermediateDirectoryName   = ".cs2j-closed-metadata-src"
Stage                       = PassStage.Abi
Status                      = PassStatus.Stub
DefaultEnabled              = false
RunAfterIds                 = { "generic-deepen" }
CatalogItems                = "C30"
Description                 = "Consume closed java.lang.reflect.Type metadata keyed by
                                generic-deepen tokens (P2; blocked on PatchClosed* archaeology)."
```

### 4.2 路线 C 落定时（M2 小改）

- stub 保留；Description 改为引用本设计与 M1 考古结论（c8d6d31f 删除事实、下游仓状态、compat 契约位置），删去 "blocked on ... archaeology"（考古已完成）。
- `--list-passes` 中保持 `Stub | default off`；显式启用仍 CS2J9001（[ProjectPassRunner.cs:53](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L53) 的拒绝路径天然生效）。
- 若实现只读审计器（§5.2），**不占用 closed-metadata id**：审计器挂在独立 diagnostics 动词或未来审计型 id 下，使 stub 的「未实施」语义保持诚实（避免审计器冒充实施）。

### 4.3 路线 A 翻转时（M3+，预登记）

```text
Id                          = "closed-metadata"
DisplayName                 = "Closed-type metadata registration"
IntermediateDirectoryName   = ".cs2j-closed-metadata-src"
Stage                       = PassStage.Abi
Status                      = PassStatus.Implemented
DefaultEnabled              = false（初版 opt-in；过 §9 门槛翻 true，且不早于 C02 默认开启）
RunAfterIds                 = { "generic-deepen" }
SupportLibrary              = PassSupportLibrary.None
NeedsSemanticModel          = true
VerifyBuildByDefault        = true
VerifyTestsByDefault        = true
CatalogItems                = "C30"
```

链位：`generic(E10) → generic-deepen(C02) → closed-metadata(C30) → …（attribute-normalize stub 亦锚 generic-deepen，拓扑上二者无先后强依赖，注册顺序由 RunAfter 偏序决定）`。本 pass 不重排既有 20 个 Implemented 条目；翻转时同步：[PassStubCatalog.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs) 删条目、保留 `Ids.ClosedMetadata` 常量（迁移到 [PassIds](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs#L6-L26) 最干净，与 C02 同先例）；[PassChainFactory.cs:98](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs#L98) `CreateAdapter` switch 增 case（仿 GenericDeepen L198-204）；注册表顺序测试、stub 计数测试（现 21 stub → 20；注：现有 PassStubCatalogTests 无硬编码计数断言）、`--list-passes` 断言同步。

### 4.4 中间目录与验证

- `.cs2j-closed-metadata-src` 输入为 generic-deepen 产物树；路线 A 必须读 C02 锚点/holder 后的最终符号形态（D5），故 RunAfter 是硬依赖不是偏好；C02 关（默认关现状）时本 pass 启用须 CS2J1205 报错（「closed-metadata 要求 generic-deepen 同批启用」），不静默降级。
- VerifyBuild/VerifyTests 默认开（最严档，同 C02）；接入既有指纹/preserve 机制（`pass:closed-metadata=on/off` 令牌随注册表全集自动生成，翻转一次性缓存失效可接受）。

### 4.5 与 foundation plan 标准交付包的对应

按 [preprocessor-foundation_plan.md](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md) L108-117 的统一交付面逐项登记（路线 A 翻转时）：

| 交付面 | 本通道落点 |
|---|---|
| Core（Options/Statistics/Diagnostics/Result/Preprocessor/Rewriter） | `src/CSharpToJava.Core/ClosedMetadata/`（§5.1 八文件） |
| CLI 一个适配器 | `Adapters/ClosedMetadataPassAdapter.cs`（§6.1），无门面类（同 C02 先例，目录名常量挂适配器） |
| 文档 | 本设计 + 后续 `docs/superpowers/plans/2026-09-29-c30-closed-metadata.md`（实施期写） |
| 测试四层 | Core 单测 + sln fixture + 中间树 Release build/test + residual 闸门 + CLI/注册表测试（§8） |
| 横切规则 | 语义改写只走 SemanticModel 全编译符号表；表达式树共享 `ExpressionTreeFacts`（C02 已提取则直接引用）；支撑库在 descriptor 显式声明（None）；不可判定显式报错 |
| 支撑库 | 不新增工程；compat 侧新增 API 属既有 `java/csharptojava-compat` 维护面，不经 `PassSupportLibrary` 注入 |

---

## 5. Core 组件设计

### 5.1 路线 A：`src/CSharpToJava.Core/ClosedMetadata/`（新目录，`ClosedMetadata*` 前缀）

| 文件 | 职责 |
|---|---|
| `ClosedMetadataOptions.cs` | 选项 record：`RegisterDeclaredClosedTypes`（D4①，默认开）、`RegisterExplicitTypeOfSites`（②）、`RegisterSerializerCallSites`（③，默认开）、`RegisterSignatureSites`（④）、`BootstrapForm`（StaticInitializer/ExplicitBootstrap，v1 仅 ExplicitBootstrap）、`Strict`、`ConsumerSurfaces`（D8 清单，默认仅 STJ+compat 面）、`OverridesPath` |
| `ClosedMetadataCollector.cs` | 全编译一次的闭型枚举器（D4）：产出确定性排序的 `ClosedTypeRecord`（规范化键、raw 符号、递归实参记录、来源站点集合、枚举规则名）；只认 SemanticModel 符号，不做文本扫描；与 C02 analyzer 共享符号工具（提取到 Core `Analysis/`），不复制族收集/TypeParameter 判定 |
| `ClosedTypeKey.cs` | 规范化键：C# 侧用 raw 的 `ToDisplayString()`（fully-qualified metadata 风格）+ 实参递归，加数组/nullable 形态标记；**与 Java 侧 closedCacheKey 的结构对齐是契约，不要求字符串相同**（跨语言各自规范化，经 Type 构造树在运行时汇合） |
| `RegistrationGenerator.cs` | 生成每项目 `_Cs2jClosedTypes.<ProjectName>.cs`：①显式 bootstrap 静态方法（确定性名 `__cs2j_register_closed_types`），方法体逐条 `global::System.Type __cs2j_closed_N = typeof(<闭型>); <compat 注册调用>`；②同一闭型去重；③生成文件头 auto-generated 注释 + CompilerGenerated（仿 [GenericDeepenHolderGenerator.cs:16-26](file:///d:/code/cs2j/src/CSharpToJava.Core/GenericPreprocess/GenericDeepenHolderGenerator.cs#L16-L26)）。**v1 注册调用目标的 C# 形态**：经转换器映射到 compat `TypeHelper.registerClosedType(Type)`——该 C# 包装 API 放哪（Cs2jPrelude 或 compat 镜像）由 M3 原型定，倾向不新增支撑库工程、直接 BCL `Type` 实参 + 转换器识别 `__cs2j` 前缀调用 |
| `ConsumptionLinker.cs` | 消费点接线：在序列化包装/转换器生成的指定消费面（D8）插入「确保 bootstrap 已执行」守卫（幂等标志位，同 C02 holder 的加锁双检形）；并产出「闭型 typeof 树 → Type 构造」的转换器协同规格（本文档登记，转换器改动单独立项，§11 风险 4） |
| `ClosedMetadataRewriter.cs` | 逐文件 rewriter：只做调用点必要的最小改写（守卫插入、显式 typeof 站点归一为注册局部）；声明枚举与文件生成不依赖逐文件改写 |
| `ClosedMetadataDiagnostics.cs` / `ClosedMetadataStatistics.cs` / `ClosedMetadataResult.cs` | 同 C02 外形；统计：`ClosedTypesEnumerated/Registered/Deduplicated/RegisterFilesGenerated/ConsumerSitesLinked/DynamicSitesSkipped/ResidualsByRule` |
| `ClosedMetadataResidualScanner.cs` | 重编译中间树：①已枚举消费面中仍以裸擦除类型反射消费且无注册可达的闭型站点；②重复/冲突键；③生成文件外的裸 `typeof(闭型)` 透传（strict 下报错） |
| `ClosedMetadataPreprocessor.cs` | 对外入口：`BindCompilation/Analyze/RewriteFile/GeneratedFileSpecs/ResidualScan`，三阶段数据流与回退归属（TouchedFiles/OwnsTouch）全仿 C02 |

### 5.2 路线 C：审计器/契约校验器（M2 可选，轻量）

- `ClosedMetadataAuditor.cs`（Core，只读）：输入 CSharpCompilation，输出 `ClosedTypeUsageReport`（闭型记录按规则分桶、消费面分类、动态闭型站点、预计走 ThreadLocal/反射签名的路径推断）；**不产诊断错误、不改树、不进执行链**。
- CLI 暴露为 `convert-project --audit-closed-types <report.md>` 或独立 `audit` 子命令（具体挂法 M2 定；不新增 pass id，见 §4.2）。
- 契约校验：在 compat 版本与生成器期望之间做静态一致性检查（compat 是否含 `registerClosedType`/`closedCacheKey`/`parameterized`——反射扫描 compat 已发布 jar 的公开方法清单），版本漂移时 Warning。M2 若成本高可只做文档化 checklist，不写代码。

### 5.3 路线 A 三阶段数据流

1. **Analyze（整树一次）**：`CompilationReadyAsync` 先校验执行计划含 generic-deepen（CS2J1205），再 `BindCompilation` → `ClosedMetadataCollector` 跑 CM-ENUM-1..4，产出 `ClosedTypeRecord` 候选集；记录按规范化键（§5.1 ClosedTypeKey）字典序排序，保证跨运行快照稳定；每条记录带 `Id/Outcome(Active|Skipped)/FailCode/FailReason/TouchedFiles`，供回退归属与 skip/force（与 C02 Plan 外形一致）。
2. **Plan/Generate（AfterFiles 一次）**：去重后的 Active 记录交 `RegistrationGenerator` 产按项目归属的注册文件内容；`ConsumptionLinker` 计算消费守卫插入点；二者清单先于逐文件改写确定，使 Rewriter 成为纯执行器。
3. **Rewrite（逐文件）+ Verify**：标准骨架 `CompilationReadyAsync → RewriteFileAsync × N → AfterFilesAsync`；clean 文件字节级复制；AfterFiles 落注册文件 + csproj Compile 接线 + residual scan；随后 restore/build/test（默认双开）。
4. **回退**：整树 build 相对基线新增错误时，按 TouchedFiles/OwnsTouch 归属唯一记录，排除后从原始副本重放；不可归属 → 仿 C02/JK 全回退（注册文件为新增文件，回退时连同 csproj 接线一并撤销）。

编译构造沿用 runner 标准（与 C02 完全一致）：`CSharpParseOptions(LanguageVersion.Latest)` + `NETCOREAPP` 预处理符号 + 合成 global usings + `GetBasicReferences()` + `allowUnsafe: true` DLL 编译；分析与残余扫描都跑在这同一棵全编译上；枚举/键判定一律以 SemanticModel 符号与 `SymbolEqualityComparer.Default` 比较为准，语法启发式仅用于单文件无模型模式的保守跳过（不用于注册内容生成）。

### 5.4 residual 后置自检（路线 A 闸门）

重写后对整树再编译并扫描（strict 下 Error，否则 Warning + 统计）：

1. D8 消费面内，闭型返回类型/实参仍以裸擦除 Class 反射消费、且沿调用图无注册/守卫可达的站点：**必须为 0**。
2. 同一规范化键对应多个互不一致 Java 目标闭型（键冲突）：0。
3. 注册文件之外、且未登记 skip 的裸 `typeof(闭型)` 透传节点：0（防 CM-TOKEN 漏网后静默流入 Java 擦除）。
4. bootstrap 守卫缺失的消费面入口：0。
5. 生成注册文件内重复注册语句（幂等性）：0。
6. 表达式树/源生成区内出现注册改写（结构性自检，须永远为 0）：0。

---

## 6. CLI 适配器与集成

### 6.1 路线 A

新增 `src/CSharpToJava.CLI/Preprocessing/Adapters/ClosedMetadataPassAdapter.cs`，仿 [GenericDeepenPassAdapter](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/Adapters/GenericDeepenPassAdapter.cs) 混合形态：

- 六参构造（options, verbose, filterUnsupportedProjects, compatNamespacePrefixes, verifyBuild, verifyTests）；`DeleteBuildOutputsAfterRestore = true`。
- `CompilationReadyAsync`：校验执行计划含 generic-deepen（否则 CS2J1205）→ BindCompilation/Analyze。
- `RewriteFileAsync`：调 rewriter，clean 文件返回 null（runner 字节级复制）。
- `AfterFilesAsync`：按 Tuple/C02 范式分发 `_Cs2jClosedTypes.<ProjectName>.cs` + csproj Compile 接线（归属算法直接复用/提取 GenericDeepenPassAdapter 的项目归属与 EnsureCompileItem）；随后 residual scan。
- `OnBuildFailed/OnTestsFailed` → CS2J1230/1231；`HasErrors`/`BuildChainSummaryLine`（打印枚举/注册/去重/消费点计数）/`BuildChainDiagnosticLines`。
- [PassChainFactory.cs](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassChainFactory.cs) switch 增 `PassIds.ClosedMetadata => new ClosedMetadataPassAdapter(new ClosedMetadataOptions(), …)`；无 legacy toggle（通用 `--enable-pass/--disable-pass closed-metadata` 控制）。
- 单文件 verb 模式：跨文件注册计划排除 + CS2J1290（同 C02/JK 限制）。

### 6.2 路线 C

无适配器；仅 stub 描述文案更新（§4.2）与可选审计器（§5.2）。CS2J9001 拒绝路径保持（[ProjectPassRunner.cs:53](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L53) 在触碰文件系统前拒绝，禁用态零目录、零耗时、不进执行计划打印）。

### 6.3 选项与开关

- v1 从简：`CreateAdapter` 中直接 `new ClosedMetadataOptions()`（同 C02，不引入 lazy OptionsFactory）；无 legacy toggle、无 `BuildLegacyToggles` 改动，全部经 [PassPlanResolver](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassPlanResolver.cs) 的 `--enable-pass/--disable-pass` 生效。
- overrides（force/skip）文件路径经 `PassChainConfiguration` 传入，只接受按类型/成员限定名的显式条目；skip 仍留 CLO001 Info 痕迹。
- 审计开关（路线 C）不占 pass 开关位：作为 `convert-project` 的附加输出选项存在，默认关，运行不改变转换结果（必须可与任意 pass 组合安全运行）。

---

## 7. 诊断策略

新增 CS2J12xx 段（C02 占用 CS2J11xx，不冲突；码位只追加）。MSBuild 格式同 C02。

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J1201 | Warning（strict Error） | 消费面需要的闭型无源符号（跨程序集/运行时构造），无法在 C# 侧注册；消息给出站点与替代形态（人工 A2 内联或改走反射签名） |
| CS2J1202 | Error | 键冲突：同一规范化键解析出不一致的 Java 目标闭型（典型：unsigned/nullable 归一时序错误），拒绝猜测 |
| CS2J1203 | Warning/Error | 闭型在当时符号形态下无法确定 Java 类型（多维数组/Nullable/元组落在未归一的中间形态），报站点与应先启用的 pass |
| CS2J1204 | Warning | STJ 源生成 `JsonSerializerContext`/AOT 形不在服务范围（D8），提示走人工或关闭本 pass |
| CS2J1205 | Error | closed-metadata 启用而 generic-deepen 未同批启用（链位硬依赖，§4.4） |
| CS2J1210 | Info（strict Warning） | 动态闭型站点（T 运行时绑定）A1 无法覆盖，登记待 A2；force 不解除 |
| CS2J1220 | Warning/strict Error | residual 闸门：消费面闭型无注册可达 / 注册文件外裸 typeof(闭型) 透传 |
| CS2J1230 | Error | 中间树 restore/build 失败 |
| CS2J1231 | Error | 中间树 test 失败 |
| CS2J1290 | Warning | 单文件模式计划非自包含（跨文件注册），排除 |
| CLO001 | Info | 明细（每闭型注册、去重、消费点链接），仅 verbose |

码段分配：转换器侧泛型码 CS2J1001-1003、pass 基建 CS2J9001/9002、C02 占 CS2J11xx，本通道占 CS2J12xx，只追加不改号；MSBuild 输出格式 `文件(行,列): warning/error CS2J12xx: [规则/符号限定名] 描述`，与 C02 完全同形。

原则沿用 catalog §4 风险 12 第一原则：不可判定即显式诊断，禁止 `/* TODO */`/占位/原样透传；Warning 经 `--strict` 升级；skip 仅经 overrides 按类型限定名显式指定且留 Info 痕迹。路线 C 下本章全部码位保留不分配实现（stub 只有 CS2J9001），避免码位空洞被误用。

---

## 8. 测试计划

三个半区分别验证，端到端只在能访问下游仓的环境成立。

### 8.1 .NET 半区（本仓，中间树）

- Core 单测 `tests/CSharpToJava.Tests/ClosedMetadataTests.cs`：内联源码 → 全编译 → Analyze/Generate，文本断言 + 输出可编译断言：
  - 枚举：字段/属性 `List<List<short>>`、`Dictionary<string, List<int>>`、嵌套 3 层、`List<short[]>`/`short?[]`、元组 struct 闭型、显式 `typeof(闭型)`、STJ 调用点实参；去重（同闭型多站点一条注册）；跨文件/跨项目归属。
  - 生成文件形态：bootstrap 方法名/注册调用顺序确定性（快照稳定）、CompilerGenerated 头、csproj 接线幂等（跑两遍不重复 Compile 项）。
  - 动态闭型（`List<T>` 泛型上下文）→ CS2J1210；跨程序集闭型 → CS2J1201；源生成上下文 → CS2J1204；nullable 未归一时序 → CS2J1203；C02 未启用 → CS2J1205。
  - residual 扫描三规则计数与 strict 升级；回退：注入必然编译失败计划验证全回退、产物与输入字节一致。
- 项目级 fixture `tests/SampleClosedMetadataSolution/`（`SampleClosedMetadataLib` + `SampleClosedMetadataApp` + `SampleClosedMetadataTests` + `.sln`，net10.0，按 SampleGenericDeepenSolution 范式以 Content 挂入 [CSharpToJava.ProjectTests.csproj](file:///d:/code/cs2j/tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj)）：
  - Lib DTO 形态：`List<List<short>>` 成员（标题用例）、`Dictionary<string, List<int>>`（字典值嵌套）、`List<short[]>`/`List<short?>`（数组/可空实参）、3 层嵌套、`CSharpKeyValuePair<string, List<int>>` 等价成员、值元组闭型成员（验 E06 之后形态）、两个 DTO 共享同一闭型（去重）、泛型 DTO `Bag<T>` 含 `List<T>` 成员（动态闭型对照，期望 CS2J1210 而非错误注册）。
  - App/Tests：STJ Serialize/Deserialize 包装调用、跨项目闭型消费、bootstrap 幂等（多次入口调用后注册计数不变）、同闭型多站点只注册一次的计数断言。
  - 断言：`result.Success`、统计计数（枚举/注册/去重/消费点链接/跳过分类）、注册文件归属正确项目且 csproj Compile 接线存在、中间树不含裸透传 `typeof(闭型)` 到注册文件外；`dotnet build -c Release` 退出码 0、`dotnet test -c Release` 全绿；clean 树字节级复制与 BOM 保持用例（仿各 pass）。

### 8.2 Java 半区（compat，本仓）

- [ClosedTypeMetadataTest.java](file:///d:/code/cs2j/java/csharptojava-compat/src/test/java/io/github/ningpp/compat/ClosedTypeMetadataTest.java) 已 pin 纯函数契约，路线 A 新增 API（`registerClosedType`/`resolveClosedType`）在同文件 TDD 增补：注册幂等、异闭型隔离、注册后查询不经任何 ThreadLocal（反射断言 7 张 ThreadLocal map 全空，沿用 L148-151 范式）。
- **多线程无 ThreadLocal 压测**：N 线程并发注册/解析混合闭型（含 `List<List<short>>` 与 `List<string>` 交错），跑完断言各 ThreadLocal 注册表为空、结果与线程亲和无关、无 ConcurrentModificationException；该测试是「零 ThreadLocal 消费」目标的直接证据（旧架构的 DiagPoison 类顺序依赖必须不可复现）。
- 字典键闭型：`CSharpDictionary<String, CSharpList<Integer>>` 键/值转换器闭型解析；泛型数组闭型；KeyValuePair 成员重绑定（substituteTypeVariables 已覆盖单测，补集成形）。

### 8.3 下游半区（dotnet-text-json，路线 C 在下游仓 / 路线 A 端到端闸）

- 0922 plan 的验收用例集回归：MyInt16TwoDimensionList 家族归零；ArrayTests 82 例基线（31E → 9E 的既得收益不回退）；readNull 8/8（含 TypeReference 路径）；DiagPoison 两步序列（object 数组 → hashtable，fresh options）无 `[repeat of consumed layer]`。
- 最小复现三件套（嵌套 List-only 往返、String/嵌套混排、反序列化先后顺序无关）。
- trace 开关 `-Dcs2j.trace.typeargs=true` 下闭型路径全部 `[closed layer]`，无 `[member layer]` 参与元素解析。
- 该半区**不在本仓 CI**；路线 C 下由下游仓持有测试与责任，本仓只在 M1/里程碑评审时人工拉取结果记录。

### 8.4 Release build+test / 双半区 / CLI

- fixture 与 `D:\csharp-sdk`（只读输入）预处理产物分别 `dotnet build -c Release` / `dotnet test -c Release`；sdk 口径「新增失败为零」；M2 审计器先在 sdk 跑测量轮，给出闭型站点频率作为路线 A 立项证据（补 catalog §5.5「—」）。
- 开关组合矩阵（fixture 参数化）：C02 off/on × C30 off/on 四格——C30 on + C02 off 必须 CS2J1205 失败退出；C02 on + C30 off 字节级等同只跑 C02；双 off 零差异；双 on 全绿。再叠 unsigned/tuple/array-method 开关各一格验证 D5 时序假设。
- 双半区联调（需 compat 先行 mvn install 到本机仓）：fixture 中间树编译引用的 compat 版本必须包含新增注册 API；版本不含时转换器/编译诊断须可定位（不允许 NoSuchMethodError 留到运行时才暴露——M3 决定该校验放在 build 期还是运行期守卫）。
- CLI/注册表：路线 C 下 `--enable-pass closed-metadata` 仍 CS2J9001（回归锁定）、`--list-passes` 文案正确；路线 A 翻转后状态/目录/默认关无目录无耗时、单文件 CS2J1290、拓扑顺序（generic-deepen 之后）与 catalogItems=="C30" 断言；指纹令牌含 `pass:closed-metadata=...`。
- 注册表一致性：[PassStubCatalogTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassStubCatalogTests.cs) 的 stub 计数（路线 A 翻转时 21 → 20）、[GenericDeepenRegistryTests.cs](file:///d:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/GenericDeepenRegistryTests.cs) 同类断言为本 pass 注册表测试的范本；ProjectPreprocessorsConsistentTests 追加本 pass 的类型存在性 + 中间目录名断言。

---

## 9. 默认开启验收门槛（仅路线 A 适用）

`defaultEnabled: false → true` 须同时满足：

1. **时序前提**：C02 generic-deepen 已默认开启且过其 §8 门槛（本 pass 硬依赖其 token 链与符号产物）。
2. **中间树正确性**：fixture 与 sdk 预处理树 Release build 零错误、test 全绿、新增失败为零。
3. **三半区证据**：.NET 中间树全绿；compat 新增并发压测 + 既有 ClosedTypeMetadataTest 全绿；下游半区（§8.3）MyInt16 归零、0 new failure、trace 无 ThreadLocal 参与元素解析。
4. **零 ThreadLocal 直接证据**：代表性嵌套 JSON 负载并发跑完，反射检查 compat 7 张 ThreadLocal map 在闭型路径上零读写（旧 registerMemberType 入口路径允许保留，但闭型元素/成员解析不得触达）。
5. **residual 闸门**：消费面闭型注册覆盖率 100%（白名单仅 CS2J1201/1204/1210 显式报告站点，全部 triage 有主）。
6. **转换器协同就绪**：闭型 typeof 树 → Type 构造树映射（D2）在代表性 Java 转换冒烟上被验证；FieldTransformer 等 fail-fast 不因本 pass 新增可达。
7. **文档**：本设计 + plan 定稿；compat 新 API javadoc 与消费面清单（D8）冻结；下游仓对接说明（路线 C/A 共存边界）成文。
8. **性能基准**（对应 §11 风险 3）：代表性嵌套负载（1/2/3 层 List 与 Dictionary 混合）上，闭型路径相对旧 ThreadLocal 路径的序列化吞吐回退不超过 5%、缓存命中率 > 99%（同一闭型不重复构造 converter/classInfo）；启动期注册开销在 sdk 规模程序上有绝对值测量并经评审接受；若启动成本超阈值，改为首次消费懒注册后重测。

不满足时任保持 opt-in；路线 C 下本条整体不适用（stub 长期保留）。默认开翻转的提交必须同时附带：三半区证据链接（中间树测试结果、compat surefire、下游回归人工记录）、sdk 基线 diff、性能基准数据，缺一不翻转——证据留存要求与 C02 §8 同口径。

---

## 10. 里程碑

| 里程碑 | 内容 | 门/产物 |
|---|---|---|
| **M1（强制门）考古收口 + 路线评审** | ①本设计评审；②在**可访问下游 dotnet-text-json 仓的环境**核实：补丁是否被搬入（查其生成 Java 是否含 `__cs2jDeclaredGenericType`/`getOrAddClass(java.lang.reflect.Type)`/`genericArgumentsInLayers` 调用）、其 compat 依赖版本、开关现状；③核实不通过时的服务范围判定（嵌套 JSON 是否仍属本仓生成器职责）；④路线 A/C 正式拍板 | **未完成 M1 不允许任何实现性提交**。产物：本文档 §2 增补下游核实结论 + 路线决议记录 |
| M2（路线 C） | stub 描述更新（§4.2）；可选只读审计器（§5.2）；sdk 闭型频率测量轮；compat 契约冻结备忘（API 清单 + 双键类是否统一的决定输入） | 审计报告；stub 长期保留决议 |
| M2'（路线 A 升格情形） | Core 骨架 + 适配器 + 注册表翻转（全空走字节复制）+ CS2J1205 链位校验 + 注册表/CLI 测试；sdk 测量轮 | `--list-passes` 可见 Implemented/default off |
| M3（路线 A） | ClosedMetadataCollector + RegistrationGenerator（A1 声明驱动）+ 文件分发；compat `registerClosedType/resolveClosedType` TDD 先行；D2 转换器原型（闭型 typeof 树映射，最小 STJ 面）；Core 单测 + fixture 最小集 | 中间树 build/test 全绿 |
| M4（路线 A） | ConsumptionLinker 消费面接线（D8 一期六点）+ residual 闸门 + strict/overrides + nullable/数组/元组规则补齐 + 并发压测；下游半区回归 | 候选默认开评审 |
| M5（路线 A） | 过 §9 门槛翻默认开；A2 调用点内联 token 原型（动态闭型）立档；登记转换器侧后续 PR 与 compat 旧 ThreadLocal API 退役顺序（仿 0922 §4 逐级退役，每级全量回归） | 默认开 |

### 10.1 M1 下游核实清单（强制门的具体动作）

在可访问下游 dotnet-text-json 仓（及其生成物）的环境中逐项核实，结论回填本文档 §2.3 末行并附提交/路径证据：

1. 下游生成 Java 中是否存在闭型链标志位与调用：`__cs2jDeclaredGenericType`、`__cs2jClosedType`、`__cs2jSetRuntimeClassInfo`、`getOrAddClass(java.lang.reflect.Type`、`getConverter(java.lang.reflect.Type`、`genericArgumentsInLayers(`、`createConverterForClosedType`、`putIfAbsentUntyped`。
2. 下游持有的形态：本仓生成器再生成后补丁仍在（说明下游有自己的后处理/分叉生成器）→ 路线 C 成立，核实其补丁与 c8d6d31f^ 版本的差异；生成物不含补丁但有手写等价改造 → 路线 C 的「下游自持手写」变体；完全没有 → 路线 A 升格近期主线的输入证据。
3. 下游依赖的 compat 版本（pom/本机仓 jar）是否包含 §2.4 全部 API 及 ClosedStructuralKey 形态；双键是否已在下游统一。
4. PatchFastPathWriteNullGuard（常开守卫，c8d6d31f 同批被删）在下游是否存在——若不存在，0922 plan fix #8 的 NPE 风险对下游是否以别的方式关闭。
5. 下游测试当前状态：MyInt16TwoDimensionList 家族、ArrayTests 82 例、DiagPoison 两序列是否仍绿（确认既得收益未随时间回退）。
6. 核实下游仓的归属与更新机制（独立仓 / 生成器分叉 / 手工移植），据此定 compat 演进的通知与回归责任。

任一项无法核实，M1 不得出具「路线 C 收口」结论；只允许出具「维持 stub + 继续阻塞」结论。

---

## 11. 风险

1. **下游仓状态未知（近期最高风险）**：补丁是否在下游 dotnet-text-json 仓自持，本树无法证实。若下游已持有而本仓重建（路线 A 激进落地），产生双份闭型链、compat 调用约定漂移；若下游未持有且仍从本仓生成物再生成，则嵌套 JSON 缺陷当前实际仍在。M1 强制门即为此设；禁止在未核实时把 stub 翻转。
2. **双键类并存的键冲突风险**：compat 现存 `ClosedGenericKey`（converterCacheKey，读 ThreadLocal 的混合键）与 `ClosedStructuralKey`（closedCacheKey，纯结构键）。路线 A 新增注册表若引入第三种键，缓存空间交汇时（`_converters`/`_classes` 混存 Class 键与结构键，0922 §5 已记录混存）可能出现「同闭型两条目」。M2 必须决定统一或显式隔离，并以 compat 测试锁死 equals/hashCode 形态。
3. **反射性能**：闭型路径把 `getOrAddClass(Class)` 变为 `getOrAddClass(Type)`，ParameterizedType 构造与结构键计算在热路径（集合元素帧逐个解析）。0922 实测功能正确但未给性能数据；路线 A 的 bootstrap 注册形在启动期一次性构造全程序闭型 Type 树，大程序可能有可观启动成本——M4 须带序列化吞吐基准（注册次数/缓存命中率），必要时改懒注册（首次消费时构造）。
4. **转换器 D2 缺口是路线 A 的真实主成本**：`typeof(List<List<short>>)` → Java 只剩 class literal，要求转换器新增闭型 Type 构造树发射，触碰其最厚的 typeof/Class token 特判簇（C02 设计 §1.1 列 20+ 相关测试）。缓解：D2 先独立原型 + 只覆盖 STJ 消费面，不做通用反射；原型不成立则路线 A 退回 C。
5. **与 c8d6d31f 架构方向的兼容性**：该提交清退的是「按硬编码文件名的后置正则补丁」。路线 A 是 **C# 中间树 pass**（新架构原住民形态，与 c01/c02/c03 同列），不违背该方向；但任何「在生成 Java 之后按文件名补丁」的诱惑（路线 B2）都已被历史否决，设计与评审中须显式拒绝。
6. **闭型枚举完备性**：A1 只能覆盖静态可见闭型；反射/字符串/dynamic/源生成构造的闭型不可枚举（CS2J1201/1204 显式化），对用户承诺必须文档化为「注册覆盖声明面，不覆盖运行时合成面」，防止静默错误输出（catalog §4 风险 12）。
7. **Nullable/数组/元组时序依赖**：枚举必须读 unsigned(E09)/tuple(E06)/array-method(E12) 之后的最终符号；链位或开关组合错误会产出错误 Java 目标类型（CS2J1202/1203 兜底，但 fixture 须覆盖开关组合矩阵）。
8. **ThreadLocal 退役顺序**：即便路线 A 全通，旧 7 张 ThreadLocal 仍承担 Class/TypeReference 入口语义（0922 §2.4 明确保留）。退役只能逐级（0922 §4 的四步顺序有实测数据支撑），每级下游全量回归；`registerMemberType` 本体最后退或不退。
9. **中间树成本**（catalog §4 风险 11）：新增一环一次树复制/reparse/restore；注册表驱动的指纹/preserve 已覆盖，holder 式生成不触发额外 restore；不接受多轮串行验证。
10. **C02 默认关的联动**：本 pass RunAfter 硬依赖 C02（§4.4），C02 默认开之前本 pass 只能 opt-in 且须双开关；两个 opt-in 长期并存违反 catalog §4 风险 10 精神，故 §9 把「C02 先默认开」列为第一门槛。
11. **compat 版本双半区失配**：路线 A 新增注册 API 后，中间树 build 与下游运行依赖同一 compat 版本；开发者本机 `D:\mvnrepo`、CI、下游仓三者版本可能漂移（0922 plan 的闸流程含 `mvn install` 步骤即手工依赖证据）。缓解：M3 定 build 期版本校验（§8.4），compat API 追加保持二进制兼容、旧入口不删（退役只走显式开关，仿 0922 退役范式）。
12. **文档状态再次漂移**：0922 设计标「已实现」次日实现即被删除而文档未回改，是本次考古的起因。本设计要求 M1 结论必须**回填本文件 §2.3/§10.1 并更新状态行**，路线决议与代码现状由同一提交保证一致；任何后续翻转/退役同步修订本设计，不允许再出现「文档说已实现、树里没有」的状态。

---

## 12. 并行协调与 Worktree 分配

### 12.1 并行协调

| 并行条目 | 关系 | 协调要点 |
|---|---|---|
| **C02 generic-deepen** | 一体两面（catalog §3.3 泛型擦除补偿族 E10+C02+C09+C30+C33）：C02 产出类型参数 token，C30 产/消闭型 Type 结构 | C30 RunAfter 硬锚 generic-deepen；共享符号工具提取到 Core `Analysis/`，不复制 analyzer；C02 不发射注册码、C30 不改 C02 节点；默认开时序 C02 先行；热文件 PassCatalog/PassStubCatalog/PassChainFactory/注册表测试按 id 分区追加 |
| **C10 attribute-normalize** | stub 同锚 generic-deepen（[PassStubCatalog.cs:177](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L177)）；`[JsonConverter]`/`[JsonPropertyName]` 等按名属性直接影响消费面枚举（0922 §3.2 条 6 属性优先级不变） | 拓扑偏序不强依赖；消费面属性识别规则各自实现，若 C10 先合则 C30 读其归一结果 |
| **C33 variance-audit** | 同审计族，读同一批泛型符号但不改节点 | 零实现冲突；审计器（§5.2）可与 C33 审计基建共享报告格式 |
| **C09 thread-static** | 同涉及 ThreadLocal 关键词，但 C09 处理 `[ThreadStatic]` 字段，C30 处理序列化类型实参队列 | 语义面不相交；文档/诊断中明确两类 ThreadLocal 的不同命运（C09 显式化字段；C30 消灭消费侧依赖） |
| **序列化 compat（TypeHelper/ConcurrentHashMapHelper/PropertyInfo/FieldInfo）** | C30 路线 A 必须改 compat（新增注册表面、可能统一双键类） | compat 改动 TDD 先行、mvn install 到本机仓后下游才能消费；任何 compat 签名变更需同步检查下游调用方（M1 核实其存在性后建立通知机制） |
| **E06 tuple / E09 unsigned / E12 array-method** | C30 枚举读取其归一后产物（D5） | 无代码冲突；fixture 覆盖开关组合 |
| **转换器 typeof/Class token 簇**（TypeOperationTransformer/ConversionContext） | 路线 A 的 D2 必须改转换器 | 转换器 PR 独立于本 pass、以本 pass 默认开为前置门（同 C02 与转换器的切分先例）；本 pass PR 不改转换器 |
| 公共热文件 | `PassStubCatalog.cs`、`PassCatalog.cs`、`PassChainFactory.cs`、`PassStubCatalogTests.cs`、`PassChainOrderTests.cs` | 各 worktree 仅追加各自 id 的行；禁止顺手重排既有链序 |

---

### 12.2 Worktree 分配（只写不执行）

> 本节为实施分配登记，**本文档任务不执行任何 git 写操作、不创建 worktree**。M1 为强制评审门，worktree 仅在路线决议通过后由实施会话创建。

- 分支：`feature/preproc-c30-closed-metadata`
- 目录：`.worktrees/preproc-c30-closed-metadata`
- base：`953293b9`（2026-09-29 `docs: update doc base commit`，HEAD 祖先，距 HEAD 2 个 docs-only 提交）
- 创建命令（实施期执行，非本文档执行）：

```bash
git worktree add .worktrees/preproc-c30-closed-metadata -b feature/preproc-c30-closed-metadata 953293b9
```

实施约束：仅在该 worktree 内改码；路线 C 落地时只允许文档/stub 描述/审计器类改动；路线 A 按 §10 里程碑逐提交、默认关先行；compat 改动与 C# 改动分提交；转换器改动留独立后续 PR；未过 §9 门槛不得翻默认开。

---

## 附：开放问题裁决（2026-09-29 拍板）

> **拍板目标**：支持全部 C# 语法/语义、便利下游 C#→Java 转换。统一口径：① 每种合法 C# 构造必须有语义等价的可编译 Java 终态，v1 可显式诊断门控但必须登记终态去向，禁止长期静默丢弃；② 中间树始终是普通合法 C#，Java 侧差异集中到 compat/映射层收口；③ 语义精确优先于分配/性能，优化变体须有画像证据；④ 预处理默认开前转换器旧臂由独立 PR 退役、不留双轨窗口，CheckPass 安全网永久保留；⑤ 合成类型名 `Cs2j<Role>_<Member>_<n>`、合成成员/临时 `_cs2j…`、让位改名 `<Name>_cs2j<Purpose>`（单下划线后缀）；⑥ 跨 pass 顺序依赖一律显式 RunAfter 固化；⑦ Implemented+默认关观察期不超过两个里程碑窗口；⑧ worktree 创建命令维持 base `953293b9`，创建后第一步 rebase 到当时 dev-3.x HEAD（本裁决时为 `ef70e5a2`），stub 计数红灯由首个锚点翻转 PR 按真值收口。

**裁决（散见决策表/里程碑门的悬置项，按主题收口）：**

1. **路线总决议**：批准 **"近期 C → 中期 A"** 单线路径——**近期 = 路线 C**（stub 保留：只更新 stub 描述、交付只读审计器/契约型输出与 sdk 频率测量，码位保留不分配实现）；**中期 = 路线 A**（C# 前置注册，A1 声明驱动先行、A2 动态闭型随后）；**路线 B（生成 Java 后的文件级补丁形态，含 B2）永久关闭**，已被 c8d6d31f 架构方向历史否决，设计与评审中不再出现。
2. **M1 为强制门且环境前提明确**：M1 下游考古**必须在可访问下游 dotnet-text-json 仓及其生成物的环境中**完成 §10.1 六项核实并回填 §2.3；本裁决时本机 **`D:\dotnet-text-json` 不存在、不可访问**，故在此之前**不允许任何实现性提交**，也不得出具"路线 C 收口"以外的结论。
3. **A 升格触发条件**：M1 若证伪路线 C（下游完全没有补丁/等价手写改造）**且嵌套 JSON 仍在本仓生成器服务范围内**，则**路线 A 升格为近期主线**（走 M2'），无需再等一个评审周期；若嵌套 JSON 已不属服务范围，则维持 C 并书面记录边界。
4. **路线 A 纪律预先锁定**：RunAfter 硬锚 generic-deepen 且默认开时序 C02 先行（双 opt-in 不长期并存）；零 ThreadLocal 消费是硬验收（7 张 ThreadLocal map 反射断言）；双键类统一/隔离在 M2 定案并以 compat 测试锁死；转换器 D2（闭型 typeof 树映射）为独立后续 PR、以本 pass 默认开为前置门。
5. **状态一致性红线**：M1 结论必须回填本文件 §2.3/§10.1 并更新状态行（路线决议与代码现状同一提交保证），任何后续翻转/退役同步修订本设计，杜绝"文档说已实现、树里没有"再次发生。
